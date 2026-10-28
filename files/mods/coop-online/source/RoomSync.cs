using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Sotn;

// Phase 3: fighting together.
//
// Apart (different rooms) each game runs its own room. Players in the SAME room
// share it: one game runs all of it - whoever was there first (ties: the lowest
// player number) - and streams its objects (slots 64..255 + their prims) to each of
// the others every frame; they show the stream and send their player's hits and
// pickups back to it (Hits.cs). Ownership is sticky: it only moves when the owner
// opens the menu or leaves.
// The game spawns and keeps objects around ONE camera, so the owner also spawns
// the layout around every other camera in the room and keeps objects near them
// alive (Spawn.cs). Kills are always shared (reliable events): a room object anyone
// kills is gone in every game for the rest of the visit.
// Each friend's body is simulated as one of our remote players.
public partial class OnlineCoop
{
    const uint ApiUpdate = 0x8003C774u;
    const int StageFirst = 64;
    const ushort DrawAbsPos = 0x2000, DrawHide = 0x08;
    const byte PrimNone = 0, PrimTile = 1, PrimEnv = 7;
    const uint FnAllocPrims = 0x800EDC80u, FnFreePrims = 0x800EDE78u;
    const uint EParams = 0x30;

    public bool RoomSync = true;

    (ushort Stage, short Left, short Top) _roomKey;
    bool _close, _follower;   // _close: someone else is in this room (sharing it)
    bool _authority;          // we run the shared room
    Friend? _owner;           // follower: who runs it (whose stream we show)
    List<Friend> _sharing = new();   // the friends in this room with us
    int _roomFrames, _noOwnerFrames;
    bool _followNow;   // follower AND still in the room we followed in (false on a room-change frame)
    uint _roomStreamFrame, _appliedRoomFrame;
    // local prim chains we made for the authority's objects, per slot
    readonly Dictionary<int, (int Index, int Count, byte Type)> _streamPrims = new();
    HashSet<uint>? _playerPrims;

    // kills: room objects (by slot + id + params) gone for good, per room, for as
    // long as either of us is in that room (a room nobody is in resets as usual)
    readonly Dictionary<(ushort, short, short), HashSet<(int Slot, ushort Id, ushort Params)>> _killedByRoom = new();
    HashSet<(int Slot, ushort Id, ushort Params)> Killed(( ushort, short, short) room)
    {
        if (!_killedByRoom.TryGetValue(room, out var set)) _killedByRoom[room] = set = new();
        return set;
    }
    HashSet<(int Slot, ushort Id, ushort Params)> _killed => Killed(_roomKey);
    readonly bool[] _seenDead = new bool[256];   // the game marked it dead (FLAG_DEAD / 0 hp)
    const uint FlagDead = 0x100;
    readonly ushort[] _seenId = new ushort[256], _seenParams = new ushort[256];
    readonly short[] _seenX = new short[256], _seenY = new short[256];
    public bool LogRoomSync = true;
    void RoomLog(string s) { if (LogRoomSync) Console.WriteLine("[coop-online] room: " + s); }

    public string RoomStatus => !_close ? "alone in this room" : _follower ? $"sharing: {_owner?.Name} runs the room" : _authority ? $"sharing with {_sharing.Count}: we run the room" : "sharing: deciding who runs it";

    (ushort, short, short) CurrentRoom(IMemory m) =>
        ((ushort)m.ReadU32(StageIdAddr), (short)m.ReadU32(TilemapLeft), (short)m.ReadU32(TilemapTop));

    static bool OnScreen(int x, int y) => x > -32 && x < 288 && y > -32 && y < 256;

    // Only gameplay objects are shared: enemies, anything with a hitbox, pickups.
    // Scenery (backgrounds, parallax layers, decorations) is the same in both games
    // and stays local - it also holds raw pointers into its own game's memory.
    static bool IsGameplay(IMemory m, uint e) =>
        (m.ReadU16(e + EEntityId) != 0 || m.ReadU16(e + EAnimSet) != 0) && m.ReadU16(e + EUnk68) == 0 &&
        (m.ReadU16(e + EEnemyId) != 0 || m.ReadU16(e + 0x3C) != 0 || (short)m.ReadU16(e + EHitPoints) > 0);

    readonly bool[] _streamed = new bool[256];   // follower: the authority runs this slot
    long _lastPrimWarn;
    readonly bool[] _everStreamed = new bool[256];
    readonly bool[] _noPrims = new bool[256];
    bool _replayingCollision;
    long _roomBytes; int _roomMax;
    readonly ushort[] _collidesId = new ushort[256];   // objects that call GetPlayerCollisionWith (by entity id)   // streamed with prims, but we have no copy

    // authority: GetPlayerCollisionWith calls our objects made this frame
    readonly List<(byte Slot, ushort W, ushort H, ushort Flags)> _platformCalls = new();
    protected override bool CollisionCountsP2 => true;
    protected override void OnPlatformCall(CpuContext c, IMemory m)
    {
        if (!_close || !_authority || _platformCalls.Count >= 255) return;
        uint e = c.A0;
        if (e < EntityAddr(StageFirst) || e >= EntityAddr(256) || (e - EntitiesAddr) % EntitySize != 0) return;
        // solid / pushable objects are shared too, even with no hitbox
        _collidesId[(e - EntitiesAddr) / EntitySize] = m.ReadU16(e + EEntityId);
        _platformCalls.Add(((byte)((e - EntitiesAddr) / EntitySize), (ushort)c.A1, (ushort)c.A2, (ushort)c.A3));
    }

