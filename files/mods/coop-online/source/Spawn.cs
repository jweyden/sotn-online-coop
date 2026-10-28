using System;
using System.Linq;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

// The game runs a room around ONE camera: the layout spawner creates objects as that
// camera scrolls over them, and the room update deletes or freezes objects that drift
// off it. The game running a shared room does both for every other camera in the
// room too, so everything each friend can see exists (and is streamed to them).
public partial class OnlineCoop
{
    const uint RespawnBits = 0x80097428u;   // g_unkGraphicsStruct.D_80097428: layout objects not to respawn
    const uint FDestroyOffCam = 0x80000000u, FDestroyBarelyOffCam = 0x40000000u, FFreezeOffCam = 0x20000000u, FAlwaysUpdate = 0x10000000u;
    const int SpawnMargin = 32;             // the friend's camera is a few frames old
    const int MaxPrims = 1280, MinFreePrims = 96;   // g_PrimBuf; keep some for our own effects (e.g. spell beams)
    long _lastBusyLog;

    // Busy rooms: two cameras' worth of objects can use up the shared graphics prims,
    // and then new effects (a Hellfire beam, a spell) silently fail. Stop spawning
    // extra objects for the friend's camera while prims are short.
    static int FreePrims(IMemory m)
    {
        int free = 0;
        var buf = RamBytes(m, PrimBufAddr, MaxPrims * 0x34);
        for (int i = 0; i < MaxPrims; i++) if (buf[i * 0x34 + 7] == 0) free++;
        return free;
    }

    // ---------------------------------------------------------------- spawning

    // where we last spawned around each friend's camera
    readonly Dictionary<int, (short X, short Y)> _spawnedAt = new();
    void ForgetSpawns() => _spawnedAt.Clear();

    // Like the game's own spawner (InitRoomEntities / UpdateRoomPosition), for a
    // friend's camera: objects coming into its range get created, once.
    void SpawnAround(CpuContext c, IMemory m, Friend fr)
    {
        uint create = StageFn(m, "CreateEntityFromLayout"), listVar = LayoutListVar(m);
        if (create == 0 || listVar == 0) return;
        uint p = m.ReadU32(listVar);
        if (p < 0x80000000u || p >= 0x80200000u) return;
        for (int guard = 0; m.ReadU16(p) != 0xFFFE; p -= 10)
            if (++guard > 4000 || p < 0x80000000u) return;   // walk back to the list's start

        short px = fr.Peer.ScrollX, py = fr.Peer.ScrollY;
        bool had = _spawnedAt.TryGetValue(fr.Number, out var last);
        bool In(short sx, short sy, int x, int y) =>
            x >= sx - 64 - SpawnMargin && x <= sx + 320 + SpawnMargin && y >= sy - 64 - SpawnMargin && y <= sy + 288 + SpawnMargin;

        var killed = Killed(CurrentRoom(m));
        bool primsChecked = false;
        for (uint q = p + 10, n = 0; m.ReadU16(q) != 0xFFFF && n < 4000; q += 10, n++)
        {
            int x = m.ReadU16(q), y = m.ReadU16(q + 2);
            if (!In(px, py, x, y) || (had && In(last.X, last.Y, x, y))) continue;
            ushort id = m.ReadU16(q + 4), roomIndex = m.ReadU16(q + 6);
            uint kind = id & 0xE000u;
            if (kind != 0 && kind != 0xA000u) continue;
            int flag = roomIndex >> 8;
            if (flag != 0)
            {
                int bit = flag - 1;
                if ((m.ReadU32(RespawnBits + (uint)(bit >> 5) * 4) & (1u << (bit & 31))) != 0) continue;   // collected / killed for good
            }
            int slot = StageFirst + (roomIndex & 0xFF);
            if (slot >= 256 || m.ReadU16(EntityAddr(slot) + EEntityId) != 0) continue;   // there already
            if (killed.Contains((slot, (ushort)(id & 0x3FF), (ushort)0))) continue;
            if (!primsChecked)
            {
                primsChecked = true;
                int free = FreePrims(m);
                if (free < MinFreePrims)
                {
                    if (_frame - _lastBusyLog > 300) { _lastBusyLog = _frame; RoomLog($"busy room: only {free} graphics prims free, not spawning more for {fr.Name}'s camera"); }
                    return;   // (and try again next frame: _spawnedAt stays where it was)
                }
            }
            Call(c, m, create, EntityAddr(slot), q);   // positions come out relative to our camera
        }
        _spawnedAt[fr.Number] = (px, py);
    }

    // ---------------------------------------------------------------- keeping objects alive

    // Objects near a friend's camera (but off ours) lose their "delete / freeze when
    // off camera" flags for the room update, and get them back after it.
    readonly List<(int Slot, ushort Id, uint Bits)> _keptAlive = new();

