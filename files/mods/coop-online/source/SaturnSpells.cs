using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

// Saturn Maria's spells online (2026-10-26). Her command moves 2 and 3 stop time
// (MarTimeStop: g_unkGraphicsStruct.unk28 = 4, which puts dra in Engine_5); with
// friends that froze everyone. Online they don't stop time, in her game either: it's
// cleared right after the two spell objects that set it run (MarEntity75: command 2's
// fire, MarEntity76: command 3's beast), before dra checks it later in the frame.
// Instead she can't be hurt while she casts (her command move steps, PL_S_MAR_CMD0
// 0x30 .. 0x35).
public partial class OnlineCoop
{
    const uint SpellFreezeAddr = 0x80097420u;                 // g_unkGraphicsStruct.unk28
    const uint MarTimeStopped = 4;                            // MarTimeStop(1)
    const ushort StepMarCmdFirst = 0x30, StepMarCmdLast = 0x35;
    const uint InvincibleTimerAddr = PlayerAddr + 0x330 + 14 * 2;   // g_Player.timers[PL_T_INVINCIBLE] (counts down)
    const ushort CastInvincibleFrames = 3;

    [PreHook("dra", "HandlePlay")]
    public static void BeforeHandlePlaySpells(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || self._inP2 || self._p1Char != CoopCharacter.MariaSaturn) return;
        if (self._net.Status != NetSession.State.Connected || !Game.InGame) return;
        ushort step = m.ReadU16(EntitiesAddr + EStep);
        if (step >= StepMarCmdFirst && step <= StepMarCmdLast && m.ReadU16(InvincibleTimerAddr) < CastInvincibleFrames)
            m.WriteU16(InvincibleTimerAddr, CastInvincibleFrames);
    }

    [PostHook("mariasat", "MarEntity75")]
    public static void AfterMarFireSpell(CpuContext c, IMemory m) => NoTimeStop(m);

    [PostHook("mariasat", "MarEntity76")]
    public static void AfterMarBeastSpell(CpuContext c, IMemory m) => NoTimeStop(m);

    static void NoTimeStop(IMemory m)
    {
        if (I is OnlineCoop self && self._net.Status == NetSession.State.Connected && m.ReadU32(SpellFreezeAddr) == MarTimeStopped)
            m.WriteU32(SpellFreezeAddr, 0);
    }
}
