using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Once two players are paired on the title screen, the host leads:
//  - the host picks New Game: the guest's game starts a new game by itself (through
//    the game's own name-entry Start, with the guest's name), and the two meet at the
//    synced start (NewGame.cs);
//  - the host loads a save: that save goes to the guest, whose game loads it the way
//    the title screen loads one from the memory card (ApplySaveData, then its own
//    fade into loading). Both start in the same save room with the same progress.
// The guest waits on the Netplay screen meanwhile.
public partial class OnlineCoop
{
    const uint PixBuf = 0x8007EFE4u;            // g_Pix[0]: where the title screen reads a save into
    const int SaveSize = 0x11CC;                // sizeof(SaveData)
    const uint FnApplySaveDataSel = 0x801B94BCu;
    const uint SaveLoadedAddr = 0x8003C730u, CardPortAddr = 0x80097924u, CardBlockAddr = 0x8006C378u;
    const uint EngLoadFadeOut = 0x104;          // Upd_Eng_260: fade, then on into the game

    bool GuestPaired => _net.Status == NetSession.State.Connected && !_net.IsHost;

    // ---------------------------------------------------------------- host

    [Replace("sel", "ApplySaveData_sel")]
    public static void ApplySaveDataHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint save = c.A0;
        orig(c, m);
        if (I is OnlineCoop self && (int)c.V0 >= 0 && self._net.IsHost && self._net.Status == NetSession.State.Connected)
            self.SendSave(m, save);
    }

    void SendSave(IMemory m, uint save, NetLink? to = null)
    {
        var raw = new byte[SaveSize];
        for (int i = 0; i < SaveSize; i++) raw[i] = m.ReadU8(save + (uint)i);
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw);
        var packed = ms.ToArray();
        const int Chunk = 900;
        int parts = (packed.Length + Chunk - 1) / Chunk;
        byte id = ++_saveSendId;
        for (int p = 0; p < parts; p++)
        {
            int off = p * Chunk, len = Math.Min(Chunk, packed.Length - off), part = p;
            Send(to, Msg.HostSave, w => { w.Write(id); w.Write((byte)part); w.Write((byte)parts); w.Write((ushort)len); w.Write(packed, off, len); });
        }
        Console.WriteLine($"[coop-online] sent the save to {to?.Name ?? _net.PeerName} ({packed.Length} bytes in {parts} parts)");
    }
    byte _saveSendId;

    // Joining a game that's already going: the host makes a save of where it is right
    // now (the game's own StoreSaveData, into spare high RAM) and sends it to whoever
    // just came in on the title screen; they load it like any host save (and teleport
    // to the others from wherever it puts them). Host, P1 live, after its update.
    const uint FnStoreSaveData = 0x800E9C14u, LateSaveAddr = 0x80700000u;
    readonly HashSet<int> _lateSaveSent = new();
    readonly Dictionary<int, long> _outsideSince = new();

    void SendSaveToLateJoiners(CpuContext c, IMemory m)
    {
        if (!_net.IsHost || !Sotn.Game.InGame || Sotn.Game.IsLoading || Paused(m)) return;
        foreach (var f in _friends)
        {
            // (out of the game for a while: not just a few frames behind a game we
            // started together)
            bool outside = f.Valid && !f.Peer.InGame && (f.Peer.Flags & 128) == 0;
            if (!outside) { _outsideSince.Remove(f.Number); _lateSaveSent.Remove(f.Number); continue; }
            if (!_outsideSince.TryGetValue(f.Number, out long since)) _outsideSince[f.Number] = since = _vsyncs;
            if (_vsyncs - since < 300 || _lateSaveSent.Contains(f.Number)) continue;
            _lateSaveSent.Add(f.Number);
            Call(c, m, FnStoreSaveData, LateSaveAddr, 0, 0);
            Console.WriteLine($"[coop-online] {f.Name} joined while we're playing: sending them the game as it is");
            SendSave(m, LateSaveAddr, f.Link);
        }
        _lateSaveSent.RemoveWhere(n => !_friends.Exists(f => f.Number == n));
    }

    // (NewGame.cs) the host's new game: the guest starts one too
    void HostStartsNewGame()
    {
        if (!_net.IsHost || _net.Status != NetSession.State.Connected) return;
        Send(Msg.HostNewGame, _ => { });
        _startedWith = _net.Peers.Select(l => l.Number).ToHashSet();   // (NewGame.cs waits for these)
    }

    // ---------------------------------------------------------------- guest

    byte[]?[]? _saveParts;
    byte _saveRecvId;
    byte[]? _hostSave;
    bool _guestNewGame;
    int _menuFrames;

    void OnHostSave(BinaryReader r)
    {
        byte id = r.ReadByte(), part = r.ReadByte(), parts = r.ReadByte();
        int len = r.ReadUInt16();
        var data = r.ReadBytes(len);
        if (_saveParts == null || id != _saveRecvId || _saveParts.Length != parts) { _saveParts = new byte[parts][]; _saveRecvId = id; }
        if (part < parts) _saveParts[part] = data;
        if (Array.Exists(_saveParts, x => x == null)) return;
        using var all = new MemoryStream();
        foreach (var x in _saveParts) all.Write(x!);
        all.Position = 0;
        using var z = new DeflateStream(all, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        _saveParts = null;
        if (raw.Length != SaveSize) { Console.WriteLine($"[coop-online] the host's save arrived damaged ({raw.Length} bytes)"); return; }
        _hostSave = raw.ToArray();
        Console.WriteLine($"[coop-online] {From} loaded a save: loading it here too");
    }

    void OnHostNewGame() { _guestNewGame = true; Console.WriteLine($"[coop-online] {From} started a new game: starting one here too"); }

    // On the title screen, before the menu's own update (game thread).
    void GuestFollowsHost(CpuContext c, IMemory m)
    {
        // (only once the menu is up: the "press start" screen, after a game over, has
        // none of its graphics yet; the save or new game waits until we get there)
        uint step = m.ReadU32(GameEngineStepAddr);
        // (and settled there: right after a game over the save is already waiting, and the
        // menu's first frames are still setting its objects up)
        _menuFrames = step == EngMainMenuIdle || step == EngNetplay ? _menuFrames + 1 : 0;
        if (_menuFrames < 30) return;
        if (_hostSave is { } save)
        {
            _hostSave = null; _guestNewGame = false;
            for (int i = 0; i < save.Length; i++) m.WriteU8(PixBuf + (uint)i, save[i]);
            if ((int)Call(c, m, FnApplySaveDataSel, PixBuf) < 0) { Console.WriteLine("[coop-online] couldn't load the host's save"); return; }
            m.WriteU32(SaveLoadedAddr, 1);
            ApplyOwnCharacter(m);
            m.WriteU32(CardPortAddr, 0);
            m.WriteU32(CardBlockAddr, unchecked((uint)-1));   // (saving later makes a new file on our own card)
            Call(c, m, FnPlaySfx, 0x80);                        // SET_UNK_80, as a card load does
            Call(c, m, FnMenuHideAllGfx);
            m.WriteU32(GameEngineStepAddr, EngLoadFadeOut);
            return;
        }
        if (_guestNewGame)
        {
            _guestNewGame = false;
            StartNewGameFromTitle(c, m);   // (NetplayMenu.cs)
        }
    }

    // Title-screen messages arrive before any game is running (where they're normally
    // read): read them here, and keep the rest for later.
    void PumpTitleMessages(IMemory m)
    {
        while (_net.TryReceiveReliable(out var from, out var body))
        {
            if (from.Number == 0 && body.Length >= 2 && body[0] == 0x50 && body[1] is (byte)Msg.HostSave or (byte)Msg.HostNewGame)
            {
                _msgFrom = from;
                var r = new BinaryReader(new MemoryStream(body, 2, body.Length - 2));
                if (body[1] == (byte)Msg.HostSave) OnHostSave(r); else OnHostNewGame();
                _msgFrom = null;
            }
            else _heldMessages.Enqueue((from, body));
        }
    }

    // A name as the name-entry screen holds it: 8 of a-z & ! - . ' and space.
    static string NameEntryText(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s.ToLowerInvariant())
        {
            if (sb.Length == 8) break;
            sb.Append(ch is >= 'a' and <= 'z' or '&' or '!' or '-' or '.' or '\'' or ' ' ? ch : ' ');
        }
        return sb.ToString().PadRight(8);
    }
}
