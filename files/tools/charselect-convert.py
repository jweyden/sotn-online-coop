"""The Saturn's character select screen (TITLE.PRG / TITLE.MAP / TITLE.CHR) ->
one file the maria mod draws over the PS1's name entry.

What TITLE.PRG does (decompiled; title states 0x1b..0x1d):
  - The menus are one VDP2 layer: four 40x64-cell pages of 1-word pattern names
    (table at 0x060AC880, uncompressed in TITLE.MAP), drawn with cells and a
    256-colour palette that are LZSS-compressed in TITLE.MAP. Character select
    is the top half of page 0, scrolled to y = 8.
  - The three frames use palettes 7 (Richter, left), 13 (Alucard) and 14
    (Maria, right). The chosen one pulses: its palette is copied each frame from
    a 20-step ramp at 0x060ACB10; the others get step 0.
  - Portraits are VDP1 sprites from TITLE.CHR: 80x88, 8 bits per pixel, one
    256-colour palette each in TITLE.PRG, centred at (72,124), (168,124),
    (264,124) on screen.
TITLE.PRG's offsets into TITLE.MAP are from 0x00252000; the file loads at
0x00256000, so they're 0x4000 past the file offset.

Output (little-endian):
  "CSEL", u16 width, height (320x224, the screen),
  u8 page[w*h]  (palette << 4 | colour; colour 0 is transparent)
  u16 pagePalette[256], u16 glow[20][16]       (Saturn RGB555)
  3 x { u16 w, h; u8 pixels[w*h]; u16 palette[256] }   Alucard, Richter, Maria

usage: charselect-convert.py DECOMP_DIR OUT
"""
import importlib.util, os, struct, sys

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("satstage", os.path.join(here, "saturn-stage.py"))
satstage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(satstage)

BASE = 0x060A5000
MAP_SHIFT = 0x4000
PAGES = 0x060AC880
CELLS_PTR = 0x060AC870          # page cells (func_060A7EEC)
CELLS_SIZE, PAL_OFS, PAL_SIZE = 0x4D46, 0x6B212, 0x112
GLOW = 0x060ACB10
SCROLL_Y = 8
LEGEND_ROW = 26                  # Saturn button legend (Japanese): left out
W, H = 320, 224
# (TITLE.CHR offset, palette in TITLE.PRG) for Alucard, Richter, Maria
PORTRAITS = [(0x1C880, 0x060B1464), (0x1E400, 0x060B166E), (0x1FF80, 0x060B1878)]
PORTRAIT_W, PORTRAIT_H = 80, 88


def main():
    decomp, out = sys.argv[1:3]
    d = decomp + "/disks/saturn/"
    prg = open(d + "TITLE.PRG", "rb").read()
    mp = open(d + "TITLE.MAP", "rb").read()
    chr_ = open(d + "TITLE.CHR", "rb").read()
    u32 = lambda a: struct.unpack_from(">I", prg, a - BASE)[0]

    cells = satstage.lzss(mp[u32(CELLS_PTR) - MAP_SHIFT:], CELLS_SIZE)
    pal = satstage.lzss(mp[PAL_OFS - MAP_SHIFT:], PAL_SIZE)
    pal = list(struct.unpack(">256H", pal[:512]))
    names = struct.unpack_from(">%dH" % (40 * 64), mp, u32(PAGES) - MAP_SHIFT)

    page = bytearray(W * H)
    for y in range(H):
        py = y + SCROLL_Y
        ty = py >> 3
        if ty >= LEGEND_ROW:
            continue
        for x in range(W):
            pn = names[ty * 40 + (x >> 3)]
            ch, hf, vf, pl = pn & 0x3FF, pn >> 10 & 1, pn >> 11 & 1, pn >> 12
            cy = 7 - (py & 7) if vf else py & 7
            cx = 7 - (x & 7) if hf else x & 7
            b = cells[ch * 32 + cy * 4 + (cx >> 1)] if ch * 32 + 32 <= len(cells) else 0
            v = b >> 4 if cx % 2 == 0 else b & 15
            if v:
                page[y * W + x] = pl << 4 | v

    blob = b"CSEL" + struct.pack("<HH", W, H) + bytes(page)
    blob += struct.pack("<256H", *pal)
    blob += struct.pack("<320H", *struct.unpack_from(">320H", prg, GLOW - BASE))
    for ofs, pa in PORTRAITS:
        px = chr_[ofs:ofs + PORTRAIT_W * PORTRAIT_H]
        # palette: a header word, then 256 colours
        cols = struct.unpack_from(">256H", prg, pa - BASE + 2)
        blob += struct.pack("<HH", PORTRAIT_W, PORTRAIT_H) + px + struct.pack("<256H", *cols)
    open(out, "wb").write(blob)
    print("charselect: %d bytes" % len(blob))


if __name__ == "__main__":
    main()
