using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Level-ups belong to P1. EntityAlucard checks for one every frame against g_Status's
// level and EXP, which aren't swapped for P2 (they're P1's), so P2's pass must not
// check: it would level P1 up on P2's body, at the wrong time. (Locally P2 gets P1's
// gains through MirrorP1Growth; online the friend's own game levels them up and says
// so, and we play the animation on P2 then.)
//
// The "LEVEL UP" animation freezes the world (unk28 = 3: the spell freeze) while it
// plays. On P2 it mustn't: only the player who levelled up stops.
public abstract partial class CoopCore
{
    const uint FreezeAddr = 0x80097420u;   // g_unkGraphicsStruct.unk28
    protected const uint BpLevelUpText = 0x19;   // BP_LEVEL_UP_TEXT

    [Replace("dra", "CheckAndDoLevelUp")]
    public static void CheckAndDoLevelUpHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is { _inP2: true }) { c.V0 = 0; return; }
        orig(c, m);
    }

    [Replace("dra", "EntityLevelUpAnimation")]
    public static void EntityLevelUpAnimationHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is not { _inP2: true }) { orig(c, m); return; }
        uint freeze = m.ReadU32(FreezeAddr);
        orig(c, m);
        m.WriteU32(FreezeAddr, freeze);
    }
}
