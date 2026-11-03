using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// The extra seat's Alucard (NetplayMenu.cs) always wears a Twilight Cloak that glows
// purple and black instead of red and black, whatever cape is on, in his own game and
// on the others' copies of him.
// UpdateCapePalette picks the cloak's palettes by the cape worn: for the Twilight Cloak
// 0x40A (its colours) and 0x415 (its glow), entries 0x0A and 0x15 of dra's palette table
// D_800A3BB8: a header (step | frame delay << 16, first colour in g_Clut, count), then
// pointers to each frame's colours, ending at -1. For him the call runs with the Twilight
// Cloak on and those two entries pointing at purple copies of them (made once, in spare
// high RAM; the palette animation keeps reading the copies).
public partial class OnlineCoop
{
    const uint PaletteTableAddr = 0x800A3BB8u;            // D_800A3BB8
    const uint PurpleCloakAddr = 0x80720000u;             // (spare high RAM: the copies)
    const int CloakColours = 0x0A, CloakGlow = 0x15;
    const uint ItemTwilightCloak = 0x38, CapeSlot = 4;
    const float PurpleHue = 285;
    uint _purpleColours, _purpleGlow;

    [Replace("dra", "UpdateCapePalette")]
    public static void UpdateCapePaletteHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || !self.PurpleCape) { orig(c, m); return; }
        if (self._purpleColours == 0) self.MakePurpleCloak(m);
        uint capeAddr = StatusEquipAddr + CapeSlot * 4, cape = m.ReadU32(capeAddr);
        uint colours = m.ReadU32(PaletteTableAddr + CloakColours * 4), glow = m.ReadU32(PaletteTableAddr + CloakGlow * 4);
        m.WriteU32(capeAddr, ItemTwilightCloak);
        m.WriteU32(PaletteTableAddr + CloakColours * 4, self._purpleColours);
        m.WriteU32(PaletteTableAddr + CloakGlow * 4, self._purpleGlow);
        try { orig(c, m); }
        finally
        {
            m.WriteU32(capeAddr, cape);
            m.WriteU32(PaletteTableAddr + CloakColours * 4, colours);
            m.WriteU32(PaletteTableAddr + CloakGlow * 4, glow);
        }
    }

    // Whose cloak this call is: a friend's copy (captured with them live), or ours
    bool PurpleCape => _capturingCape ? FriendOf(R).Name == ChaosName : _chaos && LocalAlucard;

    void MakePurpleCloak(IMemory m)
    {
        uint at = PurpleCloakAddr;
        _purpleColours = CopyPurple(m, m.ReadU32(PaletteTableAddr + CloakColours * 4), ref at);
        _purpleGlow = CopyPurple(m, m.ReadU32(PaletteTableAddr + CloakGlow * 4), ref at);
        Console.WriteLine($"[coop-online] the purple cloak: {at - PurpleCloakAddr} bytes");
    }

    // A palette entry's copy at `at`, its colours purple: header, frame pointers, frames
    static uint CopyPurple(IMemory m, uint desc, ref uint at)
    {
        uint count = m.ReadU32(desc + 8);
        int frames = 0;
        while (frames < 64 && m.ReadU32(desc + 12 + (uint)frames * 4) != 0xFFFFFFFFu) frames++;
        uint copy = at, list = copy + 12, data = (list + (uint)(frames + 1) * 4 + 3) & ~3u;
        for (uint i = 0; i < 3; i++) m.WriteU32(copy + i * 4, m.ReadU32(desc + i * 4));
        for (int f = 0; f < frames; f++)
        {
            uint src = m.ReadU32(desc + 12 + (uint)f * 4), dst = data + (uint)f * count * 2;
            for (uint k = 0; k < count; k++) m.WriteU16(dst + k * 2, CoopPalettes.WithHue(m.ReadU16(src + k * 2), PurpleHue));
            m.WriteU32(list + (uint)f * 4, dst);
        }
        m.WriteU32(list + (uint)frames * 4, 0xFFFFFFFFu);
        at = (data + (uint)frames * count * 2 + 3) & ~3u;
        return copy;
    }
}
