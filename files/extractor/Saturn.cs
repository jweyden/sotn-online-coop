// Reading the Saturn game's player data: LZSS streams, and a player's sprites (the image
// table and graphics directory in its PRG, the compressed images in its CHR). Ports of
// what sotn-decomp's tools/saturn/assets (Rust) does for these. The Saturn is big-endian.
static class Saturn
{
    public const uint PrgBase = 0x060A5000;

    public static ushort U16(byte[] d, int o) => (ushort)(d[o] << 8 | d[o + 1]);
    public static short S16(byte[] d, int o) => (short)U16(d, o);
    public static uint U32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    public static int Prg(uint address, uint base_ = PrgBase) => (int)(address - base_);

    // LZSS: a 1 KB window, flag bytes (1 = a literal), matches of 3..34 bytes
    public static byte[] Lzss(ReadOnlySpan<byte> data)
    {
        const int Window = 0x400, MaxMatch = 34, Threshold = 2;
        var dict = new byte[Window];
        int cursor = Window - MaxMatch, src = 0;
        uint flags = 0;
        var out_ = new List<byte>(data.Length * 2);
        while (src < data.Length)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0) flags = data[src++] | 0xFF00u;
            if ((flags & 1) != 0)
            {
                if (src >= data.Length) break;
                byte v = data[src++];
                out_.Add(v); dict[cursor] = v; cursor = (cursor + 1) & (Window - 1);
            }
            else
            {
                if (src + 1 >= data.Length) break;
                int first = data[src], second = data[src + 1];
                src += 2;
                int index = first | (second & 0xE0) << 3, length = (second & 0x1F) + Threshold + 1;
                for (int i = 0; i < length; i++)
                {
                    byte v = dict[(index + i) & (Window - 1)];
                    out_.Add(v); dict[cursor] = v; cursor = (cursor + 1) & (Window - 1);
                }
            }
        }
        return out_.ToArray();
    }

    public sealed record Image(int Width, int Height, byte[] Pixels);   // 4-bit indices, one per byte

    // A player's images: the image table (u8 width/2, u8 height/2, u16 8-byte units, ending
    // at a zero size) and the directory the PRG points at from 0x40 (a backing base, then a
    // (CHR address, size) pair per image).
    public static List<Image> PlayerImages(byte[] prg, byte[] chr, uint imageTable, int entryBase = 0)
    {
        var table = new List<(int W, int H, int Units)>();
        for (int at = Prg(imageTable); ; at += 4)
        {
            ushort dims = U16(prg, at);
            table.Add((dims >> 8, dims & 0xFF, U16(prg, at + 2)));
            if ((dims >> 8) == 0 || (dims & 0xFF) == 0) break;
        }
        int dir = Prg(U32(prg, 0x40));
        uint backing = U32(prg, dir);
        var images = new List<Image>();
        for (int i = 0; i < table.Count - 1; i++)
        {
            int entry = dir + (entryBase + i) * 8;
            int offset = (int)(U32(prg, entry) - backing), size = (int)U32(prg, entry + 4);
            var raw = Lzss(chr.AsSpan(offset, size));
            int allocation = (table[i + 1].Units - table[i].Units) * 8;
            if (raw.Length != allocation) throw new InvalidDataException($"player image {i}: {raw.Length} bytes, expected {allocation}");
            int w = table[i].W * 2, h = table[i].H * 2;
            var px = new byte[w * h];
            for (int k = 0; k < w * h / 2; k++) { px[2 * k] = (byte)(raw[k] >> 4); px[2 * k + 1] = (byte)(raw[k] & 15); }
            images.Add(new Image(w, h, px));
        }
        return images;
    }

    // A frame table: count big-endian pointers to frames (u16 header: part count in bits
    // 8..13; u16 flags; parts of u16 attributes, s16 x, s16 y, u16 image). null: no frame.
    public sealed record Part(ushort Attr, short X, short Y, ushort Image);
    static int PartCount(byte[] prg, int at) => (U16(prg, at) >> 8) & 0x3F;
    public static List<Part[]?> Frames(byte[] prg, uint table, int count, uint base_ = PrgBase)
    {
        var frames = new List<Part[]?>();
        for (int i = 0; i < count; i++)
        {
            uint p = U32(prg, Prg(table, base_) + i * 4);
            if (p == 0) { frames.Add(null); continue; }
            int at = Prg(p, base_), parts = PartCount(prg, at);
            frames.Add(Enumerable.Range(0, parts).Select(k => at + 4 + k * 8)
                .Select(o => new Part(U16(prg, o), S16(prg, o + 2), S16(prg, o + 4), U16(prg, o + 6))).ToArray());
        }
        return frames;
    }

    // A sprite package's frames: count frames one after another from address, then their
    // pointer table (of tableCount)
    public static List<Part[]?> FrameGroup(byte[] prg, uint address, int count, int tableCount, uint base_ = PrgBase)
    {
        int at = Prg(address, base_);
        for (int i = 0; i < count; i++) at += 4 + PartCount(prg, at) * 8;
        return Frames(prg, (uint)(at + base_), tableCount, base_);
    }

    // An image table's images (u8 width/2, u8 height/2, u16 offset in 8-byte units; ending
    // at a 0x0 entry, whose offset is the total), uncompressed in data from start
    public static (List<Image> Images, int Size) ImageTable(byte[] prg, int table, byte[] data, int start)
    {
        var images = new List<Image>();
        for (int at = table; ; at += 4)
        {
            int w = prg[at] * 2, h = prg[at + 1] * 2, o = start + U16(prg, at + 2) * 8;
            if (w == 0 && h == 0) return (images, U16(prg, at + 2) * 8);
            var px = new byte[w * h];
            for (int k = 0; k < w * h / 2; k++) { px[2 * k] = (byte)(data[o + k] >> 4); px[2 * k + 1] = (byte)(data[o + k] & 15); }
            images.Add(new Image(w, h, px));
        }
    }

    // Saturn RGB555 (bit 15 = opaque) as a PlayStation colour: index 0 is transparent, and
    // black keeps the STP bit so it stays opaque
    public static ushort Ps1Color(ushort c, int index)
    {
        if (index == 0) return 0;
        c &= 0x7FFF;
        return c != 0 ? c : (ushort)0x8000;
    }

    public static int RoundHalfEven(double v) => (int)Math.Round(v, MidpointRounding.ToEven);   // (Python's round)
}
