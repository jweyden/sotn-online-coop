using System;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

// Phase 3 step 2: hits and pickups while one game shows the other's room (follower).
//
// The follower's objects are copies of the authority's, overwritten every frame, so
// whatever its own hit detection does to them is lost. Instead:
//  - HITS: the follower's player hits the copies as usual (sound, damage numbers,
//    EXP on a kill all happen locally, for the right player); what that hit did to
//    the object (damage, hit flags, death) is sent to the authority, which applies it
//    to the real one. The authority's simulation of the friend no longer damages
//    anything while it's the authority (that would count the friend's hits twice and
//    give drops to the wrong game).
//  - PICKUPS: an item the follower's player touches stops being streamed and runs in
//    the follower's game, so the follower collects it (hearts and subweapons go to
//    the right player; gold, items and relics are shared anyway). The authority is
//    told and removes it. The friend's simulated body never collects anything.
//  - DROPS from the follower's kills go to the authority (Msg.Drop: the fresh entity,
//    just made by hit detection, no graphics yet), which spawns it for both of us;
//    whoever touches it first gets it.
public partial class OnlineCoop
{
    const uint EPfnUpdate = 0x28, EHitParams = 0x44, EHitFlags = 0x48, ENFramesInv = 0x49;
    const uint EStunFrames = 0x58, ENextPart = 0x60, EHitEffect = 0x6A;
    const int HitFirst = 64, HitEnd = 192, EffectsFirst = 160;
    const uint DeathFlags = 0x100000u | 0x8000u | 0x4000u | FlagDead | 0xFu;

    // what each slot looked like right before a hit detection pass
    readonly byte[] _preHit = new byte[256 * (int)EntitySize];
    enum HitMode { None, Claim, Suppress }
    HitMode _hitMode;

    // follower: slots our own game runs for now (an item we're collecting, our drops),
    // by the entity id they had; the stream doesn't touch them while that holds
    readonly Dictionary<int, (ushort Id, int Frames)> _local = new();

    // authority: the follower's hits / pickups, applied before objects update
    readonly Queue<(( ushort, short, short) Room, int Slot, ushort Id, short Damage, byte HitFlags, ushort HitParams, byte Inv, short Stun, bool Dead)> _claims = new();
    readonly Queue<((ushort, short, short) Room, int Slot, ushort Id)> _collected = new();

    // ---------------------------------------------------------------- around HitDetection

    void BeforeHits(IMemory m)
    {
        _hitMode = HitMode.None;
        if (!RoomSync || !_close || _net.Status != NetSession.State.Connected) return;
        bool following = _follower && CurrentRoom(m) == _roomKey;
        if (following && !_inP2) _hitMode = HitMode.Claim;          // our player hits the copies
        else if (_authority && _inP2) _hitMode = HitMode.Suppress;    // the friend's body, in our room
        if (_hitMode == HitMode.None) return;
        var live = Ram(m, EntityAddr(0), 256 * (int)EntitySize);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(live).CopyTo(_preHit);
    }

    byte PreU8(int slot, uint off) => _preHit[slot * EntitySize + off];
    ushort PreU16(int slot, uint off) => BitConverter.ToUInt16(_preHit, (int)(slot * EntitySize + off));
    uint PreU32(int slot, uint off) => BitConverter.ToUInt32(_preHit, (int)(slot * EntitySize + off));

    void AfterHits(CpuContext c, IMemory m)
    {
        var mode = _hitMode;
        _hitMode = HitMode.None;
        if (mode == HitMode.Claim) SendClaims(m);
        else if (mode == HitMode.Suppress) UndoFriendHits(m);
    }

