using System.Linq;
using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Sotn;

// Teleport to a friend (F6, the settings button, or Up, Up, Down, Triangle): the
// nearest one in this area; asking again while the last teleport's target is still
// the nearest goes to the next friend in turn. It's cast like Hellfire: Alucard takes
// the spell pose, the pillar of light comes down on him and he vanishes; while he's
// gone the jump happens, and he comes down next to the friend in the same pillar,
// wherever they are. (Other characters have no Hellfire: they just go.)
//  - same room: just move there.
//  - another room of this stage: the warp rooms' own trick. PLAYER is pushed to the
//    friend's spot in this room's coordinates and D_80097C98 = 2 makes the engine's
//    room-exit check load the room at that cell, with the player at that spot in it.
//  - another stage (area): not reachable. Jumping into an arbitrary room of another
//    area skipped loading its tiles and graphics (garbled screen), so the spell
//    fizzles there and backfires (Backfire, below).
// A room jump lands in the right room; the exact spot is set once it's in (from the
// friend's latest position), and if they've moved on meanwhile we hop again.
// The friend's game sees us arrive with the beam too (snapshot flag 16).
public partial class OnlineCoop
{
    const uint WarpModeAddr = 0x80097C98u;                         // D_80097C98
    const uint FnSetNextRoomToLoad = 0x800F0BC0u;                  // (cell x, cell y): 0 = no room there
    const uint TeleportTableAddr = 0x800A245Cu; const int TeleportCount = 131; // D_800A245C: RoomTeleport {x, y, roomId, fromStage, stageId}
    const uint RoomLockAddr = 0x80097418u;                         // g_unkGraphicsStruct.unk20: room exits are shut (boss fights)
    const uint PlayerStatusDead = 0x40000;
    const ushort InvertedCastle = 0x20;
    const uint ScreenFillFrames = 0x8003C0F8u;   // D_8003C0EC[3]: frames of solid colour (the warp's white-out)
    bool _clearWarpMode;

    enum Tp { Idle, Casting, Go, LoadingStage, Arriving, Placing }
    Tp _tp;
    long _tpSince;
    ushort _tpStage;
    bool _p1BeamWant, _castWant, _hidden;
    int _hops;
    const ushort HfhPlayerDisappear = 5, StepPlayerHit = 10;
    int _p1BeamState, _p1BeamWait;
    public string TeleportStatus = "";
    volatile bool _teleportKey, _resyncKey;

    static bool RoomLocked(IMemory m) => m.ReadU32(RoomLockAddr) != 0;

    // who we're teleporting to
    Friend _tpTarget = NoFriend;
    Snap TP => _tpTarget.Peer;
    bool TargetValid => _tpTarget.Valid && _friends.Contains(_tpTarget);
    int _tpLastNumber = -1;

    bool LocalAlucard => CoopCharacters.IsAlucard(_p1Char);

    // (only: to that friend, not the nearest - the footage bots follow their leader)
    void RequestTeleport(Friend? only = null)
    {
        if (_tp != Tp.Idle || _p1BeamState != 0) return;
        var m = RecompOne.Runtime.Runtime.Mem;
        var inCastle = ValidFriends.Where(f => f.Peer.InGame && (only == null || f == only)).OrderBy(f => f.Number).ToList();
        if (inCastle.Count == 0 || m == null) { TeleportStatus = "nobody else is in the castle"; return; }
        // (not into anyone's cutscene, nor out of our own)
        if (InStoryScene(m) || InCutscene(m)) { TeleportStatus = "not during a cutscene"; return; }
        if (inCastle.All(f => f.Peer.InScene)) { TeleportStatus = $"{string.Join(", ", inCastle.Select(f => f.Name))} {(inCastle.Count == 1 ? "is" : "are")} in a cutscene"; return; }
        inCastle = inCastle.Where(f => !f.Peer.InScene).ToList();
        ushort stage = (ushort)m.ReadU32(StageIdAddr);
        int px = (int)m.ReadU32(PlayerXAddr), py = (int)m.ReadU32(PlayerYAddr);
        short left = (short)m.ReadU32(TilemapLeft), top = (short)m.ReadU32(TilemapTop);
        long Distance(Friend f) => f.Peer.Stage != stage ? long.MaxValue / 2 + f.Number
            : Math.Abs((f.Peer.RoomLeft - left) * 256 + f.Peer.WorldX - px) + Math.Abs((f.Peer.RoomTop - top) * 256 + f.Peer.WorldY - py);
        var nearest = inCastle.OrderBy(Distance).First();
        // asked again: the next one along
        if (nearest.Number == _tpLastNumber && inCastle.Count > 1)
            nearest = inCastle[(inCastle.IndexOf(nearest) + 1) % inCastle.Count];
        _tpTarget = nearest;
        _tpLastNumber = nearest.Number;
        _tp = Tp.Casting; _tpSince = _vsyncs; _hops = 0;
        if (LocalAlucard) _castWant = true;
        else if (TP.Stage != stage) { CancelTeleport($"{nearest.Name} is in another part of the castle"); return; }
        else _tp = Tp.Go;   // no Hellfire to cast: straight there
        TeleportStatus = $"teleporting to {nearest.Name}...";
    }

