using System;
using System.Collections.Generic;
using System.Linq;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

// Deaths online: no game over, and no penalty (2026-10-25).
//  - A death plays as usual. When it's over, where the game would go to its game
//    over screen (PLAYER_STATUS_UNK80000 -> Engine_10), we load you back in: at the
//    last save room you entered (your own, not shared), or at the castle gate if
//    you haven't been in one, with full HP and MP. It's the game's own stage load,
//    set up the way loading a save from the title does it (D_8003C730 = 1: the
//    save room's middle), and without D_8003C730 the gate is the new game's spawn.
//  - An Alucard friend in your room with the fairy out and a Life Apple (the bag
//    is shared) revives you instead: the same load, back into your room, where you
//    fell. The lowest-numbered such Alucard does it, and a dead Alucard whose own
//    fairy can revive him is left to it (the game does that itself).
//  - Coming back, each character plays their own: Alucard comes down in the
//    teleport's pillar of light, Richter does the cross crash without the spinning
//    crosses, PSP Maria a pillar of light and her fire wings (MariaWings.cs), Saturn
//    Maria her heal command.
//    Friends who see it play it on their copy too.
//  - Saturn Maria's heal (command move 1) also heals friends near her: a quarter of
//    their max HP, with the potion's sound and glow.
public partial class OnlineCoop
{
    const uint GameOverEngineStep = 10, EngineNormal = 1;                   // Engine_10, Engine_Normal
    const uint PlayDefault = 3, PlayPrepareNextStage = 4;                  // g_GameStep
    const uint GameStateTitle = 1, GameStateMainMenu = 8;
    const uint RoomFlagsAddr = 0x8003C708u; const ushort RoomIsSaveRoom = 0x20;   // D_8003C708.flags (FLAG_UNK_20: loads g_SaveRoomGfx)
    const uint CurrentEntityAddr = 0x8006C3B8u;                             // g_CurrentEntity
    const uint LifeAppleAddr = HandCountAddr + 0x92;                        // equipHandCount[ITEM_LIFE_APPLE]
    const uint ServantFaerie = 3, ServantYousei = 6;                        // FAM_ACTIVE_FAERIE / _YOUSEI
    const uint SfxFaerieRegeneration = 0x887, SfxHealthPickup = 0x68E;
    const uint BpRicBlink = 33, BpMarBlink = 31;
    const uint StageCastleGate = 0x41;                                      // NO3 (spawn 0x11: Richter mode's start)

    enum Rs { Alive, Dying, Loading, Arrived }
    Rs _rs;
    long _rsSince;
    bool _reviveWant;               // a friend's fairy is bringing us back
    string _revivedBy = "";
    (uint Stage, short Left, short Top)? _saveRoom;
    (uint Stage, short Left, short Top, int X, int Y) _deathSpot;
    bool _respawnFxWant;
    long _respawnFxUntil;           // the cross crash's spinning crosses are left out until then
    readonly HashSet<int> _revivedFriends = new();   // friends whose death we've answered

    bool OnlineDeaths => _net.Status == NetSession.State.Connected;

