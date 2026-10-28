using System;
using System.Linq;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

// Testing three or more players on one machine (tools/dev/run-n.sh), set by
// environment variables:
//  - COOP_PLAYERS=n      players when hosting (2..6)
//  - COOP_BOT=follow     this copy plays itself: walks after the nearest friend,
//                        jumps and attacks now and then, confirms the select screen
//  - COOP_BOT=footage    the same, but plays like a companion, for recording: keeps a
//                        spot near the leader, runs with them, jumps to follow or when
//                        stuck, attacks enemies in front of it, teleports after them
//                        (COOP_LEAD=name: who to follow; default the host)
//                        (COOP_JOIN_HOST=name with lobbyjoin: only that host's game)
//  - COOP_NETSIM=lag:120,loss:5,jitter:20   a bad connection (Net.cs)
//  - COOP_SYNCLOG=1      once a second, a "sync" line for tools/dev/sync-compare.py
//  - COOP_BOT_PICK=richter|maria|alucard (+up for the alternate)
//                        the lobby's character pick (default: whatever was picked last)
//  - COOP_AUTOSTART=1    (the host, with the bot) once everyone has joined, Start in
//                        the lobby; the guests follow as usual
//                        (COOP_AUTOSTART_AT=n: once n have joined; the rest join late)
//  - COOP_DIE_AT=s[,every]  this copy takes a killing hit s seconds into the game
//                        (and every `every` seconds after): deaths and respawns (Respawn.cs)
//                        (COOP_DIE_WHO=name: only that copy, e.g. Friend1)
//  - COOP_TEST_SAVEROOM=stage,left,top (hex stage)  as if we'd been in that save room
//  - COOP_TEST_FAIRY=1   every Alucard counts as having the fairy out, with a Life Apple
public partial class OnlineCoop
{
    partial void FootageButtons(IMemory m, long t, ref ushort result);   // FootageBot.cs (dev only)
    bool _bot, _footage, _syncLog, _autoStart, _testPlayers;   // (_testPlayers: COOP_PLAYERS may go past the public cap)
    int _autoStartAt;
    int _botDir = 1;
    long _botTurnAt, _botJumpAt, _botAttackAt;
    ushort _botLast;
    const ushort PadLeftB = 0x8000, PadRightB = 0x2000, PadCrossB = 0x40, PadSquareB = 0x80;