    void SendClaims(IMemory m)
    {
        var room = CurrentRoom(m);
        for (int i = HitFirst; i < HitEnd; i++)
        {
            uint e = EntityAddr(i);
            ushort id = m.ReadU16(e + EEntityId);
            if (id == 0 || id != PreU16(i, EEntityId)) continue;
            if (!_streamed[i]) continue;   // one of ours already

            byte flagsBefore = PreU8(i, EHitFlags), flagsNow = m.ReadU8(e + EHitFlags);
            short hpBefore = (short)PreU16(i, EHitPoints), hpNow = (short)m.ReadU16(e + EHitPoints);
            bool deadNow = (m.ReadU32(e + EFlags) & FlagDead) != 0 && (PreU32(i, EFlags) & FlagDead) == 0;
            bool touched = (flagsNow & ~flagsBefore & 0x7F) != 0;
            if (!touched && hpNow >= hpBefore && !deadNow) continue;

            if (IsPickup(m, e))
            {
                // ours to collect: run it here from now on, tell the authority
                if ((flagsNow & 0x7F) == 0) continue;
                MakeLocal(i, id);
                Send(_owner?.Link, Msg.Collected, w => { WriteRoom(w, room); w.Write((byte)i); w.Write(id); });
                RoomLog($"collecting slot {i} (id {id:X}) here");
                continue;
            }
            short dmg = (short)Math.Max(0, hpBefore - hpNow);
            byte hf = flagsNow; ushort hp = m.ReadU16(e + EHitParams);
            byte inv = m.ReadU8(e + ENFramesInv); short stun = (short)m.ReadU16(e + EStunFrames);
            Send(_owner?.Link, Msg.Hit, w =>
            {
                WriteRoom(w, room); w.Write((byte)i); w.Write(id);
                w.Write(dmg); w.Write(hf); w.Write(hp); w.Write(inv); w.Write(stun); w.Write(deadNow);
            });
            if (deadNow) { _ourKills[i] = (id, 0); Killed(room).Add((i, id, 0)); RoomLog($"our hit kills slot {i} (id {id:X}), sent to {_owner?.Name}"); }
            else if (m.ReadU16(e + EEnemyId) < FirstRealEnemy) RoomLog($"our hit on object slot {i} (id {id:X}, hit flags {hf:X}), sent to {_owner?.Name}");
        }
        // whatever our hits spawned (blood, soul orbs, damage numbers) is ours; drops go
        // to the authority so they're there for both of us
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        for (int i = EffectsFirst; i < 256; i++)
        {
            uint e = EntityAddr(i);
            ushort id = m.ReadU16(e + EEntityId);
            if (id == 0 || PreU16(i, EEntityId) != 0) continue;
            if (i < HitEnd && m.ReadU32(e + EPrimIndex) == 0 && IsPickup(m, e))
            {
                var words = Ram(m, e, EntitySize).ToArray();
                words[EPosX / 4] = (uint)((int)words[EPosX / 4] + (sx << 16));   // to world
                words[EPosY / 4] = (uint)((int)words[EPosY / 4] + (sy << 16));
                Send(_owner?.Link, Msg.Drop, w => { WriteRoom(w, room); foreach (uint word in words) w.Write(word); });
                RoomLog($"our kill dropped id {id:X} params {m.ReadU16(e + EParams):X}: sent to {_owner?.Name}");
                m.ZeroRange(e, EntitySize);
                continue;
            }
            MakeLocal(i, id);
        }
    }

    // The friend's simulated body attacked our (authoritative) objects: their own game
    // decides what their hits do and tells us, so take back what the sim did.
    void UndoFriendHits(IMemory m)
    {
        for (int i = HitFirst; i < HitEnd; i++)
        {
            uint e = EntityAddr(i);
            if (m.ReadU16(e + EEntityId) != PreU16(i, EEntityId)) continue;
            if (i >= EffectsFirst && PreU16(i, EEntityId) == 0) continue;
            m.WriteU16(e + EHitPoints, PreU16(i, EHitPoints));
            m.WriteU32(e + EFlags, PreU32(i, EFlags));
            m.WriteU16(e + EHitParams, PreU16(i, EHitParams));
            // keep "it hit the player" (0x80): that's the friend getting hit, not hitting
            m.WriteU8(e + EHitFlags, (byte)((PreU8(i, EHitFlags) & 0x7F) | (m.ReadU8(e + EHitFlags) & 0x80)));
            m.WriteU8(e + ENFramesInv, PreU8(i, ENFramesInv));
            m.WriteU16(e + EStunFrames, PreU16(i, EStunFrames));
            m.WriteU16(e + EHitEffect, PreU16(i, EHitEffect));
        }
        // and anything their hits spawned (drops, blood): just made, no prims yet
        for (int i = EffectsFirst; i < HitEnd; i++)
            if (PreU16(i, EEntityId) == 0 && m.ReadU16(EntityAddr(i) + EEntityId) != 0)
                m.ZeroRange(EntityAddr(i), EntitySize);
    }

    // ---------------------------------------------------------------- authority: apply

