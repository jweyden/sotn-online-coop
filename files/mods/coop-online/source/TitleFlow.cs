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
//
// Saves remember their room's mode (2026-11-03): an online save made in Free Select
// says "Richter" (SaveInfo.character = 1, what the file screen shows as Richter mode),
// one made in Normal Mode says Alucard (0). On the host's file screen the saves of the
// other mode are greyed out and can't be picked. Loading one, everyone plays their own
// pick (the host too: the save's character is only its mode).
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
        // (a save of the other mode, picked anyway: the load fails, "Version error", and
        // it's back to the file list)
        if (I is OnlineCoop host && host.FilteringSaves && m.ReadU32(GameEngineStepAddr) == EngCardLoad &&
            (m.ReadU32(save + SaveInfoCharacter) != 0) != host._net.FreeSelect)
        {
            Console.WriteLine($"[coop-online] that's a {(host._net.FreeSelect ? "Normal Mode" : "Free Select")} save: not in this room");
            c.V0 = unchecked((uint)-1);
            return;
        }
        orig(c, m);
        if (I is OnlineCoop self && (int)c.V0 >= 0 && self._net.IsHost && self._net.Status == NetSession.State.Connected)
        {
            self.SendSave(m, save);
            self.ApplyOwnCharacter(m);   // (the save's character is its mode, not who we play)
        }
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

    // Joining a game that's already going (or rejoining after a dropout): someone in the
    // room on the title screen picks their character and asks (Msg.LateJoin); the host
    // makes a save of where it is right now (the game's own StoreSaveData, into spare
    // high RAM) and sends it to them; they load it like any host save (and teleport to
    // the others from wherever it puts them). Host, P1 live, after its update.
    const uint FnStoreSaveData = 0x800E9C14u, LateSaveAddr = 0x80700000u;
    readonly HashSet<int> _lateJoinWanted = new();

    void SendSaveToLateJoiners(CpuContext c, IMemory m)
    {
        if (!_net.IsHost || !Sotn.Game.InGame || Sotn.Game.IsLoading || Paused(m) || _lateJoinWanted.Count == 0) return;
        foreach (var f in _friends)
        {
            if (!_lateJoinWanted.Remove(f.Number) || !f.Valid || f.Peer.InGame) continue;
            Call(c, m, FnStoreSaveData, LateSaveAddr, 0, 0);
            Console.WriteLine($"[coop-online] {f.Name} is joining the game in progress: sending them the game as it is");
            SendSave(m, LateSaveAddr, f.Link);
        }
        _lateJoinWanted.RemoveWhere(n => !_friends.Exists(f => f.Number == n));
    }

    // A save made where the host is (not in a save room) only says which room: loading
    // it puts us at that room's own entry spot (in the entrance: outside the walls). Once
    // in, we go to the host's exact spot (the teleport's same-room move, no spell).
    // Guest, once a frame (P1 live).
    long _lateArrivalAt = -1;

    void TickLateArrival(IMemory m)
    {
        if (_lateArrivalAt < 0 || !Sotn.Game.InGame || Sotn.Game.IsLoading) return;
        if (_lateArrivalAt == 0) { _lateArrivalAt = _vsyncs + 20; return; }   // (settled first)
        if (_vsyncs < _lateArrivalAt) return;
        if (_vsyncs > _lateArrivalAt + 600) { _lateArrivalAt = -1; Console.WriteLine("[coop-online] joined in progress: couldn't get to the host (teleport with F6)"); return; }
        if (_friends.Find(f => f.Number == 0) is not { Valid: true } host || !host.Peer.InGame) return;
        RequestTeleport(host);
        if (_tp == Tp.Idle) return;   // (not now: next frame)
        if (_tp == Tp.Casting) { _castWant = false; _tp = Tp.Go; }   // (no spell: straight there)
        _lateArrivalAt = -1;
        Console.WriteLine($"[coop-online] joined in progress: going to {host.Name}");
    }

    // ---------------------------------------------------------------- saves and the room's mode

    const int SaveInfoCharacter = 0x230;   // SaveData.info.character
    uint _storingSave;

    [PreHook("dra", "StoreSaveData_dra")]
    public static void BeforeStoreSave(CpuContext c, IMemory m) { if (I is OnlineCoop self) self._storingSave = c.A0; }

    // Online, the save says its room's mode
    [PostHook("dra", "StoreSaveData_dra")]
    public static void AfterStoreSave(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || self._storingSave == 0 || self._net.Status != NetSession.State.Connected) return;
        m.WriteU32(self._storingSave + SaveInfoCharacter, self._net.FreeSelect ? 1u : 0u);
        Console.WriteLine($"[coop-online] saved: a {(self._net.FreeSelect ? "Free Select" : "Normal Mode")} save");
        self._storingSave = 0;
    }

    // The host's file screen: which saves (card, block) are of the other mode, from the
    // game's own list of what's on the cards (g_SaveSummary: it reads the cards once and
    // keeps that, so this reads the list, not the saves as they're read in).
    const uint SaveSummaryAddr = 0x801BC8E0u;       // g_SaveSummary[2], 0x3A8 each: icon[15] at 0, character[15] at 0x2D0
    const uint SummarySize = 0x3A8, SummaryCharacter = 0x2D0;
    const uint FileCursorAddr = 0x801D6B04u;        // the file screen's cursor: card * 15 + block
    const uint EngFileList = 0x33, EngNewGameName = 0x10;   // Upd_Eng_51 (the list), Upd_Eng_0x10
    const uint EngCardLoad = 0x101;                           // Upd_Eng_257: loading the picked save
    const ushort PadCross = 0x40;

    bool FilteringSaves => _net.IsHost && _net.Status is NetSession.State.Hosting or NetSession.State.Connected;
    bool OtherModeSave(int port, int block)
    {
        if (!FilteringSaves || port is < 0 or > 1 || block is < 0 or >= 15) return false;
        IMemory? m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return false;
        uint card = SaveSummaryAddr + (uint)port * SummarySize;
        if ((int)m.ReadU32(card + (uint)block * 4) < 0) return false;   // (no save there: a new game, or nothing)
        bool free = m.ReadU32(card + SummaryCharacter + (uint)block * 4) != 0;
        return free != _net.FreeSelect;
    }

    // The file screen's save icons: those of the other mode drawn dim (the cursor's one a
    // little less, so you can see where you are).
    //   func_801B27A8(x, y, w, h, u, v, clut, tpage, tge, colour): the last six on the stack
    [Replace("sel", "func_801B27A8")]
    public static void DrawFileIconHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is OnlineCoop self && self.FilteringSaves && c.A2 == 0x10 && c.A3 == 0x10 && m.ReadU32(c.SP + 0x1C) == 0x16)
        {
            int x = (int)c.A0 - 0x20, y = (int)c.A1;
            int port = x >> 8, col = (x & 0xFF) / 0x18, row = (0x90 - y) / 0x10;
            if (port is 0 or 1 && col is >= 0 and < 3 && row is >= 0 and < 5 && self.OtherModeSave(port, row * 3 + col))
            {
                uint colour = m.ReadU32(c.SP + 0x24);
                m.WriteU32(c.SP + 0x24, colour >= 0x80 ? 0x30 + (colour - 0x80) / 4 : 0x18);
            }
        }
        orig(c, m);
    }

    // (the log: what the file screen shows, as this room sees it)
    void LogFileList(IMemory m)
    {
        var saves = new List<string>();
        for (int port = 0; port < 2; port++)
            for (int block = 0; block < 15; block++)
            {
                uint card = SaveSummaryAddr + (uint)port * SummarySize;
                if ((int)m.ReadU32(card + (uint)block * 4) < 0) continue;
                bool free = m.ReadU32(card + SummaryCharacter + (uint)block * 4) != 0;
                saves.Add($"card {port + 1} block {block + 1}: {(free ? "Free Select" : "Normal")}{(OtherModeSave(port, block) ? " (greyed out)" : "")}");
            }
        Console.WriteLine($"[coop-online] file screen ({(_net.FreeSelect ? "Free Select" : "Normal")} room): {(saves.Count == 0 ? "no saves" : string.Join("; ", saves))}");
    }

    // Before the menu's update: Cross on a save of the other mode is a no.
    void FileSelectBefore(CpuContext c, IMemory m, uint step)
    {
        if (!FilteringSaves || step != EngFileList) return;
        ushort tapped = m.ReadU16(PadsAddr + 4);
        int at = (int)m.ReadU32(FileCursorAddr);
        if ((tapped & PadCross) != 0 && OtherModeSave(at / 15, at % 15))
        {
            m.WriteU16(PadsAddr + 4, (ushort)(tapped & ~PadCross));
            Call(c, m, FnPlaySfx, SfxError);
        }
    }

    // After it: the file screen's own "new game" (an empty block) starts the room's new
    // game together; on a save of the other mode, why it's greyed out.
    void FileSelectAfter(CpuContext c, IMemory m, uint stepBefore)
    {
        if (!FilteringSaves) return;
        uint step = m.ReadU32(GameEngineStepAddr);
        if (stepBefore == EngFileList && step == EngNewGameName)
        {
            if (_net.PlayerCount >= 2 && PicksFitMode()) StartNewGameFromTitle(c, m);
            else { m.WriteU32(GameEngineStepAddr, EngFileList); Call(c, m, FnPlaySfx, SfxError); }
            return;
        }
        if (step == EngFileList && stepBefore != EngFileList) LogFileList(m);
        int at = (int)m.ReadU32(FileCursorAddr);
        if (step == EngFileList && OtherModeSave(at / 15, at % 15))
        {
            DrawSmall(c, m, _net.FreeSelect ? "A Normal Mode save:" : "A Free Select save:", 0x70, 0xB8);
            DrawSmall(c, m, _net.FreeSelect ? "this room is Free Select" : "this room is Normal Mode", 0x70, 0xC8);
        }
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
    bool _hostSaveLate;
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
        _hostSaveLate = HostPlaying;   // (sent from a game that's going, not from the title screen)
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
            if (_hostSaveLate) _lateArrivalAt = 0;   // (joining a game that's going: to the host once we're in)
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
