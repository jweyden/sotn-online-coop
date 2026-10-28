using System;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// PSP Maria's respawn (2026-10-26): a pillar of light comes down, and she fades in
// within it a little above the floor, her Cardinal's fire wings spread, then floats
// softly down onto it as the wings burn out.
//  - The wings are her own Cardinal attack (MarDoSubweapon with the Cardinal, Up held:
//    EntityMariaCardinalAttack, step PL_S_CARDINAL_ATTACK). That step does nothing but
//    wait for the wings (no gravity), so she can hang in the air while they're out.
//  - The fade is the game's own: ENTITY_OPACITY (texture shading) on her body with
//    additive blending, brightness rising, as Alucard's reappearing does.
//  - The pillar is drawn here, as host images (it isn't any of her objects).
// Friends see it too: their copy of her gets the wings and the fade (her position
// comes from her own game, hovering included), and the pillar where she is.
public partial class OnlineCoop
{
    const uint PlayerPadPressedAddr = PlayerAddr + 0x318;     // g_Player.padPressed
    const uint PlayerPadUp = 0x1000;
    const uint MarSubweaponCardinal = 1;
    const ushort StepMarCardinalAttack = 28;                  // PL_S_CARDINAL_ATTACK
    const uint EOpacity = 0x6C; const byte DrawOpacity = 0x08, BlendAdd = 0x30;   // entity->opacity, ENTITY_OPACITY, BLEND_TRANSP | BLEND_ADD
    const int WingsLift = 24, WingsFade = 40, WingsLandFrom = 16, WingsLandFrames = 60, WingsEnd = 84;
    const ushort EMarCardinalAttack = 38;                      // E_CARDINAL_ATTACK: the wings

    // our own: when it started, and how high we're held right now
    long _wingsAt = -1; int _wingsLift;

    // Her Cardinal attack, free; true if she's in it (she has to be standing).
    bool StartWings(CpuContext c, IMemory m)
    {
        uint sub = m.ReadU32(StatusGearAddr), hearts = m.ReadU32(StatusStatsAddr + SHearts * 4), pad = m.ReadU32(PlayerPadPressedAddr);
        m.WriteU32(StatusGearAddr, MarSubweaponCardinal);
        m.WriteU32(StatusStatsAddr + SHearts * 4, 99);
        m.WriteU32(PlayerPadPressedAddr, pad | PlayerPadUp);
        CallByName(c, m, "maria", "MarDoSubweapon");
        m.WriteU32(PlayerPadPressedAddr, pad);
        m.WriteU32(StatusGearAddr, sub);
        m.WriteU32(StatusStatsAddr + SHearts * 4, hearts);
        return m.ReadU16(EntitiesAddr + EStep) == StepMarCardinalAttack;
    }

    // Local (P1 live): lift her, start the fade, put the pillar up.
    void StartOurWings(CpuContext c, IMemory m)
    {
        if (!StartWings(c, m)) return;
        _wingsAt = _vsyncs; _wingsLift = 0;
        AddPillar((int)m.ReadU32(PlayerXAddr), (int)m.ReadU32(PlayerYAddr));
        TickOurWings(m);
    }

