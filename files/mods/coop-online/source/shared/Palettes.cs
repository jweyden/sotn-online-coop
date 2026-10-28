using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

// Each player's colour set (CoopPalettes.cs): their character's sprite palettes,
// recoloured, go to VRAM after the game's own (the game's RAM copy, g_Clut, stays
// as it is, so its palette effects keep working). A remote player's go to its bank
// in its render pass; the local player's to the game's own rows before drawing.
// Palette p sits in VRAM at ((p & 0xF) * 16, 0xF0 + ((p >> 4) & 0xF)).
public abstract partial class CoopCore
{
    // R is live, its palettes are in its bank: recolour them there.
    protected void ApplyPalette(CpuContext c, IMemory m)
    {
        if (!CoopPalettes.Enabled || R.Palette <= 0) return;
        foreach (int p in CoopPalettes.PaletteIds(R.Character))
        {
            var pal = new ushort[16];
            for (int i = 0; i < 16; i++)
            {
                // two Alucards share palette 0x100 but for the cloak (Remote.Cape)
                pal[i] = !OwnPalettes(R) && p == AlucardPalette && i >= CapeFirstColor && i < CapeFirstColor + CapeColors
                    ? R.Cape[i - CapeFirstColor]
                    : m.ReadU16(ClutAddr + (uint)(p * 32 + i * 2));
                pal[i] = CoopPalettes.Recolor(R.Character, R.Palette, pal[i]);
            }
            UploadPalette(p, R.BankY, pal);
        }
    }

    // P1 live, before drawing: the local player's colour set over its own palettes.
    protected void ApplyLocalPalette(IMemory m)
    {
        if (!CoopPalettes.Enabled || _p1Palette <= 0) return;
        foreach (int p in CoopPalettes.PaletteIds(_p1Char))
        {
            var pal = new ushort[16];
            for (int i = 0; i < 16; i++) pal[i] = CoopPalettes.Recolor(_p1Char, _p1Palette, m.ReadU16(ClutAddr + (uint)(p * 32 + i * 2)));
            UploadPalette(p, 0, pal);
        }
    }

    static void UploadPalette(int p, int bankY, ushort[] pal)
    {
        var bytes = new byte[32];
        Buffer.BlockCopy(pal, 0, bytes, 0, 32);
        UploadVram((p & 0xF) * 16, BankedTop + ((p >> 4) & 0xF) + bankY, 16, 1, bytes);
    }
}
