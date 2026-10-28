using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Memory;
using Sotn;

// Phase 2: shared progress. Each game watches its own progress every frame and sends
// what changed over the reliable channel; the other game merges it in.
//  - castle flags: stage flags (0x000..0x0FF) take the higher value (some are
//    counters), collectible / rare-drop bits (0x100..) are OR-ed
//  - explored map: OR; relics and spells: OR (a newly shared relic is switched on)
//  - item bag and gold: deltas, so pickups, use and equipping are shared
//  - Life/Heart Max Ups: mirrored, since each one can only be collected once
// EXP, level and stats stay per player.
public partial class OnlineCoop
{
    const uint CastleFlagsAddr = 0x8003BDECu; const int CastleFlagsLen = 0x300;
    const uint CastleMapAddr = 0x8006BB74u; const int CastleMapLen = 0x800;
    const uint RoomCountAddr = 0x8003C760u;
    const uint RelicsAddr = 0x80097964u; const int RelicCount = 30;
    const uint SpellsAddr = 0x80097982u; const int SpellCount = 8;
    const uint HandCountAddr = 0x8009798Au; const int HandItems = 0xA9;
    const uint BodyCountAddr = 0x80097A33u; const int BodyItems = 0x5A;
    const uint SpellsLearntAddr = 0x80097B9Cu, GoldAddr = 0x80097BF0u, LevelAddr = 0x80097BE8u;
    const uint FnAddToInventory = 0x800FD874u;
    const uint FnPaintMapTile = 0x800F1B08u;        // func_800F1B08(x, y, partial)
    const uint FnRevealSecretPassage = 0x800F1EB0u; // RevealSecretPassageOnMap(x, y, 0xFFFF)
    const byte RelicFound = 1, RelicActive = 2;

    enum Msg : byte { FullSync = 1, Flags, Map, Relics, Spells, Bag, Gold, MaxUp, RoomCount, Killed, Hit, Collected, PvpHit, Resync, Drop, Tiles, NewGameGo, TeleportCast, LevelUp, HostNewGame, HostSave, PvpGuard, Revive, RespawnFx, SatHeal, HealShown }

    byte[] _lastFlags = Array.Empty<byte>(), _lastMap = Array.Empty<byte>();
    byte[] _lastRelics = Array.Empty<byte>(), _lastSpells = Array.Empty<byte>();
    byte[] _lastHand = Array.Empty<byte>(), _lastBody = Array.Empty<byte>();
    uint _lastSpellsLearnt, _lastRoomCount;
    int _lastGold, _lastHpMax, _lastHeartsMax, _lastLevel;
    const int MaxUpMost = 10;
    bool _progressReady, _fullSyncSent;
    public bool ShareProgress = true;

    static byte[] ReadBytes(IMemory m, uint addr, int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = m.ReadU8(addr + (uint)i);
        return b;
    }

    // Take our current progress as the baseline (nothing to send for it).
    void ResetProgressBaseline(IMemory m)
    {
        _lastFlags = ReadBytes(m, CastleFlagsAddr, CastleFlagsLen);
        _lastMap = ReadBytes(m, CastleMapAddr, CastleMapLen);
        _lastRelics = ReadBytes(m, RelicsAddr, RelicCount);
        _lastSpells = ReadBytes(m, SpellsAddr, SpellCount);
        _lastHand = ReadBytes(m, HandCountAddr, HandItems);
        _lastBody = ReadBytes(m, BodyCountAddr, BodyItems);
        _lastSpellsLearnt = m.ReadU32(SpellsLearntAddr);
        _lastRoomCount = m.ReadU32(RoomCountAddr);
        _lastGold = (int)m.ReadU32(GoldAddr);
        _lastHpMax = (int)m.ReadU32(StatusHpAddr + 4);
        _lastHeartsMax = (int)m.ReadU32(StatusStatsAddr + 12);
        _lastLevel = (int)m.ReadU32(LevelAddr);
        _progressReady = true;
    }