    // Once a frame from OnNetFrame (P1 live, end of frame).
    void TickRoom(IMemory m)
    {
        var key = CurrentRoom(m);
        if (_resyncRoom)
        {
            // start the shared room over: run our own, then decide again who runs it
            _resyncRoom = false;
            SetFollower(false); _close = false; _authority = false; ForgetSpawns();
            ClearRoomStreams();
        }
        if (key != _roomKey)
        {
            if (key.Item1 != _roomKey.Item1) _pickupFn.Clear();
            Array.Clear(_collidesId);
            _roomKey = key; SetFollower(false); _close = false; _authority = false; _roomFrames = 0; Array.Clear(_seenId); Array.Clear(_seenDead);
            ForgetSpawns();
            _local.Clear(); _ourKills.Clear(); Array.Clear(_everStreamed);
        }
        // forget kills in rooms nobody is in any more
        var rooms = new HashSet<(ushort, short, short)> { _roomKey };
        foreach (var f in ValidFriends) rooms.Add((f.Peer.Stage, f.Peer.RoomLeft, f.Peer.RoomTop));
        foreach (var room in new List<(ushort, short, short)>(_killedByRoom.Keys))
            if (!rooms.Contains(room)) _killedByRoom.Remove(room);
        ForgetTiles(rooms);
        TickTiles(m);

        if (_roomFrames < 60000) _roomFrames++;
        // (not in the first-visit castle entrance: the intro run, the door and Death's
        // scene are scripted the same in both games, and a follower doesn't run its
        // room's objects, so each game runs its own there)
        // (nor while anyone here is in a story cutscene: each game plays its own, GameModes.cs)
        bool scripted = Scripted((ushort)m.ReadU32(StageIdAddr)) || InStoryScene(m) || _friends.Any(f => Present(m, f) && f.Peer.InScene);
        _sharing = RoomSync && !scripted ? _friends.Where(f => Present(m, f)).ToList() : new List<Friend>();
        bool sameRoom = _sharing.Count > 0;
        if (sameRoom != _close)
        {
            _close = sameRoom; _authority = false; _noOwnerFrames = 0; ForgetSpawns();
            RoomLog(sameRoom ? $"{string.Join(", ", _sharing.Select(f => f.Name))} in this room: sharing it" :
                $"alone in this room: running our own (we're in stage {m.ReadU32(StageIdAddr):X} room {(short)m.ReadU32(TilemapLeft)},{(short)m.ReadU32(TilemapTop)}; " +
                string.Join("; ", _friends.Select(f => $"{f.Name}: {(f.Valid ? $"in game {f.Peer.InGame}, stage {f.Peer.Stage:X} room {f.Peer.RoomLeft},{f.Peer.RoomTop}" : "no snapshot")}")) +
                $"; scripted {scripted})");
        }

        // who runs it: whoever was here first; it only moves when they open the menu
        // (unless the room keeps running in their menu: a live menu, or a boss fight).
        // Two running it at once (both just arrived): the lowest player number keeps it.
        bool weInMenu = Paused(m) && !MenuLive(m);
        int me = _net.LocalNumber;
        var runners = _sharing.Where(f => (f.Peer.Role & 1) != 0).OrderBy(f => f.Number).ToList();
        var awake = _sharing.Where(f => !f.Peer.Frozen).ToList();
        if (!sameRoom) _authority = false;
        else if (_authority)
        {
            // (not in a locked room: its state lives in our stage code; online, a boss
            // fight keeps running in our menu anyway)
            if (weInMenu && awake.Count > 0 && !RoomLocked(m)) { _authority = false; RoomLog("menu: handing the room over"); }
            else if (runners.Any(f => f.Number < me)) { _authority = false; RoomLog($"{runners[0].Name} ran it too: the lower player number keeps it"); }
        }
        else if (!weInMenu && runners.All(f => f.Peer.Frozen))
        {
            bool first = awake.All(f => _roomFrames > f.Peer.RoomFrames + 30 || (Math.Abs(_roomFrames - f.Peer.RoomFrames) <= 30 && me < f.Number));
            bool lowest = awake.All(f => me < f.Number);
            if (awake.Count == 0 || first || (++_noOwnerFrames > 30 && lowest))
            {
                _authority = true; _noOwnerFrames = 0; ForgetSpawns();
                RoomLog(awake.Count == 0 ? "the others are in the menu: we run the room" : "we were here first: we run the room");
            }
        }
        var owner = _authority || weInMenu ? null : runners.FirstOrDefault(f => !f.Peer.Frozen);
        if (owner != _owner && _follower) SetFollower(false);   // someone else runs it now: start over on their stream
        _owner = owner;
        SetFollower(sameRoom && owner != null);

        if (_close && _authority)
        {
            var pkt = BuildRoomStream(m);
            ++_roomStreamFrame;
            foreach (var f in _sharing) _net.SendRoom(f.Link, _roomStreamFrame, pkt);
            // for internet play: over Tailscale anything past ~1200 bytes is split into pieces
            _roomBytes += pkt.Length * _sharing.Count; _roomMax = Math.Max(_roomMax, pkt.Length);
            if (_roomStreamFrame % 600 == 0)
            {
                RoomLog($"room stream to {_sharing.Count}: avg {_roomBytes / 600} bytes/frame, max {_roomMax} ({_roomBytes / 10 / 1024} KB/s), ping {_net.RttMs} ms");
                _roomBytes = 0; _roomMax = 0;
            }
        }
    }