    void OnFriendHit(BinaryReader r)
    {
        var room = ReadRoom(r);
        int slot = r.ReadByte(); ushort id = r.ReadUInt16();
        short dmg = r.ReadInt16(); byte hf = r.ReadByte(); ushort prm = r.ReadUInt16();
        byte inv = r.ReadByte(); short stun = r.ReadInt16(); bool dead = r.ReadBoolean();
        _claims.Enqueue((room, slot, id, dmg, hf, prm, inv, stun, dead));
    }

    readonly Queue<((ushort, short, short) Room, uint[] Words)> _drops = new();

    void OnFriendDrop(BinaryReader r)
    {
        var room = ReadRoom(r);
        var words = new uint[EntitySize / 4];
        for (int k = 0; k < words.Length; k++) words[k] = r.ReadUInt32();
        _drops.Enqueue((room, words));
    }

    void OnFriendCollected(BinaryReader r)
    {
        var room = ReadRoom(r);
        int slot = r.ReadByte(); ushort id = r.ReadUInt16();
        _collected.Enqueue((room, slot, id));
        if (slot < EffectsFirst) Killed(room).Add((slot, id, 0));   // a placed item: gone for the visit
    }

    // Once a frame before objects update (g_api.o.Update), in whichever role.
    void ApplyClaims(CpuContext c, IMemory m)
    {
        var room = CurrentRoom(m);
        while (_collected.TryDequeue(out var k))
        {
            if (k.Room != room) continue;
            uint e = EntityAddr(k.Slot);
            if (m.ReadU16(e + EEntityId) != k.Id || _local.ContainsKey(k.Slot)) continue;
            if (_follower && _streamed[k.Slot]) continue;   // the authority removes it (and its prims are our stream copies)
            RoomLog($"a friend collected slot {k.Slot} (id {k.Id:X})");
            DestroyEntitySafe(c, m, e);
        }
        while (_drops.TryDequeue(out var d))
        {
            if (d.Room != room || _follower) continue;
            int slot = EffectsFirst;
            while (slot < HitEnd && (m.ReadU16(EntityAddr(slot) + EEntityId) != 0 || _pvpSlots.Contains(slot))) slot++;
            if (slot >= HitEnd) { RoomLog("no free slot for a friend's drop"); continue; }
            uint e = EntityAddr(slot);
            d.Words.CopyTo(Ram(m, e, EntitySize));
            int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
            m.WriteU32(e + EPosX, (uint)((int)m.ReadU32(e + EPosX) - (sx << 16)));   // to our screen
            m.WriteU32(e + EPosY, (uint)((int)m.ReadU32(e + EPosY) - (sy << 16)));
            RoomLog($"a friend's kill dropped id {m.ReadU16(e + EEntityId):X} (slot {slot})");
        }
        while (_claims.TryDequeue(out var h))
        {
            if (h.Room != room || _follower) continue;   // a follower has nothing real to hit
            uint e = EntityAddr(h.Slot);
            if (m.ReadU16(e + EEntityId) != h.Id || (m.ReadU32(e + EFlags) & FlagDead) != 0) continue;
            short hp = (short)m.ReadU16(e + EHitPoints);
            if (h.Damage > 0 && hp != 0x7FFF && hp != 0x7FFE) { hp -= h.Damage; m.WriteU16(e + EHitPoints, (ushort)hp); }
            m.WriteU8(e + EHitFlags, (byte)(m.ReadU8(e + EHitFlags) | h.HitFlags));
            m.WriteU16(e + EHitParams, h.HitParams);
            m.WriteU8(e + ENFramesInv, h.Inv);
            if (h.Stun > (short)m.ReadU16(e + EStunFrames)) m.WriteU16(e + EStunFrames, (ushort)h.Stun);
            if (m.ReadU16(e + EHitEffect) == 0 && h.HitFlags != 0) m.WriteU16(e + EHitEffect, m.ReadU16(e + EPalette));
            if (m.ReadU16(e + EEnemyId) < FirstRealEnemy) RoomLog($"a friend hit object slot {h.Slot} (id {h.Id:X}, hit flags {h.HitFlags:X})");
            if (h.Dead || (h.Damage > 0 && hp <= 0 && m.ReadU16(e + EEnemyId) != 0))
            {
                KillEntity(m, e, h.Inv);
                // it turns into its explosion before our next hit check sees it dead, so
                // record the kill here (it must never respawn this visit)
                var killedHere = Killed(room);
                if (h.Slot < EffectsFirst && killedHere.Add((h.Slot, h.Id, 0)))
                {
                    int slot = h.Slot; ushort id = h.Id;
                    Send(Msg.Killed, w => { WriteRoom(w, room); w.Write((byte)slot); w.Write(id); w.Write((ushort)0); });
                }
                RoomLog($"a friend's hit kills slot {h.Slot} (id {h.Id:X})");
            }
        }
    }