    // Once a frame (P1 live), from TickDeath.
    void TickOurWings(IMemory m)
    {
        if (_wingsAt < 0) return;
        long t = _vsyncs - _wingsAt;
        bool on = t < WingsEnd && m.ReadU16(EntitiesAddr + EStep) == StepMarCardinalAttack;
        // up, then gently down (eased), by moving her body: dra follows it with g_PlayerY
        int lift = !on ? 0 : t < WingsLandFrom ? WingsLift
            : (int)Math.Round(WingsLift * (1 + Math.Cos(Math.PI * Math.Min(1.0, (t - WingsLandFrom) / (double)WingsLandFrames))) / 2);
        if (lift != _wingsLift)
        {
            m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) - (lift - _wingsLift)));
            m.WriteU32(EntitiesAddr + EVelY, 0);
            _wingsLift = lift;
        }
        Fade(m, on ? t : WingsFade);
        HarmlessWings(m);
        if (!on) _wingsAt = -1;
    }

    // A friend's copy of her (their pass): the wings and the fade; where she is
    // comes from her own game.
    void StartFriendWings(CpuContext c, IMemory m, FriendFxState fx)
    {
        if (!StartWings(c, m)) return;
        fx.WingsAt = _vsyncs;
        AddPillar(F.Peer.WorldX, F.Peer.WorldY + WingsLift);
        // up with her at once (her game lifts her this frame): following her up instead
        // looked like a slide
        m.WriteU16(EntitiesAddr + EPosYHi, (ushort)(m.ReadU16(EntitiesAddr + EPosYHi) - WingsLift));
        R.Discontinuity = true;
        Fade(m, 0);
    }

    void TickFriendWings(IMemory m, FriendFxState fx)
    {
        if (fx.WingsAt < 0) return;
        long t = _vsyncs - fx.WingsAt;
        Fade(m, t);
        HarmlessWings(m);
        if (t >= WingsEnd) fx.WingsAt = -1;
    }

    // A respawn hurts nobody: the wings' hitbox stays off (it's an attack of hers).
    static void HarmlessWings(IMemory m)
    {
        for (int i = 4; i < PlayerEntityCount; i++)
            if (m.ReadU16(EntityAddr(i) + EEntityId) == EMarCardinalAttack) m.WriteU16(EntityAddr(i) + EHitboxState, 0);
    }

    // Brightness up from nothing over WingsFade frames, glowing (additive) on the way.
    static void Fade(IMemory m, long t)
    {
        byte flags = m.ReadU8(EntitiesAddr + EDrawFlags);
        if (t >= WingsFade)
        {
            if ((flags & DrawOpacity) == 0) return;
            m.WriteU8(EntitiesAddr + EDrawFlags, (byte)(flags & ~DrawOpacity));
            m.WriteU8(EntitiesAddr + EBlendMode, 0);
            return;
        }
        m.WriteU8(EntitiesAddr + EDrawFlags, (byte)(flags | DrawOpacity));
        m.WriteU8(EntitiesAddr + EBlendMode, BlendAdd);
        m.WriteU8(EntitiesAddr + EOpacity, (byte)(0x80 * t / WingsFade));
    }

    // ---------------------------------------------------------------- the pillar of light

    const int PillarFrames = 48, PillarWidth = 28;
    const uint CurrentBufferPtr = 0x8006C37Cu, BackbufferX = 0x8006C39Cu, BackbufferY = 0x8006C3A0u;
    const uint OtOfs = 0x474; const int OtSize = 0x200, PillarOrder = 0x1C0;
    readonly List<(int X, int Y, long At)> _pillars = new();

    void AddPillar(int worldX, int worldY) { lock (_pillars) _pillars.Add((worldX, worldY, _vsyncs)); }

    [PostHook("dra", "RenderEntities")]
    public static void DrawPillars(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || self._inP2 || self._p2Rendering) return;
        lock (self._pillars)
        {
            self._pillars.RemoveAll(p => self._vsyncs - p.At > PillarFrames);
            if (self._pillars.Count == 0) return;
            GpuPrims.SetOrderingTable(m.ReadU32(CurrentBufferPtr) + OtOfs, OtSize);
            int bx = (int)m.ReadU32(BackbufferX), by = (int)m.ReadU32(BackbufferY);
            int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
            foreach (var (wx, wy, at) in self._pillars)
            {
                long t = self._vsyncs - at;
                // it opens, holds, then narrows away; brightest in the middle
                double open = t < 8 ? t / 8.0 : t < 28 ? 1 : 1 - (t - 28) / (double)(PillarFrames - 28);
                float half = (float)(PillarWidth / 2.0 * open), x = bx + wx - sx, bottom = by + wy - sy + 28;
                if (half < 1) continue;
                byte core = (byte)(220 * Math.Min(1.0, open + 0.2)), coreB = 255;
                PrimVertex V(float vx, float vy, byte k) => new(vx, vy, (byte)(k * core / 255), (byte)(k * core / 255), (byte)(k * coreB / 255));
                // two halves: black (adds nothing) at the edge, bright in the middle
                GpuPrims.Quad(PillarOrder, V(x - half, by, 0), V(x, by, 255), V(x - half, bottom, 0), V(x, bottom, 255),
                    semiTrans: true, blend: 1, raw: false, gouraud: true);
                GpuPrims.Quad(PillarOrder, V(x, by, 255), V(x + half, by, 0), V(x, bottom, 255), V(x + half, bottom, 0),
                    semiTrans: true, blend: 1, raw: false, gouraud: true);
            }
        }
    }
}
