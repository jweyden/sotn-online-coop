// The Saturn's character select screen (TITLE.PRG / TITLE.MAP / TITLE.CHR) as one file the
// mod draws over the PlayStation's name entry (CHARSEL.BIN). A port of
// tools/charselect-convert.py, output byte for byte the same.
//   - the menus are one VDP2 layer: 40x64-cell pages of 1-word pattern names (table at
//     0x060AC880, in TITLE.MAP), with cells and a 256-colour palette LZSS-compressed in
//     TITLE.MAP; character select is the top half of page 0, scrolled to y = 8
//   - the chosen frame pulses with a 20-step palette ramp (0x060ACB10)
//   - portraits: 80x88 8-bit sprites in TITLE.CHR, a 256-colour palette each in TITLE.PRG
//   TITLE.PRG's offsets into TITLE.MAP are 0x4000 past the file offset.
//   "CSEL", u16 width, height (320x224), u8 page[w*h] (palette << 4 | colour; 0 is
//   transparent), u16 pagePalette[256], u16 glow[20][16] (Saturn RGB555), 3 x { u16 w, h;
//   u8 pixels[w*h]; u16 palette[256] } (Alucard, Richter, Maria). Little-endian.
static class CharSelect
{
    const int MapShift = 0x4000, CellsSize = 0x4D46, PalOfs = 0x6B212, PalSize = 0x112;
    const uint Pages = 0x060AC880, CellsPtr = 0x060AC870, Glow = 0x060ACB10;
    const int ScrollY = 8, LegendRow = 26, W = 320, H = 224;   // (the row of the Saturn's button legend on: left out)
    static readonly (int Chr, uint Palette)[] Portraits = [(0x1C880, 0x060B1464), (0x1E400, 0x060B166E), (0x1FF80, 0x060B1878)];
    const int PortraitW = 80, PortraitH = 88;

    public static byte[] Build(byte[] prg, byte[] map, byte[] chr)
    {
        var cells = Lzss(map, (int)Saturn.U32(prg, Saturn.Prg(CellsPtr)) - MapShift, CellsSize);
        var pal = Lzss(map, PalOfs - MapShift, PalSize);
        int names = (int)Saturn.U32(prg, Saturn.Prg(Pages)) - MapShift;

        var page = new byte[W * H];
        for (int y = 0; y < H; y++)
        {
            int py = y + ScrollY, ty = py >> 3;
            if (ty >= LegendRow) continue;
            for (int x = 0; x < W; x++)
            {
                int pn = Saturn.U16(map, names + (ty * 40 + (x >> 3)) * 2);
                int ch = pn & 0x3FF, hf = pn >> 10 & 1, vf = pn >> 11 & 1, pl = pn >> 12;
                int cy = vf != 0 ? 7 - (py & 7) : py & 7, cx = hf != 0 ? 7 - (x & 7) : x & 7;
                int b = ch * 32 + 32 <= cells.Length ? cells[ch * 32 + cy * 4 + (cx >> 1)] : 0;
                int v = cx % 2 == 0 ? b >> 4 : b & 15;
                if (v != 0) page[y * W + x] = (byte)(pl << 4 | v);
            }
        }
        var out_ = new List<byte>();
        void U16(int v) { out_.Add((byte)v); out_.Add((byte)(v >> 8)); }
        out_.AddRange("CSEL"u8.ToArray());
        U16(W); U16(H);
        out_.AddRange(page);
        for (int i = 0; i < 256; i++) U16(Saturn.U16(pal, i * 2));
        for (int i = 0; i < 320; i++) U16(Saturn.U16(prg, Saturn.Prg(Glow) + i * 2));
        foreach (var (o, pa) in Portraits)
        {
            U16(PortraitW); U16(PortraitH);
            out_.AddRange(chr.AsSpan(o, PortraitW * PortraitH).ToArray());
            for (int i = 0; i < 256; i++) U16(Saturn.U16(prg, Saturn.Prg(pa) + 2 + i * 2));   // (after a header word)
        }
        return out_.ToArray();
    }

    // src/saturn/zero.c DecompressLZSS: srclen bytes from at (a match may end one past)
    static byte[] Lzss(byte[] src, int at, int srclen)
    {
        var buf = new byte[1024 + 34];
        var out_ = new List<byte>();
        int r = 1024 - 34, s = 0;
        uint flags = 0;
        while (true)
        {
            flags >>= 1;
            if ((flags & 0x100) == 0) flags = src[at + s++] | 0xFF00u;
            if ((flags & 1) != 0)
            {
                byte c = src[at + s++];
                out_.Add(c);
                if (s == srclen) break;
                buf[r] = c; r = (r + 1) & 1023;
            }
            else
            {
                int i = src[at + s], j = src[at + s + 1];
                s += 2;
                i |= (j & 0xE0) << 3;
                for (int k = 0; k < (j & 0x1F) + 3; k++)
                {
                    byte c = buf[(i + k) & 1023];
                    out_.Add(c); buf[r] = c; r = (r + 1) & 1023;
                }
                if (s == srclen || s == srclen + 1) break;
            }
        }
        return out_.ToArray();
    }
}