    void SetUpTestTools()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("COOP_PLAYERS"), out int n)) { MaxPlayers = Math.Clamp(n, 2, NetSession.MaxPlayersLimit); _testPlayers = true; }
        _bot = Environment.GetEnvironmentVariable("COOP_BOT") is "follow" or "footage";
        _footage = Environment.GetEnvironmentVariable("COOP_BOT") == "footage";
        _syncLog = Environment.GetEnvironmentVariable("COOP_SYNCLOG") == "1";
        _autoStart = Environment.GetEnvironmentVariable("COOP_AUTOSTART") == "1";
        _autoStartAt = int.TryParse(Environment.GetEnvironmentVariable("COOP_AUTOSTART_AT"), out int at) ? at : MaxPlayers;
        // the lobby's pick, as the select would set it (hold up = the alternate)
        var pick = (Environment.GetEnvironmentVariable("COOP_BOT_PICK") ?? "").ToLowerInvariant();
        bool up = pick.EndsWith("+up");
        if (_bot && pick.Length == 0) SetPick(CoopCharacter.Alucard, 0);
        if (pick.Length > 0)
            SetPick(pick.StartsWith("richter") ? (up ? CoopCharacter.RichterCoat : CoopCharacter.Richter)
                  : pick.StartsWith("maria") ? (up ? CoopCharacter.MariaPsp : CoopCharacter.MariaSaturn)
                  : up ? CoopCharacter.AlucardPsp : CoopCharacter.Alucard, 0);
        var die = (Environment.GetEnvironmentVariable("COOP_DIE_AT") ?? "").Split(',');
        int.TryParse(die[0], out _dieAt);
        if (die.Length > 1) int.TryParse(die[1], out _dieEvery);
        _testFairy = Environment.GetEnvironmentVariable("COOP_TEST_FAIRY") == "1";
        var sim = Environment.GetEnvironmentVariable("COOP_NETSIM") ?? "";
        foreach (var part in sim.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[1], out int v)) continue;
            if (kv[0] == "lag") _net.SimLagMs = v;
            if (kv[0] == "loss") _net.SimLossPercent = v;
            if (kv[0] == "jitter") _net.SimJitterMs = v;
        }
        if (_bot) Console.WriteLine("[coop-online] bot: this copy plays itself");
        if (sim.Length > 0) Console.WriteLine($"[coop-online] netsim: lag {_net.SimLagMs} ms, jitter {_net.SimJitterMs} ms, loss {_net.SimLossPercent}%");
    }

    // The bot's buttons in pad 1, just before the game acts on them: in game before
    // HandlePlay, on the title screens from SEL_Update (NetplayMenu.cs). (Right after
    // ReadPads is too early: the pads are set again before the title reads them.)
    [PreHook("dra", "HandlePlay")]
    public static void BotPadsInGame(CpuContext c, IMemory m)
    {
        if (I is OnlineCoop self && self._bot && Game.InGame) self.BotPads(m);
    }

    // The "press start" screen (it numbers its steps like the main menu does).
    [PreHook("sel", "HandleTitleScreen")]
    public static void BotTitleScreen(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || !self._bot) return;
        if (++self._botTitleTicks % 45 == 0) m.WriteU16(PadsAddr + 4, (ushort)(m.ReadU16(PadsAddr + 4) | PadStartB));
    }
    long _botTitleTicks;

    // (the logos, before the menu: there right after ReadPads works)
    [PostHook("dra", "ReadPads")]
    public static void BotPadsTitle(CpuContext c, IMemory m)
    {
        if (I is OnlineCoop self && self._bot && !Game.InGame && !IsMenuStep(m.ReadU32(GameEngineStepAddr))) self.BotPads(m);
    }

    static bool IsMenuStep(uint step) =>
        step is EngMainMenuFadeIn or EngMainMenuIdle or EngNameEntry or NameEntryFadeStep or EngNetplay or EngLoadFadeOut
        || (step >= EngFileSelect && step < EngFileSelect + 0x10);

    void BotPads(IMemory m)
    {
        var self = this;
        self._botTick++;   // (its own clock: the title screens run every other vsync)
        ushort pad = self.BotButtons(m);
        m.WriteU16(PadsAddr + 0, pad);
        m.WriteU16(PadsAddr + 2, self._botLast);
        m.WriteU16(PadsAddr + 4, (ushort)(pad & ~self._botLast));
        m.WriteU16(PadsAddr + 6, (ushort)(pad & ~self._botLast));
        self._botLast = pad;
    }

    const uint NameEntryFadeStep = 0x12;   // the Saturn select runs over it (source/characters)

    ushort BotButtons(IMemory m)
    {
        long t = _botTick;
        if (!Game.InGame) return BotTitleButtons(m, t);
        if (_footage && t % 300 == 0)
            Console.WriteLine($"[coop-online] footage bot: paused {Paused(m)}, cutscene {InCutscene(m)} (demo {m.ReadU32(DemoTimerAddr)}, pause allowed {m.ReadU8(PauseAllowedFlagAddr)}), step {m.ReadU16(EntitiesAddr + EStep):X}, at {m.ReadU32(PlayerXAddr)},{m.ReadU32(PlayerYAddr)}");
        if (Paused(m) || InCutscene(m)) return 0;
        if (_footage) { ushort fp = 0; FootageButtons(m, t, ref fp); return fp; }
        ushort pad = 0;
        int x = (int)m.ReadU32(PlayerXAddr), y = (int)m.ReadU32(PlayerYAddr);
        var room = CurrentRoom(m);
        var target = ValidFriends.Where(f => (f.Peer.Stage, f.Peer.RoomLeft, f.Peer.RoomTop) == room)
                                 .OrderBy(f => Math.Abs(f.Peer.WorldX - x) + Math.Abs(f.Peer.WorldY - y)).FirstOrDefault();
        if (target != null)
        {
            int dx = target.Peer.WorldX - x;
            if (Math.Abs(dx) > 28) pad |= dx < 0 ? PadLeftB : PadRightB;
            if (target.Peer.WorldY < y - 40 && t >= _botJumpAt) { pad |= PadCrossB; _botJumpAt = t + 50; }
        }
        else
        {
            // nobody here: wander
            if (t >= _botTurnAt) { _botDir = -_botDir; _botTurnAt = t + 120 + Random.Shared.Next(120); }
            pad |= _botDir < 0 ? PadLeftB : PadRightB;
        }
        if (t >= _botJumpAt && Random.Shared.Next(60) == 0) { pad |= PadCrossB; _botJumpAt = t + 40; }
        if ((_botLast & PadCrossB) != 0 && t < _botJumpAt - 30) pad |= PadCrossB;   // hold it for a full jump
        if (t >= _botAttackAt) { pad |= PadSquareB; _botAttackAt = t + 30 + Random.Shared.Next(60); }
        return pad;
    }

    const ushort PadStartB = 0x800, PadUpB = 0x1000;
    bool _botWantsStart;   // the host bot's Start, done in SEL_Update (NetplayMenu.cs)
    uint _botLastStep = 0xFFFFFFFF;
    long _botTick;

    // On the title screens: as the host with COOP_AUTOSTART, Start in the lobby once
    // enough have joined (the guests follow). The "press start" screen is BotTitleScreen.
    ushort BotTitleButtons(IMemory m, long t)
    {
        uint step = m.ReadU32(GameEngineStepAddr);
        if (step != _botLastStep) { Console.WriteLine($"[coop-online] bot: title step {step:X}"); _botLastStep = step; }
        bool menuStep = IsMenuStep(step);
        if (!menuStep) return t % 90 == 0 ? PadStartB : (ushort)0;   // logos
        if (!_autoStart || !_net.IsHost || _net.PlayerCount < _autoStartAt) return 0;
        // the lobby's Start (NetplayMenu.cs): from the main menu, Netplay first
        if (step == EngMainMenuIdle && t % 60 == 0) return PadTri;
        if (step == EngNetplay && t % 60 == 30) _botWantsStart = true;
        return 0;
    }

    // COOP_DIE_AT: right after our hit detection, a hit from ourselves that kills us
    // (the teleport backfire's way in: Teleport.cs).
    int _dieAt, _dieEvery;
    bool _testFairy;
    long _dieFrames;

    void TestDeath(IMemory m)
    {
        if (_testFairy && Game.InGame && m.ReadU8(LifeAppleAddr) == 0) m.WriteU8(LifeAppleAddr, 3);
        if (_dieAt <= 0 || !Game.InGame || Paused(m)) return;
        if (Environment.GetEnvironmentVariable("COOP_DIE_WHO") is { Length: > 0 } who && who != Environment.GetEnvironmentVariable("COOP_NAME")) return;
        if (++_dieFrames < _dieAt * 60L) return;
        _dieFrames = _dieEvery > 0 ? (_dieAt - _dieEvery) * 60L : long.MinValue;
        if ((int)m.ReadU32(StatusHpAddr) <= 0 || _rs != Rs.Alive) return;
        var room = (Environment.GetEnvironmentVariable("COOP_TEST_SAVEROOM") ?? "").Split(',');
        if (_saveRoom == null && room.Length == 3)
            _saveRoom = (Convert.ToUInt32(room[0], 16), short.Parse(room[1]), short.Parse(room[2]));
        m.WriteU32(StatusHpAddr, 1);
        m.WriteU16(SpellInvincibleTimerAddr, 0);
        m.WriteU16(SpellInvincibleTimerAddr + 2, 0);
        m.WriteU32(EntitiesAddr + EUnkB8, EntitiesAddr);
        m.WriteU8(EntitiesAddr + EHitFlags, 1);
        m.WriteU16(EntitiesAddr + EHitParams, DamageKindHit);
        m.WriteU16(EntitiesAddr + EHitPoints, 50);
        Console.WriteLine("[coop-online] test: a killing hit");
    }

    // Once a second: where we are, who runs our room and what's in it, and where our
    // copies of the friends are. sync-compare.py lines these up across the copies.
    void TickSyncLog(IMemory m)
    {

        if (!_syncLog || _vsyncs % 60 != 0 || !Game.InGame || _net.Status != NetSession.State.Connected) return;
        var room = CurrentRoom(m);
        int x = (int)m.ReadU32(PlayerXAddr), y = (int)m.ReadU32(PlayerYAddr);
        string owner = !_close ? "alone" : _authority ? "us" : _owner != null ? $"P{_owner.Number + 1}" : "none";
        // the room's objects: which slots hold what (the same set for everyone sharing it)
        int count = 0; uint hash = 2166136261;
        for (int i = StageFirst; i < HitEnd; i++)
        {
            ushort id = m.ReadU16(EntityAddr(i) + EEntityId);
            if (id == 0 || _pvpSlots.Contains(i) || !IsGameplay(m, EntityAddr(i))) continue;   // (not our PvP copies)
            count++;
            hash = (hash ^ (uint)(i << 16 | id)) * 16777619;
        }
        var bodies = string.Join(" ", _friends.Where(f => f.Body.Active).Select(f =>
        {
            var (bx, by) = ScreenOf(m, f.Body);
            return $"P{f.Number + 1}={bx + (short)m.ReadU16(ScrollXAddr)},{by + (short)m.ReadU16(ScrollYAddr)}";
        }));
        Console.WriteLine($"[coop-online] sync t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()} me=P{_net.LocalNumber + 1} room={room.Item1:X}/{room.Item2},{room.Item3} " +
                          $"pos={x},{y} hp={m.ReadU32(StatusHpAddr)} owner={owner} objs={count}:{hash:X8} bodies[{bodies}] rollbacks={Rollbacks}");
    }
}
