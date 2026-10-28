using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Any character for any player (CoopCharacters.cs has the six).
//
// The game runs one player, chosen by g_PlayableCharacter: Alucard (dra's
// EntityAlucard + UpdatePlayerEntities, his sprites ARC_F.BIN at 0x8013C000, his
// game graphics F_GAME.BIN) or a player overlay at 0x8013C000 (RIC.BIN and the mods'
// Richter-shaped overlays: main, init and entity update at 0x8013C000/4/8, game
// graphics F_GAME2.BIN). A remote player of another character carries its own copy
// of all of that, swapped in around its passes like the rest of its state:
//  - Regions: the player overlay slot (code, data, its sprites), g_PlayableCharacter,
//    all of the player palettes when the families differ, Saturn Maria's effect table.
//  - The recompiled overlay in the dispatcher (as weapon overlays are).
//  - Its VRAM bank: filled from its family's game graphics when the families differ.
//  - High RAM sprites (Saturn Maria and the long-coat Richter both use 0x80600000):
//    read-only, so reloaded only when the player using them changes.
// When the local player isn't Alucard, dra never calls UpdatePlayerEntities; the
// player overlay's calls go through the call interceptor instead, and the remote
// passes run from there.
public abstract partial class CoopCore
{
    protected const uint PlOvlMain = 0x8013C000u, PlOvlInit = 0x8013C004u, PlOvlUpdate = 0x8013C008u;
    protected const uint PlayerOvlAddr = 0x8013C000u, PlayerOvlLen = 0x34000;   // up to the familiar overlay
    protected const uint PlayerOvlEnd = 0x8017A000u;   // a Richter-mode overlay's bss runs into the familiar slot
    protected const uint PlayerClutsAddr = ClutAddr + 0x100 * 32;               // palettes 0x100..0x1FF
    protected const uint SatFxAddr = 0x80680000u, SatSpritesAddr = 0x80600000u;
    protected const uint FnAluInit = 0x80109594u;
    protected const uint PrevPlayerXAddr = 0x8013759Cu, PrevPlayerYAddr = 0x801375A0u;   // D_8013759C/D_801375A0
    protected const uint PlayerUnk7C = 0x7C;
    // the game graphics file: 32 blocks of 0x20 x 0x80 at x 0x200..0x3FF, rows
    // 0x100..0x1FF (dra cd.c, CdCallback_1), then the player palette rows
    protected const int GameChrBlocks = 0x20, GameChrBlockBytes = 0x2000;

    protected CoopCharacter _p1Char;   // the local player's, refreshed every frame (P1 live)

    // Online, the lobby picks the character: the select after name entry (CharSelect.cs)
    // stays away then (it asks this: OnlineCoop.PicksNow).
    public virtual bool PicksCharacters => false;
    protected int _p1Palette;

    static readonly string[] CharacterOverlays = { "ric", "richter2", "maria", "mariasat" };
    protected static bool IsCharacterOverlay(string name) => CharacterOverlays.Contains(name, StringComparer.OrdinalIgnoreCase);

    bool SameFamily(Remote r) => CoopCharacters.IsAlucard(r.Character) == CoopCharacters.IsAlucard(_p1Char);
    // Two Alucards share the player palettes but for the cloak (and own their weapon
    // and familiar ones); any other pair each has all of its own.
    protected bool OwnPalettes(Remote r) => !(CoopCharacters.IsAlucard(r.Character) && CoopCharacters.IsAlucard(_p1Char));

    // Which regions a remote player swaps (the rest of its buffers are unused).
    bool SwapsRegion(Remote r, int region) => region switch
    {
        // Alucard and Alucard share ARC_F (read-only); anything else runs its own overlay
        RegPlayerOvl => !(CoopCharacters.IsAlucard(r.Character) && CoopCharacters.IsAlucard(_p1Char)),
        RegPlayerCluts => OwnPalettes(r),
        RegWeaponCluts => !OwnPalettes(r),
        _ => true,
    };

    // ---------------------------------------------------------------- files

    sealed class CharFiles
    {
        public byte[]? Bin, Sprites;
        public string Status = "";
    }
    readonly Dictionary<CoopCharacter, CharFiles> _charFiles = new();
    byte[]? _fGame, _fGame2, _arcF;
    CoopCharacter? _spritesOwner;   // whose high-RAM sprites are at 0x80600000 now