    void ClearRoomStreams() { foreach (var f in _friends) f.Link.ClearRoom(); }

    // Follower: the authority's room lock (its boss / ambush shut the exits) holds for
    // us too, with its camera bounds; both go back to the room's own when it ends.
    bool _mirroringLock;
    readonly uint[] _ownBounds = new uint[4];

    void MirrorRoomLock(IMemory m, uint locked, uint x, uint y, uint w, uint h)
    {
        if (locked != 0)
        {
            if (!_mirroringLock)
            {
                _mirroringLock = true;
                for (int i = 0; i < 4; i++) _ownBounds[i] = m.ReadU32(TilemapX + (uint)(i * 4));
                RoomLog($"{_owner?.Name}'s room is locked (boss fight?): locked here too");
            }
            m.WriteU32(RoomLockAddr, locked);
            m.WriteU32(TilemapX, x); m.WriteU32(TilemapY, y); m.WriteU32(TilemapW, w); m.WriteU32(TilemapH, h);
        }
        else ReleaseRoomLock(m);
    }

    void ReleaseRoomLock(IMemory m)
    {
        if (!_mirroringLock) return;
        _mirroringLock = false;
        m.WriteU32(RoomLockAddr, 0);
        for (int i = 0; i < 4; i++) m.WriteU32(TilemapX + (uint)(i * 4), _ownBounds[i]);
        RoomLog("room unlocked");
    }

    void SetFollower(bool on)
    {
        if (on == _follower) return;
        _follower = on;
        if (!on && RecompOne.Runtime.Runtime.Mem is { } mem && CurrentRoom(mem) == _roomKey) ReleaseRoomLock(mem);
        if (!on) _mirroringLock = false;
        if (!on) _followNow = false;
        _local.Clear(); _ourKills.Clear(); Array.Clear(_everStreamed);
        _appliedRoomFrame = 0;
        ClearRoomStreams();
        // leaving follower mode: adopt the streamed objects, except ones with prims
        // (their internals may point into the other game's memory): drop those
        if (!on)
        {
            var c = RecompOne.Runtime.Runtime.Cpu; var m = RecompOne.Runtime.Runtime.Mem;
            // they keep the prims we made for them (pointers already point at ours);
            // only ones we couldn't make prims for have to go
            if (c != null && m != null)
                for (int slot = StageFirst; slot < 256; slot++)
                    if (_streamed[slot] && _noPrims[slot])
                    {
                        RoomLog($"taking over: dropping slot {slot} (id {m.ReadU16(EntityAddr(slot) + EEntityId):X}), no graphics for it");
                        m.ZeroRange(EntityAddr(slot), EntitySize);
                    }
            _streamPrims.Clear();
            Array.Clear(_noPrims);
            Array.Clear(_streamed);
        }
        RoomLog(on ? $"showing {_owner?.Name}'s objects" : "running our own objects");
    }

    protected override bool ShouldSkipCall(CpuContext c, IMemory m, uint fn)
    {
        if (fn == m.ReadU32(ApiHitDetection))
        {
            if (!_inP2) InsertFriendAttacks(m);   // PvP: before the snapshot, so they're not "new"
            // the friend's copy earns nothing here: their own game pays their kills
            else { _expBefore = m.ReadU32(ExpAddr); _killsBefore = m.ReadU32(KillCountAddr); }
            BeforeHits(m);
            return false;
        }
        if (fn == m.ReadU32(ApiUpdate))
        {
            // the room can change mid-frame (through a door) before TickRoom notices:
            // the stream is for the old room then, and applying it would wipe the new
            // room's freshly spawned objects (which the spawner never re-creates)
            _followNow = _follower && CurrentRoom(m) == _roomKey;
            RemoveFriendAttacks(m);
            if (!_inP2) CheckPrims(m, "before objects");
            if (_followNow) { ApplyRoomStream(c, m); CheckPrims(m, "after applying the stream"); }
            ApplyClaims(c, m);
            if (_close && _authority && CurrentRoom(m) == _roomKey) { foreach (var f in _sharing) SpawnAround(c, m, f); KeepAliveNearFriends(m); }
            RemoveKilled(c, m);
            return false;
        }
        if (!_followNow || _replayingCollision || fn < 0x80180000u || fn >= 0x80200000u) return false;   // stage code only
        uint e = c.A0;
        if (e < EntityAddr(StageFirst) || e >= EntityAddr(256) || (e - EntitiesAddr) % EntitySize != 0) return false;
        return _streamed[(e - EntitiesAddr) / EntitySize];   // the authority runs that one

    }

