using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Memory;

// PvP: the friends' attacks can hurt you. The host's setting counts for every game.
//
// Each game decides when its own player is hit (by a friend's simulated body), using
// the game's normal "an enemy touched you" path: just before our hit detection,
// copies of every friend's active attacks (weapon swings, subweapons, spells) go
// into spare effect slots as harmless contact-damage objects. Hit detection hands
// their attack and element to our player's own damage code (defence, knockback,
// invincibility frames, sounds). The copies go again before a friend's frame runs.
//
// No other game hits its copy of the victim (that's decided in the victim's game),
// so the victim tells them all: hit flags, damage and where the attack came from.
// Each puts the same hit on its copy of the victim (with a stand-in attacker at that
// spot for the knockback direction), and the copy's own damage code plays the
// stagger, sound and damage number there too.
public partial class OnlineCoop
{
    public bool Pvp = true;
    const uint EHitboxState = 0x3C, EParent = 0x5C, EUnkB8 = 0xB8, EHitboxOffX = 0x10, EAttack = 0x40, EAttackElement = 0x42;
    const ushort StepHit = 10; // Player_Hit
    const int PvpFirst = 160, PvpEnd = 192;

    bool PvpOn => _net.Status == NetSession.State.Connected &&
                  (_net.IsHost ? Pvp : _friends.Find(f => f.Number == 0) is { Valid: true } host && (host.Peer.Flags & 8) != 0);

    readonly List<int> _pvpSlots = new();
    readonly Dictionary<int, (Friend Who, int Slot, ushort Id)> _pvpSource = new();   // copy slot -> the friend's object it copies
    const int PvpSpawnProtect = 180;   // frames after the friend appears in our room

    // Before our player's hit detection.
    void InsertFriendAttacks(IMemory m)
    {
        RemoveFriendAttacks(m);
        if (_resim) return;
        // (hit detection only ever sets our "touched" flag; clear it first, so after the
        // check it means this frame's touch, not a stale one from an earlier hit)
        m.WriteU8(EntitiesAddr + EHitFlags, 0);
        if (!PvpOn || InCutscene(m))
        {
            string off = !PvpOn ? "PvP is off" : $"cutscene (demo timer {m.ReadU32(DemoTimerAddr)}, pause allowed {m.ReadU8(PauseAllowedFlagAddr)})";
            if (off != _pvpWhy) { PvpLog($"not armed: {off}"); _pvpWhy = off; }
            return;
        }
        int free = PvpFirst;
        foreach (var f in _friends)
        {
            string why = !IsPresent(f.Body) ? "not here" : !f.Valid ? "no snapshot" :
                f.Peer.InMenu ? "in a menu" : (f.Peer.Flags & 4) != 0 ? "dead" :
                // spawn protection: loading into the same save room puts everyone on the same spot
                _vsyncs - f.ArrivedAt < PvpSpawnProtect ? "spawn protection" : "";
            string key = $"{f.Name}: {(why == "" ? "armed" : $"not armed: {why}")}";
            if (!_pvpArmed.TryGetValue(f.Number, out var was) || was != key) { PvpLog(key); _pvpArmed[f.Number] = key; }
            if (why == "") free = CopyAttacks(m, f, free);
        }
        _pvpWhy = "";
        if (_pvpSlots.Count != _pvpLastCopies)
        {
            if (_pvpSlots.Count > 0)
                PvpLog($"copied {_pvpSlots.Count} attack(s): " + string.Join(", ", _pvpSlots.ConvertAll(s =>
                    $"{_pvpSource[s].Who.Name}'s slot {_pvpSource[s].Slot} id {_pvpSource[s].Id:X} at {(short)m.ReadU16(EntityAddr(s) + EPosXHi)},{(short)m.ReadU16(EntityAddr(s) + EPosYHi)}")) +
                    $"; us at {(short)m.ReadU16(EntitiesAddr + EPosXHi)},{(short)m.ReadU16(EntitiesAddr + EPosYHi)}" + (free >= PvpEnd ? " (slots full)" : ""));
            _pvpLastCopies = _pvpSlots.Count;
        }
    }
    readonly Dictionary<int, string> _pvpArmed = new();