    void KeepAliveNearFriends(IMemory m)
    {
        _keptAlive.Clear();
        var cams = _sharing.Select(f => (X: f.Peer.ScrollX - (short)m.ReadU16(ScrollXAddr), Y: f.Peer.ScrollY - (short)m.ReadU16(ScrollYAddr))).ToList();
        if (cams.Count == 0) return;
        for (int i = StageFirst; i < 256; i++)
        {
            uint e = EntityAddr(i);
            uint f = m.ReadU32(e + EFlags);
            if ((f & (FDestroyOffCam | FFreezeOffCam)) == 0 || m.ReadU16(e + EEntityId) == 0) continue;
            int k = SpawnMargin;
            uint bits = 0;
            foreach (var (ox, oy) in cams)
            {
                int x = (short)m.ReadU16(e + EPosXHi) - ox, y = (short)m.ReadU16(e + EPosYHi) - oy;   // on that friend's screen
                if ((f & FDestroyOffCam) != 0)
                {
                    bool keep = (f & FDestroyBarelyOffCam) != 0
                        ? x >= -64 - k && x <= 320 + k && y >= -64 - k && y <= 288 + k
                        : x >= -128 - k && x <= 384 + k && y >= -128 - k && y <= 352 + k;
                    if (keep) bits |= FDestroyOffCam;
                }
                if ((f & FFreezeOffCam) != 0 && (f & FAlwaysUpdate) == 0 &&
                    x >= -64 - k && x <= 320 + k && y >= -64 - k && y <= 288 + k)
                    bits |= FFreezeOffCam;
            }
            if (bits == 0) continue;
            m.WriteU32(e + EFlags, f & ~bits);
            _keptAlive.Add((i, m.ReadU16(e + EEntityId), bits));
        }
    }

    void RestoreKeepAlive(IMemory m)
    {
        foreach (var (slot, id, bits) in _keptAlive)
        {
            uint e = EntityAddr(slot);
            if (m.ReadU16(e + EEntityId) == id) m.WriteU32(e + EFlags, m.ReadU32(e + EFlags) | bits);
        }
        _keptAlive.Clear();
    }

    // ---------------------------------------------------------------- finding stage code / data

    ushort _stageFnStage = 0xFFFF;
    readonly Dictionary<string, uint> _stageFns = new();
    uint _layoutListVar;

    // A recompiled stage function's name is "<name>_<stage>" or "<STAGE>_<name>" (both
    // are used): is it `name`, and in which stage? (Plain "<name>" when only one stage
    // has it: stage "".)
    static bool IsStageFn(string method, string name, out string stage)
    {
        stage = "";
        if (method.EndsWith("_Impl", StringComparison.Ordinal)) return false;
        if (method == name) return true;
        if (method.StartsWith(name + "_", StringComparison.Ordinal)) { stage = method[(name.Length + 1)..]; return true; }
        if (method.EndsWith("_" + name, StringComparison.Ordinal)) { stage = method[..^(name.Length + 1)]; return true; }
        return false;
    }
    static bool IsStageFn(string method, string name) => IsStageFn(method, name, out _);

    // The current stage's copy of a shared stage function.
    uint StageFn(IMemory m, string name)
    {
        ushort stage = (ushort)m.ReadU32(StageIdAddr);
        if (stage != _stageFnStage) { _stageFns.Clear(); _layoutListVar = 0; _stageFnStage = stage; }
        if (_stageFns.TryGetValue(name, out uint addr)) return addr;
        addr = 0;
        var map = (Dictionary<uint, Action<CpuContext, IMemory>>)DispatcherFuncMap.GetValue(null)!;
        // (the map can still hold a stage we've left, at the same addresses: only a
        // loaded one counts)
        var loaded = new HashSet<string>(RecompOne.Runtime.Dispatch.Dispatcher.ActiveNames, StringComparer.OrdinalIgnoreCase);
        foreach (var (a, f) in map)
            if (a >= 0x80180000u && a < 0x80200000u && IsStageFn(f.Method.Name, name, out string st) && loaded.Contains(st))
            { addr = a; break; }
        _stageFns[name] = addr;
        if (addr == 0) RoomLog($"no {name} in this stage: can't spawn around the others");
        return addr;
    }

    // g_LayoutObjHorizontal is a stage-local variable; FindFirstEntityToTheRight's first
    // load (lui + lw) says where it is.
    uint LayoutListVar(IMemory m)
    {
        uint fn = StageFn(m, "FindFirstEntityToTheRight");
        if (_layoutListVar != 0 || fn == 0) return _layoutListVar;
        for (uint pc = fn, end = fn + 0x40; pc < end; pc += 4)
        {
            uint ins = m.ReadU32(pc);
            if (ins >> 26 != 0x0F) continue;                         // lui rt, hi
            uint rt = (ins >> 16) & 31, hi = ins & 0xFFFF;
            for (uint pc2 = pc + 4; pc2 < pc + 0x10; pc2 += 4)
            {
                uint lw = m.ReadU32(pc2);
                if (lw >> 26 == 0x23 && ((lw >> 21) & 31) == rt)     // lw rX, off(rt)
                {
                    _layoutListVar = (hi << 16) + (uint)(short)(lw & 0xFFFF);
                    // (it's a stage variable: anything else means we read the wrong code)
                    if (_layoutListVar < 0x80180000u || _layoutListVar >= 0x80200000u)
                    {
                        RoomLog($"layout list pointer 0x{_layoutListVar:X8} isn't in the stage: not spawning around the others here");
                        _layoutListVar = 0;
                        _stageFns["FindFirstEntityToTheRight"] = 0;
                        return 0;
                    }
                    RoomLog($"layout list pointer at 0x{_layoutListVar:X8}");
                    return _layoutListVar;
                }
            }
        }
        return 0;
    }
}