    // Right after a hit detection pass: room objects that were just marked dead are
    // kills (most turn into their explosion the same frame, so look now).
    protected override void AfterCall(CpuContext c, IMemory m, uint fn, uint a0)
    {
        if (fn == m.ReadU32(ApiHitDetection))
        {
            if (_inP2) { m.WriteU32(ExpAddr, _expBefore); m.WriteU32(KillCountAddr, _killsBefore); }
            CheckPrims(m, _inP2 ? "friend's hit detection" : "our hit detection");
            if (!_inP2) { Backfire(m); TestDeath(m); }
            AfterHits(c, m);
            if (_inP2) ApplyPvpHitOnFriend(m); else CheckPvpHitOnUs(m);
        }
        if (fn == m.ReadU32(ApiUpdate)) { RestoreKeepAlive(m); if (!_inP2) CheckPrims(m, "after objects"); }
        if (_follower || _net.Status != NetSession.State.Connected || fn != m.ReadU32(ApiHitDetection)) return;
        var room = CurrentRoom(m);
        if (Scripted(room.Item1)) return;
        var killedHere = Killed(room);
        for (int i = StageFirst; i < 160; i++)
        {
            uint e = EntityAddr(i);
            ushort id = m.ReadU16(e + EEntityId);
            if (id == 0 || (m.ReadU32(e + EFlags) & FlagDead) == 0) continue;
            var k = (i, id, (ushort)0);
            if (!killedHere.Add(k)) continue;
            Send(Msg.Killed, w => { w.Write(room.Item1); w.Write(room.Item2); w.Write(room.Item3); w.Write((byte)i); w.Write(id); w.Write((ushort)0); });
            RoomLog($"killed slot {i} (id {id:X})");
        }
    }

    // ---------------------------------------------------------------- kills

    // A room object that vanished while on our screen was killed/collected: tell the
    // friend, and remember it.
    void DetectKills(IMemory m)
    {
        for (int i = StageFirst; i < 256; i++)
        {
            uint e = EntityAddr(i);
            ushort id = m.ReadU16(e + EEntityId), prm = m.ReadU16(e + EParams);
            // it really died (the game marked it dead) and then went away; things that
            // just leave (zombies sinking, bats flying off) don't count
            if (_seenId[i] != 0 && (id != _seenId[i] || prm != _seenParams[i]) && _seenDead[i] && IsRoomObject(i))
            {
                var k = (i, _seenId[i], _seenParams[i]);
                if (_killed.Add(k))
                {
                    Send(Msg.Killed, w => { w.Write(_roomKey.Item1); w.Write(_roomKey.Item2); w.Write(_roomKey.Item3); w.Write((byte)i); w.Write(k.Item2); w.Write(k.Item3); });
                    RoomLog($"killed slot {i} (id {k.Item2:X})");
                }
            }
            if (id != _seenId[i] || prm != _seenParams[i]) _seenDead[i] = false;
            if (id != 0 && ((m.ReadU32(e + EFlags) & FlagDead) != 0 ||
                            (m.ReadU16(e + EEnemyId) != 0 && (short)m.ReadU16(e + EHitPoints) <= 0)))
                _seenDead[i] = true;
            _seenId[i] = id; _seenParams[i] = prm;
            _seenX[i] = (short)m.ReadU16(e + EPosXHi); _seenY[i] = (short)m.ReadU16(e + EPosYHi);
        }
    }

    // Layout-placed objects live in slots 64 + layout index; effects and projectiles
    // are allocated from 160 up. Only the former are "room objects".
    static bool IsRoomObject(int slot) => slot < 160;

    // An equipment drop loads its item's icon into one of a few icon slots when it
    // appears, and draws from that slot. A stream copy names the authority's slot,
    // which in our VRAM still holds whatever we last loaded there (the previous drop's
    // icon): load this item's icon into it. (LoadEquipIcon skips it if it's there.)
    const uint ApiLoadEquipIcon = 0x8003C82Cu, ApiEquipDefs = 0x8003C830u, ApiAccessoryDefs = 0x8003C834u;
    const int NumHandItems = 0xA9, EquipDefSize = 0x32, AccessoryDefSize = 0x1E;
    const uint EIconSlot = 0x8C;   // ext.equipItemDrop.iconSlot

    void FixDropIcon(CpuContext c, IMemory m, uint e)
    {
        if (m.ReadU16(e + EStep) == 0 || !IsEquipDrop(m, e)) return;
        int item = m.ReadU16(e + EParams) & 0x7FFF;
        uint iconSlot = m.ReadU16(e + EIconSlot);
        if (iconSlot >= 32) return;
        uint def = item < NumHandItems
            ? m.ReadU32(ApiEquipDefs) + (uint)(item * EquipDefSize) + 0x2C
            : m.ReadU32(ApiAccessoryDefs) + (uint)((item - NumHandItems) * AccessoryDefSize) + 0x18;
        if (def < 0x80000000u || def >= 0x80200000u) return;
        uint load = m.ReadU32(ApiLoadEquipIcon);
        if (load == 0) return;
        Call(c, m, load, m.ReadU16(def), m.ReadU16(def + 2), iconSlot);   // (icon, palette, slot)
        if (_dropIconLogged.Add((iconSlot, item)))
            RoomLog($"drop icon: item {item:X} (icon {m.ReadU16(def):X}, palette {m.ReadU16(def + 2):X}) into icon slot {iconSlot}");
    }
    readonly HashSet<(uint, int)> _dropIconLogged = new();

    readonly Dictionary<uint, bool> _equipDropFn = new();
    bool IsEquipDrop(IMemory m, uint e)
    {
        uint fn = m.ReadU32(e + EPfnUpdate);
        if (fn == 0) return false;
        if (!_equipDropFn.TryGetValue(fn, out bool yes))
        {
            var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
            _equipDropFn[fn] = yes = map.TryGetValue(fn, out var f) && IsStageFn(f.Method.Name, "EntityEquipItemDrop");
        }
        return yes;
    }
    const uint ExpAddr = 0x80097BECu, KillCountAddr = 0x80097BF4u;   // g_Status.exp, killCount
    uint _expBefore, _killsBefore;