    // One friend's attacks into copy slots from `free` on; returns the next free one.
    int CopyAttacks(IMemory m, Friend fr, int free)
    {
        var buf = fr.Body.Regions[RegEntities];
        int dx = fr.Body.ScrollX - (short)m.ReadU16(ScrollXAddr), dy = fr.Body.ScrollY - (short)m.ReadU16(ScrollYAddr);
        int words = EntitySize / 4;
        for (int k = 1; k < 48; k++)
        {
            if (k == FamiliarSlot) continue;   // the familiar fights enemies only
            int o = k * words;
            ushort srcId = (ushort)(buf[o + (int)(EEntityId / 4)] >> 16);
            if (srcId >= 0xD0 && srcId < 0xE0) continue;   // nor do its own objects (0xD0..0xDF; 0xE0+ are the weapons)
            if (o + words > buf.Length) break;
            uint w15 = buf[o + 0x3C / 4], w16 = buf[o + 0x40 / 4], w17 = buf[o + 0x44 / 4];
            ushort state = (ushort)w15; short attack = (short)w16;
            byte hw = (byte)(w17 >> 16), hh = (byte)(w17 >> 24);
            if ((state & 2) == 0 || attack <= 0 || hw == 0 || hh == 0) continue;   // not hitting anything

            while (free < PvpEnd && m.ReadU16(EntityAddr(free) + EEntityId) != 0) free++;
            if (free >= PvpEnd) break;
            uint e = EntityAddr(free);
            buf.AsSpan(o, words).CopyTo(Ram(m, e, EntitySize));
            m.WriteU16(e + EPosXHi, (ushort)(m.ReadU16(e + EPosXHi) + dx));
            m.WriteU16(e + EPosYHi, (ushort)(m.ReadU16(e + EPosYHi) + dy));
            if (m.ReadU16(e + EEntityId) == 0) m.WriteU16(e + EEntityId, 0x3FF);   // so the slot reads as taken
            m.WriteU32(e + EPfnUpdate, 0);              // never updated
            m.WriteU16(e + EHitboxState, 1);            // touches the player only
            m.WriteU16(e + EEnemyId, 0);
            m.WriteU16(e + EHitPoints, 0x7FFF);         // can't be hurt back
            m.WriteU32(e + EFlags, 0);
            m.WriteU8(e + EHitFlags, 0);
            m.WriteU16(e + EHitParams, 0);
            m.WriteU32(e + EParent, 0);
            m.WriteU32(e + ENextPart, 0);
            m.WriteU32(e + EPrimIndex, 0);
            m.WriteU16(e + EAnimSet, 0);                // not drawn
            for (uint t = 0; t < 11; t++) m.WriteU8(e + EHitTimers + t, 0);
            _pvpSlots.Add(free);
            _pvpSource[free] = (fr, k, (ushort)(buf[o + (int)(EEntityId / 4)] >> ((int)(EEntityId % 4) * 8)));
        }
        return free;
    }
    string _pvpWhy = "";
    int _pvpLastCopies;

    // ---------------------------------------------------------------- victim -> attacker

    // Right after OUR player's hit detection: did one of the friend's attacks touch us?
    // (Remembered, and only reported once Alucard's own update has taken the hit:
    // while he's invincible a touch does nothing, and reporting it would make our copy
    // in their game stagger for nothing.)
    void CheckPvpHitOnUs(IMemory m)
    {
        if (_pvpSlots.Count == 0) return;
        byte hf = m.ReadU8(EntitiesAddr + EHitFlags);
        uint by = m.ReadU32(EntitiesAddr + EUnkB8);
        if (hf != 0) PvpLog($"our hit flags {hf:X} after hit detection, by {by:X8} (copies {EntityAddr(_pvpSlots[0]):X8}..)");
        if (hf == 0 || by < EntityAddr(PvpFirst) || by >= EntityAddr(PvpEnd) || !_pvpSlots.Contains((int)((by - EntitiesAddr) / EntitySize))) return;
        // a dive kick always lands (at least 1 damage through our defence), like it
        // does on enemies: it's a movement move as much as an attack
        int copySlot = (int)((by - EntitiesAddr) / EntitySize);
        if (_pvpSource.TryGetValue(copySlot, out var kicked) && kicked.Id == IdDiveKick)
        {
            int least = (int)m.ReadU32(DefenseAddr) + 1;
            if ((short)m.ReadU16(EntitiesAddr + EHitPoints) < least) m.WriteU16(EntitiesAddr + EHitPoints, (ushort)Math.Min(least, short.MaxValue));
            // the kick has the empty hand's element: none. Hit detection hands us the
            // attacker's element as our hit, and Alucard ignores a hit of 0 (enemies'
            // attacks always have one): make it a plain blunt hit
            if (m.ReadU16(EntitiesAddr + EHitParams) == 0) m.WriteU16(EntitiesAddr + EHitParams, ElementHit);
        }
        ushort element = m.ReadU16(EntitiesAddr + EHitParams);
        short damage = (short)m.ReadU16(EntitiesAddr + EHitPoints);
        short dx = (short)((short)m.ReadU16(by + EPosXHi) - (short)m.ReadU16(EntitiesAddr + EPosXHi));
        short dy = (short)((short)m.ReadU16(by + EPosYHi) - (short)m.ReadU16(EntitiesAddr + EPosYHi));
        int vx = (int)m.ReadU32(by + EVelX);
        ushort facing = m.ReadU16(by + EFacing);
        short offX = (short)m.ReadU16(by + EHitboxOffX);
        _pvpTouch = (hf, element, damage, dx, dy, vx, facing, offX, _vsyncs, (int)m.ReadU32(StatusHpAddr));
        _pvpTouchInvincible = (m.ReadU16(InvincibleTimersAddr) | m.ReadU16(InvincibleTimersAddr + 2)) != 0;
        int copy = (int)((by - EntitiesAddr) / EntitySize);
        _pvpTouchBy = _pvpSource.TryGetValue(copy, out var src) ? $"{src.Who.Name}'s slot {src.Slot} (id {src.Id:X}, attack {(short)m.ReadU16(by + EAttack)})" : "?";
    }

