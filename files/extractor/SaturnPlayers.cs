// The Saturn's players as PlayStation players: their sprites squashed from the Saturn's
// 5/4-wide pixels to the PlayStation's (a .SPR, loaded at 0x80600000), and their programs'
// converted tables, written at the symbols' offsets from the recipe. Ports of the sprite
// parts of tools/mariasat-convert.py (Saturn Maria) and tools/richter2-convert.py (the
// long-coat Richter), output byte for byte the same.
static class SaturnPlayers
{
    public const uint SprBase = 0x80600000;

    // Bank: per frame, its sprite and the sprite's top left (null: no frame)
    public sealed record Result(byte[] Spr, Dictionary<string, byte[]> Symbols, List<(int Sprite, int Left, int Top)?> Bank);

    // Saturn Maria (MARIA.PRG / .CHR): her one-part frames; her palette and flash palette,
    // each frame's palette bank, her hitboxes, effect and charge palettes
    public static Result Maria(byte[] prg, byte[] chr)
    {
        const uint ImageTable = 0x060C51A8, PaletteTable = 0x060C398C, FrameTable = 0x060C4BB0;
        const int FrameCount = 382, Hitboxes = 0x1E7CC, HitboxCount = 112;
        const int EffectColors = 0x1C6CC, ChargeVariants = 0x1C770;
        var frames = Saturn.Frames(prg, FrameTable, FrameCount);
        bool OnePart(int n) => frames[n] is { Length: 1 };
        var r = Sprites(Saturn.PlayerImages(prg, chr, ImageTable), frames, OnePart);

        var sym = r.Symbols;
        var words = Palette(prg, PaletteTable, 2);
        sym["g_MariaSatPalette"] = Colors(words[..16]);
        sym["g_MarFlashPalette"] = Colors(words[16..32]);
        sym["g_MarFrameBank"] = Enumerable.Range(0, FrameCount).Select(n => OnePart(n) ? (byte)((frames[n]![0].Attr >> 8) & 31) : (byte)0).ToArray();
        var props = new List<byte>();
        for (int i = 0; i < HitboxCount; i++)
        {
            int o = Hitboxes + i * 4;
            sbyte x = (sbyte)prg[o], y = (sbyte)prg[o + 1], w = (sbyte)prg[o + 2], h = (sbyte)prg[o + 3];
            props.Add((byte)(sbyte)Saturn.RoundHalfEven(x / 1.25));
            props.Add((byte)y);
            props.Add((byte)(sbyte)(w != 0 ? Math.Max(1, Saturn.RoundHalfEven(w / 1.25)) : 0));
            props.Add((byte)h);
        }
        sym["g_MariaSatFrameProps"] = props.ToArray();
        sym["g_MarEffectPals"] = Colors(Enumerable.Range(0, 5 * 16).Select(i => Saturn.U16(prg, EffectColors + i * 2)).ToArray());
        sym["g_MarChargePals"] = Colors(Enumerable.Range(0, 15 * 16).Select(i => Saturn.U16(prg, ChargeVariants + i * 2)).ToArray());
        return r;
    }

    // The long-coat Richter (RICHTER2.PRG / .CHR): Richter's 213 frames (each one's first
    // part) and his palette
    public static Result Richter2(byte[] prg, byte[] chr)
    {
        const uint ImageTable = 0x060C120C, PaletteTable = 0x060C0240, FrameTable = 0x060C0EA0;
        var frames = Saturn.Frames(prg, FrameTable, 213);
        var r = Sprites(Saturn.PlayerImages(prg, chr, ImageTable), frames, n => frames[n] is { Length: > 0 });
        r.Symbols["g_Ric2Palette"] = Colors(Palette(prg, PaletteTable, 1)[..16]);
        return r;
    }

    // Sprites: one per image the frames use, plus mirrored copies for flipped frames, in the
    // order the frames first use them; and richter_sprites, their addresses
    static Result Sprites(List<Saturn.Image> images, List<Saturn.Part[]?> frames, Func<int, bool> used)
    {
        var sprites = new Dictionary<(int, bool), (int Index, byte[] Data)>();
        int Sprite(int idx, bool flip)
        {
            if (sprites.TryGetValue((idx, flip), out var s)) return s.Index;
            var im = images[idx];
            int w = ((im.Width * 4 + 4) / 5 + 3) & ~3;
            var data = new List<byte> { (byte)w, (byte)im.Height, 0, 0 };
            var row = new byte[w];
            for (int y = 0; y < im.Height; y++)
            {
                for (int j = 0; j < w; j++)
                {
                    int sx = (int)((j + 0.5) * 5 / 4);
                    row[j] = sx < im.Width ? (byte)(im.Pixels[y * im.Width + sx] & 15) : (byte)0;
                }
                if (flip) Array.Reverse(row);
                for (int j = 0; j < w; j += 2) data.Add((byte)(row[j] | row[j + 1] << 4));
            }
            sprites[(idx, flip)] = (sprites.Count, data.ToArray());
            return sprites.Count - 1;
        }
        var bank = new List<(int, int, int)?>();
        for (int n = 0; n < frames.Count; n++)
        {
            if (!used(n)) { bank.Add(null); continue; }
            var f = frames[n]![0];
            var im = images[f.Image];
            // (x, y) is the image centre in Saturn pixels
            bank.Add((Sprite(f.Image, (f.Attr & 0x10) != 0), Saturn.RoundHalfEven((f.X - im.Width / 2.0) / 1.25), f.Y - im.Height / 2 - 1));
        }
        var spr = new List<byte>();
        var pointers = new List<byte>();
        foreach (var (_, data) in sprites.Values.OrderBy(s => s.Index))
        {
            pointers.AddRange(BitConverter.GetBytes(SprBase + (uint)spr.Count));
            spr.AddRange(data);
            while (spr.Count % 4 != 0) spr.Add(0);
        }
        return new Result(spr.ToArray(), new() { ["richter_sprites"] = pointers.ToArray() }, bank);
    }

    // A palette object: a u16 count (banks of 16 in the low 14 bits), then the colours
    static ushort[] Palette(byte[] prg, uint at, int needBanks)
    {
        int o = Saturn.Prg(at), banks = Saturn.U16(prg, o) & 0x3FFF;
        if (banks < needBanks) throw new InvalidDataException($"the palette at {at:X8} has {banks} banks, not {needBanks}");
        return Enumerable.Range(0, needBanks * 16).Select(i => Saturn.U16(prg, o + 2 + i * 2)).ToArray();
    }

    static byte[] Colors(ushort[] words) =>
        words.Select((c, i) => Saturn.Ps1Color(c, i % 16)).SelectMany(x => new[] { (byte)x, (byte)(x >> 8) }).ToArray();

    // The converted tables written into a program (a copy of the blank one); each frame's
    // sprite entry where spritebank0 points
    public static void Apply(Result r, byte[] program, IReadOnlyDictionary<string, int> symbols)
    {
        foreach (var (name, bytes) in r.Symbols) bytes.CopyTo(program, symbols[name]);
        int table = symbols["spritebank0"];
        for (int i = 1; i < r.Bank.Count; i++)
        {
            if (r.Bank[i] is not var (s, left, top)) continue;
            int at = (int)(BitConverter.ToUInt32(program, table + i * 4) - 0x8013C000);
            foreach (var (k, v) in new[] { s | 0x8000, left, top, 0 }.Index())
                BitConverter.TryWriteBytes(program.AsSpan(at + k * 2), (short)v);
        }
    }
}