    // The top of the frame (P1 live): the game over that's about to start, and our reload.
    [PreHook("dra", "HandlePlay")]
    public static void BeforeHandlePlayDeaths(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self) return;
        self.CheckOwnOverlay(m);
        self.CatchGameOver(m);
        // dead, coming back, or in the respawn move: the buttons do nothing (and aren't
        // sent: OnNetFrame), so friends' copies of us don't swing while we can't
        if (self.RespawnLocked(m)) for (uint i = 0; i < 8; i++) m.WriteU8(PadsAddr + i, 0);
    }

    bool _respawnMoving;
    long _respawnMoveSince;

    bool RespawnLocked(IMemory m) =>
        OnlineDeaths && Game.InGame && (_rs != Rs.Alive || _respawnMoving || (int)m.ReadU32(StatusHpAddr) <= 0);

    void StartRespawnLock() { _respawnMoving = true; _respawnMoveSince = _vsyncs; }

    // What friends are told (snapshot flag 4): dead from the killing hit until we're
    // standing at the respawn spot. (The reload heals us at once, so HP alone would have
    // their copy of us get up where we fell, then slide over to where we came back.)
    bool DeadToFriends(IMemory m) =>
        (int)m.ReadU32(StatusHpAddr) <= 0 || _rs is Rs.Dying or Rs.Loading or Rs.Arrived;

    // The move is over: Alucard's beam is done, the others are standing again.
    void TickRespawnLock(IMemory m)
    {
        if (!_respawnMoving) return;
        long t = _vsyncs - _respawnMoveSince;
        bool done = LocalAlucard ? !_p1BeamWant && _p1BeamState == 0 : m.ReadU16(EntitiesAddr + EStep) == 0;
        if ((t > 20 && done) || t > 300) _respawnMoving = false;
    }

    void CatchGameOver(IMemory m)
    {
        uint state = m.ReadU32(GameStateAddr);
        if (state is GameStateTitle or GameStateMainMenu) { _saveRoom = null; _rs = Rs.Alive; _reviveWant = false; return; }
        if (!Game.InGame || m.ReadU32(GameStepAddr) != PlayDefault) return;
        bool over = m.ReadU32(GameEngineStepAddr) == GameOverEngineStep;
        if (!over || !OnlineDeaths) return;
        // not yet: the game stays in play (it asks again next frame)
        m.WriteU32(GameEngineStepAddr, EngineNormal);
        if (_rs != Rs.Dying) { _rs = Rs.Dying; _rsSince = _vsyncs; RememberDeathSpot(m); }
        // a friend's fairy may be on its way: give it a moment
        if (!_reviveWant && _vsyncs - _rsSince < 90 && FairyNearby(m)) return;
        Reload(m);
    }

    void RememberDeathSpot(IMemory m) =>
        _deathSpot = (m.ReadU32(StageIdAddr), (short)m.ReadU32(TilemapLeft), (short)m.ReadU32(TilemapTop),
                      (int)m.ReadU32(PlayerXAddr), (int)m.ReadU32(PlayerYAddr));

    // A friend here could revive us: an Alucard with the fairy out, and we have a Life Apple (shared bag).
    bool FairyNearby(IMemory m) =>
        m.ReadU8(LifeAppleAddr) > 0 && ValidFriends.Any(f => Present(m, f) && (f.Peer.Flags & 4) == 0 && HasFaerie(f.Peer));

    bool HasFaerie(Snap s) => CoopCharacters.IsAlucard((CoopCharacter)s.Character) && (_testFairy || s.Servant is ServantFaerie or ServantYousei);
    bool OurFaerieOut(IMemory m) => _testFairy || m.ReadU32(ServantAddr) is ServantFaerie or ServantYousei;

    // Back in through the game's own stage load, healed.
    void Reload(IMemory m)
    {
        m.WriteU32(StatusStatsAddr + SHp * 4, m.ReadU32(StatusStatsAddr + SHpMax * 4));
        m.WriteU32(StatusStatsAddr + SMp * 4, m.ReadU32(StatusStatsAddr + SMpMax * 4));
        m.WriteU32(WarpModeAddr, 0);
        string where;
        if (_reviveWant)
        {
            // our room again (placed where we fell once it's in)
            m.WriteU32(StageIdAddr, _deathSpot.Stage);
            m.WriteU32(TilemapLeft, (uint)_deathSpot.Left); m.WriteU32(TilemapTop, (uint)_deathSpot.Top);
            m.WriteU32(SaveLoadedAddr, 1);
            where = $"revived by {_revivedBy}'s fairy";
        }
        else if (_saveRoom is { } s)
        {
            m.WriteU32(StageIdAddr, s.Stage);
            m.WriteU32(TilemapLeft, (uint)s.Left); m.WriteU32(TilemapTop, (uint)s.Top);
            m.WriteU32(SaveLoadedAddr, 1);
            where = $"respawning in the save room at {s.Stage:X}/{s.Left},{s.Top}";
        }
        else
        {
            m.WriteU32(SaveLoadedAddr, 0);
            m.WriteU32(SpawnPointAddr, RichterModeSpawn);
            m.WriteU32(StageIdAddr, StageCastleGate);
            where = "respawning at the castle gate";
        }
        m.WriteU32(GameStepAddr, PlayPrepareNextStage);
        _rs = Rs.Loading; _rsSince = _vsyncs;
        _tp = Tp.Idle;
        Console.WriteLine($"[coop-online] died: {where}");
    }

    // After our update (P1 live): save rooms, coming back in, the respawn move, heals.
    void TickDeath(CpuContext c, IMemory m)
    {
        if (!Game.InGame || Game.IsLoading) return;
        if ((m.ReadU16(RoomFlagsAddr) & RoomIsSaveRoom) != 0 && _rs == Rs.Alive)
            _saveRoom = (m.ReadU32(StageIdAddr), (short)m.ReadU32(TilemapLeft), (short)m.ReadU32(TilemapTop));
        if (_rs == Rs.Loading && _vsyncs - _rsSince > 10 && m.ReadU32(GameStepAddr) == PlayDefault &&
            m.ReadU32(GameEngineStepAddr) == EngineNormal)
        {
            if (_reviveWant) PlaceAt(m, _deathSpot.X, _deathSpot.Y);
            Console.WriteLine($"[coop-online] back in at {m.ReadU32(StageIdAddr):X}/{(short)m.ReadU32(TilemapLeft)},{(short)m.ReadU32(TilemapTop)}, hp {m.ReadU32(StatusHpAddr)}");
            _reviveWant = false;
            _rs = Rs.Arrived; _rsSince = _vsyncs;
            StartRespawnLock();
            if (LocalAlucard) { _p1BeamWant = true; _rs = Rs.Alive; }
        }
        // the others: once they're standing (or after a second, whatever they're doing)
        if (_rs == Rs.Arrived && (m.ReadU16(EntitiesAddr + EStep) == 0 || _vsyncs - _rsSince > 60))
        {
            _rs = Rs.Alive;
            StartRespawnLock();
            StartRespawnFx(c, m, _p1Char);
            _respawnFxUntil = _vsyncs + 240;
            Send(Msg.RespawnFx, _ => { });
        }
        // (PSP Maria stays unseen until her wings bring her in)
        if (_rs == Rs.Arrived && _p1Char == CoopCharacter.MariaPsp) Fade(m, 0);
        TickOurWings(m);
        TickRespawnLock(m);
        if (_healWant > 0) { ShowHeal(c, m, _p1Char, _healWant); _healWant = 0; }
        if (_satHealDone) { _satHealDone = false; SendSatHeal(m); }
        AnswerFriendDeaths(c, m);
    }

    void PlaceAt(IMemory m, int x, int y)
    {
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        m.WriteU32(PlayerXAddr, (uint)x);
        m.WriteU32(PlayerYAddr, (uint)y);
        m.WriteU16(EntitiesAddr + EPosXHi, (ushort)(x - sx));
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(y - sy));
        m.WriteU32(EntitiesAddr + EVelX, 0); m.WriteU32(EntitiesAddr + EVelY, 0);
    }

    // ---------------------------------------------------------------- the fairy

    // A friend died in our room: if we're the Alucard whose fairy answers, use a Life
    // Apple on them.
    void AnswerFriendDeaths(CpuContext c, IMemory m)
    {
        bool weCan = _rs == Rs.Alive && (int)m.ReadU32(StatusHpAddr) > 0;
        foreach (var f in _friends)
        {
            bool dead = f.Valid && (f.Peer.Flags & 4) != 0;
            if (!dead) { _revivedFriends.Remove(f.Number); continue; }
            if (_revivedFriends.Contains(f.Number) || !Present(m, f)) continue;
            _revivedFriends.Add(f.Number);
            if (HasFaerie(f.Peer)) continue;   // their own fairy has them (if they've an apple)
            if (!weCan || !LocalAlucard || !OurFaerieOut(m) || m.ReadU8(LifeAppleAddr) == 0) continue;
            int me = _net.LocalNumber;
            bool lower = ValidFriends.Any(g => g != f && g.Number < me && Present(m, g) && (g.Peer.Flags & 4) == 0 && HasFaerie(g.Peer));
            if (lower) continue;   // theirs goes
            m.WriteU8(LifeAppleAddr, (byte)(m.ReadU8(LifeAppleAddr) - 1));
            Call(c, m, FnPlaySfx, SfxFaerieRegeneration);
            Send(f.Link, Msg.Revive, _ => { });
            Console.WriteLine($"[coop-online] our fairy used a Life Apple on {f.Name}");
        }
    }

    void OnRevive()
    {
        if (_rs is not (Rs.Alive or Rs.Dying)) return;   // (already on our way back)
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null || (_rs == Rs.Alive && (int)m.ReadU32(StatusHpAddr) > 0)) return;
        _reviveWant = true;
        _revivedBy = From;
        if (_rs == Rs.Alive) RememberDeathSpot(m);
        Console.WriteLine($"[coop-online] {From}'s fairy is reviving us");
    }

    // ---------------------------------------------------------------- the respawn move

    // On the player in slot 0, whose overlay is live: ours, or a friend's copy in its pass.
    void StartRespawnFx(CpuContext c, IMemory m, CoopCharacter ch)
    {
        if (CoopCharacters.IsAlucard(ch)) return;   // (the beam: _p1BeamWant / their beaming flag)
        uint sub = m.ReadU32(StatusGearAddr), hearts = m.ReadU32(StatusStatsAddr + SHearts * 4);
        uint current = m.ReadU32(CurrentEntityAddr);
        m.WriteU32(CurrentEntityAddr, EntitiesAddr);
        switch (ch)
        {
            case CoopCharacter.Richter or CoopCharacter.RichterCoat:
                // his cross's item crash, free (the crosses are left out: SkipCrashCrosses)
                m.WriteU32(StatusGearAddr, SubweaponCross);
                m.WriteU32(StatusStatsAddr + SHearts * 4, 99);
                CallByName(c, m, ch == CoopCharacter.Richter ? "ric" : "richter2", ch == CoopCharacter.Richter ? "RicDoCrash" : "r2_RicDoCrash");
                break;
            case CoopCharacter.MariaPsp:
                // the pillar of light and her Cardinal's fire wings (MariaWings.cs)
                if (_inP2) StartFriendWings(c, m, Fx(F)); else StartOurWings(c, m);
                break;
            case CoopCharacter.MariaSaturn:
                CallByName(c, m, "mariasat", "MarSetCommand", 1u);   // command move 1: the heal (no MP spent)
                break;
        }
        m.WriteU32(StatusGearAddr, sub);
        m.WriteU32(StatusStatsAddr + SHearts * 4, hearts);
        m.WriteU32(CurrentEntityAddr, current);
        Console.WriteLine($"[coop-online] respawn move: {CoopCharacters.Name(ch)}{(_inP2 ? " (a friend's copy)" : "")}, step now {m.ReadU16(EntitiesAddr + EStep):X}");
    }
    const uint SubweaponCross = 4;

    // A character overlay's function, by name: the mods' overlays move when rebuilt
    // (an address from an older build crashed the respawn as an unmapped call).
    static readonly Dictionary<(string, string), uint> _fnAddrs = new();

    static uint FnAddr(string overlay, string name)
    {
        if (_fnAddrs.TryGetValue((overlay, name), out var a)) return a;
        a = RecompOne.Runtime.Dispatch.Dispatcher.Overlays.TryGetValue(overlay, out var ovl)
            ? ovl.Functions.FirstOrDefault(f => f.Value.Method.Name == name).Key : 0;
        if (a == 0) Console.WriteLine($"[coop-online] no {overlay} function {name}");
        return _fnAddrs[(overlay, name)] = a;
    }

    static void CallByName(CpuContext c, IMemory m, string overlay, string name, uint a0 = 0, uint a1 = 0)
    {
        if (FnAddr(overlay, name) is var fn and not 0) Call(c, m, fn, a0, a1);
    }
    const uint BpCrashCrossesOnly = 14;

    // The cross crash's spinning crosses, while a respawn's crash is going.
    [PreHook("ric", "RicCreateEntFactoryFromEntity")]
    public static bool SkipCrashCrosses(CpuContext c, IMemory m) => KeepFactory(c);

    [PreHook("richter2", "r2_RicCreateEntFactoryFromEntity")]
    public static bool SkipCrashCrosses2(CpuContext c, IMemory m) => KeepFactory(c);

    static bool KeepFactory(CpuContext c)
    {
        if (I is not OnlineCoop self || (c.A1 & 0xFFFF) != BpCrashCrossesOnly) return true;
        long until = self._inP2 ? self.Fx(self.F).RespawnFxUntil : self._respawnFxUntil;
        if (self._vsyncs >= until) return true;
        c.V0 = 0;
        Console.WriteLine("[coop-online] respawn move: the crash's crosses left out");
        return false;
    }

    void OnFriendRespawnFx()
    {
        if (FromFriend is not { } f) return;
        var fx = Fx(f);
        fx.RespawnFxWant = true; fx.RespawnFxAt = _vsyncs;
    }

    // In their pass (their body live in slot 0): their respawn move on our copy of them.
    void TickFriendRespawnFx(CpuContext c, IMemory m, Friend f, FriendFxState fx)
    {
        if (fx.HealWant > 0) { ShowHeal(c, m, R.Character, 0); fx.HealWant = 0; }
        TickFriendWings(m, fx);
        if (!fx.RespawnFxWant) return;
        // (their copy was dropped when they came back to life: it has to rejoin first)
        long waited = _vsyncs - fx.RespawnFxAt;
        if (waited > 300) { fx.RespawnFxWant = false; Console.WriteLine($"[coop-online] {f.Name}'s respawn move: not shown (their copy never came back here)"); return; }
        if (m.ReadU16(EntitiesAddr + EStep) != 0 && waited < 60) return;   // standing first (for a second at most)
        fx.RespawnFxWant = false;
        Console.WriteLine($"[coop-online] {f.Name}'s respawn move: on our copy of them, {waited} frames after they sent it");
        fx.RespawnFxUntil = _vsyncs + 240;
        StartRespawnFx(c, m, R.Character);
        R.Discontinuity = true;   // started here, not by their buttons: a rollback mustn't undo it
    }

    // ---------------------------------------------------------------- Saturn Maria's heal

    // Her heal entity adds her quarter at the step it goes from 1 to 2.
    uint _satHealEntity;
    ushort _satHealStep;
    bool _satHealDone;

    [PreHook("mariasat", "MarEntity74")]
    public static void BeforeSatHeal(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || self._inP2) return;
        self._satHealEntity = c.A0;
        self._satHealStep = m.ReadU16(c.A0 + EStep);
    }

    [PostHook("mariasat", "MarEntity74")]
    public static void AfterSatHeal(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || self._inP2 || self._satHealEntity == 0) return;
        if (self._satHealStep == 1 && m.ReadU16(self._satHealEntity + EStep) == 2) self._satHealDone = true;
        self._satHealEntity = 0;
    }

    const int SatHealReach = 96;   // px, either way

    void SendSatHeal(IMemory m)
    {
        if (!OnlineDeaths) return;
        var (stage, left, top) = CurrentRoom(m);
        int x = (int)m.ReadU32(PlayerXAddr), y = (int)m.ReadU32(PlayerYAddr);
        Send(Msg.SatHeal, w => { w.Write(stage); w.Write(left); w.Write(top); w.Write(x); w.Write(y); });
    }

    int _healWant;

    void OnSatHeal(IMemory m, System.IO.BinaryReader r)
    {
        ushort stage = r.ReadUInt16(); short left = r.ReadInt16(), top = r.ReadInt16();
        int x = r.ReadInt32(), y = r.ReadInt32();
        if (!Game.InGame || CurrentRoom(m) != (stage, left, top) || _rs != Rs.Alive) return;
        int hp = (int)m.ReadU32(StatusHpAddr);
        if (hp <= 0) return;
        if (Math.Abs((int)m.ReadU32(PlayerXAddr) - x) > SatHealReach || Math.Abs((int)m.ReadU32(PlayerYAddr) - y) > SatHealReach) return;
        int max = (int)m.ReadU32(StatusStatsAddr + SHpMax * 4);
        int add = Math.Min(max / 4, max - hp);
        _healWant = Math.Max(add, 1);
        if (add > 0) m.WriteU32(StatusHpAddr, (uint)(hp + add));
        Send(Msg.HealShown, _ => { });   // (so the friends' copies of us glow too)
        Console.WriteLine($"[coop-online] {From}'s heal: +{add} HP");
    }

    void OnFriendHealShown() { if (FromFriend is { } f) Fx(f).HealWant = 1; }

    // The potion's sound and glow on the player in slot 0 (its own blink, by character).
    void ShowHeal(CpuContext c, IMemory m, CoopCharacter ch, int amount)
    {
        if (amount > 0) Call(c, m, FnPlaySfx, SfxHealthPickup);
        uint current = m.ReadU32(CurrentEntityAddr);
        m.WriteU32(CurrentEntityAddr, EntitiesAddr);
        foreach (uint glow in ch == CoopCharacter.MariaSaturn ? new uint[] { 0x58 } : new uint[] { 0x48, 0x44 })
        {
            switch (ch)
            {
                case CoopCharacter.Alucard or CoopCharacter.AlucardPsp: Call(c, m, FnCreateEntFactory, EntitiesAddr, BpBlinkWhite | glow << 16, 0); break;
                case CoopCharacter.Richter: CallByName(c, m, "ric", "RicCreateEntFactoryFromEntity", EntitiesAddr, BpRicBlink | glow << 16); break;
                case CoopCharacter.RichterCoat: CallByName(c, m, "richter2", "r2_RicCreateEntFactoryFromEntity", EntitiesAddr, BpRicBlink | glow << 16); break;
                case CoopCharacter.MariaPsp: CallByName(c, m, "maria", "MarCreateEntFactoryFromEntity", EntitiesAddr, BpMarBlink | glow << 16); break;
                case CoopCharacter.MariaSaturn: CallByName(c, m, "mariasat", "mas_RicCreateEntFactoryFromEntity", EntitiesAddr, BpRicBlink | glow << 16); break;
            }
        }
        m.WriteU32(CurrentEntityAddr, current);
    }
}