    (byte Flags, ushort Element, short Damage, short Dx, short Dy, int VelX, ushort Facing, short OffX, long At, int Hp)? _pvpTouch;
    ushort _lastP1Step;
    string _pvpTouchBy = "";
    bool _pvpTouchInvincible;
    long _lastGuardSent = -1000;
    const uint InvincibleTimersAddr = 0x80072F00u + 13 * 2;   // g_Player.timers[ALU_T_INVINCIBLE], [_CONSUMABLES]
    const uint DefenseAddr = 0x80097C24u;   // g_Status.defenseEquip
    const ushort IdDiveKick = 6;            // EntityDiveKickAttack
    const ushort ElementHit = 0x20;         // ELEMENT_HIT (blunt)
    const uint PlayerUnk44Addr = 0x80072F64u; // g_Player.unk44: 0x80 = the dive kick hit something (bounce)

    // Our dive kick, on the friend: in their game it's our copy that hits them; here
    // the kick never touches anything the game counts, so it wouldn't bounce us off
    // them. Do hit detection's overlap test ourselves, against their body, and set the
    // same "the kick hit" flag EntityDiveKickAttack sets: we bounce, as off an enemy.
    // (P1 live, after our update: EntityAlucard reads the flag next frame.)
    void DiveKickBounce(IMemory m)
    {
        if (!PvpOn || m.ReadU16(EntitiesAddr + EStepS) != 0x70) return;
        uint kick = 0;
        for (int i = 0x11; i < 0x20 && kick == 0; i++)
            if (m.ReadU16(EntityAddr(i) + EEntityId) == IdDiveKick) kick = EntityAddr(i);
        if (kick == 0) return;
        var (kx, ky, kw, kh) = Hitbox(m.ReadU16(kick + EPosXHi), m.ReadU16(kick + EPosYHi), m.ReadU16(kick + EFacing),
                                      m.ReadU16(kick + EHitboxOffX), m.ReadU16(kick + 0x12), m.ReadU8(kick + 0x46), m.ReadU8(kick + 0x47));
        foreach (var f in _friends)
        {
            if (!IsPresent(f.Body) || !f.Valid || (f.Peer.Flags & 4) != 0) continue;
            var body = f.Body.Regions[RegEntities];
            var (px, py) = ScreenOf(m, f.Body);
            var (bx, by, bw, bh) = Hitbox((ushort)px, (ushort)py, (ushort)body[0x14 / 4],
                                          (ushort)body[0x10 / 4], (ushort)(body[0x10 / 4] >> 16), (byte)(body[0x44 / 4] >> 16), (byte)(body[0x44 / 4] >> 24));
            if (kw == 0 || bw == 0 || Math.Abs(kx - bx) > kw + bw || Math.Abs(ky - by) > kh + bh) continue;
            m.WriteU16(PlayerUnk44Addr, (ushort)(m.ReadU16(PlayerUnk44Addr) | 0x80));
            return;
        }
    }

    // centre and half-size, the way HitDetection works them out
    static (int X, int Y, int W, int H) Hitbox(ushort x, ushort y, ushort facingLeft, ushort offX, ushort offY, byte w, byte h)
    {
        int cx = (short)x + ((facingLeft & 0xFF) != 0 ? -(short)offX : (short)offX);
        return (cx, (short)y + (short)offY, w, h);
    }

