using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;
using Sotn;
using ImGuiNET;

/*
 * Local co-op: a second Alucard.
 *
 * The game only knows about one player: PLAYER is g_Entities[0], its weapons and
 * effects live in entity slots 1..63, and its state is spread over g_Player, parts
 * of g_Status and a block of dra statics. Player 2 keeps its own copy of all of
 * that. Each frame, after the game updates player 1, we swap player 2's copy into
 * RAM, run hit detection + EntityAlucard + UpdatePlayerEntities + the HUD update
 * again with pad 2 as pad 1, then swap back. Rendering does the same swap around a
 * second RenderEntities call.
 *
 * Shared between players: level/exp, gold, relics, spells, the item bag.
 * Per player: HP/MP/hearts and their maxes, stats, equipment, buffs, HUD.
 *
 * Graphics: PS1 VRAM is full, so the engine is extended to 1024 lines (the
 * PS1's 2MB-VRAM layout; see VramShadow.Height). Lines 512..1023 are "bank 2",
 * a private copy of the texture/palette half of VRAM for P2. While P2 runs,
 * every texture/palette upload goes to bank 2 instead, and everything P2 draws
 * (entity polys, effect prims, HUD) sets the bank bit (tpage bit 11, CLUT y
 * bit 9), so P2's sprites, weapons, effects, cloak and HUD never touch P1's.
 *
 * Addresses are SLUS-00067 (US), taken from sotn-decomp.
 */
// Shared by the local (mods/coop) and online (mods/coop-online) co-op mods: everything
// that makes a second Alucard exist in one game. Subclasses decide where P2's input
// comes from and when P2 joins.
public abstract partial class CoopCore : IMod
{
    // dra functions
    protected const uint FnEntityAlucard = 0x8010A5BCu;
    protected const uint FnUpdatePlayerEntities = 0x8011A4D0u;
    protected const uint FnDrawHud = 0x8010189Cu;
    protected const uint FnDrawHudSubweapon = 0x80101A80u;
    protected const uint FnCalcDefense = 0x800F4FD0u;
    protected const uint FnCalcAttack = 0x800F4F48u;     // make_att
    protected const uint FnAllocPrimitives = 0x800EDC80u;
    protected const uint FnFreePrimitives = 0x800EDE78u;
    protected const uint FnDestroyEntity = 0x80106590u;
    protected const uint FnStand = 0x8010E570u;          // func_8010E570: back to standing/falling
    protected const uint FnCreateEntFactory = 0x8011AAFCu; // CreateEntFactoryFromEntity
    protected const uint EquipDefsAddr = 0x800A4B04u;      // g_EquipDefs, 0x34 each, weaponId at +0x0F
    protected const uint EquippedWeaponIdsAddr = 0x8003C90Cu;
    protected const ushort PadHandAttacks = 0x00A0;        // Square | Circle

    // Hellfire's light beam (EntityHellfire, 84B88.c), used for P2's entrances.
    protected const uint BpHellfire = 0x21;               // BP_HELLFIRE
    protected const ushort EHellfire = 25;                // g_DraEntityTbl index of EntityHellfire
    protected const ushort StepSpellHellfire = 34;        // Player_SpellHellfire
    protected const ushort HfhStartDelay = 1, HfhPlayerReappear = 6, HfhFinalPhase = 7;
    protected const uint EStep = 0x2C, EStepS = 0x2E, EHfBeamWidth = 0x7C, EHfBeamHeight = 0x7E, EHfTimer = 0x80;
    protected const ushort PalPlayerHidden = 0x810D, PalAlucard = 0x8100;
    // main (libgpu)
    protected const uint FnMoveImage = 0x80012BECu;
    protected const uint FnLoadImage = 0x80012B24u;

    // globals
    protected const uint EntitiesAddr = 0x800733D8u;
    protected const int EntitySize = 0xBC;
    protected const int PlayerEntityCount = 64;          // 0x00..0x3F belong to the player
    protected const uint PadsAddr = 0x80097490u;         // g_pads[2], 8 bytes each
    protected const uint ScrollXAddr = 0x8007308Eu;      // g_Tilemap.scrollX.i.hi
    protected const uint ScrollYAddr = 0x80073092u;      // g_Tilemap.scrollY.i.hi
    protected const uint PlayerAddr = 0x80072BD0u;       // g_Player
    protected const uint PlayerStatusAddr = 0x80072F2Cu; // g_Player.status
    protected const uint PlayerDrawAddr = 0x80097D1Cu;   // g_PlayerDraw[16], 0x28 each
    protected const uint PrimBufAddr = 0x80086FECu;      // g_PrimBuf, 0x34 each
    protected const uint PlayerHudAddr = 0x8013796Cu;    // static PlayerHud g_PlayerHud (5F60C.c)
    protected const uint ApiHitDetection = 0x8003C778u;  // g_api.o.HitDetection (current stage)
    protected const uint DeathSubStepAddr = 0x80137FECu; // D_80137FEC, used by PlayerStepKill

    // g_Status
    protected const uint StatusStatsAddr = 0x80097BA0u;  // hp .. statsTotal[4]
    protected const uint StatusGearAddr = 0x80097BFCu;   // subWeapon .. elementsAbsorb
    protected const uint StatusHpAddr = 0x80097BA0u, StatusMpAddr = 0x80097BB0u;
    protected const uint StatusEquipAddr = 0x80097C00u;  // equipment[7]

    // entity field offsets
    protected const uint EPosX = 0x00, EPosY = 0x04, EVelX = 0x08, EVelY = 0x0C, EPosXHi = 0x02, EPosYHi = 0x06;
    protected const uint EFacingLeft = 0x14, EEnemyId = 0x3A, EHitPoints = 0x3E;
    protected const uint EBlendMode = 0x18; protected const byte BlendTransp = 0x10;
    protected const uint EPalette = 0x16, EDrawFlags = 0x19, EEntityId = 0x26, EFlags = 0x34;
    protected const uint EAnimSet = 0x54, EUnk5A = 0x5A, EPrimIndex = 0x64, EUnk68 = 0x68, EHitTimers = 0x6D;
    protected const uint FlagCameraLocked = 0x08000000, FlagPlayerLocked = 0x40000, FlagHasPrims = 0x800000, FlagUnk20000 = 0x20000;

    // Everything that is "the player". Swapped wholesale between P1 and P2.
    protected const int RegEntities = 0, RegDraw = 3, RegStats = 6, RegGear = 7, RegHud = 8, RegServant = 14;
    protected const int RegWeaponCluts = 15, RegPlayerOvl = 18, RegPlayable = 19, RegPlayerCluts = 20, RegSatFx = 21;
    protected static readonly (uint Addr, int Len)[] Regions =
    {
        (EntitiesAddr, PlayerEntityCount * EntitySize), // 0  player + afterimages + weapons + effects
        (PlayerAddr, 0x3D0),                            // 1  g_Player
        (0x80137FB4u, (int)(0x80138454u - 0x80137FB4u)),         // 2  dra player statics (bss.c up to sound.c)
        (PlayerDrawAddr, 0x280),                        // 3  g_PlayerDraw[16]
        (0x800ACF74u, 8),                               // 4  D_800ACF74, g_AlucardChairSleepTimer
        (0x8013B5E8u, 4),                               // 5  knockback invincibility / HUD hp delay
        (StatusStatsAddr, 0x48),                        // 6  hp, hpMax, hearts, heartsMax, mp, mpMax, statsBase/Equip/Total
        (StatusGearAddr, 0x34),                         // 7  subWeapon, equipment[7], attackHands, defense, elements
        (PlayerHudAddr, 0x30),                          // 8  g_PlayerHud (prims, displayed hp, healing mail timer)
        (0x80139828u, 0x40),                            // 9  g_StatBuffTimers (potions)
        (0x80138FC0u, 0x40),                            // 10 g_ButtonCombo (spell input progress)
        (WeaponOvlAddr, 0x6000),                        // 11 weapon overlays w0 (0x8017A000) + w1 (0x8017D000): code, data, bss
        (0x8003C90Cu, 8),                               // 12 g_EquippedWeaponIds: which weapon overlays are loaded
        (0x8013905Cu, 8),                               // 13 g_PrevEquippedWeapons
        (ServantAddr, 4),                               // 14 g_Servant: P2's own familiar
        (P2ClutAddr, 0x800),                            // 15 g_Clut palettes 0x110..0x14F: weapons + familiar (rows 0xF1..0xF4)
        (FamiliarOvlAddr, 0xA000),                      // 16 familiar overlay tt_xxx (0x80170000): code, data, bss
        (ServantLoadedAddr, 4),                         // 17 g_ServantLoaded: which familiar overlay is in RAM
        // Characters.cs (SwapsRegion decides which a player uses)
        (0x8013C000u, 0x34000),                         // 18 player overlay slot: ARC_F (Alucard's sprites) or a Richter-mode overlay
        (0x8003C9A0u, 4),                               // 19 g_PlayableCharacter
        (0x8006CBCCu + 0x100 * 32, 0x2000),             // 20 g_Clut palettes 0x100..0x1FF (other family; overlaps 15)
        (0x80680000u, 0x400),                           // 21 Saturn Maria's effect table and colour offset (characters/Maria.cs)
        (0x80072FA0u, 0x10 * 0xC),                      // 22 g_GfxLoad: graphics waiting to go to VRAM (they point into its overlay)
    };
    // RegStats word indices
    protected const int SHp = 0, SHpMax = 1, SHearts = 2, SHeartsMax = 3, SMp = 4, SMpMax = 5, SBase = 6, STotal = 14;

    // empty gear for a new player 2: hands, head, armor, cape, two accessories
    protected static readonly uint[] EmptyEquipment = { 0x00, 0x00, 0x1A, 0x00, 0x30, 0x39, 0x39 };

    // VRAM bank 2. Uploads to the texture half (y 0x100..0x1FF) and the palette rows
    // (y 0xF0..0xFF) move here while P2 is live.
    protected const int BankedTop = 0xF0, BankedBottom = 0x200;
    // A remote player's bank: tpage bits 11..13 (VramShadow.Bank), and CLUT bit 15 for
    // "the palette is in that bank too".
    protected const ushort TpageBankMask = 0x3800, ClutBank2 = 0x8000;
    protected const uint CurrentBufferAddr = 0x8006C37Cu;   // g_CurrentBuffer (GpuBuffer*)
    protected const uint GpuUsageGt4 = 0x80097930u, GpuUsageGt3 = 0x80097938u;
    protected const uint PolyGt4Offset = 0x3C74, PolyGt3Offset = 0xFC74, PolyGt4Size = 0x34, PolyGt3Size = 0x28;
    protected const uint SpriteBanksPtr = 0x8003C788u;      // g_api.o.spriteBanks; [0x10..0x13] are the weapons'
    protected const uint P2ClutAddr = 0x8006CBCCu + 0x110 * 32;  // palettes P2 owns: weapons (0x110..0x13F) + familiar (0x140..0x14F)
    protected const uint FamiliarOvlAddr = 0x80170000u;
    protected const uint ServantLoadedAddr = 0x80073064u;
    protected const uint FnInitializeServant = 0x800E6218u;
    // Menu exit steps 5..14 load gear graphics and weapons; the open menu itself
    // (0x10 and up) must keep drawing from P1's bank.
    protected const int MenuStepExit5 = 5, MenuStepReturnToGameplay = 14;
    protected const uint MenuStepAddr = 0x800978F8u;
    protected const ushort HudTpage = 0x1F;

    protected const uint WeaponOvlAddr = 0x8017A000u;
    protected const uint ServantAddr = 0x8006CBC4u;      // g_Servant: active familiar
    protected const uint GameEngineStepAddr = 0x8003C9A4u;
    protected const uint EngineMenu = 2;
    protected const uint FnUpdateCapePalette = 0x800FF60Cu;
    protected const ushort PadStart = 0x0800;

    // Palettes (CLUTs). g_Clut is the RAM copy; palette p in 0x100..0x1FF sits in
    // VRAM at ((p & 0xF) * 16, 0xF0 + ((p >> 4) & 0xF)). Alucard is 0x100 and the
    // cloak is its colours 6..9.
    protected const uint ClutAddr = 0x8006CBCCu;
    protected const int AlucardPalette = 0x100, CapeFirstColor = 6, CapeColors = 4;
    protected const uint FnPaletteUpdate = 0x800EA7CCu; // runs queued palette ops into g_Clut
    protected const uint FnLoadClut = 0x80011218u;

    protected static CoopCore? I;

    // Weapon overlays (w0_xxx/w1_xxx) are code at a fixed address, so each player
    // needs their own: the RAM is in Regions, and the recompiled functions are
    // swapped in the dispatcher's address map here.
    protected static readonly FieldInfo DispatcherActive =
        typeof(Dispatcher).GetField("_active", BindingFlags.NonPublic | BindingFlags.Static)!;
    protected static readonly FieldInfo DispatcherFuncMap =
        typeof(Dispatcher).GetField("_funcMap", BindingFlags.NonPublic | BindingFlags.Static)!;