    void CancelTeleport(string why)
    {
        _tp = Tp.Idle;
        _castWant = false;
        TeleportStatus = why;
        Console.WriteLine($"[coop-online] teleport: {why}");
    }

    // Once a frame from OnNetFrame (P1 live).
    void TickTeleport(IMemory m)
    {
        if (_teleportKey) { _teleportKey = false; RequestTeleport(); }
        if (TeleportCommand(m)) RequestTeleport();
        if (_tp == Tp.Idle) return;
        if (_vsyncs - _tpSince > 900) { CancelTeleport("teleport timed out"); return; }
        // they went into a cutscene on the way (not once we're arriving)
        if (TargetValid && TP.InScene && _tp is Tp.Go or Tp.LoadingStage) { CancelTeleport($"{_tpTarget.Name} is in a cutscene"); return; }
        if (_tp == Tp.Casting) return;   // (AfterP1Update runs the cast)
        // the next stage is in and running: now hop to the friend's room in it
        if (_tp == Tp.LoadingStage && Game.InGame && !Game.IsLoading &&
            (ushort)m.ReadU32(StageIdAddr) == _tpStage && m.ReadU32(GameEngineStepAddr) == 1 /* Engine_Normal */)
            _tp = Tp.Go;
    }

    // The engine's per-frame room-exit check is about to run (P1 live).
    protected override bool BeforeCamera(CpuContext c, IMemory m, bool frameUpdate)
    {
        // our warp left D_80097C98 at 3 ("came in through a warp"); clear it once we're in
        if (frameUpdate && _clearWarpMode && _tp == Tp.Idle)
        {
            if (m.ReadU32(WarpModeAddr) == 3) m.WriteU32(WarpModeAddr, 0);
            _clearWarpMode = false;
        }
        if (!frameUpdate) return false;
        if (_tp == Tp.Placing)
        {
            if (!TargetValid || !Present(m, _tpTarget))
            {
                // they moved on while we were on our way (or the jump fell short)
                if (++_hops > 3) { CancelTeleport($"couldn't catch up with {_tpTarget.Name}"); return false; }
                _tp = Tp.Go;
            }
            else { PlaceNextToPeer(m); return false; }
        }
        if (_tp != Tp.Go) return false;
        if (!TargetValid || !TP.InGame) { CancelTeleport($"{_tpTarget.Name} left the castle"); return false; }
        if ((m.ReadU32(PlayerStatusAddr) & PlayerStatusDead) != 0) { CancelTeleport("can't teleport while dead"); return false; }
        if (RoomLocked(m)) { CancelTeleport("this room is locked (boss fight?): finish it first"); return false; }
        if (TP.RoomLocked) { CancelTeleport($"{_tpTarget.Name} is in a locked room (boss fight?)"); return false; }

        ushort stage = (ushort)m.ReadU32(StageIdAddr);
        // (another area: only reachable through its loading zone, which loads its
        // tiles and graphics properly; the cast fizzles before we get here)
        if (TP.Stage != stage) { CancelTeleport($"{_tpTarget.Name} is in another part of the castle"); return false; }

        short left = (short)m.ReadU32(TilemapLeft), top = (short)m.ReadU32(TilemapTop);
        if (TP.RoomLeft == left && TP.RoomTop == top) { PlaceNextToPeer(m); return false; }

        // another room of this stage: point PLAYER at the friend's spot, in this
        // room's coordinates, and let the warp-room room change take us there
        int ax = (TP.RoomLeft - left) * 256 + TP.WorldX, ay = (TP.RoomTop - top) * 256 + TP.WorldY;
        if (ax < short.MinValue || ax > short.MaxValue || ay < short.MinValue || ay > short.MaxValue) { CancelTeleport("too far"); return false; }
        // is there a room at that cell? (the engine's check asks the same thing next)
        if (Call(c, m, FnSetNextRoomToLoad, (uint)(left + (ax >> 8)), (uint)(top + (ay >> 8))) == 0)
        { CancelTeleport($"can't find {_tpTarget.Name}'s room"); return false; }
        // the room load puts us at (spot in that cell + this room's scroll), wrapped to
        // the cell: warp rooms never scroll, but rooms we teleport from do, so take the
        // scroll off the spot now (the cell, from the high byte, stays the same)
        int sx0 = (short)m.ReadU16(ScrollXAddr), sy0 = (short)m.ReadU16(ScrollYAddr);
        m.WriteU16(EntitiesAddr + EPosXHi, (ushort)((ax & ~0xFF) | ((ax - sx0) & 0xFF)));
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)((ay & ~0xFF) | ((ay - sy0) & 0xFF)));
        // the load also frames the new room's camera on the cell g_PlayerX/Y are in
        // (in a warp room that's where you stand): make that the friend's cell too
        m.WriteU32(PlayerXAddr, (uint)ax);
        m.WriteU32(PlayerYAddr, (uint)ay);
        m.WriteU32(EntitiesAddr + EVelX, 0); m.WriteU32(EntitiesAddr + EVelY, 0);
        m.WriteU32(WarpModeAddr, 2);
        _clearWarpMode = true;
        _tp = Tp.Arriving; _tpSince = _vsyncs;
        return false;   // the engine's check now loads that room
    }

    // A teleport that leads into `target` (any one; one from the stage we're in reads
    // best). The table holds normal-castle ids; in the inverted castle they flip.
    static int FindTeleportTo(IMemory m, ushort from, ushort target)
    {
        ushort flip = (ushort)(from & InvertedCastle);
        int any = -1;
        for (int i = 0; i < TeleportCount; i++)
        {
            uint t = TeleportTableAddr + (uint)(i * 10);
            ushort to = (ushort)(m.ReadU16(t + 8) ^ flip), src = (ushort)(m.ReadU16(t + 6) ^ flip);
            if (to != target) continue;
            if (src == from) return i;
            if (any < 0) any = i;
        }
        return any;
    }

    // The teleport "spell": Up, Up, Down, Triangle, each within a third of a second of
    // the last (any other button breaks it).
    const ushort PadTriangle = 0x0010, PadUp = 0x1000, PadDown = 0x4000;
    static readonly ushort[] TeleportInput = { PadUp, PadUp, PadDown, PadTriangle };
    int _cmdStep;
    long _cmdAt;

    bool TeleportCommand(IMemory m)
    {
        if (!Game.InGame || Paused(m)) { _cmdStep = 0; return false; }
        ushort tapped = m.ReadU16(PadsAddr + 4);
        if (tapped == 0) return false;
        if (_cmdStep > 0 && _vsyncs - _cmdAt > 20) _cmdStep = 0;
        if (tapped == TeleportInput[_cmdStep])
        {
            _cmdAt = _vsyncs;
            if (++_cmdStep < TeleportInput.Length) return false;
            _cmdStep = 0;
            return true;
        }
        _cmdStep = tapped == PadUp ? 1 : 0;   // a fresh start
        _cmdAt = _vsyncs;
        return false;
    }

    // Same room as the friend now: stand where they are (world = room coordinates).
    void PlaceNextToPeer(IMemory m)
    {
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        m.WriteU32(PlayerXAddr, (uint)TP.WorldX);
        m.WriteU32(PlayerYAddr, (uint)TP.WorldY);
        m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(TP.WorldX - sx));
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(TP.WorldY - sy));
        m.WriteU32(EntitiesAddr + EVelX, 0); m.WriteU32(EntitiesAddr + EVelY, 0);
        Arrived();
    }

    void Arrived()
    {
        _tp = Tp.Idle;
        _hidden = false;   // (the arrival beam shows him again)
        _p1BeamWant = LocalAlucard;
        TeleportStatus = $"teleported to {_tpTarget.Name}";
        Console.WriteLine($"[coop-online] teleport: arrived next to {_tpTarget.Name}");
    }

    // P1 live, right after P1's update: the arrival beam.
    int _backfireWatch;
    bool _cursedSwingWant;
    long _backfireAt;
    const uint PadSimAttack = 0x80 | 0x20;   // PAD_SQUARE | PAD_CIRCLE: both hands

    // After a curse backfire, once the flinch is over: "What!?", a moment of curse
    // (set directly, so immunity doesn't stop it) and one fake attack press through
    // the game's own demo control, so the cursed "nothing comes out" swing plays.
    const uint FnPlaySfx = 0x801347F8u, SfxWhat = 0x6F2;   // PlaySfx, SFX_VO_ALU_WHAT
    const uint CurseTimerAddr = 0x80072F00u + 1 * 2;     // g_Player.timers[ALU_T_CURSE]
    const ushort ShortCurse = 60;

    void CursedSwing(CpuContext c, IMemory m)
    {
        if (!_cursedSwingWant) return;
        if (_vsyncs - _backfireAt > 180) { _cursedSwingWant = false; return; }
        ushort step = m.ReadU16(EntitiesAddr + EStep);
        if (_vsyncs - _backfireAt < 20 || step > 3 /* not standing, walking, crouching or falling */) return;
        _cursedSwingWant = false;
        if (m.ReadU16(CurseTimerAddr) < ShortCurse) m.WriteU16(CurseTimerAddr, ShortCurse);
        Call(c, m, FnPlaySfx, SfxWhat);
        m.WriteU32(PlayerPadSimAddr, PadSimAttack);
        m.WriteU32(PlayerDemoTimerAddr, 2);
    }

    protected override void AfterP1Update(CpuContext c, IMemory m)
    {
        if (_backfireWatch > 0)
        {
            _backfireWatch--;
            Console.WriteLine($"[coop-online] backfire watch: step {m.ReadU16(EntitiesAddr + EStep)}/{m.ReadU16(EntitiesAddr + EStepS)}, hp {m.ReadU32(StatusHpAddr)}, " +
                              $"hitParams {m.ReadU16(EntitiesAddr + EHitParams):X}, inv {m.ReadU16(SpellInvincibleTimerAddr)}/{m.ReadU16(SpellInvincibleTimerAddr + 2)}, curse timer {m.ReadU16(0x80072F02u)}");
        }
        CheckPvpLanded(m);
        DiveKickBounce(m);
        SendSaveToLateJoiners(c, m);
        TickCast(c, m);
        CursedSwing(c, m);
        TickDeath(c, m);
        TickNoStory(m);
        TickLoadout(c, m);
        // a teleport that fell through after he vanished: bring him back
        if (_hidden && LocalAlucard && _tp == Tp.Idle && !_p1BeamWant && _p1BeamState == 0)
        {
            m.WriteU16(EntitiesAddr + EPalette, PalAlucard);
            _hidden = false;
        }
        if (_p1BeamWant && m.ReadU32(GameEngineStepAddr) == 1)
        {
            _p1BeamWant = false;
            // the warp whites the screen out for 0x40 frames (a warp room fades it back
            // itself); keep just a flash so the beam shows
            if (m.ReadU32(ScreenFillFrames) > 6) m.WriteU32(ScreenFillFrames, 6);
            StartBeam(c, m, ref _p1BeamState, ref _p1BeamWait);
        }
        TickBeam(c, m, ref _p1BeamState, ref _p1BeamWait);
    }

    // The cast: Alucard's own Hellfire step (spell pose, pillar of light, sound) up to
    // the moment he vanishes, then the jump instead of the fireballs.
    void TickCast(CpuContext c, IMemory m)
    {
        if (_castWant)
        {
            _castWant = false;
            ushort step = m.ReadU16(EntitiesAddr + EStep);
            if (m.ReadU32(GameEngineStepAddr) != 1 || step == StepPlayerHit || step == StepSpellHellfire)
            { CancelTeleport("can't cast that right now"); return; }
            m.WriteU16(EntitiesAddr + EStep, StepSpellHellfire);
            m.WriteU16(EntitiesAddr + EStepS, 0);
            Send(Msg.TeleportCast, _ => { });   // their copy of us casts too
            Console.WriteLine("[coop-online] teleport: casting");
            return;
        }
        if (_tp != Tp.Casting) return;
        uint beam = 0;
        for (int i = 4; i < PlayerEntityCount && beam == 0; i++)
            if (m.ReadU16(EntityAddr(i) + EEntityId) == EHellfire) beam = EntityAddr(i);
        if (m.ReadU16(EntitiesAddr + EStep) != StepSpellHellfire || (beam == 0 && _vsyncs - _tpSince > 10))
        { CancelTeleport("the teleport was interrupted"); return; }
        if (beam == 0 || m.ReadU16(beam + EStep) < HfhPlayerDisappear) return;
        if (TP.Stage != (ushort)m.ReadU32(StageIdAddr))
        {
            // another part of the castle: the spell fizzles and bites back (see Backfire)
            Call(c, m, FnDestroyEntity, beam);
            Call(c, m, FnStand, 0);
            m.WriteU16(EntitiesAddr + EPalette, PalAlucard);
            _backfireStone = Random.Shared.Next(8) == 0;
            _backfireWant = true;
            CancelTeleport($"{_tpTarget.Name} is in another part of the castle: the spell can't reach them");
            return;
        }
        // he's gone: drop the spell (no fireballs) and stay hidden until the arrival
        Call(c, m, FnDestroyEntity, beam);
        Call(c, m, FnStand, 0);
        m.WriteU16(EntitiesAddr + EPalette, PalPlayerHidden);
        _hidden = true;
        _tp = Tp.Go; _tpSince = _vsyncs;
    }

    // A teleport the friend's too far away for backfires: once Alucard's out of the
    // spell, right after our hit detection (so nothing overwrites it), he takes a
    // weak curse hit from himself through the game's own damage code: a flinch and
    // the curse. One time in eight it's stone instead (wiggle out).
    bool _backfireWant, _backfireStone;
    const ushort ElementCurse = 0x100, ElementStone = 0x200, DamageKindHit = 2;
    const uint SpellInvincibleTimerAddr = 0x80072F00u + 13 * 2;   // g_Player.timers[ALU_T_INVINCIBLE] (and _CONSUMABLES after it)

    void Backfire(IMemory m)
    {
        if (!_backfireWant || m.ReadU16(EntitiesAddr + EStep) == StepSpellHellfire) return;
        _backfireWant = false;
        // the cast left him briefly invincible (both timers), and the game ignores hits then
        m.WriteU16(SpellInvincibleTimerAddr, 0);
        m.WriteU16(SpellInvincibleTimerAddr + 2, 0);
        m.WriteU32(EntitiesAddr + EUnkB8, EntitiesAddr);   // "hit by himself" (for the knockback side)
        m.WriteU8(EntitiesAddr + EHitFlags, 1);
        // stone through the game's own element; the curse part is played by hand below
        // (an element would be guarded against by curse immunity: gear, relics)
        m.WriteU16(EntitiesAddr + EHitParams, (ushort)((_backfireStone ? ElementStone : 0) | DamageKindHit));
        m.WriteU16(EntitiesAddr + EHitPoints, 1);
        // then, cursed, he tries to strike and nothing comes out (once the flinch is over)
        _cursedSwingWant = !_backfireStone; _backfireAt = _vsyncs;
        Console.WriteLine($"[coop-online] teleport: the spell backfired ({(_backfireStone ? "stone" : "curse")}); " +
                          $"timers inv {m.ReadU16(SpellInvincibleTimerAddr + 2)} (consumables), grab {m.ReadU16(0x80072F80u)}, step {m.ReadU16(EntitiesAddr + EStep)}");
        _backfireWatch = 3;
    }
}
