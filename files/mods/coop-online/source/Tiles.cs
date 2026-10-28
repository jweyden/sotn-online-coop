using Sotn;
using System;
using System.Linq;
using RecompOne.Runtime.Context;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Memory;

// Room layout changes (breakable walls and floors, trapdoors, opened passages). Stage
// objects change the room by writing its foreground tiles (g_Tilemap.fg), which is
// also what collision reads. Each game watches the tiles of the room it's in and sends
// what its own objects changed; the friend applies that to the same room, now if
// they're in it, or when they get there (kept, like kills, while either of us is
// in that room).
public partial class OnlineCoop
{
    const uint TilemapFg = 0x80073084u, TilemapHSize = 0x800730A4u, TilemapVSize = 0x800730A8u;
    const int MaxTileChangesPerFrame = 64;   // more than this is an animation, not a wall
    const int MaxTileChangesPerVisit = 1200;

    (ushort, short, short) _tileRoom = (0xFFFF, 0, 0);
    uint _tileFg; int _tileCount;
    ushort[] _tileBase = Array.Empty<ushort>();
    int _tileSentThisVisit;
    readonly Dictionary<(ushort, short, short), Dictionary<int, ushort>> _tilesByRoom = new();

    Dictionary<int, ushort> TilesFor((ushort, short, short) room)
    {
        if (!_tilesByRoom.TryGetValue(room, out var d)) _tilesByRoom[room] = d = new();
        return d;
    }

    // Once a frame (P1 live, end of frame, in game).
    void TickTiles(IMemory m)
    {
        var room = CurrentRoom(m);
        uint fg = m.ReadU32(TilemapFg);
        int count = (int)(m.ReadU32(TilemapHSize) * 16 * m.ReadU32(TilemapVSize) * 16);
        if (fg < 0x80000000u || fg >= 0x80200000u || count <= 0 || count > 0x20000 || (fg & 0x1FFFFF) + count * 2 > 0x200000)
        { _tileRoom = (0xFFFF, 0, 0); return; }
        var live = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(RamBytes(m, fg & ~1u, count * 2));

        if (room != _tileRoom || fg != _tileFg || count != _tileCount)
        {
            // a new room: the friend's changes to it first, then that's the baseline
            _tileRoom = room; _tileFg = fg; _tileCount = count; _tileSentThisVisit = 0;
            var theirs = NoTileSync(room.Item1) ? null : _tilesByRoom.GetValueOrDefault(room);
            if (theirs != null)
                foreach (var (i, v) in theirs) if (i < count) live[i] = v;
            _tileBase = live.ToArray();
            if (theirs != null && theirs.Count > 0) RoomLog($"applied {theirs.Count} tile changes the others made to this room");
            return;
        }
        if (_net.Status != NetSession.State.Connected || NoTileSync(room.Item1)) { live.CopyTo(_tileBase); return; }

        var doors = DoorTiles(m, (int)m.ReadU32(TilemapHSize) * 16, count);
        List<(int, ushort)>? changed = null;
        for (int i = 0; i < count; i++)
        {
            if (live[i] == _tileBase[i]) continue;
            _tileBase[i] = live[i];
            if (doors.Contains(i)) continue;   // (a door's own tiles: each game's door is its own)
            (changed ??= new()).Add((i, live[i]));
        }
        if (changed == null || changed.Count > MaxTileChangesPerFrame || _tileSentThisVisit > MaxTileChangesPerVisit) return;
        _tileSentThisVisit += changed.Count;
        var mine = TilesFor(room);
        foreach (var (i, v) in changed) mine[i] = v;   // ours count for this room too (if we go and come back)
        Send(Msg.Tiles, w =>
        {
            WriteRoom(w, room);
            w.Write((ushort)changed.Count);
            foreach (var (i, v) in changed) { w.Write(i); w.Write(v); }
        });
        RoomLog($"{changed.Count} tiles changed here, sent to {_net.PeerName}");
    }

    // The tiles red doors (loading-zone doors) write: solid while shut, passable while
    // open. Each game runs its own doors (RoomSync's LocalFns), so these never go
    // across, or the friend's still-shut door would shut ours again.
    HashSet<int> DoorTiles(IMemory m, int rowTiles, int count)
    {
        var set = new HashSet<int>();
        if (rowTiles <= 0) return set;
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        for (int s = StageFirst; s < 256; s++)
        {
            uint e = EntityAddr(s);
            if (m.ReadU16(e + EEntityId) == 0 || !IsDoor(m, e)) continue;
            int tx = ((short)m.ReadU16(e + EPosXHi) + sx) >> 4, ty = ((short)m.ReadU16(e + EPosYHi) + sy) >> 4;
            for (int y = ty - 3; y <= ty + 3; y++)
                for (int x = tx - 2; x <= tx + 2; x++)
                {
                    int i = y * rowTiles + x;
                    if (x >= 0 && x < rowTiles && i >= 0 && i < count) set.Add(i);
                }
        }
        return set;
    }

    readonly Dictionary<uint, bool> _doorFn = new();
    bool IsDoor(IMemory m, uint e)
    {
        uint fn = m.ReadU32(e + EPfnUpdate);
        if (fn == 0) return false;
        if (!_doorFn.TryGetValue(fn, out bool yes))
        {
            var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
            _doorFn[fn] = yes = map.TryGetValue(fn, out var f) && IsStageFn(f.Method.Name, "EntityRedDoor");
        }
        return yes;
    }

    // The first-visit castle entrance's gates and doors close behind each player as
    // part of their own intro: a door the friend's intro shut must not shut on us
    // (it pushed us out of the room, over and over). Nothing there is shared.
    static bool NoTileSync(ushort stage) => stage == Stage_Entrance;

    void OnFriendTiles(BinaryReader r)
    {
        var room = ReadRoom(r);
        int n = r.ReadUInt16();
        if (NoTileSync(room.Item1)) return;
        var theirs = TilesFor(room);
        var m = RecompOne.Runtime.Runtime.Mem;
        bool here = m != null && room == _tileRoom && Game.InGame && CurrentRoom(m) == room;
        Span<ushort> live = here ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(RamBytes(m!, _tileFg & ~1u, _tileCount * 2)) : default;
        var doors = here ? DoorTiles(m!, (int)m!.ReadU32(TilemapHSize) * 16, _tileCount) : new HashSet<int>();
        for (int k = 0; k < n; k++)
        {
            int i = r.ReadInt32(); ushort v = r.ReadUInt16();
            if (doors.Contains(i)) continue;
            theirs[i] = v;
            if (here && i < _tileCount) { live[i] = v; _tileBase[i] = v; }   // (and not "our" change to send back)
        }
        RoomLog($"{From} changed {n} tiles in room {room}{(here ? " (here)" : "")}");
    }

    // like kills: forget rooms neither of us is in any more
    void ForgetTiles(HashSet<(ushort, short, short)> keep)
    {
        foreach (var room in new List<(ushort, short, short)>(_tilesByRoom.Keys))
            if (!keep.Contains(room)) _tilesByRoom.Remove(room);
    }
}