    static byte[]? ReadDiscFile(string name)
    {
        var fs = Runtime.Cd?.Fs;
        if (fs == null || !fs.Locate(name, out int lba, out uint size)) return null;
        var data = new byte[size];
        for (int i = 0; i * 2048 < size; i++)
        {
            var s = fs.ReadDiscSector(lba + i);   // the disc's own, not a mod's served file
            Array.Copy(s, 0, data, i * 2048, Math.Min(2048, (int)size - i * 2048));
        }
        return data;
    }

    CharFiles Files(CoopCharacter c)
    {
        if (_charFiles.TryGetValue(c, out var f) && f.Status == "ready") return f;
        f = new CharFiles();
        (string? bin, string? spr) = c switch
        {
            CoopCharacter.Richter => ("disc:BIN/RIC.BIN", null),
            CoopCharacter.RichterCoat => ("disc/richter2/RICHTER2.BIN", "disc/richter2/RICHTER2.SPR"),
            CoopCharacter.MariaPsp => ("disc/maria/MARIA.BIN", null),
            CoopCharacter.MariaSaturn => ("disc/mariasat/MARIASAT.BIN", "disc/mariasat/MARIASAT.SPR"),
            _ => ((string?)null, (string?)null),
        };
        if (bin != null)
        {
            f.Bin = bin.StartsWith("disc:") ? ReadDiscFile(bin[5..]) : File.Exists(bin) ? File.ReadAllBytes(bin) : null;
            if (f.Bin == null) f.Status = $"{bin} not found";
        }
        if (spr != null && f.Status == "")
        {
            f.Sprites = File.Exists(spr) ? File.ReadAllBytes(spr) : null;
            if (f.Sprites == null) f.Status = $"{spr} not found";
        }
        if (f.Status == "") f.Status = "ready";
        _charFiles[c] = f;
        return f;
    }

    // Can this machine run that character for a friend? (Its files are the player's own.)
    public bool CanPlay(CoopCharacter c, out string why)
    {
        why = Files(c).Status;
        // (and the game was recompiled with its overlay: build-maria.sh / build-mariasat.sh)
        if (why == "ready" && CoopCharacters.Overlay(c) is { } ovl && !Dispatcher.Overlays.ContainsKey(ovl)) why = $"no recompiled {ovl} overlay";
        return why == "ready";
    }

    byte[]? GameChr(bool alucard) =>
        alucard ? _fGame ??= ReadDiscFile("BIN/F_GAME.BIN") : _fGame2 ??= ReadDiscFile("BIN/F_GAME2.BIN");

    // ---------------------------------------------------------------- the local player

    // P1 live. Refreshes _p1Char from the save's choice.
    protected void RefreshLocalCharacter(IMemory m)
    {
        var (c, p) = CoopCharacters.FromSave(m);
        // the save's choice only counts if the game is running that family
        if (CoopCharacters.IsAlucard(c) != (m.ReadU32(PlayableCharacterAddr) == 0))
            c = m.ReadU32(PlayableCharacterAddr) == 0 ? CoopCharacter.Alucard : CoopCharacter.Richter;
        _p1Char = c;
        _p1Palette = p;
    }

    bool LocalIsOverlay(IMemory m) => !_inP2 && m.ReadU32(PlayableCharacterAddr) != 0;

    // The local player's player-overlay calls (main, init, entity update), when it
    // isn't Alucard: the same hooks Alucard's dra functions get.
    void OnPlayerOverlayCall(CpuContext c, IMemory m, uint fn)
    {
        if (!LocalIsOverlay(m)) return;
        if (fn == m.ReadU32(PlOvlMain)) _alucardRan = true;
        else if (fn == m.ReadU32(PlOvlInit)) { foreach (var r in _remotes) if (r.Active) r.NeedStageReinit = true; }
        else if (fn == m.ReadU32(PlOvlUpdate)) PostUpdatePlayerEntities(c, m);
    }

    // In Richter mode a spell freeze (Engine_5) updates only the stage, so an
    // Alucard friend's spell entities would never run and the freeze never end:
    // run the remote passes there too.
    protected long _remotesRanFrame = -1;