    // One remote player: local co-op's player 2, or an online friend. Everything that
    // is "that player" lives here: its copy of every swapped region, its overlays and
    // sprite banks, cloak, camera position and life cycle. R is the one being worked on
    // right now (inside a pass); outside passes it's the first one.
    protected sealed class Remote
    {
        public readonly int Number;                 // 1 = player 2, 2 = player 3, ...
        public readonly uint[][] Regions = new uint[CoopCore.Regions.Length][];
        public List<string> Overlays = new();       // its weapon/familiar/character overlays
        public readonly uint[] SpriteBanks = new uint[5];
        public readonly ushort[] Cape = { 0x994A, 0xA5CE, 0x8421, 0x9CE7 }; // no cloak
        public short ScrollX, ScrollY;              // camera position its entities were stored at
        public bool Active;
        // requests, handled at the start of its next pass
        public bool WantJoin, WantDrop, NeedInit, NeedStageReinit;
        // Joins with empty hands but P1's weapon overlays in RAM. If empty hands need a
        // different overlay, its hand attacks stay off until its menu loads theirs.
        public bool NeedLoadout;
        public bool JoinedThisPass; // the Start press that joined shouldn't also open the menu
        public string? WantWarp;
        public int OffscreenFrames;
        public int PinFrames; // after a room change the camera settles over a few frames; keep it on P1 until then
        public int DeadFrames;
        public bool HudShifted, HudHidden;
        public int Hp, Mp, Hearts;
        public int BeamState; // Hellfire entrance: 0 off, 1 waiting for the beam, 2 beam up, 3 beam closing
        public int BeamWait;
        public bool Discontinuity; // something rollback can't replay happened (join, warp, beam, menu)
        public uint Freeze, Stopwatch; public bool TimeHeld; // ours, while its pass runs (OwnTimeStops)
        // the platform it stood on last frame, for carrying it along
        public uint StandEntity;
        public int StandX, StandY;
        public long StandFrame;
        public readonly long[] Touch = new long[256];   // frame it last touched each object
        public uint IntroOffsetFor;                     // the intro push object it was last set back for (Intro.cs)
        public CoopCharacter Character;                 // who it plays (Characters.cs)
        public int Palette;                             // its colours, 0..2 (Palettes.cs)
        // its own VRAM bank (engine: VramShadow banks 1..5)
        public int BankY => RecompOne.Runtime.Hle.VramShadow.BankOffset(Number);
        public ushort TpageBank => (ushort)RecompOne.Runtime.Hle.VramShadow.BankBits(Number);

        public Remote(int number)
        {
            Number = number;
            for (int i = 0; i < Regions.Length; i++) Regions[i] = new uint[CoopCore.Regions[i].Len / 4];
        }
    }
    protected readonly List<Remote> _remotes = new() { new Remote(1) };
    protected Remote R;

    // Run `body` with R = r, then put R back.
    protected void WithRemote(Remote r, Action body)
    {
        var was = R;
        R = r;
        try { body(); }
        finally { R = was; }
    }

    protected static bool IsPlayerOverlay(string name) =>
        name.StartsWith("w0_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("w1_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("tt_", StringComparison.OrdinalIgnoreCase) ||
        IsCharacterOverlay(name);

    // Dual menu: when P2 opens the menu, P2 *becomes* the player (every swapped
    // region and overlay) until the menu has closed, so the stock menu edits P2's
    // gear and stats and the exit sequence loads P2's weapons.
    protected bool _p2MenuPending;
    protected bool _roleSwapped;

    // Per-player cloak: P2's copy of Alucard's palette in bank 2 carries P2's cloak
    // colours (Remote.Cape), captured by running the game's own cloak code with P2 live.
    protected bool _restoreP1Cape;

    // g_api.o.spriteBanks slots owned by the player's overlays: weapons 0x10..0x13, familiar 0x14
    protected static readonly int[] PlayerSpriteBanks = { 0x10, 0x11, 0x12, 0x13, 0x14 };
    protected bool _p2Rendering;
    public bool TeleportOnSpawn = true;

    // While true, uploads to the banked part of VRAM go to the current remote player's bank (R).
    protected bool Bank2Writes(IMemory m) =>
        _inP2 || _p2Rendering || _hudRedirect ||
        (_roleSwapped && (int)m.ReadU32(MenuStepAddr) is >= MenuStepExit5 and <= MenuStepReturnToGameplay);

    protected bool _alucardRan;
    protected bool _rejoinAfterDrop;
    protected bool _inP2;
    protected bool _hudRedirect;

    protected long _frame;
    protected readonly int[] _p1Last = new int[18]; // P1's stats last frame, for level-up mirroring
    protected bool _p1LastValid;

    public int HudDx = 168; // how far right of P1's HUD to draw P2's

    // VRAM usage tracking, to find free spots
    public bool TrackVram = true;
    protected readonly bool[] _vramUsed = new bool[RecompOne.Runtime.Hle.VramShadow.Width * RecompOne.Runtime.Hle.VramShadow.Height];

    public void OnLoad()
    {
        I = this;
        R = _remotes[0];

        Event.AddListener<RoomLayerLoadEvent>(OnRoomLoad);
        Event.AddListener<VSyncEvent>(OnVSync);
        Dispatcher.CallInterceptor = InterceptCall;
        if (RecompOne.Runtime.Hle.VramShadow.Height < 1024)
            Console.WriteLine("[coop] WARNING: engine VRAM is 512 lines; P2 graphics need the 1024-line engine patch");
        LoadP2Voices();
        OnLoaded();
        Console.WriteLine("[coop] loaded");
    }

    // ---- what a subclass decides
    protected virtual void OnLoaded() { }
    protected virtual void OnUnloading() { }
    protected virtual void DrawModeSettings() { }
    // Show the local co-op options (P2 join, camera, beam, HUD...) under the mode's own.
    protected virtual bool ShowCoreSettings => true;
    // P2 should join this frame (P1 live)
    protected virtual bool ShouldJoin(IMemory m) => false;
    // P2 may open the pause menu on this machine (local co-op only)
    protected virtual bool AllowP2Menu => false;
    // Put P2's input in g_pads[0] for P2's pass, and put P1's back afterwards.
    protected virtual void BeginP2Input(IMemory m) => SwapPads(m);
    protected virtual void EndP2Input(IMemory m) => SwapPads(m);
    // Pull P2 back to P1 when off-screen too long (local co-op). Online, the remote
    // player's real position decides.
    protected virtual bool LeashP2 => true;
    // Run `body` with P2's buttons in pad 1 (objects P2 drives that read the pad).
    protected virtual void WithP2Pad(IMemory m, Action body)
    {
        SwapPads(m);
        try { body(); }
        finally { SwapPads(m); }
    }
    // Right after P1's own update on normal frames (P1 live), with or without a P2.
    protected virtual void AfterP1Update(CpuContext c, IMemory m) { }
    // Runs inside P2's pass after P2 updated (P2 live), on normal frames.
    protected virtual void AfterP2Update(CpuContext c, IMemory m) { }
    // Normal frames, just before P2's pass (P1 live, P2 in its buffers). Online uses
    // this for P2 rollback.
    protected virtual void BeforeP2Pass(CpuContext c, IMemory m) { }
    // Every indirect call (entity updates, g_api.o stage hooks) passes here first;
    // return true to skip it. Online followers skip the stage's own entity updates.
    protected virtual bool ShouldSkipCall(CpuContext c, IMemory m, uint fn) => false;
    // ...and this runs after it (a0 = its first argument).
    protected virtual void AfterCall(CpuContext c, IMemory m, uint fn, uint a0) { }
    // P2 is a see-through, frozen "ghost": its player is in a menu in their own game
    // (online). It doesn't move, nobody can hit it and it touches nothing.
    protected virtual bool P2Ghost => false;
    // Just before the engine's per-frame camera / room-exit check (P1 live). Return
    // true with V0 set to skip it and report a room change of your own (1 = load
    // D_801375BC's room, n > 1 = go to stage teleport n - 2).
    protected virtual bool BeforeCamera(CpuContext c, IMemory m, bool frameUpdate) => false;
    // Where P2 appears when joining (screen coords, 16.16). Default: on P1.
    protected virtual (uint X, uint Y) SpawnPoint(IMemory m, uint p1x, uint p1y) => (p1x, p1y);
    // P1 entered a new room. Default: bring P2 along.
    protected virtual void OnLocalRoomChange(int layer)
    {
        if (R.Active) { R.WantWarp = $"room load (layer {layer})"; R.PinFrames = 8; }
    }

    public void OnUnload()
    {
        Event.RemoveListener<RoomLayerLoadEvent>(OnRoomLoad);
        Event.RemoveListener<VSyncEvent>(OnVSync);
        if (Dispatcher.CallInterceptor == InterceptCall) Dispatcher.CallInterceptor = null;
        OnUnloading();
        I = null;
    }

    public void DrawSettings()
    {
        DrawModeSettings();
        if (!ShowCoreSettings) return;
        ImGui.Text(R.Active ? $"Player 2: HP {R.Hp}  MP {R.Mp}  Hearts {R.Hearts}" : "Player 2: press Start (P) to join");
        if (ImGui.Button(R.Active ? "Drop player 2" : "Join player 2")) { if (R.Active) R.WantDrop = true; else R.WantJoin = true; }
        if (R.Active && ImGui.Button("Warp player 2 to player 1")) R.WantWarp = "manual";
        // player 2's character and colours (changing them rejoins player 2)
        int ch = (int)R.Character, pal = R.Palette;
        if (ImGui.Combo("Player 2 character", ref ch, CoopCharacters.Names, CoopCharacters.Count) && (CoopCharacter)ch != R.Character)
        {
            if (!CanPlay((CoopCharacter)ch, out string why)) Console.WriteLine($"[coop] can't play {CoopCharacters.Name((CoopCharacter)ch)}: {why}");
            else { if (R.Active) { R.WantDrop = true; _rejoinAfterDrop = true; } R.Character = (CoopCharacter)ch; }
        }
        if (CoopPalettes.Enabled && ImGui.Combo("Player 2 colours", ref pal, CoopPalettes.SetNames, CoopCharacters.Palettes)) R.Palette = pal;
        ImGui.Checkbox("Hellfire beam on join/revive/warp", ref TeleportOnSpawn);
        ImGui.Checkbox("Shared camera", ref SharedCamera);
        ImGui.Checkbox("Enemies target nearest player", ref EnemiesTargetBoth);
        ImGui.Checkbox($"{PspVoiceLabel} ({_p2VoiceStatus})", ref P2PspVoices);
        if (_p2Voice == null && ImGui.Button("Reload PSP voices")) LoadP2Voices();
        ImGui.SliderInt("Camera speed", ref CameraStep, 1, 16);
        ImGui.SliderInt("Look-ahead X", ref LookAheadX, 0, 96);
        ImGui.SliderInt("Look-ahead Y", ref LookAheadY, 0, 96);
        ImGui.InputInt("P2 HUD offset", ref HudDx);
        ImGui.Checkbox("Track VRAM usage", ref TrackVram);
        if (ImGui.Button("Dump VRAM")) DumpVram();
    }

    protected static bool IsEmpty(KeyBindings k) =>
        k.Up.Length == 0 && k.Down.Length == 0 && k.Left.Length == 0 && k.Right.Length == 0 &&
        k.Cross.Length == 0 && k.Start.Length == 0;

    protected void OnRoomLoad(RoomLayerLoadEvent e) => OnLocalRoomChange(e.LayerIndex);

    // ---------------------------------------------------------------- update

    [PostHook("dra", "EntityAlucard")]
    public static void PostEntityAlucard(CpuContext c, IMemory m)
    {
        if (I != null && !I._inP2) I._alucardRan = true;
    }

    // A stage load destroys every entity and primitive, then re-runs DrawHud and
    // AluInit for P1. P2's entities and prims are gone too; rebuild them.
    [PostHook("dra", "AluInit")]
    public static void PostAluInit(CpuContext c, IMemory m)
    {
        if (I != null && !I._inP2 && I.R.Active) I.R.NeedStageReinit = true;
    }

    // Runs once per frame right after player 1's entities update.
    [PostHook("dra", "UpdatePlayerEntities")]
    public static void PostUpdatePlayerEntities(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || self._inP2) return;

        // Normal play calls EntityAlucard then UpdatePlayerEntities. During a spell
        // freeze-frame (g_unkGraphicsStruct.unk28 != 0, Engine_5) only
        // UpdatePlayerEntities runs, and only the spell's own entities move; one of
        // them ends the freeze. If P2 cast it, P2's entities must keep updating or
        // the game stays frozen forever.
        bool normalFrame = self._alucardRan;
        self._alucardRan = false;
        self._remotesRanFrame = self._frame;
        self.RefreshLocalCharacter(m);

        if (normalFrame)
        {
            // A P2 menu request from last frame that didn't open (pause not allowed)
            // must not hijack P1's next menu.
            self._p2MenuPending = false;

            foreach (var r in self._remotes)
                self.WithRemote(r, () =>
                {
                    if (!r.Active && self.ShouldJoin(m))
                        r.WantJoin = true;
                    if (r.WantJoin && !r.Active)
                    {
                        self.CaptureP1(m);
                        r.Active = true;
                        r.NeedInit = true;
                    }
                    r.WantJoin = false;
                });

            self.MirrorP1Growth(m);
            if (!self._roleSwapped) self.AfterP1Update(c, m);
        }
        if (self._roleSwapped) return;

        // P1's position, for warps. Same camera frame as P2 once P2 is swapped in.
        uint p1x = m.ReadU32(EntitiesAddr + EPosX), p1y = m.ReadU32(EntitiesAddr + EPosY);
        ushort p1Palette = m.ReadU16(EntitiesAddr + EPalette);
        // (P1 hidden for a moment, e.g. mid-teleport, mid-beam: P2 mustn't start out invisible)
        if (p1Palette == PalPlayerHidden) p1Palette = PalAlucard;

        foreach (var r in self._remotes)
        {
            if (!r.Active) continue;
            self.WithRemote(r, () => self.UpdateRemote(c, m, normalFrame, p1x, p1y, p1Palette));
        }

        // P2 pressed Start: open the menu for P2. The engine checks P1's Start later
        // this frame, so press it for them; the role swap happens when the menu runs.
        // (Local co-op only, so only ever the first remote player.)
        var p2 = self.R;
        if (self.AllowP2Menu && normalFrame && p2.Active && p2.DeadFrames == 0 && !p2.NeedInit &&
            (m.ReadU16(PadsAddr + 8 + 4) & PadStart) != 0 && !p2.JoinedThisPass)
        {
            m.WriteU16(PadsAddr + 4, (ushort)(m.ReadU16(PadsAddr + 4) | PadStart));
            self._p2MenuPending = true;
        }
        foreach (var r in self._remotes) r.JoinedThisPass = false;
    }

