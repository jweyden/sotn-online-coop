using System;
using System.Linq;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// A new game's castle-entrance intro, for both Alucards. Alucard doesn't run in by
// himself there: two stage objects push PLAYER along (EntityPushAlucard on the run
// to the gate, EntityCastleDoorTransition through the door into the hall) and fake
// his buttons through g_Player.padSim/demo_timer. They only ever touch PLAYER, so
// P2 would stand at the start. Each one runs a second time with P2's body swapped
// in, from the same state it had before P1's turn, and afterwards everything it
// touched is put back the way P1's turn left it: one intro, two runners. P2 runs a
// little behind (in the friend's game it's the other way round).
public abstract partial class CoopCore
{
    const uint GraphicsStructAddr = 0x800973F8u; const int GraphicsStructSize = 0x94; // g_unkGraphicsStruct (camera offset unk14)
    const uint PauseAllowedAddr = 0x8003C8B8u;     // g_PauseAllowed
    const int RunBehind = 40, DoorBehind = 16;     // px P2 runs behind P1 (outside; through the door, closer)
    protected int IntroBehind = RunBehind;
    long _introPushMs = -1_000_000;   // (not long.MinValue: now - MinValue overflows, and the intro never ends)
    protected bool IntroRunning => Environment.TickCount64 - _introPushMs < 250;   // a push moved P1 just now

    [Replace("no3", "EntityPushAlucard")]
    public static void PushAlucardHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is { SkipEntranceRun: true } self) { self.SkipRun(c, m); return; }
        PushBoth(orig, c, m, RunBehind);
    }

    // Online, the run is skipped: everyone starts on the entrance's first screen, in
    // control, side by side. The push object goes on its first frame (before it takes
    // the player), and pausing is allowed (the run would have turned it off).
    protected virtual bool SkipEntranceRun => false;
    const uint FnDestroyEntityIntro = 0x80106590u;   // DestroyEntity

    void SkipRun(CpuContext c, IMemory m)
    {
        Call(c, m, FnDestroyEntityIntro, c.A0);
        m.WriteU8(PauseAllowedAddr, 1);
        m.WriteU32(PlayerPadSimAddr, 0);
        m.WriteU32(PlayerDemoTimerAddr, 0);
        Console.WriteLine("[coop] entrance: no run, everyone starts here");
    }

    // (online too: the door's push is the run's end, and a Richter or Maria start can
    // still have it, sliding them in while friends' copies jump about)
    [Replace("no3", "EntityCastleDoorTransition")]
    public static void CastleDoorTransitionHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is { SkipEntranceRun: true } self) { self.SkipRun(c, m); return; }
        PushBoth(orig, c, m, DoorBehind);
    }

    static void PushBoth(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m, int behind)
    {
        var self = I;
        var pushedToo = self?._remotes.Where(r => self.IsPresent(r) && self.IntroAllowedFor(r)).ToList();
        if (self == null || pushedToo == null || pushedToo.Count == 0) { orig(c, m); return; }
        uint e = c.A0;
        var regs = c.Snapshot();
        var before = RamBytes(m, e, EntitySize).ToArray();
        uint p1x = m.ReadU32(EntitiesAddr);   // posX

        orig(c, m);   // P1's turn
        // (the door one stays in the hall, idle, once it's done: only count real pushes)
        bool pushed = m.ReadU32(EntitiesAddr) != p1x;
        if (pushed) { self._introPushMs = Environment.TickCount64; self.IntroBehind = behind; }

        var after = RamBytes(m, e, EntitySize).ToArray();
        var gfx = RamBytes(m, GraphicsStructAddr, GraphicsStructSize).ToArray();
        var afterimage = RamBytes(m, EntityAddr(1), EntitySize).ToArray();   // (P1's after-image switch)
        uint pause = m.ReadU32(PauseAllowedAddr);
        var regsAfter = c.Snapshot();

        if (m.ReadU16(e + EEntityId) != 0)   // (still there: it didn't finish this frame)
        {
            // each other player's turn, from the same starting point
            foreach (var r in pushedToo)
            {
                before.CopyTo(RamBytes(m, e, EntitySize));
                c.Restore(regs);
                self.WithRemote(r, () => self.WithP2Body(m, () => orig(c, m)));
                if (r.IntroOffsetFor != e)
                {
                    r.IntroOffsetFor = e;
                    r.Regions[RegEntities][0] -= (uint)(behind << 16);   // posX.i.hi
                }
            }
            after.CopyTo(RamBytes(m, e, EntitySize));
            gfx.CopyTo(RamBytes(m, GraphicsStructAddr, GraphicsStructSize));
            afterimage.CopyTo(RamBytes(m, EntityAddr(1), EntitySize));
            m.WriteU32(PauseAllowedAddr, pause);
            c.Restore(regsAfter);
        }
        if (pushed) foreach (var r in pushedToo) r.Discontinuity = true;   // pushed, not walked: a replay from inputs can't redo it
    }

    bool IntroAllowedFor(Remote r) { var was = R; R = r; try { return IntroAllowed; } finally { R = was; } }
}
