using System;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Hle;
using ImGuiNET;

/*
 * Maria from the PSP version, as a player character.
 *
 * sotn-decomp's mods/mariapsp-forpsx builds PSP Maria's decompiled C as a PS1
 * player overlay (MARIA.BIN, loaded at 0x8013C000 like RIC.BIN) and turns her
 * PSP voice bank into a PS1 one (SD_RIH.VH/VB). It takes Richter's place: the
 * game runs Richter mode, with Maria's code, Richter's sound bank, and her lines
 * played from her bank's samples over his (the Marias' own sounds, below). Her
 * own init patches the bits of dra that differ (HUD subweapon icons, the damage
 * hooks; the voice table it patches is put back).
 *
 * This mod serves those files whenever the game reads Richter's from the disc
 * (engine patch: DiscFs.SectorOverrides), and swaps the recompiled overlay: when
 * the dispatcher loads "ric" for those sectors, it loads "maria" instead (the
 * recompiler builds it from disc/maria/MARIA.BIN, config/sotn.json).
 *
 * Choosing her: name the save MARIA. That starts Richter mode on any save (the
 * game only allows RICHTER on a cleared one), and saves keep the name, so a
 * loaded MARIA save gets her back.
 *
 * Files (built from the player's own PSP disc by build-maria.sh):
 * disc/maria/MARIA.BIN, SD_RIH.VH, SD_RIH.VB.
 *
 * Saturn Maria works the same way under the name SATMARIA: sotn-decomp's
 * mods/mariasat-forpsx builds MARIASAT.BIN (PS1 Richter's C with Saturn Maria's
 * moves ported in; recompiled as "mariasat"), and her sprites, too big for the
 * player overlay slot, go into high RAM (MARIASAT.SPR at 0x80600000; the recomp
 * has 8MB). Built from the player's own Saturn disc by build-mariasat.sh:
 * disc/mariasat/MARIASAT.BIN, MARIASAT.SPR.
 *
 * The Saturn's long-coat Richter (RICHTER2) works the same way under the name
 * RICHTER2: Richter's code with his Saturn art, disc/richter2/RICHTER2.BIN and
 * RICHTER2.SPR, also built by build-mariasat.sh.
 *
 * Or skip the names: a new game shows the Saturn's character select after name
 * entry (CharSelect.cs), and the choice is kept in the save.
 */
public partial class MariaMod : IMod
{
    const string Dir = "disc/maria";
    const string SatDir = "disc/mariasat";
    const uint SatSpritesAddr = 0x80600000u;
    const uint PlayableCharacterAddr = 0x8003C9A0u; // g_PlayableCharacter
    const uint SaveNameAddr = 0x80097B90u;           // g_Status.saveName[12]
    const uint PlayerRichter = 1;

    public bool AlwaysMaria; // Maria in every Richter-mode game, whatever the name
    public bool AlwaysSaturn; // with AlwaysMaria: the Saturn one

    static MariaMod? I;
    (int Lba, int Sectors, byte[] Data)[] _files = [], _satFiles = [], _r2Files = [];
    byte[]? _satSprites, _r2Sprites;
    bool _servedR2;
    int _ricLba = -1;
    bool _servedRic, _servedSat, _triedLoad;
    string _satStatus = "not loaded";
    string _status = "waiting for the disc";

    public void OnLoad()
    {
        I = this;
        Event.AddListener<OverlayLoadedEvent>(OnOverlayLoaded);
        DiscFs.SectorOverrides.Add(ServeSector);
        RecompOne.Runtime.Host.Window.ModOverlay.Draw += DrawSelectText;
    }

    public void OnUnload()
    {
        Event.RemoveListener<OverlayLoadedEvent>(OnOverlayLoaded);
        DiscFs.SectorOverrides.Remove(ServeSector);
        RecompOne.Runtime.Host.Window.ModOverlay.Draw -= DrawSelectText;
        I = null;
    }

    public void DrawSettings()
    {
        ImGui.Text($"Maria (PSP): {_status}");
        ImGui.Text($"Maria (Saturn): {_satStatus}");
        ImGui.Text("Name your save MARIA (PSP) or SATMARIA (Saturn) to play as her,");
        ImGui.Text("or RICHTER2 for the Saturn's long-coat Richter.");
        ImGui.Text($"Saturn character select: {_selStatus}");
        ImGui.Checkbox("Character select after name entry", ref CharacterSelect);
        ImGui.Checkbox("Maria in every Richter-mode game", ref AlwaysMaria);
        ImGui.Checkbox("...the Saturn one", ref AlwaysSaturn);
        if (ImGui.Button("Reload files")) _status = LoadFiles();
    }

    // Our files, placed over the disc files they replace. Locate reads the disc's
    // directory through ServeSector, which serves nothing while _files is empty.
    string LoadFiles()
    {
        _files = [];
        _ricLba = -1;
        var fs = Runtime.Cd?.Fs;
        if (fs == null) return "no disc";
        _triedLoad = true;
        // each Maria (and the long-coat Richter) loads on its own
        _satStatus = LoadSatFiles(fs);
        LoadR2Files(fs);
        LoadCharSel();
        return LoadPspFiles(fs);
    }

    string LoadPspFiles(DiscFs fs)
    {
        (string Ours, string Disc)[] files =
        {
            ("MARIA.BIN", "BIN/RIC.BIN"),   // (her voice plays from its own samples: Richter's bank stays)
        };
        var loaded = new (int, int, byte[])[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            string path = Path.Combine(Dir, files[i].Ours);
            if (!File.Exists(path)) return $"{path} not found";
            if (!fs.Locate(files[i].Disc, out int lba, out uint size)) return $"{files[i].Disc} not on the disc";
            var data = File.ReadAllBytes(path);
            if (data.Length > size) return $"{files[i].Ours} is bigger than {files[i].Disc}";
            loaded[i] = (lba, (int)((size + 2047) / 2048), data);
        }
        _ricLba = loaded[0].Item1;
        _files = loaded;
        Console.WriteLine($"[maria] files ready (RIC.BIN at lba {_ricLba})");
        return "ready";
    }