    // Every frame while connected and in game (P1 live).
    void TickProgress(IMemory m)
    {
        if (!Game.InGame) { _progressReady = false; _fullSyncSent = false; }
        if (_net.Status != NetSession.State.Connected || !Game.InGame || Game.IsLoading) return;
        if (ShareProgress)
        {
            if (!_progressReady) ResetProgressBaseline(m);
            if (!_fullSyncSent) { SendFullSync(m); _fullSyncSent = true; }
        }

        // (room sync, PvP and resync messages arrive here too, shared progress or not)
        while (_heldMessages.TryDequeue(out var held)) { _msgFrom = held.From; HandleMessage(m, held.Body); }   // (read during a new game's wait)
        while (_net.TryReceiveReliable(out var from, out var body)) { _msgFrom = from; HandleMessage(m, body); }
        _msgFrom = null;
        if (!ShareProgress) return;

        SendByteDiffs(m, Msg.Flags, CastleFlagsAddr, _lastFlags);
        SendByteDiffs(m, Msg.Map, CastleMapAddr, _lastMap);
        // (only Alucard finds relics and learns spells: Richter mode starts with them all)
        if (CoopCharacters.IsAlucard(_p1Char))
        {
            SendByteDiffs(m, Msg.Relics, RelicsAddr, _lastRelics);
            SendByteDiffs(m, Msg.Spells, SpellsAddr, _lastSpells);
        }
        SendBagDiffs(m);

        uint learnt = m.ReadU32(SpellsLearntAddr);
        if (learnt != _lastSpellsLearnt) { _lastSpellsLearnt = learnt; Send(Msg.Spells, w => { w.Write((ushort)0xFFFF); w.Write(learnt); }); }

        int gold = (int)m.ReadU32(GoldAddr);
        if (gold != _lastGold) { int d = gold - _lastGold; _lastGold = gold; Send(Msg.Gold, w => w.Write(d)); }

        // Max-ups: the max went up without a level-up, by what one pickup gives (+5); a
        // bigger jump is a character's stats going on (Saturn Maria's, at a load), not a find
        int level = (int)m.ReadU32(LevelAddr), hpMax = (int)m.ReadU32(StatusHpAddr + 4), heartsMax = (int)m.ReadU32(StatusStatsAddr + 12);
        if (level == _lastLevel && _rs == Rs.Alive)
        {
            if (hpMax > _lastHpMax && hpMax - _lastHpMax <= MaxUpMost) { int d = hpMax - _lastHpMax; Send(Msg.MaxUp, w => { w.Write((byte)0); w.Write(d); }); }
            if (heartsMax > _lastHeartsMax && heartsMax - _lastHeartsMax <= MaxUpMost) { int d = heartsMax - _lastHeartsMax; Send(Msg.MaxUp, w => { w.Write((byte)1); w.Write(d); }); }
        }
        _lastLevel = level; _lastHpMax = hpMax; _lastHeartsMax = heartsMax;
    }

    // To everyone (or to one friend).
    void Send(Msg type, Action<BinaryWriter> body) => Send(null, type, body);

