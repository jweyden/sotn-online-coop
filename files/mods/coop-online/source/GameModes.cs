using System;
using System.Linq;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

// Game modes and cutscenes online (2026-10-26).
//
// All Alucards: the normal game, together. Story cutscenes play in each game as usual
// and nobody gets in the way of anyone's: while someone in a room is in one, the room
// isn't shared (each game runs its own objects: RoomSync), nobody can teleport to them,
// and they can't teleport out (Teleport.cs). Snapshot Flags2 bit 1 says who is.
//
// An Alucard with a Richter or a Maria (character mode, CharacterMode): there's no
// story for the others, so Alucard has none either. He gets the castle flags Richter
// mode starts with (dra HandleNowLoading: Death has already robbed him, Maria met,
// Shaft's orb beaten, the inverted castle open), so those scenes go before they start,
// Death's above all (its first check is "already robbed": the flag isn't shared, each
// game's intro sets its own). The few scenes no flag covers skip themselves (the
// game's own Start skip, which still runs the scene's events and sets its flags).
// He comes fully loaded: every relic (the familiar cards found but not on: one
// familiar at a time), the prologue armour where a slot is empty, and one of every
// item in the bag (shared: given once, by the lowest-numbered Alucard, after the
// new game's progress sync).
public partial class OnlineCoop
{
    const uint CutsceneControlAddr = 0x8003C704u;   // g_CutsceneHasControl
    const uint GameClearFlagAddr = 0x8003BDE0u;     // g_GameClearFlag

    // Richter mode's story flags at a new game (dra game_handlers.c, Play_Init):
    // IVE_BEEN_ROBBED, MET_MARIA_AFTER_HIPPOGRYPH, MET_MARIA_IN_DAI, MET_MARIA_IN_NZ0,
    // SHAFT_ORB_DEFEATED, INVERTED_CASTLE_UNLOCKED
    static readonly int[] RichterModeFlags = { 0x35, 0x62, 0x63, 0x85, 0x95, 0x96 };

    // Once a frame (P1 live): an Alucard in character mode plays without the story.
    void TickNoStory(IMemory m)
    {
        if (!Game.InGame || Game.IsLoading || !LocalAlucard || !CharacterMode || _net.Status != NetSession.State.Connected) return;
        int set = 0;
        foreach (int f in RichterModeFlags)
            if (m.ReadU8(CastleFlagsAddr + (uint)f) == 0) { m.WriteU8(CastleFlagsAddr + (uint)f, 1); set++; }
        if (set > 0) Console.WriteLine($"[coop-online] character mode: no story for Alucard ({set} of Richter mode's castle flags set: no Death, no Maria scenes)");
    }

    // A story cutscene has us (what friends are told).
    static bool InStoryScene(IMemory m) => Game.InGame && m.ReadU32(CutsceneControlAddr) != 0;

    // ---------------------------------------------------------------- character mode: no cutscenes

    bool SkipOurCutscenes => _net.Status == NetSession.State.Connected && LocalAlucard && CharacterMode;
    uint _skipClearFlag; ushort _skipTapped; bool _skipping;

    // Each stage's cutscene object: for this call, the game counts as cleared and Start
    // as just tapped, so it skips (once its first dialogue box is up: g_IsCutsceneDone).
    static void BeforeCutscene(IMemory m)
    {
        if (I is not OnlineCoop self || self._inP2 || !self.SkipOurCutscenes) return;
        if (m.ReadU16(self.CurrentEntity(m) + EStep) == 0) return;   // (not started yet)
        self._skipping = true;
        self._skipClearFlag = m.ReadU32(GameClearFlagAddr);
        self._skipTapped = m.ReadU16(PadsAddr + 4);
        m.WriteU32(GameClearFlagAddr, 1);
        m.WriteU16(PadsAddr + 4, PadStart);
    }

    static void AfterCutscene(IMemory m)
    {
        if (I is not OnlineCoop { _skipping: true } self) return;
        self._skipping = false;
        m.WriteU32(GameClearFlagAddr, self._skipClearFlag);
        m.WriteU16(PadsAddr + 4, self._skipTapped);
    }

    uint CurrentEntity(IMemory m) => m.ReadU32(CurrentEntityAddr);