    // Prims the players' own objects (slots 0..63: after-images, spells, weapons) use
    // right now. A stream copy's prims must never be among them: if one is (we lost
    // track of a chain the game freed and handed out again), let go of it rather than
    // write room-object graphics over a player's.
    HashSet<uint> PlayerPrims(IMemory m)
    {
        var set = new HashSet<uint>();
        void Chain(uint flags, uint prim)
        {
            if ((flags & FlagHasPrims) == 0) return;
            int idx = (int)prim;
            if (idx <= 0 || idx >= MaxPrims) return;
            uint p = PrimBufAddr + (uint)idx * 0x34;
            for (int k = 0; k < MaxPrims && p >= PrimBufAddr && p < PrimBufAddr + MaxPrims * 0x34 && set.Add(p); k++) p = m.ReadU32(p);
        }
        for (int i = 0; i < 64; i++) { uint e = EntityAddr(i); Chain(m.ReadU32(e + EFlags), m.ReadU32(e + EPrimIndex)); }
        // (each friend's body's objects, swapped out right now)
        foreach (var r in _remotes)
        {
            if (!r.Active) continue;
            var ents = r.Regions[RegEntities];
            for (int i = 0; i < 64; i++) { int o = i * EntitySize / 4; Chain(ents[o + (int)EFlags / 4], ents[o + (int)EPrimIndex / 4]); }
        }
        return set;
    }

    bool ChainClashes(IMemory m, int index, HashSet<uint> players)
    {
        uint p = PrimBufAddr + (uint)index * 0x34;
        for (int k = 0; k < 256 && p != 0; k++, p = m.ReadU32(p))
            if (players.Contains(p)) return true;
        return false;
    }

    // Objects a follower runs its own copy of (driven by its own player) instead of
    // showing the authority's:
    //  - ones whose effect is game-wide, not in the object: the first hallway's
    //    lightning turns the castle's lights on (scene colours) when Alucard walks in;
    //  - doors: each player opens them for themselves (otherwise a door only reacts to
    //    the player whose game runs the room);
    //  - scenery that places itself from the camera (parallax trees, clouds, distant
    //    walls): the authority's copy sits where its camera is, so on a follower it
    //    moved with the authority's view (and its pointers broke on a handover).
    static readonly string[] LocalFns =
    {
        "EntityBackgroundLightning",
        "EntityRedDoor", "EntityCavernDoor", "EntityCastleDoor", "EntityMistDoor",
        "EntityBackgroundBushes", "EntityBackgroundTrees", "EntityBackgroundPineTrees",
        "EntityBackgroundClouds", "EntityClouds", "EntityBackgroundCastleWall", "EntityBackgroundSkyLand",
        "EntityBgColumnsParallax", "EntityDistantBackgroundTrees", "EntityDistantTreeMaker", "EntityDistantBookshelves",
    };
    readonly Dictionary<uint, bool> _localFn = new();

    // (the streamed copy's update function decides: one of these is never written over
    // our slot, whether or not our own copy is there yet; our spawner makes our own. A
    // copy of theirs would carry pointers into the other game's memory.)
    bool RunsLocally(IMemory m, int slot, ushort streamedId, uint streamedFn)
    {
        uint e = EntityAddr(slot);
        uint fn = m.ReadU16(e + EEntityId) == streamedId && m.ReadU32(e + EPfnUpdate) != 0 ? m.ReadU32(e + EPfnUpdate) : streamedFn;
        return IsLocalFn(fn) || IsLocalFn(streamedFn);
    }

    bool IsLocalFn(uint fn)
    {
        if (fn == 0) return false;
        if (!_localFn.TryGetValue(fn, out bool yes))
        {
            var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
            string name = map.TryGetValue(fn, out var f) ? f.Method.Name : "";
            _localFn[fn] = yes = Array.Exists(LocalFns, n => IsStageFn(name, n));
        }
        return yes;
    }

    // The new-game intro in the castle entrance (the run, the gate, the door, later
    // Death's scene) is scripted the same in both games, and the game doesn't allow
    // pausing while a script has Alucard: nothing is shared then. A follower doesn't
    // run its room's objects, the gate closing behind one player would shut the other
    // out, and the intro's own objects "die" as the script ends. (The Wargs and the
    // rest, between the scripts, are shared as usual.)
    const uint PauseAllowedAddr = 0x8003C8B8u;   // g_PauseAllowed
    // Maria's clock room (MAR) is a cutscene stage of its own: each game plays it alone.
    const ushort Stage_MariaClockRoom = 0x17;
    static bool Scripted(ushort stage) =>
        stage == Stage_MariaClockRoom ||
        (stage == Stage_Entrance && RecompOne.Runtime.Runtime.Mem is { } m && m.ReadU8(PauseAllowedAddr) == 0);

    void OnFriendKill(IMemory m, BinaryReader r)
    {
        var key = (r.ReadUInt16(), r.ReadInt16(), r.ReadInt16());
        int slot = r.ReadByte(); ushort id = r.ReadUInt16(), prm = r.ReadUInt16();
        if (Scripted(key.Item1)) return;
        // remembered even for a room we're not in yet: it stays dead when we get there
        if (Killed(key).Add((slot, id, 0))) RoomLog($"{From} killed slot {slot} (id {id:X}) in room {key}");
    }