    // One remote player's frame (R): swap it in, run its hits, its player and its
    // effects, and swap P1 back.
    void UpdateRemote(CpuContext c, IMemory m, bool normalFrame, uint p1x, uint p1y, ushort p1Palette)
    {
        if (normalFrame && !R.NeedInit) BeforeP2Pass(c, m);
        string who = $"player {R.Number + 1}";

        var regs = c.Snapshot();
        SwapIn(m);
        BeginP2Input(m);
        if (R.NeedLoadout)
            for (uint o = 0; o < 8; o += 2)
                m.WriteU16(PadsAddr + o, (ushort)(m.ReadU16(PadsAddr + o) & ~PadHandAttacks));
        _inP2 = true;
        SetPrimBank(m, false, 0); // its effect code sees its prims as it left them
        try
        {
            if (normalFrame)
            {
                if (R.WantDrop)
                {
                    DropP2(c, m);
                    if (_rejoinAfterDrop) { _rejoinAfterDrop = false; R.WantJoin = true; }   // (a new character)
                    return;
                }
                if (R.NeedInit)
                {
                    InitP2(c, m, p1Palette);
                    R.JoinedThisPass = true;
                    var spawn = SpawnPoint(m, p1x, p1y);
                    WarpTo(c, m, spawn.X, spawn.Y, destroyEffects: false);
                    TeleportIn(c, m);
                    Console.WriteLine($"[coop] {who} joined");
                }
                else if (R.NeedStageReinit)
                {
                    ReinitAfterStageLoad(c, m, p1Palette);
                    WarpTo(c, m, p1x, p1y, destroyEffects: false);
                    Console.WriteLine($"[coop] {who} rebuilt after stage load");
                }
                else if (R.WantWarp != null && R.DeadFrames == 0)
                {
                    WarpTo(c, m, p1x, p1y, destroyEffects: true);
                    if (!R.WantWarp.StartsWith("room")) TeleportIn(c, m);
                    Console.WriteLine($"[coop] {who} warped ({R.WantWarp})");
                }
                else if (R.PinFrames > 0 && R.DeadFrames == 0)
                {
                    WarpTo(c, m, p1x, p1y, destroyEffects: false);
                }
                if (R.PinFrames > 0) R.PinFrames--;
                R.NeedInit = R.NeedStageReinit = false;
                R.WantWarp = null;

                if (R.DeadFrames > 0 && ++R.DeadFrames > 180 && AutoRevive)
                {
                    Revive(c, m, p1Palette);
                    WarpTo(c, m, p1x, p1y, destroyEffects: true);
                    TeleportIn(c, m);
                    Console.WriteLine($"[coop] {who} revived");
                }

                // Same order as the engine: hits first, then the player reacts to them.
                uint hitDetection = m.ReadU32(ApiHitDetection);
                if (P2Ghost) { /* frozen in place: no hits, no moves */ }
                else if (hitDetection != 0)
                {
                    // HitDetection ticks each enemy's hit cooldowns down by one; bump
                    // them first so the extra pass doesn't make them run faster.
                    BumpEnemyHitTimers(m);
                    Dispatcher.Call(c, m, hitDetection);
                }
                if (!P2Ghost) RunPlayerMain(c, m);
            }
            if (!P2Ghost) RunPlayerEntities(c, m);

            if (normalFrame)
            {
                TickBeam(c, m);
                if (CoopCharacters.IsAlucard(R.Character)) TintAfterimages(m);
                UpdateHud(c, m);
                CheckOffscreenAndDeath(m);
                AfterP2Update(c, m);
            }
            FlushPendingGfx(c, m);
        }
        catch (Exception ex)
        {
            // Something in its pass blew up (bad state from a desync, an unexpected
            // entity...). Drop it rather than take the whole game down with it.
            Console.WriteLine($"[coop] {who} update failed, dropping {who}: {ex.GetType().Name}: {ex.Message}");
            R.WantDrop = true;
        }
        finally
        {
            if (R.Active) SetPrimBank(m, true, R.TpageBank); // and they draw from its bank
            _inP2 = false;
            EndP2Input(m);
            SwapOut(m); // always: this is what puts P1 back
            c.Restore(regs);
        }
        if (_restoreP1Cape) RestoreP1Cape(c, m);
    }

