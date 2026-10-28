using System;

// Each character's three colour sets (shared by the co-op mods and the Maria mod's
// select screen, source/characters). Set 0 is the game's own; sets 1
// and 2 turn the hue of the character's clothing colours, leaving skin, hair and
// greys (whites, blacks, metal) alone. They're applied to the character's sprite
// palettes wherever that player is drawn (Palettes.cs).
public static class CoopPalettes
{
    // Colour sets are off until the flow around them is settled: everyone plays set 0.
    public const bool Enabled = false;

    // The sprite palettes each character's body draws with (palettes.h: PAL_ALUCARD,
    // PAL_RICHTER; PSP Maria's PAL_MARIA, and Richter's slot in case her PS1 build uses it).
    public static int[] PaletteIds(CoopCharacter c) => c switch
    {
        CoopCharacter.Alucard or CoopCharacter.AlucardPsp => new[] { 0x100 },
        CoopCharacter.MariaPsp => new[] { 0x114, 0x120 },
        _ => new[] { 0x120 },
    };

    // Hue turn (degrees) for sets 1 and 2.
    static readonly int[,] Turn =
    {
        { 140, 220 },   // Alucard: crimson, emerald
        { 140, 220 },   // PSP Alucard
        { 150, 250 },   // Richter: red, green
        { 100, 200 },   // long-coat Richter
        { 160, 240 },   // PSP Maria: blue, green
        { 150, 230 },   // Saturn Maria
    };

    public static readonly string[] SetNames = { "Original", "Colour 2", "Colour 3" };

    public static ushort Recolor(CoopCharacter c, int set, ushort bgr555)
    {
        if (set <= 0 || set >= 3 || (int)c >= Turn.GetLength(0)) return bgr555;
        int r = bgr555 & 31, g = (bgr555 >> 5) & 31, b = (bgr555 >> 10) & 31;
        if (r == 0 && g == 0 && b == 0) return bgr555;   // transparent / black
        RgbToHsv(r / 31f, g / 31f, b / 31f, out float h, out float s, out float v);
        // greys, and skin and hair (warm, not too saturated) stay as they are
        if (s < 0.18f) return bgr555;
        if (h >= 10 && h <= 55 && s < 0.7f) return bgr555;
        h = (h + Turn[(int)c, set - 1]) % 360;
        HsvToRgb(h, s, v, out float nr, out float ng, out float nb);
        return (ushort)((bgr555 & 0x8000) | To5(nr) | To5(ng) << 5 | To5(nb) << 10);
    }

    static int To5(float f) => Math.Clamp((int)Math.Round(f * 31), 0, 31);

    static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0f),
            < 2 => (x, c, 0f),
            < 3 => (0f, c, x),
            < 4 => (0f, x, c),
            < 5 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        r += m; g += m; b += m;
    }
}