    void Send(NetLink? to, Msg type, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)0x50);  // channel tag: progress
        w.Write((byte)type);
        body(w);
        if (to == null) _net.SendReliable(ms.ToArray()); else _net.SendReliable(to, ms.ToArray());
    }

    // Who sent the message being handled (HandleMessage), and their name for the log.
    NetLink? _msgFrom;
    string From => _msgFrom?.Name ?? "a friend";
    Friend? FromFriend => FriendOf(_msgFrom);

    void SendByteDiffs(IMemory m, Msg type, uint addr, byte[] last)
    {
        List<(ushort, byte)>? changes = null;
        for (int i = 0; i < last.Length; i++)
        {
            byte v = m.ReadU8(addr + (uint)i);
            if (v == last[i]) continue;
            last[i] = v;
            (changes ??= new()).Add(((ushort)i, v));
        }
        if (changes == null) return;
        for (int start = 0; start < changes.Count; start += 200)
        {
            int n = Math.Min(200, changes.Count - start), s = start;
            Send(type, w => { w.Write((ushort)n); for (int k = s; k < s + n; k++) { w.Write(changes[k].Item1); w.Write(changes[k].Item2); } });
        }
    }

    void SendBagDiffs(IMemory m)
    {
        List<(byte Kind, byte Id, sbyte Delta)>? changes = null;
        void Scan(byte kind, uint addr, byte[] last)
        {
            for (int i = 0; i < last.Length; i++)
            {
                byte v = m.ReadU8(addr + (uint)i);
                if (v == last[i]) continue;
                (changes ??= new()).Add((kind, (byte)i, (sbyte)Math.Clamp(v - last[i], -99, 99)));
                last[i] = v;
            }
        }
        Scan(0, HandCountAddr, _lastHand);
        Scan(1, BodyCountAddr, _lastBody);
        if (changes != null)
            Send(Msg.Bag, w => { w.Write((ushort)changes.Count); foreach (var (k, id, d) in changes) { w.Write(k); w.Write(id); w.Write(d); } });
    }

    void SendFullSync(IMemory m)
    {
        Send(Msg.FullSync, w =>
        {
            w.Write(_net.IsHost);
            w.Write(ReadBytes(m, CastleFlagsAddr, CastleFlagsLen));
            w.Write(ReadBytes(m, CastleMapAddr, CastleMapLen));
            // (a Richter-mode game's relics and spells are that mode's, all of them: not shared)
            bool alucard = CoopCharacters.IsAlucard(_p1Char);
            w.Write(alucard ? ReadBytes(m, RelicsAddr, RelicCount) : new byte[RelicCount]);
            w.Write(alucard ? ReadBytes(m, SpellsAddr, SpellCount) : new byte[SpellCount]);
            w.Write(alucard ? m.ReadU32(SpellsLearntAddr) : 0u);
            w.Write(ReadBytes(m, HandCountAddr, HandItems));
            w.Write(ReadBytes(m, BodyCountAddr, BodyItems));
            w.Write((int)m.ReadU32(GoldAddr));
            w.Write(m.ReadU32(RoomCountAddr));
        });
        Console.WriteLine("[coop-online] sent full progress sync");
    }

    // ---------------------------------------------------------------- applying the friend's progress

    void HandleMessage(IMemory m, byte[] body)
    {
        if (body.Length < 2 || body[0] != 0x50) return;
        var r = new BinaryReader(new MemoryStream(body, 2, body.Length - 2));
        var type = (Msg)body[1];
        switch (type)
        {
            case Msg.Killed: OnFriendKill(m, r); return;
            case Msg.Hit: OnFriendHit(r); return;
            case Msg.Collected: OnFriendCollected(r); return;
            case Msg.PvpHit: OnPvpHit(r); return;
            case Msg.Resync: OnResyncRequest(r); return;
            case Msg.Drop: OnFriendDrop(r); return;
            case Msg.Tiles: OnFriendTiles(r); return;
            case Msg.NewGameGo: OnNewGameGo(); return;
            case Msg.TeleportCast: OnFriendCast(); return;
            case Msg.LevelUp: OnFriendLevelUp(); return;
            case Msg.PvpGuard: OnFriendGuard(); return;
            case Msg.Revive: OnRevive(); return;
            case Msg.RespawnFx: OnFriendRespawnFx(); return;
            case Msg.SatHeal: OnSatHeal(m, r); return;
            case Msg.HealShown: OnFriendHealShown(); return;
        }
        if (!ShareProgress || !_progressReady) return;
        switch (type)
        {
            case Msg.FullSync: ApplyFullSync(m, r); break;
            case Msg.Flags: { int n = r.ReadUInt16(); for (int i = 0; i < n; i++) MergeFlag(m, r.ReadUInt16(), r.ReadByte()); break; }
            case Msg.Map: { int n = r.ReadUInt16(); for (int i = 0; i < n; i++) MergeMap(m, r.ReadUInt16(), r.ReadByte()); break; }
            case Msg.Relics: { int n = r.ReadUInt16(); for (int i = 0; i < n; i++) MergeRelic(m, r.ReadUInt16(), r.ReadByte()); break; }
            case Msg.Spells:
            {
                int n = r.ReadUInt16();
                if (n == 0xFFFF) { uint v = m.ReadU32(SpellsLearntAddr) | r.ReadUInt32(); m.WriteU32(SpellsLearntAddr, v); _lastSpellsLearnt = v; }
                else for (int i = 0; i < n; i++) MergeOr(m, SpellsAddr, _lastSpells, r.ReadUInt16(), r.ReadByte());
                break;
            }
            case Msg.Bag: { int n = r.ReadUInt16(); for (int i = 0; i < n; i++) ApplyBag(m, r.ReadByte(), r.ReadByte(), r.ReadSByte()); break; }
            case Msg.Gold: AddGold(m, r.ReadInt32()); break;
            case Msg.MaxUp:
            {
                byte kind = r.ReadByte(); int d = r.ReadInt32();
                uint max = kind == 0 ? StatusHpAddr + 4 : StatusStatsAddr + 12, cur = max - 4;
                m.WriteU32(max, (uint)((int)m.ReadU32(max) + d));
                m.WriteU32(cur, (uint)((int)m.ReadU32(cur) + d));
                if (kind == 0) _lastHpMax += d; else _lastHeartsMax += d;
                Console.WriteLine($"[coop-online] {From} found a {(kind == 0 ? "Life" : "Heart")} Max Up: +{d} for you too");
                break;
            }
            case Msg.RoomCount: { uint v = Math.Max(m.ReadU32(RoomCountAddr), r.ReadUInt32()); m.WriteU32(RoomCountAddr, v); _lastRoomCount = v; break; }
        }
    }

    void ApplyFullSync(IMemory m, BinaryReader r)
    {
        bool fromHost = r.ReadBoolean();
        var flags = r.ReadBytes(CastleFlagsLen);
        var map = r.ReadBytes(CastleMapLen);
        var relics = r.ReadBytes(RelicCount);
        var spells = r.ReadBytes(SpellCount);
        uint learnt = r.ReadUInt32();
        var hand = r.ReadBytes(HandItems);
        var body = r.ReadBytes(BodyItems);
        int gold = r.ReadInt32();
        uint rooms = r.ReadUInt32();

        for (int i = 0; i < flags.Length; i++) MergeFlag(m, (ushort)i, flags[i]);
        for (int i = 0; i < map.Length; i++) MergeMap(m, (ushort)i, map[i]);
        // (a Richter-mode player's relics and spells are that mode's, all of them: not shared)
        var senderChar = (CoopCharacter)(FromFriend is { Valid: true } sf ? sf.Peer.Character : _msgFrom?.Character ?? 0);
        if (CoopCharacters.IsAlucard(senderChar))
        {
            for (int i = 0; i < relics.Length; i++) MergeRelic(m, (ushort)i, relics[i]);
            for (int i = 0; i < spells.Length; i++) MergeOr(m, SpellsAddr, _lastSpells, (ushort)i, spells[i]);
            m.WriteU32(SpellsLearntAddr, m.ReadU32(SpellsLearntAddr) | learnt); _lastSpellsLearnt = m.ReadU32(SpellsLearntAddr);
        }

        // The bag and gold are one shared pool; the host's is the starting point.
        if (fromHost)
        {
            for (int i = 0; i < HandItems; i++) ApplyBag(m, 0, (byte)i, (sbyte)Math.Clamp(hand[i] - m.ReadU8(HandCountAddr + (uint)i), -99, 99));
            for (int i = 0; i < BodyItems; i++) ApplyBag(m, 1, (byte)i, (sbyte)Math.Clamp(body[i] - m.ReadU8(BodyCountAddr + (uint)i), -99, 99));
            AddGold(m, gold - (int)m.ReadU32(GoldAddr));
        }
        Console.WriteLine($"[coop-online] merged {From}'s progress{(fromHost ? " (and took the host's items and gold)" : "")}");
    }

    void MergeFlag(IMemory m, ushort i, byte v)
    {
        if (i >= CastleFlagsLen) return;
        if (i is 0x34 or 0x35) { _lastFlags[i] = m.ReadU8(CastleFlagsAddr + i); return; }   // prologue done / robbed by Death: each game's own intro sets them
        byte cur = m.ReadU8(CastleFlagsAddr + i);
        byte merged = i < 0x100 ? Math.Max(cur, v) : (byte)(cur | v);
        if (merged != cur) m.WriteU8(CastleFlagsAddr + i, merged);
        _lastFlags[i] = merged;
    }

    // Map: each byte holds 4 tiles x 2 bits (low bit of a pair = explored, high =
    // seen). The map screen is a picture the game paints a tile at a time when a
    // room is first explored (all_map_check, 5087C.c), so paint the friend's newly
    // explored tiles ourselves and count them, exactly as the game does.
    void MergeMap(IMemory m, ushort i, byte v)
    {
        if (i >= CastleMapLen) return;
        byte cur = m.ReadU8(CastleMapAddr + i), merged = (byte)(cur | v), added = (byte)(merged & ~cur);
        _lastMap[i] = merged;
        if (added == 0) return;
        m.WriteU8(CastleMapAddr + i, merged);

        var c = RecompOne.Runtime.Runtime.Cpu;
        bool inverted = i >= 0x400;
        bool showing = ((m.ReadU32(StageIdAddr) & 0x20) != 0) == inverted; // map of the castle we're in
        int tileY = (i & 0x3FF) / 16, baseX = (i & 0xF) * 4;
        for (int t = 0; t < 4; t++)
        {
            int shift = (3 - t) * 2, tileX = baseX + t;
            bool explored = (added & (1 << shift)) != 0, seen = (added & (2 << shift)) != 0;
            if (explored)
            {
                m.WriteU32(RoomCountAddr, m.ReadU32(RoomCountAddr) + 1);
                if (showing && c != null)
                {
                    Call(c, m, FnPaintMapTile, (uint)tileX, (uint)tileY, 0);
                    Call(c, m, FnRevealSecretPassage, (uint)tileX, (uint)tileY, 0xFFFF);
                }
            }
            else if (seen && showing && c != null)
                Call(c, m, FnPaintMapTile, (uint)tileX, (uint)tileY, 1);
        }
        _lastRoomCount = m.ReadU32(RoomCountAddr);
    }

    static void MergeOr(IMemory m, uint addr, byte[] last, ushort i, byte v)
    {
        if (i >= last.Length) return;
        byte cur = m.ReadU8(addr + i), merged = (byte)(cur | v);
        if (merged != cur) m.WriteU8(addr + i, merged);
        last[i] = merged;
    }

    void MergeRelic(IMemory m, ushort i, byte v)
    {
        if (i >= RelicCount) return;
        byte cur = m.ReadU8(RelicsAddr + i);
        if ((v & RelicFound) != 0 && (cur & RelicFound) == 0)
        {
            cur |= RelicFound | RelicActive;
            m.WriteU8(RelicsAddr + i, cur);
            Console.WriteLine($"[coop-online] {From} found relic {i}: you have it too");
        }
        _lastRelics[i] = cur;
    }

    // Add/remove items through the game's own AddToInventory so new ones show up in
    // the menu's item order.
    void ApplyBag(IMemory m, byte kind, byte id, sbyte delta)
    {
        uint addr = (kind == 0 ? HandCountAddr : BodyCountAddr) + id;
        var c = RecompOne.Runtime.Runtime.Cpu;
        if (delta > 0 && c != null)
            for (int k = 0; k < delta; k++) Call(c, m, FnAddToInventory, id, kind == 0 ? 0u : 2u /* EQUIP_HAND / EQUIP_ARMOR */);
        else if (delta < 0)
            m.WriteU8(addr, (byte)Math.Max(0, m.ReadU8(addr) + delta));
        (kind == 0 ? _lastHand : _lastBody)[id] = m.ReadU8(addr);
    }

    void AddGold(IMemory m, int d)
    {
        int g = Math.Clamp((int)m.ReadU32(GoldAddr) + d, 0, 999999);
        m.WriteU32(GoldAddr, (uint)g);
        _lastGold = g;
    }
}