    [PreHook("no3", "NO3_EntityCutscene")] public static void BeforeNo3Scene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("no3", "NO3_EntityCutscene")] public static void AfterNo3Scene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("nz0", "NZ0_EntityCutscene")] public static void BeforeNz0Scene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("nz0", "NZ0_EntityCutscene")] public static void AfterNz0Scene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("no4", "NO4_EntityCutscene")] public static void BeforeNo4Scene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("no4", "NO4_EntityCutscene")] public static void AfterNo4Scene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("dai", "DAI_EntityCutsceneDialogue")] public static void BeforeDaiScene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("dai", "DAI_EntityCutsceneDialogue")] public static void AfterDaiScene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("dre", "DRE_EntityCutsceneDialogue")] public static void BeforeDreScene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("dre", "DRE_EntityCutsceneDialogue")] public static void AfterDreScene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("lib", "LIB_EntityCutscene")] public static void BeforeLibScene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("lib", "LIB_EntityCutscene")] public static void AfterLibScene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("cen", "CEN_EntityCutscene")] public static void BeforeCenScene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("cen", "CEN_EntityCutscene")] public static void AfterCenScene(CpuContext c, IMemory m) => AfterCutscene(m);
    [PreHook("top", "TOP_EntityCutscene")] public static void BeforeTopScene(CpuContext c, IMemory m) => BeforeCutscene(m);
    [PostHook("top", "TOP_EntityCutscene")] public static void AfterTopScene(CpuContext c, IMemory m) => AfterCutscene(m);

    // ---------------------------------------------------------------- character mode: fully loaded

    const int BodyItemIds = 0x50;                          // NUM_BODY_ITEMS (the bag array runs on past it)
    const byte ItemSwordFamiliar = 0x7E;                   // the sword familiar's own weapon
    static readonly byte[] NoBodyItem = { 0x00, 0x1A, 0x30, 0x39 };   // no armour, head, cape, accessory
    const int FirstCard = 18, LastCard = 22;               // RELIC_BAT_CARD .. RELIC_SWORD_CARD
    // the prologue's armour (InitStatsAndGear): head, armour, cape, first accessory
    static readonly (int Slot, uint Empty, uint Item)[] PrologueArmour =
        { (2, 0x1A, 0x2D), (3, 0x00, 0x0F), (4, 0x30, 0x38), (5, 0x39, 0x4E) };   // Dragon Helm, Alucard Mail, Twilight Cloak, Necklace of J
    bool _loadedOut;
    long _loadoutAt = -1;

    // Once a frame (P1 live).
    void TickLoadout(CpuContext c, IMemory m)
    {
        if (!Game.InGame || _net.Status != NetSession.State.Connected) { _loadedOut = false; _loadoutAt = -1; return; }
        if (_loadedOut || Game.IsLoading || !LocalAlucard || !CharacterMode || !_fullSyncSent) return;
        // (a moment after the progress sync, so the host's bag doesn't replace what we give)
        if (_loadoutAt < 0) { _loadoutAt = _vsyncs + 180; return; }
        if (_vsyncs < _loadoutAt) return;
        _loadedOut = true;

        int relics = 0;
        for (int i = 0; i < RelicCount; i++)
        {
            byte v = m.ReadU8(RelicsAddr + (uint)i);
            if ((v & RelicFound) != 0) continue;
            m.WriteU8(RelicsAddr + (uint)i, (byte)(i is >= FirstCard and <= LastCard ? RelicFound : RelicFound | RelicActive));
            relics++;
        }
        int worn = 0;
        foreach (var (slot, empty, item) in PrologueArmour)
        {
            uint a = StatusEquipAddr + (uint)slot * 4;
            if (m.ReadU32(a) != empty) continue;
            m.WriteU32(a, item);
            worn++;
        }
        if (worn > 0) { Call(c, m, FnCalcDefense); Call(c, m, FnCalcAttack); }

        // the bag: one of everything (the shared bag passes it on to the others)
        int me = _net.LocalNumber, items = 0;
        bool giver = !_net.Peers.Any(l => CoopCharacters.IsAlucard((CoopCharacter)l.Character) && l.Number < me);
        if (giver)
        {
            for (uint id = 1; id < HandItems; id++)
                if (id != ItemSwordFamiliar && m.ReadU8(HandCountAddr + id) == 0) { Call(c, m, FnAddToInventory, id, 0u /* EQUIP_HAND */); items++; }
            for (uint id = 0; id < BodyItemIds; id++)
                if (!NoBodyItem.Contains((byte)id) && m.ReadU8(BodyCountAddr + id) == 0) { Call(c, m, FnAddToInventory, id, 2u /* EQUIP_ARMOR */); items++; }
        }
        Console.WriteLine($"[coop-online] character mode: Alucard fully loaded ({relics} relics, {worn} armour worn, {items} items{(giver ? "" : ": another Alucard gives the items")})");
    }
}