    string LoadSatFiles(DiscFs fs)
    {
        _satFiles = [];
        _satSprites = null;
        string bin = Path.Combine(SatDir, "MARIASAT.BIN"), spr = Path.Combine(SatDir, "MARIASAT.SPR");
        if (!File.Exists(bin) || !File.Exists(spr)) return "not built (build-mariasat.sh)";
        (string Ours, string Disc)[] files =
        {
            ("MARIASAT.BIN", "BIN/RIC.BIN"),
        };
        var loaded = new List<(int, int, byte[])>();
        int lba = -1;
        foreach (var (ours, disc) in files)
        {
            string path = Path.Combine(SatDir, ours);
            if (!File.Exists(path)) continue;
            if (!fs.Locate(disc, out int l, out uint size)) return $"{disc} not on the disc";
            var data = File.ReadAllBytes(path);
            if (data.Length > size) return $"{ours} is bigger than {disc}";
            if (lba < 0) lba = l;
            loaded.Add((l, (int)((size + 2047) / 2048), data));
        }
        _satFiles = loaded.ToArray();
        _satHeader = BitConverter.ToUInt32(File.ReadAllBytes(bin), 0);
        _satSprites = File.ReadAllBytes(spr);
        int ricLba = lba;
        string fx = Path.Combine(SatDir, "MARIASAT.FX");
        _fx = File.Exists(fx) ? LoadFx(File.ReadAllBytes(fx)) : null;
        string snd = Path.Combine(SatDir, "MARIASAT.SND");
        _snd = File.Exists(snd) ? File.ReadAllBytes(snd) : null;
        _sndLoaded = false;
        _sounds.Clear();
        _ricLba = ricLba;
        return "ready";
    }

    void LoadR2Files(DiscFs fs)
    {
        _r2Files = [];
        string bin = Path.Combine("disc/richter2", "RICHTER2.BIN"), spr = Path.Combine("disc/richter2", "RICHTER2.SPR");
        if (!File.Exists(bin) || !File.Exists(spr) || !fs.Locate("BIN/RIC.BIN", out int lba, out uint size)) return;
        var data = File.ReadAllBytes(bin);
        if (data.Length > size) return;
        _r2Files = [(lba, (int)((size + 2047) / 2048), data)];
        _r2Sprites = File.ReadAllBytes(spr);
        _ricLba = lba;
    }

    // 0: Richter, 1: PSP Maria, 2: Saturn Maria, 3: long-coat Richter
    int WantMaria()
    {
        var m = Runtime.Mem;
        if (m == null || m.ReadU32(PlayableCharacterAddr) != PlayerRichter) return 0;
        switch (SelectedVariant(m))
        {
            case 0: return 0;
            case 1: return _files.Length > 0 ? 1 : 0;
            case 2: return _satFiles.Length > 0 ? 2 : 0;
            case 3: return _r2Files.Length > 0 ? 3 : 0;
        }
        if (IsName(m, "richter2")) return _r2Files.Length > 0 ? 3 : 0;
        if (IsName(m, "satmaria") || AlwaysMaria && AlwaysSaturn) return _satFiles.Length > 0 ? 2 : 0;
        return AlwaysMaria || IsName(m, "maria") ? 1 : 0;
    }

    static bool IsMariaName(IMemory m) => IsName(m, "maria") || IsName(m, "satmaria") || IsName(m, "richter2");

    static bool IsName(IMemory m, string name)
    {
        for (int i = 0; i < name.Length; i++)
            if (char.ToLowerInvariant((char)m.ReadU8(SaveNameAddr + (uint)i)) != name[i]) return false;
        byte next = m.ReadU8(SaveNameAddr + (uint)name.Length);
        return next == ' ' || next == 0;
    }

    // Past the end of our file, the rest of the disc file's sectors read as zeros
    // (MARIA.BIN is shorter than RIC.BIN; the tail is her zeroed bss).
    byte[]? ServeSector(int lba)
    {
        if (!_triedLoad && Runtime.Cd != null)
        {
            _status = LoadFiles();
            if (_status != "ready") Console.WriteLine($"[maria] {_status}");
        }
        int want = WantMaria();
        if (want == 0) return null;
        foreach (var (start, sectors, data) in want == 3 ? _r2Files : want == 2 ? _satFiles : _files)
        {
            if (lba < start || lba >= start + sectors) continue;
            if (start == _ricLba)
            {
                if (want == 3) _servedR2 = true;
                else if (want == 2) _servedSat = true;
                else _servedRic = true;
            }
            var sector = new byte[2048];
            int off = (lba - start) * 2048;
            if (off < data.Length) Array.Copy(data, off, sector, 0, Math.Min(2048, data.Length - off));
            return sector;
        }
        return null;
    }

    // The dispatcher loads "ric" when RIC.BIN's sectors start landing in RAM; if
    // those were Maria's, run her recompiled code there instead.
    void OnOverlayLoaded(OverlayLoadedEvent e)
    {
        if (e.Name != "ric") return;
        RestoreSfxTable();
        bool sat = _servedSat, r2 = _servedR2;
        _mariaLive = _servedRic || sat;
        _satLive = false;
        if (!_servedRic && !sat && !r2) return;
        _servedRic = _servedSat = _servedR2 = false;
        string name = r2 ? "richter2" : sat ? "mariasat" : "maria";
        if (!Dispatcher.Overlays.ContainsKey(name))
        {
            Console.WriteLine($"[maria] WARNING: no recompiled {name} overlay (its .BIN missing when recompiling?)");
            return;
        }
        Dispatcher.Unload("ric");
        Dispatcher.Load(name);
        _satLive = sat;
        if (sat && _satSprites != null) Runtime.Mem?.LoadBytes(SatSpritesAddr, _satSprites);
        if (r2 && _r2Sprites != null) Runtime.Mem?.LoadBytes(SatSpritesAddr, _r2Sprites);
    }

    // Hit sounds. Outside Alucard mode, every stage's hit detection plays
    // SFX_RIC_WHIP_HIT (0x705) when the player's attack hits, a sample in
    // Richter's bank. On the PSP Maria gets Alucard's weapon hit instead: so both
    // Marias do here.
    const uint SfxRicWhipHit = 0x705, SfxWeaponHitA = 0x678;
    bool _mariaLive;