    [PostHook("dra", "HandlePlay")]
    public static void AfterHandlePlayCoop(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || self._remotesRanFrame == self._frame || !self.LocalIsOverlay(m)) return;
        if (m.ReadU32(GameEngineStepAddr) != 5 || !self._remotes.Any(r => r.Active)) return;
        PostUpdatePlayerEntities(c, m);
    }

    // ---------------------------------------------------------------- a remote player's character

    // Runs in its pass (R live), right after the generic part of InitP2 / a stage
    // reload: its overlay, sprites, family graphics and palettes, then its own init.
    // Returns false for Alucard-with-Alucard (nothing to do).
    protected bool SetUpCharacter(CpuContext c, IMemory m, bool fresh)
    {
        var ch = R.Character;
        bool alucard = CoopCharacters.IsAlucard(ch);
        m.WriteU32(PlayableCharacterAddr, CoopCharacters.Playable(ch));
        if (alucard && CoopCharacters.IsAlucard(_p1Char)) return false;

        // The player overlay slot: Alucard's sprites, or the character's overlay with
        // its bss cleared (into the familiar slot, which a Richter-mode player doesn't
        // use). A stage reload keeps its code and data; only a join loads it.
        if (fresh)
        {
            byte[]? bin = alucard ? _arcF ??= ReadDiscFile("BIN/ARC_F.BIN") : Files(ch).Bin;
            if (bin == null) throw new InvalidOperationException($"no files for {CoopCharacters.Name(ch)}");
            var slot = RamBytes(m, PlayerOvlAddr, (int)((alucard ? PlayerOvlAddr + PlayerOvlLen : PlayerOvlEnd) - PlayerOvlAddr));
            slot.Clear();
            bin.AsSpan(0, Math.Min(bin.Length, slot.Length)).CopyTo(slot);
        }
        SetLiveCharacterOverlay(CoopCharacters.Overlay(ch));
        EnsureSprites(m, ch);

        // a different character starts from its family's graphics and palettes (its
        // own init then patches what it changes)
        if (fresh && R.Character != _p1Char) LoadGameChr(c, m, alucard);
        if (!alucard) Call(c, m, m.ReadU32(PlOvlInit), 0);
        else if (fresh) Dispatcher.Call(c, m, FnAluInit);
        return true;
    }

    // Starting stats for a character that isn't Alucard (Alucard's come from InitP2).
    protected void SetStartingStats(IMemory m)
    {
        uint[] s = R.Character == CoopCharacter.MariaSaturn
            ? new uint[] { 200, 200, 99, 999, 200, 200 }   // Saturn GAME.PRG
            : new uint[] { 50, 50, 30, 99, 20, 20 };       // dra InitStatsAndGear, Richter
        for (int i = 0; i < s.Length; i++) m.WriteU32(StatusStatsAddr + (uint)i * 4, s[i]);
        m.WriteU32(StatusGearAddr, 0);   // no subweapon
    }

    // The family's game graphics into this player's bank, and its palettes into its
    // copy of g_Clut's player palettes (R is live).
    void LoadGameChr(CpuContext c, IMemory m, bool alucard)
    {
        var chr = GameChr(alucard);
        if (chr == null || chr.Length < GameChrBlocks * GameChrBlockBytes + 0x2000) return;
        for (int i = 0; i < GameChrBlocks; i++)
        {
            int x = 0x200 + (i >> 2) * 0x40 + (i & 1) * 0x20;   // D_800AC958
            int y = ((i << 6) & 0x80) + 0x100;
            int h = 0x80;
            if (i == 0x1A) { y = 0x181; h = 0x7F; }
            UploadVram(x, y + R.BankY, 0x20, h, chr.AsSpan(i * GameChrBlockBytes, 0x20 * h * 2));
        }
        var pal = chr.AsSpan(GameChrBlocks * GameChrBlockBytes, 0x2000);
        UploadVram(0, BankedTop + R.BankY, 0x100, 0x10, pal);
        pal.CopyTo(RamBytes(m, PlayerClutsAddr, 0x2000));
    }

    // Straight to VRAM (GP0 0xA0), past the game's LoadImage and the bank redirect.
    protected static void UploadVram(int x, int y, int w, int h, ReadOnlySpan<byte> pixels)
    {
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        if (gpu == null) return;
        gpu.WriteGp0(0xA0000000u);
        gpu.WriteGp0(((uint)y << 16) | (uint)x);
        gpu.WriteGp0(((uint)h << 16) | (uint)w);
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels);
        foreach (uint word in words) gpu.WriteGp0(word);
    }

    // ---------------------------------------------------------------- Alucard's weapons, from disc

    // A remote Alucard's weapons have to exist in this game too. A weapon is 14
    // sectors in BIN/WEAPON0.BIN (left hand) or WEAPON1.BIN (right hand) at
    // weaponId * 0x7000: 0x4000 of graphics (the game LoadTPages them to 0x240,0x100 /
    // 0x240,0x180) then 0x3000 of code+data for 0x8017A000 / 0x8017D000 (cd.c,
    // func_801078C4). Graphics into its bank, code into its overlay RAM (R is live),
    // then the game's own InitWeapon sets up its palette.
    protected const uint FnInitWeapon = 0x800FA9DCu;
    readonly byte[]?[] _weaponFiles = new byte[2][];

    // anyOverlay: empty hands (weapon id 0xFF) normally keep whatever overlay is in;
    // with nothing in (an Alucard in a Richter-mode game) they get weapon 0's.
    protected void EnsureWeapons(CpuContext c, IMemory m, bool anyOverlay = false)
    {
        for (uint hand = 0; hand < 2; hand++)
        {
            uint equip = m.ReadU32(StatusEquipAddr + hand * 4);
            byte wid = m.ReadU8(EquipDefsAddr + equip * 0x34 + 0x0F);
            if (wid == 0xFF && anyOverlay) wid = 0;
            if (wid == 0xFF || m.ReadU32(EquippedWeaponIdsAddr + hand * 4) == wid) continue;
            var file = _weaponFiles[hand] ??= ReadDiscFile($"BIN/WEAPON{hand}.BIN");
            int off = wid * 0x7000;
            if (file == null || off + 0x7000 > file.Length) continue;

            UploadVram(0x240, 0x100 + (int)hand * 0x80 + R.BankY, 0x40, 0x80, file.AsSpan(off, 0x4000));
            file.AsSpan(off + 0x4000, 0x3000).CopyTo(RamBytes(m, WeaponOvlAddr + hand * 0x3000, 0x3000));
            Dispatcher.Load($"w{hand}_{wid:D3}");
            R.Discontinuity = true; // overlay RAM changed under the checkpoints
            m.WriteU32(EquippedWeaponIdsAddr + hand * 4, wid);
            Call(c, m, FnInitWeapon, hand);
            Console.WriteLine($"[coop] player {R.Number + 1}: {(hand == 0 ? "left" : "right")} hand weapon {wid} loaded");
        }
        R.NeedLoadout = false;
    }

    // Between frames (P1 live): our character's code must be the one the dispatcher
    // runs. It has been found swapped out at a stage load (unmapped call to the
    // player init); put it back rather than crash, and say so.
    protected void CheckOwnOverlay(IMemory m)
    {
        if (_inP2 || m.ReadU32(PlayableCharacterAddr) == 0 || CoopCharacters.Overlay(_p1Char) is not { } name) return;
        uint init = m.ReadU32(PlOvlInit);
        var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
        if (map.ContainsKey(init) || !Dispatcher.Overlays.TryGetValue(name, out var ovl) || !ovl.Functions.ContainsKey(init)) return;
        Console.WriteLine($"[coop] our character's code ({name}) wasn't the live one (active: {string.Join(" ", Dispatcher.ActiveNames)}): put back");
        SetLiveCharacterOverlay(name);
    }

    // Swap the live character overlay in the dispatcher (R is live).
    void SetLiveCharacterOverlay(string? name)
    {
        var active = (List<string>)DispatcherActive.GetValue(null)!;
        var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
        lock (active)
        {
            foreach (var live in active.Where(IsCharacterOverlay).ToList())
            {
                if (string.Equals(live, name, StringComparison.OrdinalIgnoreCase)) return;
                active.Remove(live);
                foreach (var addr in Dispatcher.Overlays[live].Functions.Keys) map.Remove(addr);
            }
            if (name == null) return;
            if (!Dispatcher.Overlays.TryGetValue(name, out var ovl))
                throw new InvalidOperationException($"no recompiled {name} overlay");
            active.Add(name);
            foreach (var (addr, fn) in ovl.Functions) map[addr] = fn;
        }
    }

    // High-RAM sprites are read-only: load them only when the user changes.
    void EnsureSprites(IMemory m, CoopCharacter c)
    {
        var spr = Files(c).Sprites;
        if (spr == null || _spritesOwner == c) return;
        spr.CopyTo(RamBytes(m, SatSpritesAddr, spr.Length));
        _spritesOwner = c;
    }

    // ---------------------------------------------------------------- the passes

    // Each player has its own queue of graphics waiting for VRAM (g_GfxLoad, Region
    // 22): they point into its own overlay. A remote player's is worked off at the end
    // of its pass, while its memory is in and its uploads go to its bank (compressed
    // ones go four a call, so call until it's empty).
    protected const uint GfxLoadAddr = 0x80072FA0u, FnLoadPendingGfx = 0x800EB314u;

    protected void FlushPendingGfx(CpuContext c, IMemory m)
    {
        for (int n = 0; n < 64; n++)
        {
            bool pending = false;
            for (uint i = 0; i < 0x10 && !pending; i++) pending = m.ReadU16(GfxLoadAddr + i * 0xC + 4) != 0;
            if (!pending) return;
            Call(c, m, FnLoadPendingGfx);
        }
    }

    // The live player's own update and its entities' update.
    protected void RunPlayerMain(CpuContext c, IMemory m)
    {
        if (CoopCharacters.IsAlucard(R.Character)) { Dispatcher.Call(c, m, FnEntityAlucard); return; }
        // what dra does before the player overlay's main (HandlePlay, Engine_Normal),
        // with P1's values put back after
        uint px = m.ReadU32(PrevPlayerXAddr), py = m.ReadU32(PrevPlayerYAddr);
        short x = (short)m.ReadU16(EntitiesAddr + EPosXHi), y = (short)m.ReadU16(EntitiesAddr + EPosYHi);
        m.WriteU32(PrevPlayerXAddr, (uint)x);
        m.WriteU32(PrevPlayerYAddr, (uint)y);
        m.WriteU16(PlayerAddr + PlayerUnk7C, (ushort)x);
        m.WriteU16(PlayerAddr + PlayerUnk7C + 2, (ushort)y);
        try { Dispatcher.Call(c, m, m.ReadU32(PlOvlMain)); }
        finally { m.WriteU32(PrevPlayerXAddr, px); m.WriteU32(PrevPlayerYAddr, py); }
    }

    protected void RunPlayerEntities(CpuContext c, IMemory m)
    {
        if (CoopCharacters.IsAlucard(R.Character)) Dispatcher.Call(c, m, FnUpdatePlayerEntities);
        else Dispatcher.Call(c, m, m.ReadU32(PlOvlUpdate));
    }

    // Before/after a remote pass's swaps: its high-RAM sprites in, P1's back.
    void BeforeSwapIn(IMemory m) => EnsureSprites(m, R.Character);
    void AfterSwapOut(IMemory m) => EnsureSprites(m, _p1Char);

    // Saturn Maria's effects are host images drawn from her effect table (the online
    // mod's characters/Maria.cs, MariaMod.DrawPlayerFx); a remote Saturn Maria's are
    // drawn in her render pass with her table live.
    protected void DrawCharacterFx(IMemory m)
    {
        if (R.Character == CoopCharacter.MariaSaturn) DrawSaturnFx(m);
    }

    protected virtual void DrawSaturnFx(IMemory m) { }
}