    [PreHook("dra", "MenuHandle")]
    public static void PreMenuHandle(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) return;
        if (self._p2MenuPending && !self._roleSwapped && self.R.Active)
        {
            self.SwapIn(m);
            self._roleSwapped = true;
            self.R.Discontinuity = true;
            Console.WriteLine("[coop] player 2 menu");
        }
        self._p2MenuPending = false;
    }

    [PostHook("dra", "MenuHandle")]
    public static void PostMenuHandle(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || !self._roleSwapped) return;
        if (m.ReadU32(GameEngineStepAddr) == EngineMenu) return;

        // Menu closed (and P2's weapons are loaded): remember P2's cloak colours,
        // then give the game back to P1.
        self.CaptureP2Cape(c, m);
        self.SwapOut(m);
        self._roleSwapped = false;
        self.R.NeedLoadout = false;
        self.RestoreP1Cape(c, m);
        Console.WriteLine("[coop] player 2 menu closed");
    }

    // While P2 runs the menu, the menu reads pad 1: feed it pad 2. The menu moves
    // its cursor with .repeat, which the game only computes for pad 1
    // (UpdatePadsRepeat), so compute pad 2's here the same way.
    protected readonly byte[] _repeatTimers = new byte[16];

    [PostHook("dra", "ReadPads")]
    public static void PostReadPads(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || !self._roleSwapped) return;
        ushort pressed = m.ReadU16(PadsAddr + 8), previous = m.ReadU16(PadsAddr + 8 + 2), tapped = m.ReadU16(PadsAddr + 8 + 4);
        ushort repeat = 0;
        for (int i = 0; i < 16; i++)
        {
            ushort b = (ushort)(1 << i);
            if ((pressed & b) == 0) continue;
            if ((tapped & b) != 0) { repeat |= b; self._repeatTimers[i] = 0x10; }
            else if (self._repeatTimers[i]-- == 0) { repeat |= b; self._repeatTimers[i] = 5; }
        }
        m.WriteU16(PadsAddr + 0, pressed);
        m.WriteU16(PadsAddr + 2, previous);
        m.WriteU16(PadsAddr + 4, tapped);
        m.WriteU16(PadsAddr + 6, repeat);
    }

    // The menu redraws the HUD every frame; with P2 in charge that's P2's HUD, so
    // give it the same treatment as P2's normal HUD update.
    [Replace("dra", "DrawHudSubweapon")]
    public static void DrawHudSubweapon(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var self = I;
        if (self != null && self._roleSwapped && !self._hudRedirect) { self.UpdateHud(c, m); return; }
        orig(c, m);
    }

    // ---------------------------------------------------------------- P2 lifecycle (all run with P2 swapped in)

    // Copy player 1's current state into P2's buffers; InitP2 then strips it down.
    protected void CaptureP1(IMemory m)
    {
        for (int r = 0; r < Regions.Length; r++)
            Ram(m, Regions[r].Addr, Regions[r].Len).CopyTo(R.Regions[r]);
        // P2 starts with P1's weapon overlays loaded (the RAM was just copied);
        // P2's first menu exit loads the right ones for P2's gear.
        R.Overlays = Dispatcher.ActiveNames.Where(IsPlayerOverlay).ToList();
        R.Regions[RegServant][0] = 0;
        uint banks = m.ReadU32(SpriteBanksPtr);
        for (int i = 0; i < PlayerSpriteBanks.Length; i++)
            R.SpriteBanks[i] = banks != 0 ? m.ReadU32(banks + (uint)(PlayerSpriteBanks[i] * 4)) : 0;

        R.ScrollX = (short)m.ReadU16(ScrollXAddr);
        R.ScrollY = (short)m.ReadU16(ScrollYAddr);
    }

    protected void InitP2(CpuContext c, IMemory m, ushort palette)
    {
        R.Discontinuity = true;
        // Bank 2 starts as a copy of P1's textures and palettes (P2 starts with P1's
        // weapon overlays, so that includes the right weapon graphics). From here on
        // P2's own uploads keep it up to date.
        MoveImage(c, m, 0, BankedTop, 1024, BankedBottom - BankedTop, 0, BankedTop + R.BankY);
        // The entity block is a copy of P1's: its prims belong to P1, so drop it raw.
        ZeroEntities(m, 1, PlayerEntityCount);
        // P2's g_Player is a copy of P1's too, including any scripted control P1 is
        // under right now (a save room walking P1 to the save point, a door push):
        // P2 isn't, so it mustn't walk off by itself on those fake presses.
        m.WriteU32(PlayerPadSimAddr, 0);
        m.WriteU32(PlayerDemoTimerAddr, 0);

        if (!CoopCharacters.IsAlucard(R.Character))
        {
            // its overlay, graphics, palettes and its own init (Characters.cs)
            SetUpCharacter(c, m, fresh: true);
            SetStartingStats(m);
            InitHud(c, m);
            R.DeadFrames = 0;
            R.OffscreenFrames = 0;
            R.NeedLoadout = false;
            return;
        }
        bool crossFamily = !CoopCharacters.IsAlucard(_p1Char);   // an Alucard in a Richter-mode game
        if (!crossFamily) InitAfterimages(c, m, palette);
        // (P2's body is a copy of P1's: if P1 is hidden right now, mid-teleport or
        // mid-beam, P2 must not start out invisible)
        if (m.ReadU16(EntitiesAddr + EPalette) == PalPlayerHidden) m.WriteU16(EntitiesAddr + EPalette, palette);

        // Own gear: start with nothing equipped; the bag is shared.
        m.WriteU32(StatusGearAddr, 0); // no subweapon
        for (int i = 0; i < EmptyEquipment.Length; i++) m.WriteU32(StatusEquipAddr + (uint)(i * 4), EmptyEquipment[i]);
        Dispatcher.Call(c, m, FnCalcDefense);
        Dispatcher.Call(c, m, FnCalcAttack);

        R.NeedLoadout = false;
        if (crossFamily)
        {
            // nothing of Alucard's is loaded in Richter mode: his weapons, sprites,
            // game graphics, then the game's own AluInit (Characters.cs)
            for (uint hand = 0; hand < 2; hand++) m.WriteU32(EquippedWeaponIdsAddr + hand * 4, 0xFFFFFFFF);
            EnsureWeapons(c, m, anyOverlay: true);
            SetUpCharacter(c, m, fresh: true);
            m.WriteU32(PlayerPadSimAddr, 0);
            m.WriteU32(PlayerDemoTimerAddr, 0);
            uint[] st = { 70, 70, 10, 50, 20, 20 };   // a new game's (dra InitStatsAndGear)
            for (int i = 0; i < st.Length; i++) m.WriteU32(StatusStatsAddr + (uint)i * 4, st[i]);
        }
        else
            for (uint hand = 0; hand < 2; hand++)
            {
                byte want = m.ReadU8(EquipDefsAddr + EmptyEquipment[hand] * 0x34 + 0x0F);
                uint loaded = m.ReadU32(EquippedWeaponIdsAddr + hand * 4);
                if (want != 0xFF && want != loaded) R.NeedLoadout = true;
            }
        Console.WriteLine($"[coop] player 2 hands: {(R.NeedLoadout ? "open the menu (P) to load weapons" : "ready")}");

        m.WriteU32(StatusHpAddr, m.ReadU32(StatusHpAddr + 4));
        m.WriteU32(StatusMpAddr, m.ReadU32(StatusMpAddr + 4));

        InitHud(c, m);
        R.DeadFrames = 0;
        R.OffscreenFrames = 0;
        CaptureP2Cape(c, m);
    }

    protected void ReinitAfterStageLoad(CpuContext c, IMemory m, ushort palette)
    {
        R.Discontinuity = true;
        if (R.BeamState != 0) EndBeam(c, m);
        // The stage load already freed every prim; just forget ours.
        ZeroEntities(m, 1, PlayerEntityCount);
        if (CoopCharacters.IsAlucard(R.Character)) InitAfterimages(c, m, palette);
        SetUpCharacter(c, m, fresh: false);   // a player overlay's own init sets its body up again
        InitHud(c, m);
        // the engine re-spawns P1's familiar after a stage load; do the same for P2
        if (m.ReadU32(ServantAddr) != 0) Call(c, m, FnInitializeServant, 0 /* ENGINE_INIT_SERVANT_0 */);
    }

    // Matches AluInit's setup of entity slots 1..3.
    protected static void InitAfterimages(CpuContext c, IMemory m, ushort palette)
    {
        for (int i = 1; i <= 3; i++)
        {
            uint e = EntityAddr(i);
            m.WriteU16(e + EAnimSet, 1);
            m.WriteU16(e + EUnk5A, (ushort)i);
            m.WriteU16(e + EPalette, palette);
            m.WriteU32(e + EFlags, FlagUnk20000 | FlagCameraLocked);
        }
        uint prim = Call(c, m, FnAllocPrimitives, 1 /* PRIM_TILE */, 8);
        if ((int)prim <= 0) return;
        uint e1 = EntityAddr(1);
        m.WriteU32(e1 + EPrimIndex, prim);
        m.WriteU32(e1 + EFlags, m.ReadU32(e1 + EFlags) | FlagHasPrims);
        uint p = PrimBufAddr + prim * 0x34;
        for (int i = 0; i < 6 && p != 0; i++, p = m.ReadU32(p))
            m.WriteU16(p + 0x32, 0x10A); // DRAW_UNK_100 | DRAW_HIDE | DRAW_UNK02
    }

    protected void WarpTo(CpuContext c, IMemory m, uint x, uint y, bool destroyEffects)
    {
        R.Discontinuity = true;
        if (destroyEffects)
            for (int i = 4; i < PlayerEntityCount; i++)
            {
                ushort id = m.ReadU16(EntityAddr(i) + EEntityId);
                // keep the familiar (servant entities are 0xD0..0xDF); it follows P2 over
                if (id != 0 && (id < 0xD0 || id > 0xDF))
                    Call(c, m, FnDestroyEntity, EntityAddr(i));
            }
        m.WriteU32(EntitiesAddr + EPosX, x);
        m.WriteU32(EntitiesAddr + EPosY, y);
        m.WriteU32(EntitiesAddr + EVelX, 0);
        m.WriteU32(EntitiesAddr + EVelY, 0);
        R.OffscreenFrames = 0;
    }

    protected void Revive(CpuContext c, IMemory m, ushort palette)
    {
        R.Discontinuity = true;
        m.WriteU32(StatusHpAddr, m.ReadU32(StatusHpAddr + 4));
        m.WriteU32(PlayerStatusAddr, 0);
        m.WriteU32(DeathSubStepAddr, 0);
        m.WriteU8(EntitiesAddr + EDrawFlags, 0);
        m.WriteU16(EntitiesAddr + EPalette, palette);
        m.WriteU8(PlayerDrawAddr + 0x1B, 0); // g_PlayerDraw[0].enableColorBlend
        if (CoopCharacters.IsAlucard(R.Character)) Call(c, m, FnStand, 0);
        else Call(c, m, m.ReadU32(PlOvlInit), 0);   // a player overlay's init stands it up again
        R.DeadFrames = 0;
    }

    // P2 appears the way Alucard comes back in Hellfire: the light beam opens on
    // the spot and he fades in. That's the second half of EntityHellfire, so start
    // the real spell's beam and skip it straight to HFH_PLAYER_REAPPEAR, then cut
    // its fireball and shockwave events. The player is parked in the Hellfire step
    // (with no sub-step that does anything) so the beam doesn't cancel itself.
    protected void TeleportIn(CpuContext c, IMemory m)
    {
        R.Discontinuity = true;
        // (the beam parks the player in Alucard's Hellfire step: Alucard only)
        if (!TeleportOnSpawn || !CoopCharacters.IsAlucard(R.Character)) return;
        StartBeam(c, m, ref R.BeamState, ref R.BeamWait);
    }

    // The beam, for whichever player is live (slot 0) right now; state/wait are that
    // player's. P1 uses it too (online: arriving next to the friend).
    protected static void StartBeam(CpuContext c, IMemory m, ref int state, ref int wait)
    {
        if (state != 0) return;
        m.WriteU16(EntitiesAddr + EStep, StepSpellHellfire);
        m.WriteU16(EntitiesAddr + EStepS, 0xFF);
        m.WriteU16(EntitiesAddr + EPalette, PalPlayerHidden);
        state = 1;
        if ((int)Call(c, m, FnCreateEntFactory, EntitiesAddr, BpHellfire, 0) == 0) EndBeam(c, m, ref state, ref wait);
    }

    protected void TickBeam(CpuContext c, IMemory m)
    {
        int before = R.BeamState;
        TickBeam(c, m, ref R.BeamState, ref R.BeamWait);
        if (before != 0 && R.BeamState == 0) R.Discontinuity = true;
    }

    protected static void TickBeam(CpuContext c, IMemory m, ref int state, ref int wait)
    {
        if (state == 0) return;
        uint beam = 0;
        for (int i = 4; i < PlayerEntityCount && beam == 0; i++)
            if (m.ReadU16(EntityAddr(i) + EEntityId) == EHellfire) beam = EntityAddr(i);

        if (beam == 0)
        {
            if (state >= 2 || ++wait > 30) EndBeam(c, m, ref state, ref wait); // done (or never spawned)
            return;
        }
        ushort step = m.ReadU16(beam + EStep);
        if (state == 1 && step >= HfhStartDelay)
        {
            // set up (prim, beam texture, sound); jump to the reappear
            m.WriteU16(beam + EStep, HfhPlayerReappear);
            m.WriteU16(beam + EHfBeamWidth, 0);
            m.WriteU16(beam + EHfBeamHeight, 0x80);
            state = 2;
        }
        else if (step == HfhFinalPhase)
        {
            // timer counts down from 0x60: fireballs at 0x30, shockwave at 0x50,
            // beam closes below 0x48, entity ends at 0x28
            short t = (short)m.ReadU16(beam + EHfTimer);
            if (t > 0x4F) t = 0x4F;
            if (t == 0x31) t = 0x2F;
            m.WriteU16(beam + EHfTimer, (ushort)t);
            state = 3;
        }
    }

    protected void EndBeam(CpuContext c, IMemory m)
    {
        R.Discontinuity = true;
        EndBeam(c, m, ref R.BeamState, ref R.BeamWait);
    }

    protected static void EndBeam(CpuContext c, IMemory m, ref int state, ref int wait)
    {
        m.WriteU16(EntitiesAddr + EPalette, PalAlucard);
        if (m.ReadU16(EntitiesAddr + EStep) == StepSpellHellfire) Call(c, m, FnStand, 0);
        state = 0;
        wait = 0;
    }

    protected void DropP2(CpuContext c, IMemory m)
    {
        R.Discontinuity = true;
        if (R.NeedStageReinit)
        {
            // A stage load since its last pass took its prims with it, and the new stage
            // has handed those indices out again (the fade's come first): freeing them
            // here would free someone else's (UpdateFade then wrote through a dead
            // prim). Just forget its objects.
            ZeroEntities(m, 1, PlayerEntityCount);
            R.NeedStageReinit = false;
        }
        else
        {
            for (int i = 1; i < PlayerEntityCount; i++)
                if (m.ReadU16(EntityAddr(i) + EEntityId) != 0 || m.ReadU32(EntityAddr(i) + EPrimIndex) != 0)
                    Call(c, m, FnDestroyEntity, EntityAddr(i));
            uint hudPrims = m.ReadU32(PlayerHudAddr + 4);
            if ((int)hudPrims > 0) Call(c, m, FnFreePrimitives, hudPrims);
        }
        // P2's buffers are about to be swapped out and forgotten.
        R.Active = false;
        R.WantDrop = false;
        Console.WriteLine("[coop] player 2 dropped");
    }

    protected void CheckOffscreenAndDeath(IMemory m)
    {
        R.Hp = (int)m.ReadU32(StatusHpAddr);
        R.Mp = (int)m.ReadU32(StatusMpAddr);
        R.Hearts = (int)m.ReadU32(StatusStatsAddr + SHearts * 4);

        // The death animation is safe to play: game over is driven by P1's g_Player,
        // which isn't live while P2 runs. Revive P2 next to P1 after a few seconds.
        if (R.DeadFrames == 0 && (R.Hp <= 0 || (m.ReadU32(PlayerStatusAddr) & 0x40000) != 0))
        {
            R.DeadFrames = 1;
            Console.WriteLine("[coop] player 2 died");
        }
        if (R.DeadFrames > 0) return;

        short x = (short)m.ReadU16(EntitiesAddr + EPosXHi);
        short y = (short)m.ReadU16(EntitiesAddr + EPosYHi);
        bool off = x < -16 || x > 272 || y < -16 || y > 256;
        R.OffscreenFrames = off ? R.OffscreenFrames + 1 : 0;
        if (R.OffscreenFrames > 60 && LeashP2) R.WantWarp = $"off-screen at {x},{y}";
    }

    // Level-ups and max-up items only ever land on P1 (they happen in stage code,
    // with P1 live). Give every remote player the same gains. Hearts pickups are shared the same way.
    protected void MirrorP1Growth(IMemory m)
    {
        var now = new int[18];
        for (int i = 0; i < 18; i++) now[i] = (int)m.ReadU32(StatusStatsAddr + (uint)(i * 4));
        foreach (var r in _remotes)
        {
            if (!_p1LastValid || !r.Active || r.NeedInit) continue;
            var s = r.Regions[RegStats];
            void Grow(int max, int cur)
            {
                int d = now[max] - _p1Last[max];
                if (d <= 0) return;
                s[max] = (uint)((int)s[max] + d);
                s[cur] = (uint)Math.Min((int)s[cur] + d, (int)s[max]);
            }
            Grow(SHpMax, SHp);
            Grow(SMpMax, SMp);
            Grow(SHeartsMax, SHearts);
            for (int i = 0; i < 4; i++)
            {
                int d = now[SBase + i] - _p1Last[SBase + i];
                if (d <= 0) continue;
                s[SBase + i] = (uint)((int)s[SBase + i] + d);
                s[STotal + i] = (uint)((int)s[STotal + i] + d);
            }
            int dh = now[SHearts] - _p1Last[SHearts];
            if (dh > 0 && now[SHeartsMax] == _p1Last[SHeartsMax])
                s[SHearts] = (uint)Math.Min((int)s[SHearts] + dh, (int)s[SHeartsMax]);
        }
        Array.Copy(now, _p1Last, 18);
        _p1LastValid = true;
    }

    // P2's afterimages fade white -> gold instead of white -> blue. DrawPlayerAfterImage
    // recomputes g_PlayerDraw[1..3]'s colour from its prims every frame, so remapping
    // the result here never feeds back into the game.
    protected static void TintAfterimages(IMemory m)
    {
        for (uint slot = 1; slot <= 3; slot++)
        {
            uint d = PlayerDrawAddr + slot * 0x28;
            if (m.ReadU8(d + 0x1B) == 0) continue; // enableColorBlend
            // four vertex colours stored as r, b, g at 0x18, 0x1C, 0x20, 0x24
            for (uint v = 0x18; v <= 0x24; v += 4)
            {
                int r = m.ReadU8(d + v), b = m.ReadU8(d + v + 1), g = m.ReadU8(d + v + 2);
                int hi = Math.Max(r, b);
                m.WriteU8(d + v, (byte)hi);                         // r
                m.WriteU8(d + v + 2, (byte)(hi * 3 / 4));           // g
                m.WriteU8(d + v + 1, (byte)(Math.Min(r, g) / 3));   // b
            }
        }
    }

    // ---------------------------------------------------------------- cloak

    // Run the game's own cloak logic with P2 live and read back the result, so
    // every cloak works (animated ones are captured as one frame), including the
    // Joseph's Cloak colours from the config menu.
    protected void CaptureP2Cape(CpuContext c, IMemory m)
    {
        Call(c, m, FnUpdateCapePalette);
        Call(c, m, FnPaletteUpdate);
        for (int i = 0; i < CapeColors; i++)
            R.Cape[i] = m.ReadU16(ClutAddr + (uint)((AlucardPalette * 16 + CapeFirstColor + i) * 2));
        _restoreP1Cape = true;
    }

    protected void RestoreP1Cape(CpuContext c, IMemory m)
    {
        _restoreP1Cape = false;
        if (!CoopCharacters.IsAlucard(_p1Char)) return;   // no cloak to put back
        Call(c, m, FnUpdateCapePalette);
        Call(c, m, FnPaletteUpdate);
    }

    // ---------------------------------------------------------------- HUD

    // P2's HUD is the game's own HUD, run a second time with P2's g_PlayerHud and
    // g_Status. Its uploads (subweapon icon, MP bar) land in bank 2 like everything
    // else P2 does; its prims are moved right by HudDx and read from bank 2.
    protected void InitHud(CpuContext c, IMemory m)
    {
        m.ZeroRange(PlayerHudAddr, 0x30);
        Dispatcher.Call(c, m, FnDrawHud);
        R.HudShifted = false;
    }

    protected void UpdateHud(CpuContext c, IMemory m)
    {
        uint first = m.ReadU32(PlayerHudAddr + 4); // primIndex1
        if ((int)first <= 0) return;

        if (R.HudShifted) ShiftHud(m, first, -HudDx);
        _hudRedirect = true;
        try { Dispatcher.Call(c, m, FnDrawHudSubweapon); }
        finally { _hudRedirect = false; }
        ShiftHud(m, first, HudDx);
        R.HudShifted = true;
        // the game's HUD for this player, or none (online with several friends: the
        // overlay draws small ones instead)
        bool show = ShowGameHud(R);
        if (show && !R.HudHidden) return;
        uint p = PrimBufAddr + first * 0x34;
        for (int n = 0; p != 0 && n < 64; n++, p = m.ReadU32(p))
            m.WriteU16(p + 0x32, (ushort)(show ? m.ReadU16(p + 0x32) & ~DrawHideFlag : m.ReadU16(p + 0x32) | DrawHideFlag));
        R.HudHidden = !show;
    }

    protected const ushort DrawHideFlag = 0x08;   // DRAW_HIDE
    protected virtual bool ShowGameHud(Remote r) => true;

    protected void ShiftHud(IMemory m, uint first, int dx)
    {
        uint p = PrimBufAddr + first * 0x34;
        for (int n = 0; p != 0 && n < 64; n++, p = m.ReadU32(p))
        {
            foreach (uint xo in new uint[] { 0x08, 0x14, 0x20, 0x2C })
                m.WriteU16(p + xo, (ushort)(m.ReadU16(p + xo) + dx));
            ushort tp = m.ReadU16(p + 0x1A);
            if ((tp & 0x1F) == HudTpage) m.WriteU16(p + 0x1A, (ushort)(tp | R.TpageBank));
        }
    }

    // ---------------------------------------------------------------- bank 2

    protected static bool Banked(int y) => y >= BankedTop && y < BankedBottom;

    [Replace("main", "LoadImage")] // (RECT*, src)
    public static void LoadImageHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m) =>
        WithBankedRect(orig, c, m, c.A0);

    [Replace("main", "ClearImage")] // (RECT*, r, g, b)
    public static void ClearImageHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m) =>
        WithBankedRect(orig, c, m, c.A0);

    [Replace("main", "MoveImage")] // (RECT* src, x, y)
    public static void MoveImageHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var self = I;
        if (self != null && Banked((short)c.A2) && self.Bank2Writes(m)) c.A2 = (uint)((int)c.A2 + self.R.BankY);
        orig(c, m);
    }

    protected static void WithBankedRect(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m, uint rect)
    {
        var self = I;
        short y = (short)m.ReadU16(rect + 2);
        if (self == null || !Banked(y) || !self.Bank2Writes(m)) { orig(c, m); return; }
        m.WriteU16(rect + 2, (ushort)(y + self.R.BankY));
        try { orig(c, m); }
        finally { m.WriteU16(rect + 2, (ushort)y); }
    }

    // A remote bank's palette rows follow P1's every frame (palette animations, flashes),
    // except the palettes P2 owns: Alucard's (P2's cloak), weapons and familiar.
    protected void SyncBank2Palettes(CpuContext c, IMemory m)
    {
        if (OwnPalettes(R))
        {
            // the stage's palettes follow P1's; the player palettes are its own (R is
            // live: its copy of g_Clut's 0x100..0x1FF is in RAM)
            MoveImage(c, m, 0x100, BankedTop, 0x200, 0x10, 0x100, BankedTop + R.BankY);
            LoadImage(c, m, 0, BankedTop + R.BankY, 0x100, 0x10, PlayerClutsAddr);
            ApplyPalette(c, m);
            return;
        }
        MoveImage(c, m, 0, BankedTop, 0x300, 0x10, 0, BankedTop + R.BankY);
        LoadImage(c, m, 0, BankedTop + 1 + R.BankY, 0x100, 4, P2ClutAddr);       // rows 0xF1..0xF4
        var pal = new ushort[16];
        for (int i = 0; i < 16; i++)
            pal[i] = i >= CapeFirstColor && i < CapeFirstColor + CapeColors
                ? R.Cape[i - CapeFirstColor]
                : m.ReadU16(ClutAddr + (uint)(AlucardPalette * 32 + i * 2));
        WithScratch(c, m, 32, buf =>
        {
            for (int i = 0; i < 16; i++) m.WriteU16(buf + (uint)(i * 2), pal[i]);
            LoadImage(c, m, 0, BankedTop + R.BankY, 16, 1, buf);                  // palette 0x100
        });
        ApplyPalette(c, m);
    }

    // Prims owned by P2's entities (effects) draw from its bank. Cleared while P2's
    // code runs, since effect code reads its own prims back.
    protected static void SetPrimBank(IMemory m, bool on, ushort bank)
    {
        for (int i = 1; i < PlayerEntityCount; i++)
        {
            uint e = EntityAddr(i);
            if ((m.ReadU32(e + EFlags) & FlagHasPrims) == 0) continue;
            int first = (int)m.ReadU32(e + EPrimIndex);
            if (first <= 0) continue;
            uint p = PrimBufAddr + (uint)first * 0x34;
            for (int n = 0; p != 0 && n < 256; n++, p = m.ReadU32(p))
            {
                byte type = (byte)(m.ReadU8(p + 7) & 0xF);
                if (type < 4 || type > 6) continue; // GT4, GT3, SPRT
                ushort tp = m.ReadU16(p + 0x1A);
                m.WriteU16(p + 0x1A, on ? (ushort)((tp & ~TpageBankMask) | bank) : (ushort)(tp & ~TpageBankMask));
            }
        }
    }

    protected static void BankPolys(IMemory m, uint usage, uint offset, uint size, uint from, ushort bank)
    {
        uint to = m.ReadU32(usage);
        uint buffer = m.ReadU32(CurrentBufferAddr);
        for (uint i = from; i < to; i++)
        {
            uint p = buffer + offset + i * size;
            m.WriteU16(p + 0x0E, (ushort)(m.ReadU16(p + 0x0E) | ClutBank2));
            m.WriteU16(p + 0x1A, (ushort)((m.ReadU16(p + 0x1A) & ~TpageBankMask) | bank));
        }
    }

    protected static void MoveImage(CpuContext c, IMemory m, int x, int y, int w, int h, int dx, int dy) =>
        WithScratch(c, m, 8, rect =>
        {
            WriteRect(m, rect, x, y, w, h);
            Call(c, m, FnMoveImage, rect, (uint)dx, (uint)dy);
        });

    protected static void LoadImage(CpuContext c, IMemory m, int x, int y, int w, int h, uint src) =>
        WithScratch(c, m, 8, rect =>
        {
            WriteRect(m, rect, x, y, w, h);
            Call(c, m, FnLoadImage, rect, src);
        });

    protected static void WriteRect(IMemory m, uint rect, int x, int y, int w, int h)
    {
        m.WriteU16(rect + 0, (ushort)x); m.WriteU16(rect + 2, (ushort)y);
        m.WriteU16(rect + 4, (ushort)w); m.WriteU16(rect + 6, (ushort)h);
    }

    // Some scratch RAM on the game's stack, below the live frame.
    protected static void WithScratch(CpuContext c, IMemory m, int size, Action<uint> body)
    {
        var regs = c.Snapshot();
        uint frame = (uint)((size + 0x10 + 7) & ~7);
        c.SP -= frame + 0x40;
        try { body(c.SP + 0x40); }
        finally { c.Restore(regs); }
    }

    [Replace("dra", "func_800EA5E4")] // HP digit colour (shared palette): leave it to P1
    public static void HudPalette(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I != null && I._hudRedirect) return;
        orig(c, m);
    }

    // ---------------------------------------------------------------- camera

    // Shared camera. func_800F0CD8 is the engine's camera/room-exit logic: it
    // checks room exits against P1's world position (g_PlayerX/Y) and then centres
    // the scroll on P1. Run it as-is (P1 still leads through doors, edge and boss
    // locks still apply), then, if no room change started, ease the scroll toward
    // the midpoint between the players and recompute P1's screen position from
    // P1's world position. The engine moves every other entity by the scroll
    // change after this returns.
    protected const uint PlayerXAddr = 0x800973F0u, PlayerYAddr = 0x800973F4u;   // g_PlayerX/Y (P1, world)
    protected const uint PrevScrollXAddr = 0x801375ACu, PrevScrollYAddr = 0x801375B0u; // scroll before this update
    protected const uint TilemapX = 0x800730C0u, TilemapY = 0x800730C4u, TilemapW = 0x800730C8u, TilemapH = 0x800730CCu;
    public bool SharedCamera = true;
    public int CameraStep = 8;      // max px per frame the camera moves on its own
    public int CameraMarginX = 40;  // P1 never gets closer than this to the screen edge
    public int CameraMarginY = 24;
    public int LookAheadX = 64;     // how far the camera leads the direction of travel
    public int LookAheadY = 80;

    protected float _lookX, _lookY, _trendY;
    protected int _lastP1Y = int.MinValue;

    [Replace("dra", "func_800F0CD8")]
    public static void Camera(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        bool frameUpdate = c.A0 != 0; // the per-frame call; 0 is the stage-init snap
        if (I != null && I.BeforeCamera(c, m, frameUpdate)) return; // (V0 set: a room/stage change)
        orig(c, m);

        var self = I;
        if (self == null || !self.SharedCamera || !frameUpdate || c.V0 != 0 || self._roleSwapped) return;
        // A room change just happened: let the engine's snap stand, the new room's
        // scroll has nothing to ease from.
        if (self._remotes.Any(r => r.Active && r.PinFrames > 0)) { self._lastP1Y = int.MinValue; return; }

        int p1x = (int)m.ReadU32(PlayerXAddr), p1y = (int)m.ReadU32(PlayerYAddr);
        int vx = (int)m.ReadU32(EntitiesAddr + EVelX);
        int facing = m.ReadU16(EntitiesAddr + EFacingLeft) != 0 ? -1 : 1;

        // Who the camera frames: every player, leaving out anyone down, beaming in,
        // or not playing. Either way it eases rather than snapping.
        int sumX = p1x, sumY = p1y, sumVx = vx, n = 1;
        foreach (var r in self._remotes)
        {
            if (!r.Active || r.DeadFrames != 0 || r.BeamState != 0) continue;
            sumX += r.ScrollX + (short)(r.Regions[RegEntities][0] >> 16);
            sumY += r.ScrollY + (short)(r.Regions[RegEntities][1] >> 16);
            sumVx += (int)r.Regions[RegEntities][2];
            int rFacing = (r.Regions[RegEntities][5] & 0xFFFF) != 0 ? -1 : 1; // facingLeft @ 0x14
            if (rFacing != facing) facing = 0;
            n++;
        }
        int fx = sumX / n, fy = sumY / n;
        vx = sumVx / n;

        // Look-ahead: horizontally from speed, vertically from P1's climb/descent
        // trend over the last second or so (hopping up platforms reads as "up").
        float leadX = Math.Clamp(facing * self.LookAheadX * 0.4f + vx / 65536f * 16f, -self.LookAheadX, self.LookAheadX);
        if (self._lastP1Y != int.MinValue)
        {
            int dy = Math.Clamp(p1y - self._lastP1Y, -8, 8); // ignore teleports/room jumps
            self._trendY = self._trendY * 0.94f + dy * 0.06f;
        }
        self._lastP1Y = p1y;
        float leadY = Math.Clamp(self._trendY * 48f, -self.LookAheadY, self.LookAheadY);
        self._lookX += (leadX - self._lookX) * 0.08f;
        self._lookY += (leadY - self._lookY) * 0.08f;

        int sx = self.EaseScroll((short)m.ReadU16(ScrollXAddr), (int)m.ReadU32(PrevScrollXAddr),
            fx + (int)self._lookX - 128, p1x, 256, self.CameraMarginX,
            (int)m.ReadU32(TilemapX), (int)m.ReadU32(TilemapW) - 256);
        int sy = self.EaseScroll((short)m.ReadU16(ScrollYAddr), (int)m.ReadU32(PrevScrollYAddr),
            fy + (int)self._lookY - 136, p1y, 240, self.CameraMarginY,
            (int)m.ReadU32(TilemapY) + 4, (int)m.ReadU32(TilemapH) - 252);

        m.WriteU16(ScrollXAddr, (ushort)sx);
        m.WriteU16(ScrollYAddr, (ushort)sy);
        m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(p1x - sx));
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(p1y - sy));
    }

    // One axis: from last frame's scroll, move a fraction of the way to the target
    // (at most CameraStep px), inside the room's scroll range, keeping P1 at least
    // `margin` from the screen edge.
    protected int EaseScroll(int engine, int prev, int target, int p1, int view, int margin, int lo, int hi)
    {
        if (hi < lo) return engine; // room no bigger than the screen on this axis
        target = Math.Clamp(target, lo, hi);
        int d = target - prev;
        int move = d / 6;
        if (move == 0 && d != 0) move = Math.Sign(d);
        int next = prev + Math.Clamp(move, -CameraStep, CameraStep);
        // P1 must stay on screen; if the room range allows it, the margin wins
        int keepLo = Math.Max(lo, p1 - (view - margin)), keepHi = Math.Min(hi, p1 - margin);
        if (keepLo <= keepHi) next = Math.Clamp(next, keepLo, keepHi);
        return Math.Clamp(next, lo, hi);
    }

    // ---------------------------------------------------------------- P2 rollback support

    // P2's whole state is its swap buffers (plus a few side tables), so it can be
    // checkpointed and rewound on its own. Things that can't be replayed (joins,
    // warps, beams, menus) set R.Discontinuity; the online mod drops its history.
    protected const uint PlayerPadSimAddr = 0x80072EF4u, PlayerDemoTimerAddr = 0x80072EFCu;   // g_Player.padSim, demo_timer
    static int _drawFailLogs, _p1DrawFails, _objectFails;
    // The new-game intro may push P2 too (Intro.cs). Online, only in a new game started
    // together this session (a loaded game never goes near it).
    protected virtual bool IntroAllowed => true;
    protected virtual bool AutoRevive => true;   // P2 comes back next to P1 a few seconds after dying
    protected virtual string PspVoiceLabel => "P2 uses PSP voices";
    protected bool _resim;             // replaying past P2 frames: no sound, no hits, no HUD

    protected sealed class P2Checkpoint
    {
        public uint[][] Regions = Array.Empty<uint[]>();
        public uint[] SpriteBanks = Array.Empty<uint>();
        public List<string> Overlays = new();
        public short ScrollX, ScrollY;
        public List<(int Slot, int Prim)> PrimOwners = new();
        public List<(uint Addr, byte[] Bytes)> Prims = new();
    }

    // Only valid while P2 is swapped out.
    protected P2Checkpoint CaptureP2(IMemory m, P2Checkpoint? reuse = null)
    {
        var cp = reuse ?? new P2Checkpoint();
        if (cp.Regions.Length != R.Regions.Length) cp.Regions = new uint[R.Regions.Length][];
        for (int r = 0; r < R.Regions.Length; r++)
        {
            if (cp.Regions[r] == null || cp.Regions[r].Length != R.Regions[r].Length) cp.Regions[r] = new uint[R.Regions[r].Length];
            Array.Copy(R.Regions[r], cp.Regions[r], R.Regions[r].Length);
        }
        cp.SpriteBanks = (uint[])R.SpriteBanks.Clone();
        cp.Overlays = new List<string>(R.Overlays);
        cp.ScrollX = R.ScrollX; cp.ScrollY = R.ScrollY;
        cp.PrimOwners = P2PrimOwners();
        cp.Prims.Clear();
        foreach (var (_, first) in cp.PrimOwners)
        {
            uint p = PrimBufAddr + (uint)first * 0x34;
            for (int n = 0; p != 0 && n < 256; n++, p = m.ReadU32(p))
            {
                var bytes = new byte[0x34];
                for (uint i = 0; i < 0x34; i++) bytes[i] = m.ReadU8(p + i);
                cp.Prims.Add((p, bytes));
            }
        }
        return cp;
    }

    // Rewind P2 to a checkpoint. Refused if P2's prim-owning entities changed since
    // (an effect started or ended): its prims may belong to someone else by now.
    protected bool RestoreP2(IMemory m, P2Checkpoint cp)
    {
        var now = P2PrimOwners();
        if (now.Count != cp.PrimOwners.Count) return false;
        for (int i = 0; i < now.Count; i++) if (now[i] != cp.PrimOwners[i]) return false;
        for (int r = 0; r < R.Regions.Length; r++) Array.Copy(cp.Regions[r], R.Regions[r], R.Regions[r].Length);
        Array.Copy(cp.SpriteBanks, R.SpriteBanks, R.SpriteBanks.Length);
        R.Overlays = new List<string>(cp.Overlays);
        R.ScrollX = cp.ScrollX; R.ScrollY = cp.ScrollY;
        // Prims are in screen coordinates, and the engine moves the ones flagged
        // DRAW_UNK02 (e.g. the afterimage trail) along with the camera every frame.
        // Restored ones are from the checkpoint's camera position: catch them up.
        short dx = (short)(cp.ScrollX - (short)m.ReadU16(ScrollXAddr));
        short dy = (short)(cp.ScrollY - (short)m.ReadU16(ScrollYAddr));
        foreach (var (addr, bytes) in cp.Prims)
        {
            for (uint i = 0; i < 0x34; i++) m.WriteU8(addr + i, bytes[i]);
            if ((m.ReadU16(addr + 0x32) & 0x02) != 0 && (dx != 0 || dy != 0))
            {
                m.WriteU16(addr + 0x08, (ushort)(m.ReadU16(addr + 0x08) + dx));
                m.WriteU16(addr + 0x0A, (ushort)(m.ReadU16(addr + 0x0A) + dy));
            }
        }
        return true;
    }

    List<(int Slot, int Prim)> P2PrimOwners()
    {
        var list = new List<(int, int)>();
        var ent = R.Regions[RegEntities];
        const int words = EntitySize / 4;
        for (int i = 1; i < PlayerEntityCount; i++)
        {
            uint flags = ent[i * words + (int)(EFlags / 4)];
            int prim = (int)ent[i * words + (int)(EPrimIndex / 4)];
            if ((flags & FlagHasPrims) != 0 && prim > 0) list.Add((i, prim));
        }
        return list;
    }

    // Run one past P2 frame again (P1 live on entry and exit). Input comes from the
    // subclass's BeginP2Input, which can tell it's a replay by _resim.
    protected void ResimulateP2Frame(CpuContext c, IMemory m)
    {
        var regs = c.Snapshot();
        _resim = true;
        SwapIn(m);
        BeginP2Input(m);
        _inP2 = true;
        SetPrimBank(m, false, 0);
        try
        {
            RunPlayerMain(c, m);
            RunPlayerEntities(c, m);
        }
        finally
        {
            SetPrimBank(m, true, R.TpageBank);
            _inP2 = false;
            EndP2Input(m);
            SwapOut(m);
            c.Restore(regs);
            _resim = false;
        }
    }

    // Replays must not re-trigger sounds that already played. P2's sounds are
    // tagged for P2's voice (P2Voices.cs).
    [Replace("dra", "PlaySfx")]
    public static void PlaySfxHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) { orig(c, m); return; }
        if (self._resim) return;
        int pos = m.ReadU16(SfxWritePosAddr);
        orig(c, m);
        self.TagSfx(m, pos);
    }

    [Replace("dra", "PlaySfxVolPan")]
    public static void PlaySfxVolPanHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) { orig(c, m); return; }
        if (self._resim) { c.V0 = 0; return; }
        int pos = m.ReadU16(SfxWritePosAddr);
        orig(c, m);
        self.TagSfx(m, pos);
    }

    // ---------------------------------------------------------------- P2's body in P1's place

    // Stage code (platforms, enemy AI) only ever looks at PLAYER (slot 0), g_Player
    // and g_Status. To make it act on P2 for one call, put P2's copies of those in
    // place (at the current camera position), run it, and put P1's back.
    protected bool _bodySwapped;

    protected void WithP2Body(IMemory m, Action body)
    {
        short dx = (short)(R.ScrollX - (short)m.ReadU16(ScrollXAddr));
        short dy = (short)(R.ScrollY - (short)m.ReadU16(ScrollYAddr));
        var slot0 = Ram(m, EntitiesAddr, EntitySize);
        var p2Slot0 = R.Regions[RegEntities].AsSpan(0, EntitySize / 4);
        SwapWords(slot0, p2Slot0);
        SwapWords(Ram(m, PlayerAddr, 0x3D0), R.Regions[1]);
        SwapWords(Ram(m, StatusStatsAddr, 0x48), R.Regions[RegStats]);
        m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(m.ReadU16(EntitiesAddr + EPosXHi) + dx));
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) + dy));
        _bodySwapped = true;
        try { body(); }
        finally
        {
            _bodySwapped = false;
            m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(m.ReadU16(EntitiesAddr + EPosXHi) - dx));
            m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) - dy));
            SwapWords(Ram(m, StatusStatsAddr, 0x48), R.Regions[RegStats]);
            SwapWords(Ram(m, PlayerAddr, 0x3D0), R.Regions[1]);
            SwapWords(slot0, p2Slot0);
        }
    }

    // P2 is a normal, controllable body right now (not in a menu, dead, or beaming in)
    protected bool P2Present => IsPresent(R);
    protected bool IsPresent(Remote r) =>
        r.Active && !_inP2 && !_roleSwapped && !_p2Rendering && r.DeadFrames == 0 && r.BeamState == 0 && !GhostOf(r);
    bool GhostOf(Remote r) { var was = R; R = r; try { return P2Ghost; } finally { R = was; } }

    // P2's position in P1's current screen coordinates
    protected (int X, int Y) P2Screen(IMemory m) => ScreenOf(m, R);
    protected (int X, int Y) ScreenOf(IMemory m, Remote r) =>
        (r.ScrollX - (short)m.ReadU16(ScrollXAddr) + (short)(r.Regions[RegEntities][0] >> 16),
         r.ScrollY - (short)m.ReadU16(ScrollYAddr) + (short)(r.Regions[RegEntities][1] >> 16));

    // ---------------------------------------------------------------- platforms

    // Platforms, lifts, doors and solid props call GetPlayerCollisionWith(self, w,
    // h, flags) from their update to push PLAYER out of them and mark it as standing
    // or blocked. Run every such call a second time for P2, so P2 can stand on and
    // bump into them too. If P2 stood on the same entity last frame, first move P2
    // by how far that entity moved (lifts and moving platforms carry P2).

    [Replace("no0", "GetPlayerCollisionWith_no0")]
    [Replace("no1", "GetPlayerCollisionWith_no1")]
    [Replace("no2", "GetPlayerCollisionWith_no2")]
    [Replace("no3", "GetPlayerCollisionWith_no3")]
    [Replace("no4", "GetPlayerCollisionWith_no4")]
    [Replace("nz0", "GetPlayerCollisionWith_nz0")]
    [Replace("nz1", "GetPlayerCollisionWith_nz1")]
    [Replace("np3", "GetPlayerCollisionWith_np3")]
    [Replace("cat", "GetPlayerCollisionWith_cat")]
    [Replace("chi", "GetPlayerCollisionWith_chi")]
    [Replace("dai", "GetPlayerCollisionWith_dai")]
    [Replace("wrp", "GetPlayerCollisionWith_wrp")]
    [Replace("top", "GetPlayerCollisionWith_top")]
    [Replace("are", "GetPlayerCollisionWith_are")]
    [Replace("mad", "GetPlayerCollisionWith_mad")]
    [Replace("dre", "GetPlayerCollisionWith_dre")]
    [Replace("cen", "GetPlayerCollisionWith_cen")]
    [Replace("lib", "GetPlayerCollisionWith_lib")]
    [Replace("st0", "GetPlayerCollisionWith_st0")]
    [Replace("mar", "GetPlayerCollisionWith_mar")]
    [Replace("bo4", "GetPlayerCollisionWith_bo4")]
    [Replace("bo6", "GetPlayerCollisionWith_bo6")]
    [Replace("rbo0", "GetPlayerCollisionWith_rbo0")]
    [Replace("rbo3", "GetPlayerCollisionWith_rbo3")]
    [Replace("rbo5", "GetPlayerCollisionWith_rbo5")]
    [Replace("rcat", "GetPlayerCollisionWith_rcat")]
    [Replace("rnz0", "GetPlayerCollisionWith_rnz0")]
    [Replace("rtop", "GetPlayerCollisionWith_rtop")]
    [Replace("rare", "GetPlayerCollisionWith_rare")]
    [Replace("rwrp", "GetPlayerCollisionWith_rwrp")]
    public static void PlatformCollision(Action<CpuContext, IMemory> origRaw, CpuContext c, IMemory m)
    {
        var self = I;
        Action<CpuContext, IMemory> orig = (cc, mm) => CollideAnywhere(origRaw, cc, mm);
        // (also while the object runs with P2 as "the player": the friend's game still
        // needs the call to stand on it)
        if (self != null && !self._inP2) self.OnPlatformCall(c, m);
        if (self != null && self._driverSwapped && self._bodySwapped)
        {
            // the object is being run with P2 as "the player" (P2 is the one touching
            // it): P2 is live here, so this call is P2's; P1 still gets pushed too
            var a = c.Snapshot();
            orig(c, m);
            uint v2 = c.V0;
            var r2 = c.Snapshot();
            uint v1 = 0;
            self.WithP1BodyWhileSwapped(m, () => { c.Restore(a); orig(c, m); v1 = c.V0; });
            c.Restore(r2);
            self.NoteTouchP1(a.gpr[4], v1);
            self.NoteTouch(self.R, a.gpr[4], v2);
            return;
        }
        if (self == null || self._bodySwapped || !self._remotes.Any(self.IsPresent)) { orig(c, m); return; }

        var args = c.Snapshot();
        orig(c, m);
        var p1Result = c.Snapshot();

        uint entity = args.gpr[4];
        int ex = (short)m.ReadU16(entity + EPosXHi) + (short)m.ReadU16(ScrollXAddr);
        int ey = (short)m.ReadU16(entity + EPosYHi) + (short)m.ReadU16(ScrollYAddr);
        self.NoteTouchP1(entity, p1Result.gpr[2]);
        uint othersResult = 0;
        foreach (var r in self._remotes)
        {
            if (!self.IsPresent(r)) continue;
            if (r.StandEntity == entity && r.StandFrame == self._frame - 1)
            {
                // carry: its buffered position is relative to its saved scroll, so a world delta applies as-is
                var w = r.Regions[RegEntities];
                w[0] = (uint)((int)w[0] + ((ex - r.StandX) << 16));
                w[1] = (uint)((int)w[1] + ((ey - r.StandY) << 16));
            }

            uint result = 0;
            self.WithRemote(r, () => self.WithP2Body(m, () =>
            {
                c.Restore(args);
                orig(c, m);
                result = c.V0;
            }));
            if (result == 4) { r.StandEntity = entity; r.StandX = ex; r.StandY = ey; r.StandFrame = self._frame; }
            self.NoteTouch(r, entity, result);
            if (othersResult == 0) othersResult = result;
        }

        c.Restore(p1Result);
        // switches, lifts and pressure plates react to the others standing on / bumping them too
        if (self.CollisionCountsP2 && c.V0 == 0) c.V0 = othersResult;
    }

    // GetPlayerCollisionWith gives up on objects off this game's screen (x & 0x100),
    // but the other player may be right next to one. Only the distance between the
    // two matters, so move both onto the screen for the call and back after.
    static void CollideAnywhere(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint e = c.A0;
        short ex = (short)m.ReadU16(e + EPosXHi), ey = (short)m.ReadU16(e + EPosYHi);
        if ((ex & 0x100) == 0 && ey >= -0x180 && ey <= 0x180) { orig(c, m); return; }
        short dx = (short)(128 - ex), dy = (short)(ey < -0x180 || ey > 0x180 ? 128 - ey : 0);
        void Shift(short x, short y)
        {
            m.WriteU16(e + EPosXHi, (ushort)(m.ReadU16(e + EPosXHi) + x));
            m.WriteU16(e + EPosYHi, (ushort)(m.ReadU16(e + EPosYHi) + y));
            m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(m.ReadU16(EntitiesAddr + EPosXHi) + x));
            m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) + y));
        }
        Shift(dx, dy);
        try { orig(c, m); }
        finally { Shift((short)-dx, (short)-dy); }
    }

    // Who touched each object last frame (by its collision results), so an object only
    // P2 is touching runs with P2 as "the player": a box P2 pushes reads P2's
    // direction and speed, not P1's.
    protected readonly long[] _touchP1 = new long[256];   // (each remote player's are in Remote.Touch)
    protected readonly byte[] _driver = new byte[256];    // 0 = P1, n = remote player n
    protected readonly ushort[] _driverId = new ushort[256];
    protected bool _driverSwapped;   // an object's update is running with P2 as PLAYER
    protected const ushort FirstRealEnemy = 6;
    public bool LogDrivers = true;

    static int RoomSlot(uint entity) =>
        entity < EntityAddr(PlayerEntityCount) || entity >= EntityAddr(256) || (entity - EntitiesAddr) % EntitySize != 0
            ? -1 : (int)((entity - EntitiesAddr) / EntitySize);

    void NoteTouchP1(uint entity, uint result)
    {
        int i = RoomSlot(entity);
        if (i >= 0 && result != 0) _touchP1[i] = _frame;
    }

    void NoteTouch(Remote r, uint entity, uint result)
    {
        int i = RoomSlot(entity);
        if (i >= 0 && result != 0) r.Touch[i] = _frame;
    }

    // P2 is live (inside WithP2Body): put P1 back for one call, then P2 again.
    protected void WithP1BodyWhileSwapped(IMemory m, Action body)
    {
        short dx = (short)(R.ScrollX - (short)m.ReadU16(ScrollXAddr));
        short dy = (short)(R.ScrollY - (short)m.ReadU16(ScrollYAddr));
        var slot0 = Ram(m, EntitiesAddr, EntitySize);
        var p2Slot0 = R.Regions[RegEntities].AsSpan(0, EntitySize / 4);
        m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(m.ReadU16(EntitiesAddr + EPosXHi) - dx));
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) - dy));
        SwapWords(Ram(m, StatusStatsAddr, 0x48), R.Regions[RegStats]);
        SwapWords(Ram(m, PlayerAddr, 0x3D0), R.Regions[1]);
        SwapWords(slot0, p2Slot0);
        _bodySwapped = false;
        try { body(); }
        finally
        {
            _bodySwapped = true;
            SwapWords(slot0, p2Slot0);
            SwapWords(Ram(m, PlayerAddr, 0x3D0), R.Regions[1]);
            SwapWords(Ram(m, StatusStatsAddr, 0x48), R.Regions[RegStats]);
            m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(m.ReadU16(EntitiesAddr + EPosXHi) + dx));
            m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) + dy));
        }
    }

    // Every GetPlayerCollisionWith call a stage object makes for P1 (a0 = entity,
    // a1..a3 = width, height, flags).
    protected virtual void OnPlatformCall(CpuContext c, IMemory m) { }
    protected virtual bool CollisionCountsP2 => false;

    // ---------------------------------------------------------------- enemies

    // Enemy AI reads PLAYER directly. Entity updates are indirect calls through
    // Dispatcher.Call, so wrap each enemy's update: if P2 is the nearer player
    // (with some hysteresis so it doesn't flip-flop), run it with P2 in slot 0.
    // Whatever the enemy does to "the player" (aiming, grabbing, contact damage
    // through g_Status) then happens to P2.
    public bool EnemiesTargetBoth = true;
    protected const int TargetHysteresis = 32;
    protected readonly byte[] _enemyTarget = new byte[256];   // 0 = P1, n = remote player n
    protected readonly ushort[] _enemyTargetId = new ushort[256];

    protected static void InterceptCall(CpuContext c, IMemory m, uint fn, Action<CpuContext, IMemory> body)
    {
        var self = I;
        if (self != null && self.ShouldSkipCall(c, m, fn)) return;
        uint a0 = c.A0;
        // A room object whose update runs into bad memory (stale pointers in it) is
        // removed, logged, rather than taking the game down.
        if (fn >= 0x80180000u && fn < 0x80200000u && RoomSlot(a0) >= 0)
        {
            var regs = c.Snapshot();
            try { InterceptEnemy(c, m, fn, body); }
            catch (InvalidOperationException e)
            {
                c.Restore(regs);
                if (_objectFails++ < 10)
                    Console.WriteLine($"[coop] object in slot {RoomSlot(a0)} (id {m.ReadU16(a0 + EEntityId):X}, update {fn:X8}) failed and was removed: {e.Message}; " +
                                      $"flags {m.ReadU32(a0 + EFlags):X8}, prim {m.ReadU32(a0 + EPrimIndex):X}, ext {m.ReadU32(a0 + 0x7C):X8} {m.ReadU32(a0 + 0x80):X8} {m.ReadU32(a0 + 0x84):X8}");
                m.ZeroRange(a0, EntitySize);
            }
            self?.AfterCall(c, m, fn, a0);
            if (self != null && fn >= PlayerOvlAddr && fn < PlayerOvlAddr + PlayerOvlLen) self.OnPlayerOverlayCall(c, m, fn);
            return;
        }
        InterceptEnemy(c, m, fn, body);
        // the local player's overlay (Richter mode) stands in for EntityAlucard & co
        if (self != null && fn >= PlayerOvlAddr && fn < PlayerOvlAddr + PlayerOvlLen) self.OnPlayerOverlayCall(c, m, fn);
        self?.AfterCall(c, m, fn, a0);
    }

    static void InterceptEnemy(CpuContext c, IMemory m, uint fn, Action<CpuContext, IMemory> body)
    {
        var self = I;
        uint e = c.A0;
        int idx = RoomSlot(e);
        if (self == null || !self.EnemiesTargetBoth || self._bodySwapped || idx < 0 ||
            fn < 0x80180000u || fn >= 0x80200000u ||                    // stage overlay code only
            !self._remotes.Any(self.IsPresent))
        {
            body(c, m);
            return;
        }
        ushort id = m.ReadU16(e + EEntityId);
        if (self._enemyTargetId[idx] != id) { self._enemyTargetId[idx] = id; self._enemyTarget[idx] = 0; }
        // objects you push / stand on: whoever is touching it (only a remote player -> it drives it).
        // Enemy ids 0..5 are the game's generic object kinds (breakable, pickup,
        // particle, common, spawner, interactable); real enemies start at 6.
        if (m.ReadU16(e + EEnemyId) < FirstRealEnemy)
        {
            if (self._driverId[idx] != id) { self._driverId[idx] = id; self._driver[idx] = 0; }
            bool p1Touch = self._touchP1[idx] >= self._frame - 1;
            Remote? toucher = null, driver = null;
            foreach (var r in self._remotes)
            {
                if (!self.IsPresent(r)) continue;
                if (r.Number == self._driver[idx]) driver = r;
                if (r.Touch[idx] >= self._frame - 1 && (toucher == null || r == driver)) toucher = r;
            }
            byte was = self._driver[idx];
            // sticky: a push nudges the pusher away every other frame, so only switch
            // when ANOTHER player touches it (or nobody has for a while)
            if (toucher != null && !p1Touch) self._driver[idx] = (byte)toucher.Number;
            else if (p1Touch && toucher == null) self._driver[idx] = 0;
            else if (!p1Touch && toucher == null && (driver == null || driver.Touch[idx] < self._frame - 30)) self._driver[idx] = 0;
            driver = self._driver[idx] == 0 ? null : self._remotes.FirstOrDefault(r => r.Number == self._driver[idx]);
            if (was != self._driver[idx] && self.LogDrivers)
                Console.WriteLine($"[coop] object slot {idx} (id {id:X}) now reacts to player {self._driver[idx] + 1} " +
                                  $"(P1 {(short)m.ReadU16(EntitiesAddr + EPosXHi)},{(short)m.ReadU16(EntitiesAddr + EPosYHi)} obj {(short)m.ReadU16(e + EPosXHi)},{(short)m.ReadU16(e + EPosYHi)})");
            if (driver == null) { body(c, m); return; }
            self._driverSwapped = true;
            // (and its pad: a lift it rides goes where it presses)
            try { self.WithRemote(driver, () => self.WithP2Body(m, () => self.WithP2Pad(m, () => body(c, m)))); }
            finally { self._driverSwapped = false; }
            return;
        }
        // enemies only: something with an enemy id and hit points
        if ((short)m.ReadU16(e + EHitPoints) <= 0) { body(c, m); return; }

        // nearest player, with hysteresis so it doesn't flip-flop
        int x = (short)m.ReadU16(e + EPosXHi), y = (short)m.ReadU16(e + EPosYHi);
        int dCur = Math.Abs(x - (short)m.ReadU16(EntitiesAddr + EPosXHi)) + Math.Abs(y - (short)m.ReadU16(EntitiesAddr + EPosYHi));
        int best = 0, dBest = dCur;
        Remote? target = null;
        foreach (var r in self._remotes)
        {
            if (!self.IsPresent(r)) continue;
            var (rx, ry) = self.ScreenOf(m, r);
            int d = Math.Abs(x - rx) + Math.Abs(y - ry);
            if (r.Number == self._enemyTarget[idx]) { dCur = d; target = r; }
            if (d < dBest) { dBest = d; best = r.Number; }
        }
        if (self._enemyTarget[idx] != 0 && target == null) self._enemyTarget[idx] = 0;   // gone: back to P1
        else if (best != self._enemyTarget[idx] && dBest + TargetHysteresis < dCur) self._enemyTarget[idx] = (byte)best;
        target = self._enemyTarget[idx] == 0 ? null : self._remotes.FirstOrDefault(r => r.Number == self._enemyTarget[idx]);

        if (target == null) { body(c, m); return; }
        // (anything solid it touches still collides with every player)
        self._driverSwapped = true;
        try { self.WithRemote(target, () => self.WithP2Body(m, () => body(c, m))); }
        finally { self._driverSwapped = false; }
    }

    // ---------------------------------------------------------------- render

    [Replace("dra", "RenderEntities")]
    public static void RenderEntities(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (!Game.InGame) ClearStrandedObjects(m);
        var before = c.Snapshot();
        try { orig(c, m); }
        catch (InvalidOperationException e) when (_p1DrawFails < 20 || !Game.InGame)
        {
            // An object pointing at sprite data that isn't there (seen once, loading a save
            // after a game over): say which, and skip drawing this frame rather than crash.
            c.Restore(before);
            if (_p1DrawFails++ < 3)
            {
                Console.WriteLine($"[coop] drawing failed ({e.Message}): game state {m.ReadU32(0x8003C734u)}, engine step {m.ReadU32(GameEngineStepAddr):X}, stage {m.ReadU32(0x800974A0u):X}, character {m.ReadU32(PlayableCharacterAddr)}");
                for (int i = 0; i < 256; i++)
                {
                    uint ent = EntityAddr(i);
                    ushort set = m.ReadU16(ent + EAnimSet), frame = m.ReadU16(ent + 0x56);
                    if (set == 0) continue;
                    string chain = "";
                    try
                    {
                        uint banks = m.ReadU32(SpriteBanksPtr), bank = m.ReadU32(banks + (uint)(set & 0x7FFF) * 4);
                        chain = $" banks {banks:X8} -> bank {bank:X8}";
                        uint fr = m.ReadU32(bank + (uint)(frame & 0x7FFF) * 4);
                        chain += $" -> frame {fr:X8}";
                    }
                    catch (Exception x) { chain += $" ({x.Message})"; }
                    Console.WriteLine($"[coop]   slot {i}: id {m.ReadU16(ent + EEntityId):X} anim set {set:X} frame {frame:X}{chain}");
                }
            }
            return;
        }

        var self = I;
        if (self == null || self._inP2 || self._roleSwapped || !Game.InGame) return;
        self.ApplyLocalPalette(m);
        foreach (var r in self._remotes)
        {
            // On its way out (e.g. P1 just left the room): its entities may point at
            // the old room's data, so don't draw it this frame.
            if (!r.Active || r.WantDrop || r.NeedStageReinit) continue;
            self.WithRemote(r, () => self.RenderRemote(orig, c, m));
        }
    }

    void RenderRemote(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var self = this;
        // Draw only P2's entities: swap them into slots 0..63 and hide the stage
        // entities. P2's sprite uploads go to bank 2 and its polys read from it.
        var regs = c.Snapshot();
        self.SwapIn(m);
        self._p2Rendering = true;
        self.SyncBank2Palettes(c, m);
        uint gt4From = m.ReadU32(GpuUsageGt4), gt3From = m.ReadU32(GpuUsageGt3);
        var hidden = new ushort[256 - PlayerEntityCount];
        for (int i = PlayerEntityCount; i < 256; i++)
        {
            uint a = EntityAddr(i) + EAnimSet;
            hidden[i - PlayerEntityCount] = m.ReadU16(a);
            m.WriteU16(a, 0);
        }
        // a ghost draws at half opacity (BLEND_TRANSP); remember the real blend modes
        byte[]? blends = null;
        if (self.P2Ghost)
        {
            blends = new byte[PlayerEntityCount];
            for (int i = 0; i < PlayerEntityCount; i++)
            {
                uint b = EntityAddr(i) + EBlendMode;
                blends[i] = m.ReadU8(b);
                if (blends[i] == 0) m.WriteU8(b, BlendTransp);
            }
        }
        try
        {
            orig(c, m);
            DrawCharacterFx(m);
            BankPolys(m, GpuUsageGt4, PolyGt4Offset, PolyGt4Size, gt4From, self.R.TpageBank);
            BankPolys(m, GpuUsageGt3, PolyGt3Offset, PolyGt3Size, gt3From, self.R.TpageBank);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[coop] drawing player {R.Number + 1} failed, dropping them: {ex.GetType().Name}: {ex.Message}");
            if (_drawFailLogs++ < 3) Console.WriteLine(ex.StackTrace);
            self.R.WantDrop = true;
        }
        finally
        {
            self._p2Rendering = false;
            for (int i = PlayerEntityCount; i < 256; i++)
                m.WriteU16(EntityAddr(i) + EAnimSet, hidden[i - PlayerEntityCount]);
            if (blends != null)
                for (int i = 0; i < PlayerEntityCount; i++) m.WriteU8(EntityAddr(i) + EBlendMode, blends[i]);
            self.SwapOut(m);
            c.Restore(regs);
        }
    }

    // After a game over the title screen can still hold castle objects whose stage
    // sprites aren't loaded any more (their sprite bank is empty): drawing them reads
    // through a null bank. Clear those (title screen only).
    static void ClearStrandedObjects(IMemory m)
    {
        uint banks = m.ReadU32(SpriteBanksPtr);
        if (banks < 0x80000000u || banks >= 0x80200000u) return;
        for (int i = 0; i < 256; i++)
        {
            uint e = EntityAddr(i);
            ushort set = m.ReadU16(e + EAnimSet);
            if ((set & 0x8000) == 0 || set == 0x8000) continue;
            if (m.ReadU32(banks + (uint)(set & 0x7FFF) * 4) != 0) continue;
            m.ZeroRange(e, EntitySize);
        }
    }

    // ---------------------------------------------------------------- state swapping

    protected void SwapIn(IMemory m)
    {
        BeforeSwapIn(m);
        SwapRegions(m);
        // P2 was stored at an older camera position; move its entities so their
        // world position stays put, the same way the engine moves P1's each frame.
        short dx = (short)(R.ScrollX - (short)m.ReadU16(ScrollXAddr));
        short dy = (short)(R.ScrollY - (short)m.ReadU16(ScrollYAddr));
        if (dx == 0 && dy == 0) return;
        for (int i = 0; i < PlayerEntityCount; i++)
        {
            uint e = EntityAddr(i);
            if (i != 0)
            {
                if (m.ReadU16(e + EEntityId) == 0 && i > 3) continue;
                uint flags = m.ReadU32(e + EFlags);
                if (m.ReadU16(e + EUnk68) != 0 || (flags & (FlagCameraLocked | FlagPlayerLocked)) == 0) continue;
            }
            m.WriteU16(e + EPosXHi, (ushort)(m.ReadU16(e + EPosXHi) + dx));
            m.WriteU16(e + EPosYHi, (ushort)(m.ReadU16(e + EPosYHi) + dy));
        }
    }

    protected void SwapOut(IMemory m)
    {
        SwapRegions(m);
        AfterSwapOut(m);
        R.ScrollX = (short)m.ReadU16(ScrollXAddr);
        R.ScrollY = (short)m.ReadU16(ScrollYAddr);
    }

    protected void SwapRegions(IMemory m)
    {
        SwapOverlays();
        // Weapon sprite banks live in the stage's g_api.o.spriteBanks table.
        uint banks = m.ReadU32(SpriteBanksPtr);
        if (banks != 0)
            for (int i = 0; i < PlayerSpriteBanks.Length; i++)
            {
                uint a = banks + (uint)(PlayerSpriteBanks[i] * 4);
                uint live = m.ReadU32(a);
                m.WriteU32(a, R.SpriteBanks[i]);
                R.SpriteBanks[i] = live;
            }
        for (int r = 0; r < Regions.Length; r++)
            if (SwapsRegion(R, r)) SwapWords(Ram(m, Regions[r].Addr, Regions[r].Len), R.Regions[r]);
        if (OwnTimeStops)
        {
            if (!R.TimeHeld)
            {
                // going in: ours kept aside, and time runs for them
                R.Freeze = m.ReadU32(FreezeAddr); R.Stopwatch = m.ReadU32(StopwatchAddr);
                m.WriteU32(FreezeAddr, 0); m.WriteU32(StopwatchAddr, 0);
            }
            else
            {
                // coming out: whatever they set is dropped, ours goes back
                m.WriteU32(FreezeAddr, R.Freeze); m.WriteU32(StopwatchAddr, R.Stopwatch);
            }
            R.TimeHeld = !R.TimeHeld;
        }
    }

    // Online, a friend's time stop (Saturn Maria's spells, a stopwatch) doesn't stop our
    // game, nor their copy: during their pass time always runs. (Kept per friend, a time
    // stop their copy never ended - corrected, dropped, rejoined - stayed on, and her
    // blueprint factory spawns nothing while it's on: no fireballs, no spell effects.)
    // On one screen P2's freezes everyone.
    protected virtual bool OwnTimeStops => false;
    const uint StopwatchAddr = 0x800973FCu;   // g_unkGraphicsStruct.D_800973FC

    protected void SwapOverlays()
    {
        var active = (List<string>)DispatcherActive.GetValue(null)!;
        var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
        lock (active)
        {
            var live = active.Where(IsPlayerOverlay).ToList();
            foreach (var name in live)
            {
                active.Remove(name);
                foreach (var addr in Dispatcher.Overlays[name].Functions.Keys) map.Remove(addr);
            }
            foreach (var name in R.Overlays)
            {
                if (!Dispatcher.Overlays.TryGetValue(name, out var ovl)) continue;
                active.Add(name);
                foreach (var (addr, fn) in ovl.Functions) map[addr] = fn;
            }
            R.Overlays = live;
        }
    }

    // Mirrors HitDetection's own filter for which enemies it ticks.
    protected static void BumpEnemyHitTimers(IMemory m)
    {
        for (int i = 64; i < 192; i++)
        {
            uint e = EntityAddr(i);
            if (m.ReadU16(e + 0x3C) == 0 || m.ReadU8(e + 0x46) == 0 || m.ReadU8(e + 0x47) == 0 ||
                (m.ReadU32(e + EFlags) & 0x100) != 0)
                continue;
            for (uint t = 0; t < 11; t++)
            {
                byte v = m.ReadU8(e + EHitTimers + t);
                if (v != 0 && v != 0xFF) m.WriteU8(e + EHitTimers + t, (byte)(v + 1));
            }
        }
    }

    protected static void SwapPads(IMemory m)
    {
        for (uint i = 0; i < 8; i += 4)
        {
            uint p1 = m.ReadU32(PadsAddr + i);
            m.WriteU32(PadsAddr + i, m.ReadU32(PadsAddr + 8 + i));
            m.WriteU32(PadsAddr + 8 + i, p1);
        }
    }

    // Direct access to emulated RAM for the big per-frame swaps (the per-word
    // IMemory path is far slower and would fire overlay-load write watchers).
    protected static readonly FieldInfo RamField = typeof(PSMemory).GetField("_ram", BindingFlags.NonPublic | BindingFlags.Instance)!;
    protected static IMemory? _ramOwner;
    protected static byte[] _ram = Array.Empty<byte>();

    protected static Span<byte> RamBytes(IMemory m, uint addr, int len)
    {
        Ram(m, 0x80000000u, 4);   // (make sure _ram is this IMemory's)
        return _ram.AsSpan((int)(addr & (uint)(_ram.Length - 1)), len);
    }

    protected static Span<uint> Ram(IMemory m, uint addr, int len)
    {
        if (!ReferenceEquals(m, _ramOwner)) { _ram = (byte[])RamField.GetValue(m)!; _ramOwner = m; }
        int off = (int)(addr & (uint)(_ram.Length - 1));   // (8MB: the mods use high RAM)
        return MemoryMarshal.Cast<byte, uint>(_ram.AsSpan(off, len));
    }

    protected static void SwapWords(Span<uint> live, Span<uint> buf)
    {
        for (int i = 0; i < live.Length; i++) (live[i], buf[i]) = (buf[i], live[i]);
    }

    protected static void ZeroEntities(IMemory m, int from, int to) =>
        m.ZeroRange(EntityAddr(from), (uint)((to - from) * EntitySize));

    protected static uint EntityAddr(int i) => EntitiesAddr + (uint)(i * EntitySize);

    // Call a game function with up to 4 args; returns V0. Registers are restored.
    protected static uint Call(CpuContext c, IMemory m, uint fn, uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        var regs = c.Snapshot();
        c.A0 = a0; c.A1 = a1; c.A2 = a2; c.A3 = a3;
        Dispatcher.Call(c, m, fn);
        uint v0 = c.V0;
        c.Restore(regs);
        return v0;
    }

    // ---------------------------------------------------------------- VRAM debugging

    protected void OnVSync(VSyncEvent e)
    {
        _frame++;
        if (!TrackVram || _frame % 15 != 0) return;
        var px = RecompOne.Runtime.Runtime.Gpu?.Shadow.Pixels;
        if (px == null) return;
        for (int i = 0; i < px.Length; i++) if (px[i] != 0) _vramUsed[i] = true;

        if (_frame % 1800 == 0) DumpVram();
    }

    // Writes coop_vram.png (current VRAM, 15-bit colour) and coop_vram_usage.png
    // (white = written at some point while tracking) next to the game.
    protected void DumpVram()
    {
        var px = RecompOne.Runtime.Runtime.Gpu?.Shadow.Pixels;
        if (px == null) return;
        int height = px.Length / 1024;
        var rgb = new byte[px.Length * 3];
        var used = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++)
        {
            ushort v = px[i];
            rgb[i * 3 + 0] = (byte)((v & 0x1F) << 3);
            rgb[i * 3 + 1] = (byte)(((v >> 5) & 0x1F) << 3);
            rgb[i * 3 + 2] = (byte)(((v >> 10) & 0x1F) << 3);
            byte u = (byte)(_vramUsed[i] || v != 0 ? 255 : 0);
            used[i * 3] = used[i * 3 + 1] = used[i * 3 + 2] = u;
        }
        // grid every 64 px so texture pages are easy to read off
        for (int y = 0; y < height; y++)
            for (int x = 0; x < 1024; x++)
                if (x % 64 == 0 || y % 256 == 0)
                {
                    int i = (y * 1024 + x) * 3;
                    used[i] = 255; used[i + 1] = 0; used[i + 2] = 0;
                }
        WritePng(Path.GetFullPath("coop_vram.png"), 1024, height, rgb);
        WritePng(Path.GetFullPath("coop_vram_usage.png"), 1024, height, used);
        Console.WriteLine("[coop] wrote coop_vram.png / coop_vram_usage.png");
    }

    protected static void WritePng(string path, int w, int h, byte[] rgb)
    {
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(f, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * w * 3, w * 3);
            }
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    protected static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, Crc32(td)); s.Write(crc);
    }

    protected static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    protected static uint Crc32(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