    // What HitDetection does to a killed enemy (every part of it), minus the EXP and
    // drops: those happened in the killer's game.
    static void KillEntity(IMemory m, uint e, byte inv)
    {
        uint p = e;
        for (int guard = 0; guard < 32; guard++)
        {
            uint f = (m.ReadU32(p + EFlags) | DeathFlags) & ~0x20000000u;
            m.WriteU32(p + EFlags, f);
            if (m.ReadU16(p + EHitEffect) == 0) m.WriteU16(p + EHitEffect, m.ReadU16(p + EPalette));
            m.WriteU8(p + ENFramesInv, inv);
            p = m.ReadU32(p + ENextPart);
            if (p == 0 || p == e || p < EntityAddr(0) || p >= EntityAddr(256)) break;
        }
    }

    // ---------------------------------------------------------------- follower: local slots

    void MakeLocal(int slot, ushort id)
    {
        _local[slot] = (id, 0);
        _streamed[slot] = false;
        // the prims we made for its stream copy now belong to it (it frees them itself)
        _streamPrims.Remove(slot);
    }

    // Called by ApplyRoomStream for each slot the authority sends: false = leave ours.
    bool StreamTakesSlot(int slot, ushort streamedId)
    {
        if (!_local.TryGetValue(slot, out var l)) return true;
        if (streamedId == l.Id && l.Frames < 600) return false;   // still the thing we took
        _local.Remove(slot);
        return true;
    }

    // Once a frame (follower): forget local slots the authority no longer has and we're done with.
    void TickLocalSlots(IMemory m, bool[] present)
    {
        foreach (var slot in new List<int>(_local.Keys))
        {
            var l = _local[slot];
            bool ours = m.ReadU16(EntityAddr(slot) + EEntityId) == l.Id;
            if ((!present[slot] && !ours) || ++l.Frames > 600) _local.Remove(slot);
            else _local[slot] = l;
        }
    }

    // Something we killed stays dead in our copy until the authority's stream shows it
    // dying too (so a second hit can't kill it, and pay out, again).
    readonly Dictionary<int, (ushort Id, int Frames)> _ourKills = new();

    void TickOurKills(IMemory m)
    {
        foreach (var slot in new List<int>(_ourKills.Keys))
        {
            var k = _ourKills[slot];
            uint e = EntityAddr(slot);
            if (m.ReadU16(e + EEntityId) != k.Id || ++k.Frames > 120) { _ourKills.Remove(slot); continue; }
            m.WriteU32(e + EFlags, m.ReadU32(e + EFlags) | FlagDead);   // hit detection skips it
            _ourKills[slot] = k;
        }
    }

    // ---------------------------------------------------------------- helpers

    // Pickups, by the stage function that runs them.
    readonly Dictionary<uint, bool> _pickupFn = new();
    static readonly string[] PickupFns =
        { "EntityPrizeDrop", "EntityEquipItemDrop", "EntityRelicOrb", "EntityLifeUpSpawn", "EntityPersistentItemDrop", "EntityHeartDrop" };

    bool IsPickup(IMemory m, uint e)
    {
        uint fn = m.ReadU32(e + EPfnUpdate);
        if (fn == 0) return false;
        if (!_pickupFn.TryGetValue(fn, out bool yes))
        {
            var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
            string name = map.TryGetValue(fn, out var f) ? f.Method.Name : "";
            yes = Array.Exists(PickupFns, p => IsStageFn(name, p));
            _pickupFn[fn] = yes;
        }
        return yes;
    }

    static void WriteRoom(BinaryWriter w, (ushort, short, short) room) { w.Write(room.Item1); w.Write(room.Item2); w.Write(room.Item3); }
    static (ushort, short, short) ReadRoom(BinaryReader r) => (r.ReadUInt16(), r.ReadInt16(), r.ReadInt16());
}