    // After our player's update (P1 live): did that touch become a hit? Then tell them,
    // with the damage it really did and which way we ended up facing.
    void CheckPvpLanded(IMemory m)
    {
        ushort step = m.ReadU16(EntitiesAddr + EStep), prev = _lastP1Step;
        _lastP1Step = step;
        if (_pvpTouch is not { } t) return;
        if (_vsyncs - t.At > 2) { _pvpTouch = null; return; }   // shrugged off (invincible)
        if (step != StepHit && prev != StepHit && t.At == _vsyncs && !_pvpTouchInvincible)
        {
            // it touched us, we weren't invincible, and we didn't flinch: our defence
            // turned it away (the game's "guard": a white blink). Show it on their copy
            // of us too, so they see their attack connect and bounce off.
            _pvpTouch = null;
            if (_vsyncs - _lastGuardSent >= 20)
            {
                _lastGuardSent = _vsyncs;
                PvpLog($"guarded a hit (with {_pvpTouchBy}): telling everyone");
                Send(Msg.PvpGuard, _ => { });
            }
            return;
        }
        if (step != StepHit || prev == StepHit) return;
        _pvpTouch = null;
        short damage = (short)Math.Max(0, t.Hp - (int)m.ReadU32(StatusHpAddr));
        byte facing = (byte)m.ReadU16(EntitiesAddr + EFacing);
        PvpLog($"hit (damage {damage}, with {_pvpTouchBy}): telling everyone");
        Send(Msg.PvpHit, w => { w.Write(t.Flags); w.Write(t.Element); w.Write(damage); w.Write(t.Dx); w.Write(t.Dy); w.Write(t.VelX); w.Write(t.Facing); w.Write(t.OffX); w.Write(facing); });
    }

    // The sender took a hit in their game: show it on our copy of them.
    void OnPvpHit(BinaryReader r)
    {
        var f = FromFriend;
        if (f == null) return;
        if (f.PvpHit != null) PvpLog($"hit on {f.Name} replaced by a newer one before it was shown");
        f.PvpHit = (r.ReadByte(), r.ReadUInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt32(), r.ReadUInt16(), r.ReadInt16(), r.ReadByte(), _vsyncs);
    }

    // After a friend's update, their body live: face the way they faced in their game.
    void FixPvpFacing(IMemory m)
    {
        var f = F;
        if (f.PvpFacingWant < 0) return;
        if (m.ReadU16(EntitiesAddr + EStep) == StepHit) m.WriteU16(EntitiesAddr + EFacing, (ushort)f.PvpFacingWant);
        f.PvpFacingWant = -1;
    }

    static void PvpLog(string s) => Console.WriteLine($"[coop-online] pvp: {s}");

    // Right after a friend's hit detection, their body live: give it the hit their game
    // says they took, so their player code plays it.
    void ApplyPvpHitOnFriend(IMemory m)
    {
        var f = F;
        if (f.PvpHit is not { } h) return;
        if (_vsyncs - h.At > 30 || !PvpOn) { f.PvpHit = null; PvpLog($"hit on {f.Name} dropped (stale by {_vsyncs - h.At} frames)"); return; }
        // our copy is still reeling from the last hit (it runs a little behind their
        // game): keep this one until it's back on its feet
        if (m.ReadU16(EntitiesAddr + EStep) == StepHit) return;
        f.PvpHit = null;
        int slot = PvpFirst;
        while (slot < PvpEnd && m.ReadU16(EntityAddr(slot) + EEntityId) != 0) slot++;
        if (slot >= PvpEnd) { PvpLog("hit dropped (no free slot for the attacker)"); return; }
        // the stand-in attacker: only its position, speed and facing are read
        uint a = EntityAddr(slot);
        m.ZeroRange(a, EntitySize);
        m.WriteU16(a + EEntityId, 0x3FF);
        m.WriteU16(a + EPosXHi, (ushort)((short)m.ReadU16(EntitiesAddr + EPosXHi) + h.Dx));
        m.WriteU16(a + EPosYHi, (ushort)((short)m.ReadU16(EntitiesAddr + EPosYHi) + h.Dy));
        m.WriteU32(a + EVelX, (uint)h.VelX);
        m.WriteU16(a + EFacing, h.Facing);
        m.WriteU16(a + EHitboxOffX, (ushort)h.OffX);
        _pvpSlots.Add(slot);
        m.WriteU32(EntitiesAddr + EUnkB8, a);
        m.WriteU8(EntitiesAddr + EHitFlags, h.Flags);
        m.WriteU16(EntitiesAddr + EHitParams, h.Element);
        m.WriteU16(EntitiesAddr + EHitPoints, (ushort)h.Damage);
        R.Discontinuity = true;   // a rollback must not replay this frame without the hit
        f.PvpFacingWant = h.VictimFacing;
        PvpLog($"showing the hit on {f.Name} (damage {h.Damage})");
    }

    // Before the friend's frame (their own hit detection must not see these) and at
    // the latest before our objects update.
    void RemoveFriendAttacks(IMemory m)
    {
        if (_pvpSlots.Count == 0) return;
        // (Alucard's damage code has already read the attacker for the knockback side)
        foreach (int slot in _pvpSlots) m.ZeroRange(EntityAddr(slot), EntitySize);
        _pvpSlots.Clear();
    }
}