    // Remove anything one of us killed this visit (alive in our game, or respawned).
    long _removeCalls;
    void RemoveKilled(CpuContext c, IMemory m)
    {
        // the room we're in right now (_roomKey only updates at the end of the frame,
        // and a stale one would apply the last room's kills to this room's objects)
        var room = CurrentRoom(m);
        if (Scripted(room.Item1) || !_killedByRoom.TryGetValue(room, out var killed)) return;
        if (++_removeCalls % 600 == 1 && killed.Count > 0)
            RoomLog($"kill list for {room}: {string.Join(", ", killed)}");
        foreach (var (slot, id, prm) in killed)
        {
            uint e = EntityAddr(slot);
            // slot + type identify a room object (each layout object has a fixed slot);
            // params is not compared: many enemies change it while alive
            // (not one that's dying right now in our own game: let it finish and drop its loot)
            if (_followNow && _streamed[slot]) continue;   // the authority removes it
            if (m.ReadU16(e + EEntityId) == id && (m.ReadU32(e + EFlags) & FlagDead) == 0)
            {
                RoomLog($"removing slot {slot} (id {id:X}): already killed this visit");
                DestroyEntitySafe(c, m, e);
                _seenId[slot] = 0;
            }
        }
    }

    // ---------------------------------------------------------------- authority: send