// Character mode: any team that isn't all Alucards plays with Alucard fully geared
// (and levelling as usual). Death's scene in the entrance takes Alucard's gear
// (InitStatsAndGear(1)); in character mode his equipment and items are put back
// right after (the rest of what that call sets up, his post-prologue stats, stays).
public abstract partial class CoopCore
{
    const uint EquipHandCountAddr = 0x8009798Au, EquipBodyCountAddr = 0x80097A33u;
    const int EquipHandItems = 0xA9, EquipBodyItems = 0x5A;
    byte[]? _gearBeforeDeath;

    protected virtual bool CharacterMode =>
        !CoopCharacters.IsAlucard(_p1Char) || _remotes.Exists(r => r.Active && !CoopCharacters.IsAlucard(r.Character));

    [PreHook("dra", "InitStatsAndGear")]
    public static void BeforeInitStatsAndGear(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) return;
        self._gearBeforeDeath = null;
        if (c.A0 != 1 || self._inP2 || !self.CharacterMode) return;
        var keep = new byte[7 * 4 + EquipHandItems + EquipBodyItems];
        RamBytes(m, StatusEquipAddr, 7 * 4).CopyTo(keep);
        RamBytes(m, EquipHandCountAddr, EquipHandItems).CopyTo(keep.AsSpan(7 * 4));
        RamBytes(m, EquipBodyCountAddr, EquipBodyItems).CopyTo(keep.AsSpan(7 * 4 + EquipHandItems));
        self._gearBeforeDeath = keep;
    }

    [PostHook("dra", "InitStatsAndGear")]
    public static void AfterInitStatsAndGear(CpuContext c, IMemory m)
    {
        var self = I;
        if (self?._gearBeforeDeath is not { } keep) return;
        self._gearBeforeDeath = null;
        keep.AsSpan(0, 7 * 4).CopyTo(RamBytes(m, StatusEquipAddr, 7 * 4));
        keep.AsSpan(7 * 4, EquipHandItems).CopyTo(RamBytes(m, EquipHandCountAddr, EquipHandItems));
        keep.AsSpan(7 * 4 + EquipHandItems, EquipBodyItems).CopyTo(RamBytes(m, EquipBodyCountAddr, EquipBodyItems));
        Call(c, m, FnCalcDefense);
        Call(c, m, FnCalcAttack);
        Console.WriteLine("[coop] character mode: Alucard keeps his gear");
    }
}
