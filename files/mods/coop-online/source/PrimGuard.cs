using System;
using System.Collections.Generic;
using RecompOne.Runtime.Memory;

// (Diagnostics) Something has been writing a drawing-environment packet (0x06......
// tag) over a prim's next link, and the game later crashes following it. Check every
// prim's link at a few points in the frame and log the first broken one: which part
// of the frame broke it, whose prim it is, and what's there.
public partial class OnlineCoop
{
    string _primPhase = "start";
    readonly HashSet<uint> _primReported = new();
    int _primReports;

    void CheckPrims(IMemory m, string phase)
    {
        if (_primReports < 20)
        {
            const uint end = PrimBufAddr + MaxPrims * 0x34;
            for (int i = 0; i < MaxPrims; i++)
            {
                uint p = PrimBufAddr + (uint)i * 0x34;
                if (m.ReadU8(p + 7) == 0) continue;   // free
                uint next = m.ReadU32(p);
                if (next == 0 || (next >= PrimBufAddr && next < end && (next - PrimBufAddr) % 0x34 == 0)) continue;
                if (!_primReported.Add(p)) continue;
                _primReports++;
                Console.WriteLine($"[coop-online] PRIM BROKEN: prim {i} next=0x{next:X8} type {m.ReadU8(p + 7):X2}, " +
                                  $"broke between '{_primPhase}' and '{phase}'; owner {PrimOwner(m, i)}; " +
                                  $"words {m.ReadU32(p + 4):X8} {m.ReadU32(p + 8):X8} {m.ReadU32(p + 12):X8} {m.ReadU32(p + 16):X8}; " +
                                  $"follower {_follower}, authority {_authority}, in P2 {_inP2}");
            }
        }
        _primPhase = phase;
    }

    string PrimOwner(IMemory m, int prim)
    {
        uint target = PrimBufAddr + (uint)prim * 0x34;
        for (int s = 0; s < 256; s++)
        {
            uint e = EntityAddr(s);
            int idx = (int)m.ReadU32(e + EPrimIndex);
            if (idx <= 0 || idx >= MaxPrims) continue;
            uint p = PrimBufAddr + (uint)idx * 0x34;
            for (int k = 0; k < 64 && p != 0; k++)
            {
                if (p == target) return $"slot {s} (id {m.ReadU16(e + EEntityId):X}, link {k}{(_streamPrims.ContainsKey(s) ? ", stream copy" : "")})";
                if (p < PrimBufAddr || p >= PrimBufAddr + MaxPrims * 0x34) break;
                p = m.ReadU32(p);
            }
        }
        foreach (var (slot, ch) in _streamPrims) if (ch.Index == prim) return $"stream copy of slot {slot}";
        return "nobody we can see (P2's, or leaked)";
    }
}