    byte[] BuildRoomStream(IMemory m)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
        {
            w.Write(_roomKey.Item1); w.Write(_roomKey.Item2); w.Write(_roomKey.Item3);
            w.Write((short)m.ReadU16(ScrollXAddr)); w.Write((short)m.ReadU16(ScrollYAddr));
            // room lock (boss fights, ambushes) and the camera bounds that come with it
            w.Write(m.ReadU32(RoomLockAddr));
            for (uint a = TilemapX; a <= TilemapH; a += 4) w.Write(m.ReadU32(a));
            var used = new List<int>();
            for (int i = StageFirst; i < 256; i++)
                if (IsGameplay(m, EntityAddr(i)) || (_collidesId[i] != 0 && _collidesId[i] == m.ReadU16(EntityAddr(i) + EEntityId))) used.Add(i);
            w.Write((byte)used.Count);
            foreach (int i in used)
            {
                w.Write((byte)i);
                foreach (uint word in Ram(m, EntityAddr(i), EntitySize)) w.Write(word);
                uint e = EntityAddr(i);
                int first = (int)m.ReadU32(e + EPrimIndex);
                var prims = new List<uint>();
                if ((m.ReadU32(e + EFlags) & FlagHasPrims) != 0 && first > 0)
                    for (uint p = PrimBufAddr + (uint)first * 0x34; p != 0 && prims.Count < 255; p = m.ReadU32(p)) prims.Add(p);
                w.Write((byte)prims.Count);
                foreach (uint p in prims)
                {
                    w.Write(p);   // its address here: objects keep pointers to their prims
                    for (uint k = 4; k < 0x34; k += 4) w.Write(m.ReadU32(p + k)); // not the next pointer
                }
            }
            // the objects' collision calls this frame (platforms, lifts, blocks), so the
            // friend's game can push / carry its own player the same way
            int calls = Math.Min(_platformCalls.Count, 255);
            w.Write((byte)calls);
            for (int k = 0; k < calls; k++)
            {
                var pc = _platformCalls[k];
                w.Write(pc.Slot); w.Write(pc.W); w.Write(pc.H); w.Write(pc.Flags);
            }
            _platformCalls.Clear();
        }
        using var packed = new MemoryStream();
        using (var z = new DeflateStream(packed, CompressionLevel.Fastest, true)) raw.WriteTo(z);
        return packed.ToArray();
    }

    // ---------------------------------------------------------------- follower: apply

    void ApplyRoomStream(CpuContext c, IMemory m)
    {
        var data = _owner?.Link.Room;
        if (data == null) return;
        _appliedRoomFrame = _owner!.Link.RoomFrame;

        BinaryReader r;
        try
        {
            using var z = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            var buf = new MemoryStream();
            z.CopyTo(buf);
            buf.Position = 0;
            r = new BinaryReader(buf);
        }
        catch { return; }

        var room = CurrentRoom(m);
        if (r.ReadUInt16() != room.Item1 || r.ReadInt16() != room.Item2 || r.ReadInt16() != room.Item3) return;
        short dx = (short)(r.ReadInt16() - (short)m.ReadU16(ScrollXAddr));
        short dy = (short)(r.ReadInt16() - (short)m.ReadU16(ScrollYAddr));
        MirrorRoomLock(m, r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());

        // (re)applied every frame, also without a new packet, since our spawner keeps
        // writing into the room's slots underneath
        var present = new bool[256];
        var runLocal = new bool[256];
        _playerPrims = PlayerPrims(m);
        int count = r.ReadByte();
        var words = new uint[EntitySize / 4];
        for (int n = 0; n < count; n++)
        {
            int slot = r.ReadByte();
            for (int k = 0; k < words.Length; k++) words[k] = r.ReadUInt32();
            int primCount = r.ReadByte();
            var prims = new uint[primCount][];
            var primAddrs = new uint[primCount];
            for (int p = 0; p < primCount; p++) { primAddrs[p] = r.ReadUInt32(); prims[p] = new uint[12]; for (int k = 0; k < 12; k++) prims[p][k] = r.ReadUInt32(); }
            present[slot] = true;
            if (!StreamTakesSlot(slot, (ushort)(words[EEntityId / 4] >> 16))) continue;   // ours for now
            if (RunsLocally(m, slot, (ushort)(words[EEntityId / 4] >> 16), words[EPfnUpdate / 4])) { runLocal[slot] = true; continue; }
            WriteStreamedEntity(c, m, slot, words, prims, primAddrs, dx, dy);
        }
        TickLocalSlots(m, present);
        TickOurKills(m);
        for (int i = StageFirst; i < 256; i++)
        {
            _streamed[i] = present[i] && !_local.ContainsKey(i) && !runLocal[i];
            if (_streamed[i]) _everStreamed[i] = true;
        }

        // their objects' collision calls, for our player: stand on their platforms, ride
        // their lifts, bump into their blocks
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            uint collide = StageFn(m, "GetPlayerCollisionWith");
            int calls = r.ReadByte();
            for (int k = 0; k < calls; k++)
            {
                int slot = r.ReadByte(); ushort w = r.ReadUInt16(), h = r.ReadUInt16(), f = r.ReadUInt16();
                if (collide == 0 || !_streamed[slot]) continue;
                _replayingCollision = true;   // not "skip: the authority runs this one"
                try { Call(c, m, collide, EntityAddr(slot), w, h, f); }
                finally { _replayingCollision = false; }
            }
        }
        for (int i = StageFirst; i < 256; i++)
        {
            if (present[i] || _local.ContainsKey(i)) continue;
            // 192+ is effects (damage numbers, sparks, our player's stuff): only clear
            // what came from the stream there
            if (i >= HitEnd && !_everStreamed[i]) continue;
            _everStreamed[i] = false;
            uint e = EntityAddr(i);
            if (!_streamPrims.ContainsKey(i) && !IsGameplay(m, e)) continue;   // our scenery: leave it
            if (!_streamed[i] && m.ReadU16(e + EEntityId) != 0) RoomLog($"follower: clearing our slot {i} (id {m.ReadU16(e + EEntityId):X}), not in {_owner?.Name}'s stream");
            if (_streamPrims.ContainsKey(i)) ReleaseStreamPrims(c, m, i);
            else if ((m.ReadU32(e + EFlags) & FlagHasPrims) != 0) { DestroyEntitySafe(c, m, e); continue; }
            m.ZeroRange(e, EntitySize);
        }
    }

    void WriteStreamedEntity(CpuContext c, IMemory m, int slot, uint[] words, uint[][] prims, uint[] primAddrs, short dx, short dy)
    {
        uint e = EntityAddr(slot);
        // our own version of this slot (if any) owns prims we didn't make: free them
        if (!_streamPrims.ContainsKey(slot) && (m.ReadU32(e + EFlags) & FlagHasPrims) != 0 && m.ReadU32(e + EPrimIndex) != 0)
            DestroyEntitySafe(c, m, e);

        int local = 0;
        if (prims.Length > 0)
        {
            byte type = SafePrimType((byte)(prims[0][0] >> 24)); // Primitive.type @ 0x07
            if (_streamPrims.TryGetValue(slot, out var held) && _playerPrims != null && ChainClashes(m, held.Index, _playerPrims))
            {
                RoomLog($"slot {slot}'s stream prims ({held.Index}) are a player's now: letting go of them");
                _streamPrims.Remove(slot);
                m.WriteU32(e + EPrimIndex, 0);
                m.WriteU32(e + EFlags, m.ReadU32(e + EFlags) & ~FlagHasPrims);
            }
            if (!_streamPrims.TryGetValue(slot, out var chain) || chain.Count != prims.Length || chain.Type != type)
            {
                ReleaseStreamPrims(c, m, slot);
                int idx = (int)Call(c, m, FnAllocPrims, (uint)(type & 0xF), (uint)prims.Length);
                if (idx > 0) _streamPrims[slot] = chain = (idx, prims.Length, type);
                else if (_frame - _lastPrimWarn > 120) { _lastPrimWarn = _frame; RoomLog($"out of graphics prims: can't draw slot {slot} ({prims.Length} prims, {_streamPrims.Count} streamed objects hold prims)"); }
            }
            if (_streamPrims.TryGetValue(slot, out chain))
            {
                local = chain.Index;
                uint p = PrimBufAddr + (uint)chain.Index * 0x34;
                for (int k = 0; k < prims.Length && p != 0; k++, p = m.ReadU32(p))
                {
                    byte theirs = (byte)(prims[k][0] >> 24);
                    if (SafePrimType(theirs) != theirs)
                    {
                        // a drawing-environment prim points into the authority's memory (the
                        // game writes through it when the prim is freed): don't copy it,
                        // keep a hidden plain prim in its place
                        m.WriteU8(p + 7, PrimTile);
                        m.WriteU16(p + 0x32, (ushort)(m.ReadU16(p + 0x32) | DrawHide));
                        continue;
                    }
                    for (int w = 0; w < 12; w++) m.WriteU32(p + 4 + (uint)(w * 4), prims[k][w]);
                    ShiftPrim(m, p, dx, dy);
                }
            }
        }
        else ReleaseStreamPrims(c, m, slot);

        // our own hit timers stay ours: they're what stops one swing of ours hitting
        // the same thing every frame
        Span<byte> timers = stackalloc byte[11];
        for (int t = 0; t < 11; t++) timers[t] = m.ReadU8(e + EHitTimers + (uint)t);
        bool sameThing = m.ReadU16(e + EEntityId) == (ushort)(words[EEntityId / 4] >> 16);
        // their prim pointers (ext.prim etc.) -> our copies of those prims, so the
        // object still works if we take it over
        if (prims.Length > 0)
        {
            var mine = new uint[prims.Length];
            uint lp = _streamPrims.TryGetValue(slot, out var ch) ? PrimBufAddr + (uint)ch.Index * 0x34 : 0;
            for (int k = 0; k < mine.Length && lp != 0; k++, lp = m.ReadU32(lp)) mine[k] = lp;
            for (int wi = 0x7C / 4; wi < words.Length; wi++)
            {
                int at = Array.IndexOf(primAddrs, words[wi]);
                if (at >= 0) words[wi] = mine[at];   // 0 if we have no copy: _noPrims below
            }
            _noPrims[slot] = mine[0] == 0;
        }
        else _noPrims[slot] = false;
        words.CopyTo(Ram(m, e, EntitySize));
        if (sameThing) for (int t = 0; t < 11; t++) m.WriteU8(e + EHitTimers + (uint)t, timers[t]);
        m.WriteU32(e + EPrimIndex, (uint)local);
        if (local == 0) m.WriteU32(e + EFlags, m.ReadU32(e + EFlags) & ~FlagHasPrims);

        FixDropIcon(c, m, e);

        // positions are screen coordinates of the authority's camera; move them to ours
        uint flags = m.ReadU32(e + EFlags);
        ushort parallax = m.ReadU16(e + EUnk68);
        if (parallax != 0)
        {
            m.WriteU32(e + EPosX, (uint)((int)m.ReadU32(e + EPosX) + dx * parallax * 0x100));
            m.WriteU32(e + EPosY, (uint)((int)m.ReadU32(e + EPosY) + dy * parallax * 0x100));
        }
        else if ((flags & FlagCameraLocked) != 0)
        {
            m.WriteU16(e + EPosXHi, (ushort)(m.ReadU16(e + EPosXHi) + dx));
            m.WriteU16(e + EPosYHi, (ushort)(m.ReadU16(e + EPosYHi) + dy));
        }
    }

    // Prims are screen coordinates too (unless drawn in absolute/menu space).
    static void ShiftPrim(IMemory m, uint p, short dx, short dy)
    {
        if ((dx == 0 && dy == 0) || (m.ReadU16(p + 0x32) & DrawAbsPos) != 0) return;
        int type = m.ReadU8(p + 7) & 0xF;
        uint[] xs = type switch
        {
            1 or 6 => new uint[] { 0x08 },                              // TILE, SPRT
            2 => new uint[] { 0x08, 0x14 },                             // LINE_G2
            5 => new uint[] { 0x08, 0x14, 0x20 },                       // GT3
            3 or 4 => new uint[] { 0x08, 0x14, 0x20, 0x2C },            // G4, GT4
            _ => Array.Empty<uint>(),
        };
        foreach (uint x in xs)
        {
            m.WriteU16(p + x, (ushort)(m.ReadU16(p + x) + dx));
            m.WriteU16(p + x + 2, (ushort)(m.ReadU16(p + x + 2) + dy));
        }
    }

    void ReleaseStreamPrims(CpuContext c, IMemory m, int slot)
    {
        if (!_streamPrims.Remove(slot, out var chain)) return;
        if (CheckPrimChain(m, chain.Index, $"slot {slot}'s stream copy")) Call(c, m, FnFreePrims, (uint)chain.Index);
    }

    static byte SafePrimType(byte t) => (t & 0xF) is PrimEnv or PrimNone ? PrimTile : t;

    // The game frees a prim chain by following next pointers to the end: a chain that
    // loops (or runs off the buffer) would hang it. Check before anything frees one,
    // and cut a bad chain where it goes wrong (false: don't free it, it's been dealt with).
    bool CheckPrimChain(IMemory m, int index, string what)
    {
        if (index <= 0 || index >= MaxPrims) return false;
        var seen = new HashSet<uint>();
        uint p = PrimBufAddr + (uint)index * 0x34, prev = 0;
        while (p != 0)
        {
            bool inBuf = p >= PrimBufAddr && p < PrimBufAddr + MaxPrims * 0x34 && (p - PrimBufAddr) % 0x34 == 0;
            if (!inBuf || !seen.Add(p))
            {
                RoomLog($"broken graphics chain ({what}, prim {index}, {(inBuf ? "loops" : $"bad link 0x{p:X8}")} after {seen.Count}): cut");
                if (prev != 0) m.WriteU32(prev, 0);
                foreach (uint q in seen) m.WriteU8(q + 7, PrimNone);   // freed by hand
                return false;
            }
            // an environment prim with no environment: freeing it writes through null
            if ((m.ReadU8(p + 7) & 0xF) == PrimEnv && m.ReadU32(p + 0x10) == 0) m.WriteU8(p + 7, PrimTile);
            prev = p; p = m.ReadU32(p);
        }
        return true;
    }

    void DestroyEntitySafe(CpuContext c, IMemory m, uint e)
    {
        if ((m.ReadU32(e + EFlags) & FlagHasPrims) != 0 && !CheckPrimChain(m, (int)m.ReadU32(e + EPrimIndex), $"slot {(e - EntitiesAddr) / EntitySize}"))
        {
            m.WriteU32(e + EFlags, m.ReadU32(e + EFlags) & ~FlagHasPrims);   // (already freed by hand)
            m.WriteU32(e + EPrimIndex, 0);
        }
        Call(c, m, FnDestroyEntity, e);
    }
}