    [PreHook("dra", "PlaySfx")]
    public static void BeforePlaySfx(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) return;
        self._sfxWritePos = m.ReadU16(SfxWritePosAddr);
        if (m.ReadU32(GameStateAddr) <= GameTitle) self._satLive = self._mariaLive = false;   // her game was left
        if (self._mariaLive && c.A0 == SfxRicWhipHit && m.ReadU32(PlayableCharacterAddr) == PlayerRichter)
            c.A0 = SfxWeaponHitA;
    }

    // PSP Maria's init (MarPsxInit) points rows of dra's sound table at her voice
    // bank's layout: Richter's voice ids, in a table every player shares in co-op
    // and that stays in RAM for the next game. Her lines play from their own
    // samples instead (her sounds, below), so the rows are put back right after,
    // from the disc's DRA.BIN (in RAM, a friend's Maria may have written hers).
    const uint SfxDataAddr = 0x800BF554u;   // g_SfxData, 7 bytes per id from 0x600
    const uint SfxPatchedFirst = 0x6E0, SfxPatchedEnd = 0x730;
    const uint DraAddr = 0x800A0000u;   // where DRA.BIN loads
    byte[]? _sfxTable;

    [PostHook("maria", "MarPsxInit")]
    public static void AfterPspInit(CpuContext c, IMemory m) => I?.RestoreSfxTable();

    void RestoreSfxTable()
    {
        var m = Runtime.Mem;
        if (m == null) return;
        uint at = SfxDataAddr + (SfxPatchedFirst - 0x600) * 7, len = (SfxPatchedEnd - SfxPatchedFirst) * 7;
        if (_sfxTable == null)
        {
            try
            {
                var dra = Runtime.Cd?.Fs?.ReadFile("DRA.BIN");
                uint off = at - DraAddr;
                if (dra != null && off + len <= dra.Length) _sfxTable = dra.AsSpan((int)off, (int)len).ToArray();
            }
            catch (Exception e) { Console.WriteLine($"[maria] DRA.BIN: {e.Message}"); }
            if (_sfxTable == null) return;
        }
        for (uint i = 0; i < len; i++) m.WriteU8(at + i, _sfxTable[i]);
    }

    // Name entry: MARIA starts Richter mode (with Maria), cleared save or not.
    // Other names go on to the character select.
    [PostHook("sel", "func_801AD66C")]
    public static void AfterNameEntry(CpuContext c, IMemory m)
    {
        if (I == null) return;
        I.StartSelect(m);
        if (!IsMariaName(m)) return;
        m.WriteU32(PlayableCharacterAddr, PlayerRichter);
        if (IsName(m, "satmaria")) I._satNewGame = true;
    }

    // A new SATMARIA game gets Saturn Maria's stats (Saturn GAME.PRG: 200 HP,
    // 99/999 hearts, 200 MP; she spends MP on attacks). Her init runs after the
    // game has set Richter's.
    const uint StatusHpAddr = 0x80097BA0u; // hp, hpMax, hearts, heartsMax, mp, mpMax
    bool _satNewGame, _coopNewGame;   // (_coopNewGame: a new game picked online)

    [PostHook("mariasat", "mas_RicInit")]
    public static void AfterSatInit(CpuContext c, IMemory m)
    {
        if (I == null || !(I._satNewGame || I._coopNewGame && SelectedVariant(m) == 2)) return;
        I._satNewGame = I._coopNewGame = false;
        uint[] stats = { 200, 200, 99, 999, 200, 200 };
        for (int i = 0; i < stats.Length; i++) m.WriteU32(StatusHpAddr + (uint)i * 4, stats[i]);
    }

    // Her time stops (command moves 2, 3: MarTimeStop sets g_unkGraphicsStruct.unk28)
    // put dra in Engine_5, which in Richter mode on the PS1 only updates the
    // stage, so she'd never finish the move and time would stay stopped. The PSP
    // runs the player overlay there too (its unk28 == 0x100 case): do that.
    const uint TimeStopAddr = 0x80097420u, GameEngineStepAddr = 0x8003C9A4u;
    const uint PlOvlMain = 0x8013C000u, PlOvlUpdateEntities = 0x8013C008u;
    const uint EngineTimeStop = 5, MarTimeStopValue = 4, SetUnpauseSfxScripts = 0xF;
    bool _timeStopped;

    [PreHook("dra", "HandlePlay")]
    public static void BeforeHandlePlay(CpuContext c, IMemory m)
    {
        if (I != null) I._timeStopped = m.ReadU32(GameEngineStepAddr) == EngineTimeStop;
    }

    [PostHook("dra", "HandlePlay")]
    public static void AfterHandlePlay(CpuContext c, IMemory m)
    {
        if (I == null || !I.SatPlaying(m)) return;
        if (m.ReadU32(GameEngineStepAddr) != EngineTimeStop || m.ReadU32(TimeStopAddr) != MarTimeStopValue) return;
        if (m.ReadU32(PlayableCharacterAddr) != PlayerRichter) return;
        var regs = c.Snapshot();
        if (!I._timeStopped)
        {
            // going in, dra paused the sound scripts, cutting off her voice line;
            // the Saturn doesn't
            c.A0 = SetUnpauseSfxScripts;
            Dispatcher.Call(c, m, FnPlaySfx);
            c.Restore(regs);
            return;
        }
        Dispatcher.Call(c, m, m.ReadU32(PlOvlMain));
        Dispatcher.Call(c, m, m.ReadU32(PlOvlUpdateEntities));
        c.Restore(regs);
    }

    // --- the Marias' own sounds ---------------------------------------------------
    // Neither Maria's voice (nor Saturn Maria's effects) is in a PS1 bank, and
    // both play with Richter's bank loaded, his sound table untouched. Each of
    // her sounds is played as one of Richter's voice sounds, a carrier, tagged
    // with which of hers it is (Saturn Maria's overlay says so at MAR_SOUND as it
    // plays it; PSP Maria's lines are swapped here, BeforeMarPlaySfx). The tag
    // follows the sound through dra's queue, and when libsnd keys the carrier on,
    // the voice is armed with her sample in the SPU's second sample RAM (as
    // co-op's PSP voices are), at the pitch that plays it at its own rate over
    // whatever bank the carrier's tone is in. So it's the same on every player's
    // game in co-op, whoever's bank is loaded.
    //   Saturn: MARIASAT.SND (tools/mariasat-sfx.py), tags 1..34 (20 voice lines,
    //     14 effects; the overlay's MarOwnSound + 1)
    //   PSP: her lines in disc/maria/SD_RIH.VH/VB (build-maria.sh), tags 0x101..
    const uint SoundAddr = 0x80680208u;   // MAR_SOUND
    const uint SfxWritePosAddr = 0x80139000u, SfxReadPosAddr = 0x80138FACu;
    const int SfxRingSize = 0x100, PspTagBase = 0x100;
    const int PspVoiceFirst = 0x8E6, PspVoiceCount = 16;
    // (as mariasat.c's g_MarCarriers: full-volume tones in Richter's and
    // Alucard's banks; they take turns, as dra cuts a sound short when the same
    // id plays again)
    static readonly int[] Carriers = { 0x6F9, 0x6FA, 0x6FB, 0x6FC, 0x6FF, 0x700 };
    int _pspCarrier;

    const uint SndRam2Base = 0x20000; // co-op's PSP voices (64 KB) use the bottom of it
    // one ADPCM block of nothing that ends the voice (for the pair's second voice)
    static readonly byte[] Silence = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    byte[]? _snd;
    bool _sndLoaded;
    // tag -> sample in Ram2 and the pitch (libsnd's units, 0x1000 = 44.1 kHz) it plays at
    readonly Dictionary<int, (uint Addr, int Pitch)> _sounds = new();
    readonly ushort[] _slotSound = new ushort[SfxRingSize];
    int _sfxWritePos, _sndPending, _sndVoice = -1, _sndKeying, _sndArmed = -1;

    void LoadSounds()
    {
        _sndLoaded = true;
        var spu = RecompOne.Runtime.Runtime.Spu;
        if (spu == null) return;
        spu.LoadBank2(SndRam2Base, Silence);
        uint dst = SndRam2Base + (uint)Silence.Length;
        int sat = 0, psp = 0;
        if (_snd is { } d && d.Length >= 8 && BitConverter.ToUInt32(d, 0) == 0x324E534D)
        {
            int count = BitConverter.ToInt32(d, 4);
            for (int i = 0; i < count; i++, sat++)
            {
                int off = BitConverter.ToInt32(d, 8 + i * 12), size = BitConverter.ToInt32(d, 12 + i * 12);
                int rate = BitConverter.ToInt32(d, 16 + i * 12);
                spu.LoadBank2(dst, d.AsSpan(off, size));
                _sounds[1 + i] = (dst, (int)Math.Round(4096.0 * rate / 44100));
                dst += (uint)(size + 15) & ~15u;
            }
        }
        string vh = Path.Combine(Dir, "SD_RIH.VH"), vb = Path.Combine(Dir, "SD_RIH.VB");
        if (File.Exists(vh) && File.Exists(vb))
            psp = LoadPspVoice(spu, File.ReadAllBytes(vh), File.ReadAllBytes(vb), ref dst);
        Console.WriteLine($"[maria] sounds in SPU bank 2: Saturn {sat}, PSP {psp}");
    }

    // PSP Maria's 16 lines, as her bank lays them out (sotn-decomp's
    // tools/maria-sfx): line n on program 1 + n / 8, tone n % 8 * 2, played at
    // note 60. VH: 0x20 header (programs at 0x12), 128 programs of 0x10, 16 tones
    // of 0x20 for each program that has tones (centre +4, fine +5, VAG +0x16),
    // 256 VAG sizes / 8; VB: the VAGs back to back.
    int LoadPspVoice(RecompOne.Runtime.Spu spu, byte[] vh, byte[] vb, ref uint dst)
    {
        if (vh.Length < 0x820 || BitConverter.ToUInt32(vh, 0) != 0x56414270) return 0;
        int programs = BitConverter.ToUInt16(vh, 0x12), toneBase = 0x20 + 128 * 0x10;
        int sizes = toneBase + programs * 16 * 0x20;
        if (sizes + 512 > vh.Length) return 0;
        int loaded = 0;
        for (int n = 0; n < PspVoiceCount; n++)
        {
            int prog = 1 + n / 8, block = 0;
            for (int p = 0; p < prog; p++) if (vh[0x20 + p * 0x10] != 0) block++;
            int at = toneBase + (block * 16 + n % 8 * 2) * 0x20, vag = BitConverter.ToInt16(vh, at + 0x16);
            if (block >= programs || vag <= 0 || vag > 255) continue;
            int off = 0;
            for (int v = 0; v < vag; v++) off += BitConverter.ToUInt16(vh, sizes + v * 2) * 8;
            int len = BitConverter.ToUInt16(vh, sizes + vag * 2) * 8;
            if (len == 0 || off + len > vb.Length) continue;
            spu.LoadBank2(dst, vb.AsSpan(off, len));
            _sounds[PspTagBase + 1 + n] = (dst, NotePitch(null, 60, vh[at + 4], vh[at + 5], 0));
            dst += (uint)(len + 15) & ~15u;
            loaded++;
        }
        return loaded;
    }

    // PSP Maria plays her lines as 0x8E6..; her overlay would turn them into
    // Richter's ids on her bank's rows
    [PreHook("maria", "MarPlaySfx")]
    public static void BeforeMarPlaySfx(CpuContext c, IMemory m)
    {
        var self = I;
        int n = (int)c.A0 - PspVoiceFirst;
        if (self == null || n < 0 || n >= PspVoiceCount) return;
        m.WriteU16(SoundAddr, (ushort)(PspTagBase + 1 + n));
        c.A0 = (uint)Carriers[self._pspCarrier];
        self._pspCarrier = (self._pspCarrier + 1) % Carriers.Length;
    }

    // a tagged sound went into dra's queue (co-op's replays queue nothing)
    [PostHook("dra", "PlaySfx")]
    public static void AfterPlaySfx(CpuContext c, IMemory m)
    {
        var self = I;
        int tag = m.ReadU16(SoundAddr);
        if (self == null || tag == 0) return;
        m.WriteU16(SoundAddr, 0);
        if (m.ReadU16(SfxWritePosAddr) != self._sfxWritePos) self._slotSound[self._sfxWritePos & (SfxRingSize - 1)] = (ushort)tag;
    }

    // ExecSfx plays the queued sound at the read position
    [PreHook("dra", "func_8013572C")]
    public static bool BeforePlayQueuedSfx(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) return true;
        int pos = m.ReadU16(SfxReadPosAddr) & (SfxRingSize - 1);
        self._sndPending = self._slotSound[pos];
        self._slotSound[pos] = 0;
        if (self._sndPending != 0 && !self._sndLoaded) self.LoadSounds();
        return true;
    }

    // (a sound of hers that didn't reach its key-on, crowded out: say so)
    [PostHook("dra", "func_8013572C")]
    public static void AfterPlayQueuedSfx(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || self._sndPending == 0) return;
        Console.WriteLine($"[maria] sound {self._sndPending:X} wasn't keyed on");
        self._sndPending = 0;
    }

    // func_80132A04(voice, vabId, prog, tone, note, volume, distance) keys on
    // (voice, tone) and (voice + 1, tone + 1), each through libsnd's SsUtKeyOnV.
    // A voice's arm (here, or co-op's voices, CoopCore.KeyOnPairHook, which runs
    // after this) is for this sound only: the SPU takes it up at its next key-on,
    // a moment later on the audio thread, so if dra gives the voice to another
    // sound first (a warg's death howl over Alucard's killing-blow grunt), that
    // sound would play the sample. So each pair starts disarmed.
    [PreHook("dra", "func_80132A04")]
    public static bool BeforeKeyOnPair(CpuContext c, IMemory m)
    {
        var self = I;
        int voice = (short)c.A0;
        var spu = RecompOne.Runtime.Runtime.Spu;
        if (spu != null && voice is >= 0 and < 24)
        {
            spu.DisarmBank2(voice);
            if (voice < 23) spu.DisarmBank2(voice + 1);
        }
        if (self == null || self._sndPending == 0) return true;
        if (voice is >= 0 and < 23 && self._sounds.ContainsKey(self._sndPending))
        {
            self._sndVoice = voice;
            self._sndKeying = self._sndPending;
        }
        self._sndPending = 0;
        return true;
    }

    // Her sample goes on the carrier's first voice, silence on the second (the
    // carrier's tone again, detuned). Armed only as SsUtKeyOnV keys them (it skips
    // a tone with no sample): an arm left behind would play on that voice's next
    // sound. Runs after co-op's arming (CoopCore.KeyOnPairHook), so it wins.
    [PreHook("main", "SsUtKeyOnV")]
    public static bool BeforeSsKeyOn(CpuContext c, IMemory m)
    {
        var self = I;
        var spu = RecompOne.Runtime.Runtime.Spu;
        if (self == null) return true;
        self._sndArmed = -1;
        if (spu == null || self._sndVoice < 0) return true;
        int voice = (short)c.A0;
        if (voice == self._sndVoice && self._sounds.TryGetValue(self._sndKeying, out var s))
        {
            // SsUtKeyOnV(voice, vab, prog, tone, note, fine, voll, volr)
            int game = NotePitch(m, (short)m.ReadU16(c.SP + 0x10), -1, -1, (short)m.ReadU16(c.SP + 0x14),
                (short)c.A1, (short)c.A2, (short)c.A3);
            spu.ArmBank2(voice, s.Addr, game > 0 ? (int)((long)s.Pitch * 4096 / game) : s.Pitch);
            self._sndArmed = voice;
        }
        else if (voice == self._sndVoice + 1)
        {
            spu.ArmBank2(voice, SndRam2Base, 0x1000);
            self._sndArmed = voice;
            self._sndVoice = -1;
        }
        return true;
    }

    [PostHook("main", "SsUtKeyOnV")]
    public static void AfterSsKeyOn(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || self._sndArmed < 0) return;
        if ((short)c.V0 < 0) RecompOne.Runtime.Runtime.Spu?.DisarmBank2(self._sndArmed);
        self._sndArmed = -1;
    }

    // libsnd's pitch for a note on a tone (note2pitch2, a u16), from its own
    // tables in RAM: the tone's centre and fine tune either given, or looked up
    // as SsUtKeyOnV does (the vab's program -> its tone block -> the tone).
    const uint SvmVabPg = 0x8003C8C8u, SvmVabTn = 0x8003C958u, NotePitchTable = 0x80032F14u;
    static readonly ushort[] PitchTable = BuildPitchTable();

    static ushort[] BuildPitchTable()
    {
        var t = new ushort[12 * 16];
        for (int i = 0; i < t.Length; i++) t[i] = (ushort)Math.Floor(4096 * Math.Pow(2, i / 192.0));
        return t;
    }

    static int NotePitch(IMemory? m, int note, int centre, int shift, int fine, int vab = 0, int prog = 0, int tone = 0)
    {
        if (centre < 0)
        {
            if (m == null || vab is < 0 or >= 16 || prog is < 0 or >= 128 || tone is < 0 or >= 16) return 0;
            uint pg = m.ReadU32(SvmVabPg + (uint)vab * 4), tn = m.ReadU32(SvmVabTn + (uint)vab * 4);
            if (pg == 0 || tn == 0) return 0;
            uint at = tn + (uint)((m.ReadU8(pg + (uint)prog * 0x10 + 8) * 16 + tone) * 0x20);
            centre = m.ReadU8(at + 4);
            shift = m.ReadU8(at + 5);
        }
        int a3 = (fine + shift) / 8, a2 = 0;
        if (a3 >= 16) { a2 = 1; a3 -= 16; }
        short n = (short)(note + 60 - centre + a2);
        int octave = n / 12 - 5, pos = n % 12 * 16 + a3;
        if (pos < 0 || pos >= 192) return 0;
        ushort p = m != null ? m.ReadU16(NotePitchTable + (uint)pos * 2) : PitchTable[pos];
        if (octave > 0) p = (ushort)(p << octave);
        else if (octave < 0) p = (ushort)(p >> -octave);
        return p;
    }

    // --- Saturn Maria's effects ---------------------------------------------------
    // Her objects (mariasat overlay, entity slots 4..63) don't go through dra's
    // drawing: each one that has graphics fills an entry in a table at
    // 0x80680000 (MarFx: entityId, package, palette bank, blend, brightness, blue's brightness) and
    // this draws the frame of that Saturn sprite package that its animCurFrame
    // picks, as host images. Saturn art is 5/4 wider, so it's drawn at 4/5 width.
    const uint FxTableAddr = 0x80680000u, EntitiesAddr = 0x800733D8u;
    const int EntitySize = 0xBC;
    const uint CurrentBufferPtr = 0x8006C37Cu, BackbufferX = 0x8006C39Cu, BackbufferY = 0x8006C3A0u;
    const uint OtOfs = 0x474;
    const int OtSize = 0x200;

    sealed class FxPkg
    {
        public ushort[] Colors = [];
        public (int W, int H, byte[] Px)[] Images = [];
        public (ushort Attr, short X, short Y, ushort Img)[][] Frames = [];
        public readonly Dictionary<int, int> Tex = new();
    }

    FxPkg?[]? _fx;
    bool _satLive; // her overlay replaced "ric" when it last loaded

    // Saturn Maria is playing: her overlay is the one in RAM (its first word, the
    // player main, is MARIASAT.BIN's) and the game is in play, not on the title,
    // a menu or another character's game
    const uint PlayerOverlayAddr = 0x8013C000u, GameStateAddr = 0x8003C734u;
    const int GameTitle = 1, GamePlay = 2;
    uint _satHeader;

    bool SatPlaying(IMemory? m) =>
        _satLive && m != null && _satHeader != 0 && m.ReadU32(PlayerOverlayAddr) == _satHeader &&
        m.ReadU32(GameStateAddr) == GamePlay;

    static FxPkg?[]? LoadFx(byte[] d)
    {
        if (d.Length < 8 || BitConverter.ToUInt32(d, 0) != 0x5846534D) return null;
        int count = BitConverter.ToInt32(d, 4);
        var pkgs = new FxPkg?[count];
        for (int n = 0; n < count; n++)
        {
            int o = BitConverter.ToInt32(d, 8 + n * 4);
            if (o == 0) continue;
            int banks = BitConverter.ToUInt16(d, o), nImages = BitConverter.ToUInt16(d, o + 2), nFrames = BitConverter.ToUInt16(d, o + 4);
            o += 8;
            var p = new FxPkg { Colors = new ushort[banks * 16], Images = new (int, int, byte[])[nImages], Frames = new (ushort, short, short, ushort)[nFrames][] };
            for (int i = 0; i < banks * 16; i++, o += 2) p.Colors[i] = BitConverter.ToUInt16(d, o);
            for (int i = 0; i < nImages; i++)
            {
                int w = BitConverter.ToUInt16(d, o), h = BitConverter.ToUInt16(d, o + 2);
                p.Images[i] = (w, h, d.AsSpan(o + 4, w * h).ToArray());
                o += 4 + w * h;
            }
            for (int f = 0; f < nFrames; f++)
            {
                int parts = BitConverter.ToUInt16(d, o);
                o += 2;
                var fr = new (ushort, short, short, ushort)[parts];
                for (int k = 0; k < parts; k++, o += 8)
                    fr[k] = (BitConverter.ToUInt16(d, o), BitConverter.ToInt16(d, o + 2), BitConverter.ToInt16(d, o + 4), BitConverter.ToUInt16(d, o + 6));
                p.Frames[f] = fr;
            }
            pkgs[n] = p;
        }
        return pkgs;
    }

    static int FxTexture(FxPkg p, int img, int bank)
    {
        int key = img * 64 + bank;
        if (p.Tex.TryGetValue(key, out int t)) return t;
        var (w, h, px) = p.Images[img];
        int nb = p.Colors.Length / 16;
        if (bank >= nb) bank = 0;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int idx = px[i];
            if (idx == 0) continue;
            ushort c = p.Colors[bank * 16 + idx];
            rgba[i * 4] = (byte)((c & 31) << 3);
            rgba[i * 4 + 1] = (byte)(((c >> 5) & 31) << 3);
            rgba[i * 4 + 2] = (byte)(((c >> 10) & 31) << 3);
            rgba[i * 4 + 3] = 255;
        }
        t = GpuPrims.RegisterImage(rgba, w, h);
        if (t >= 0) p.Tex[key] = t;
        return t;
    }

    // The save's colour set, when playing without co-op (co-op's Palettes.cs does it
    // otherwise): the character's sprite palettes, recoloured, over the game's own in
    // VRAM before drawing (palette p at ((p & 0xF) * 16, 0xF0 + ((p >> 4) & 0xF))).
    const uint ClutAddr = 0x8006CBCCu;
    static bool CoopRunning() => OnlineCoop.Running;

    [PostHook("dra", "RenderEntities")]
    public static void ApplySoloPalette(CpuContext c, IMemory m)
    {
        if (I == null || !I.ColourSets || !Sotn.Game.InGame || CoopRunning()) return;
        var (ch, set) = CoopCharacters.FromSave(m);
        if (set == 0 || CoopCharacters.IsAlucard(ch) != (m.ReadU32(PlayableCharacterAddr) == 0)) return;
        var gpu = Runtime.Gpu;
        if (gpu == null) return;
        foreach (int p in CoopPalettes.PaletteIds(ch))
        {
            gpu.WriteGp0(0xA0000000u);
            gpu.WriteGp0((uint)(0xF0 + ((p >> 4) & 0xF)) << 16 | (uint)((p & 0xF) * 16));
            gpu.WriteGp0(1u << 16 | 16u);
            for (int i = 0; i < 16; i += 2)
            {
                ushort lo = CoopPalettes.Recolor(ch, set, m.ReadU16(ClutAddr + (uint)(p * 32 + i * 2)));
                ushort hi = CoopPalettes.Recolor(ch, set, m.ReadU16(ClutAddr + (uint)(p * 32 + i * 2 + 2)));
                gpu.WriteGp0((uint)hi << 16 | lo);
            }
        }
    }

    [PostHook("dra", "RenderEntities")]
    public static void AfterRenderEntities(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || !self.SatPlaying(m) || self._fx == null) return;
        int bx = (int)m.ReadU32(BackbufferX), by = (int)m.ReadU32(BackbufferY);
        DrawSatHud(m, bx, by);
        DrawSpellScreen(m);
        DrawPlayerFx(m);
    }

    // The live player's effects (entity slots 4..63) from the effect table. Also
    // called by co-op (mods/shared/Characters.cs) in a friend Saturn Maria's render
    // pass, with her entities and table swapped in.
    public static void DrawPlayerFx(IMemory m)
    {
        var self = I;
        if (self == null || self._fx == null) return;
        bool table = false;
        int bx = (int)m.ReadU32(BackbufferX), by = (int)m.ReadU32(BackbufferY);
        for (int i = 4; i < 64; i++)
        {
            uint e = EntitiesAddr + (uint)(i * EntitySize);
            ushort id = m.ReadU16(e + 0x26);
            if (id == 0) continue;
            uint fx = FxTableAddr + (uint)i * 8;
            if (m.ReadU16(fx) != id) continue;
            int pkgNo = m.ReadU8(fx + 2);
            if (pkgNo == 0 || pkgNo >= self._fx.Length || self._fx[pkgNo] is not FxPkg pkg) continue;
            int frame = m.ReadU16(e + 0x56) & 0x7FFF;
            if (frame >= pkg.Frames.Length) continue;
            sbyte bankOverride = (sbyte)m.ReadU8(fx + 3);
            int blend = m.ReadU8(fx + 4);
            byte bright = m.ReadU8(fx + 5), brightB = m.ReadU8(fx + 6);
            if (brightB == 0) brightB = bright;
            if (!table)
            {
                GpuPrims.SetOrderingTable(m.ReadU32(CurrentBufferPtr) + OtOfs, OtSize);
                table = true;
            }
            float ex = bx + (short)m.ReadU16(e + 2), ey = by + (short)m.ReadU16(e + 6);
            bool facing = m.ReadU16(e + 0x14) != 0;
            int drawFlags = m.ReadU8(e + 0x19);
            float sx = (drawFlags & 1) != 0 ? (short)m.ReadU16(e + 0x1A) / 256f : 1f;
            float sy = (drawFlags & 2) != 0 ? (short)m.ReadU16(e + 0x1C) / 256f : 1f;
            double ang = (drawFlags & 4) != 0 ? ((short)m.ReadU16(e + 0x1E) & 0xFFF) * Math.PI * 2 / 4096 : 0;
            if (facing) ang = -ang;
            float cos = (float)Math.Cos(ang), sin = (float)Math.Sin(ang);
            int z = Math.Clamp((int)m.ReadU16(e + 0x24), 0, OtSize - 1);
            foreach (var (attr, px, py, img) in pkg.Frames[frame])
            {
                if (img >= pkg.Images.Length) continue;
                int bank = bankOverride >= 0 ? bankOverride : (attr >> 8) & 0x1F;
                int tex = FxTexture(pkg, img, bank);
                if (tex < 0) continue;
                var (iw, ih, _) = pkg.Images[img];
                // laid out in Saturn pixels, then 5:4 narrower on screen (after
                // turning, so turned art keeps its shape)
                float w = iw, h = ih;
                float cx = px, cy = py;
                bool hf = ((attr & 0x10) != 0) ^ facing, vf = (attr & 0x20) != 0;
                if (facing) cx = -cx;
                float u0 = hf ? 1 : 0, u1 = 1 - u0, v0 = vf ? 1 : 0, v1 = 1 - v0;
                PrimVertex V(float dx, float dy, float u, float v)
                {
                    dx *= sx; dy *= sy;
                    return new PrimVertex(ex + (dx * cos - dy * sin) * 0.8f, ey + dx * sin + dy * cos, u, v, bright, bright, brightB);
                }
                GpuPrims.Quad(z,
                    V(cx - w / 2, cy - h / 2, u0, v0), V(cx + w / 2, cy - h / 2, u1, v0),
                    V(cx - w / 2, cy + h / 2, u0, v1), V(cx + w / 2, cy + h / 2, u1, v1),
                    tex, blend != 0, blend switch { 1 => 1, 3 => 2, _ => 0 }, raw: false);
            }
        }
    }

    // The Saturn's colour offset (SCL_SetColorOffset), which her fire spell
    // (step 0x32) uses to darken the whole screen by up to 30 levels: the
    // overlay leaves it at 0x80680200 (MAR_COLOR_OFFSET), drawn here as a
    // subtractive (or additive) quad over everything.
    const uint ColorOffsetAddr = 0x80680200u;
    const int ColorOffsetStep = 0x32, ColorOffsetOrder = 0x1FE;

    static void DrawSpellScreen(IMemory m)
    {
        if (m.ReadU16(EntitiesAddr + 0x2C) != ColorOffsetStep) return;
        int offset = (short)m.ReadU16(ColorOffsetAddr);
        if (offset == 0) return;
        GpuPrims.SetOrderingTable(m.ReadU32(CurrentBufferPtr) + OtOfs, OtSize);
        FullScreen(ColorOffsetOrder, (byte)Math.Min(Math.Abs(offset), 255), offset < 0 ? 2 : 1);
    }

    static void FullScreen(int order, byte level, int blend) =>
        GpuPrims.Quad(order, new PrimVertex(0, 0, level, level, level), new PrimVertex(256, 0, level, level, level),
            new PrimVertex(0, 240, level, level, level), new PrimVertex(256, 240, level, level, level),
            semiTrans: true, blend: blend, raw: false);

    // --- Saturn Maria's HUD ----------------------------------------------------
    // The Saturn gives her a HUD of her own (GAME.PRG func_0607672C sets it up,
    // UpdateCompactStatusHud runs it), drawn from her sprite package 5: a frame
    // with an HP column and an MP column, hearts, the subweapon, and her name
    // plate, which changes colour with her charge. No boss bar. Here it's drawn
    // as host images in place of the PS1's Richter HUD (hidden), at the Saturn's
    // positions, 4/5 as wide. Not done: the plate turning over to "PAUSE".
    const uint PlayerHudAddr = 0x8013796Cu, PrimBufAddr = 0x80086FECu;
    const int PrimSize = 0x34, PrimDrawMode = 0x32, DrawHide = 8;
    const uint StatusAddr = 0x80097BA0u; // hp, hpMax, hearts, heartsMax, mp, mpMax
    const uint SubWeaponAddr = 0x80097BFCu, GameTimerAddr = 0x8003C8C4u, TimerAddr = 0x8003C998u;
    const uint MarChargeAddr = 0x80680204u;
    const int HudPkg = 5, HudOrder = 0x1F2;
    // images in package 5 and their palettes (g_AlternateStatusHudSpriteOffsets)
    const int HudPlate = 0, HudFrame = 5, HudFramePal = 1, HudDigit0 = 6, HudBar = 17, HudSubIcon = 18, HudMpPal = 10;
    // subweapons 1..9: icon palette and top-left (local_31, 0x06085D3C)
    static readonly byte[] HudSubPal = [0, 8, 8, 7, 7, 8, 8, 9, 8, 8];
    static readonly (int X, int Y)[] HudSubPos =
        [(0x14, 0x29), (0x14, 0x2D), (0x12, 0x24), (0x14, 0x25), (0x11, 0x28), (0x14, 0x25), (0x14, 0x25), (0x19, 0x2E), (0x13, 0x2A), (0x11, 0x25)];
    int _hudHp = -1;
    readonly Dictionary<int, int> _mpTex = new();

    // DrawRichterHudSubweapon updates the PS1's Richter HUD every frame: hide it
    [PostHook("dra", "DrawRichterHudSubweapon")]
    public static void AfterRichterHud(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || !self.SatPlaying(m) || self._fx == null) return;
        for (uint f = 4; f <= 8; f += 4)
        {
            int index = (int)m.ReadU32(PlayerHudAddr + f);
            if (index <= 0) continue;
            for (uint p = PrimBufAddr + (uint)(index * PrimSize), n = 0; p != 0 && n < 64; p = m.ReadU32(p), n++)
                m.WriteU16(p + PrimDrawMode, (ushort)(m.ReadU16(p + PrimDrawMode) | DrawHide));
        }
    }

    static void DrawSatHud(IMemory m, int bx, int by)
    {
        var self = I!;
        if (self._fx![HudPkg] is not FxPkg pkg || pkg.Images.Length <= HudSubIcon) return;
        int hp = (int)m.ReadU32(StatusAddr), hpMax = (int)m.ReadU32(StatusAddr + 4);
        int hearts = (int)m.ReadU32(StatusAddr + 8);
        int mp = (int)m.ReadU32(StatusAddr + 16), mpMax = (int)m.ReadU32(StatusAddr + 20);
        if (hpMax <= 0 || mpMax <= 0) return;
        GpuPrims.SetOrderingTable(m.ReadU32(CurrentBufferPtr) + OtOfs, OtSize);

        // a Saturn-pixel rectangle -> screen
        void Rect(int order, int tex, float x0, float y0, float x1, float y1)
        {
            if (tex < 0) return;
            float l = bx + x0 * 0.8f, r = bx + x1 * 0.8f, t = by + y0, b = by + y1;
            GpuPrims.Quad(order, new PrimVertex(l, t, 0, 0), new PrimVertex(r, t, 1, 0),
                new PrimVertex(l, b, 0, 1), new PrimVertex(r, b, 1, 1), tex, raw: true);
        }
        void Image(int order, int img, int bank, int x, int y)
        {
            var (w, h, _) = pkg.Images[img];
            Rect(order, FxTexture(pkg, img, bank), x, y, x + w, y + h);
        }

        // the frame, behind the HP column and in front of the MP column
        Image(HudOrder + 1, HudFrame, HudFramePal, 2, 0x18);

        // HP: eases 5 a frame towards her HP; a column 90 high at full
        if (self._hudHp < 0) self._hudHp = hp;
        self._hudHp = self._hudHp < hp ? Math.Min(self._hudHp + 5, hp) : Math.Max(self._hudHp - 5, hp);
        if (self._hudHp > 0)
            Rect(HudOrder + 2, FxTexture(pkg, HudBar, 0), 4, 0x74 - self._hudHp * 90f / hpMax, 12, 0x74);

        // MP: a column 57 high, 4 wide; its colour is palette 10's colour
        // (shift | pixel): 15 (one colour) until full, then cycling 1..14
        int t = (int)m.ReadU32(TimerAddr);
        int shift = 15;
        if (mp >= mpMax)
            shift = Math.Min(((t & 0x10) != 0 ? t % 16 : ~t % 16 & 15) + 1, 14);
        if (mp > 0)
            Rect(HudOrder, self.MpTexture(pkg, shift), 0x19, 0x74 - mp * 57f / mpMax, 0x1D, 0x74);

        // hearts: hundreds and tens only when there are some
        int digitY = 0x1D;
        if (hearts >= 100) Image(HudOrder + 2, HudDigit0 + hearts / 100 % 10, 0, 0x10, digitY);
        if (hearts >= 10) Image(HudOrder + 2, HudDigit0 + hearts / 10 % 10, 0, 0x18, digitY);
        Image(HudOrder + 2, HudDigit0 + hearts % 10, 0, 0x20, digitY);

        int sub = (int)m.ReadU32(SubWeaponAddr);
        if (sub >= 1 && sub <= 9)
            Image(HudOrder + 2, HudSubIcon + sub - 1, HudSubPal[sub], HudSubPos[sub].X, HudSubPos[sub].Y);

        // the name plate: palette 0, or flashing 3/4 (charged) and 5/6 (fully)
        int charge = (int)m.ReadU32(MarChargeAddr), gt = (int)m.ReadU32(GameTimerAddr);
        int plate = charge > 300 ? ((gt >> 2) & 1) + 5 : charge > 200 ? ((gt >> 2) & 1) + 3 : charge > 100 ? ((gt >> 3) & 1) + 3 : 0;
        Image(HudOrder + 1, HudPlate, plate, 0x2D, 0x15);
    }

    // the bar image, coloured from palette 10 at (shift | pixel)
    int MpTexture(FxPkg pkg, int shift)
    {
        if (_mpTex.TryGetValue(shift, out int t)) return t;
        var (w, h, px) = pkg.Images[HudBar];
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            if (px[i] == 0) continue;
            ushort c = pkg.Colors[HudMpPal * 16 + ((shift | px[i]) & 15)];
            rgba[i * 4] = (byte)((c & 31) << 3);
            rgba[i * 4 + 1] = (byte)(((c >> 5) & 31) << 3);
            rgba[i * 4 + 2] = (byte)(((c >> 10) & 31) << 3);
            rgba[i * 4 + 3] = 255;
        }
        t = GpuPrims.RegisterImage(rgba, w, h);
        if (t >= 0) _mpTex[shift] = t;
        return t;
    }
}
