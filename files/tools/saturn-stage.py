"""Converts a Saturn-only stage (STAGE_15 Garden, STAGE_16 Prison) from your own
Saturn disc into one file the saturn-areas mod loads.

usage: saturn-stage.py SATURN_TRACK1_BIN PRG MAP OUT [LAYERSET ...]
  PRG, MAP: the stage's files on the disc (STAGE_15.PRG STAGE_15.MAP; the
  Entrance revisit is STAGE_01.PRG STAGE_01.MAP). LAYERSETs pick rooms by their
  layer set (default: every normal room).

Per room, the Saturn .MAP holds three VDP2 layers (4bpp cells, RGB555 palettes,
1-word pattern names; all LZSS-compressed) and a collision grid already in PS1
coordinates (16px cells; the Saturn draws rooms 320px wide, PS1 x * 5/4). The
room bounds and stream offsets are the tables in sotn-decomp's src/saturn/
stage_xx/metadata.c, read here straight from the PRG's header (big-endian, loaded at
0x060DC000): rooms, compressed sizes, map offsets, layer setup, collision.
Layers are written as full-colour RGBA at Saturn resolution; the mod
draws them squeezed to PS1 width.

Output (little-endian):
  "SOTNSAT2", u32 roomCount
  per room: u8 left, top, right, bottom; u16 colW, colH; collision bytes;
            u32 layerCount; per layer: u16 width, height (pixels);
            u8 scrollDivX, scrollDivY, priority, pad; u32 zlibLen; zlib(RGBA);
            u32 entityCount; per entity: u16 x, y (Saturn pixels), id, slot, params
            (the room's spawn list, layout entry layer set + 1)
"""
import os, struct, sys, zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
iso = __import__("extract-iso")


def lzss(src, srclen):
    """src/saturn/zero.c DecompressLZSS."""
    if srclen == 0:
        return b""
    buf = bytearray(1024 + 34)
    r, out, s, flags = 1024 - 34, bytearray(), 0, 0
    while True:
        flags >>= 1
        if not flags & 0x100:
            flags = src[s] | 0xFF00
            s += 1
        if flags & 1:
            c = src[s]
            s += 1
            out.append(c)
            if s == srclen:
                break
            buf[r] = c
            r = (r + 1) & 1023
        else:
            i, j = src[s], src[s + 1]
            s += 2
            i |= (j & 0xE0) << 3
            for k in range((j & 0x1F) + 3):
                c = buf[(i + k) & 1023]
                out.append(c)
                buf[r] = c
                r = (r + 1) & 1023
            if s in (srclen, srclen + 1):
                break
    return bytes(out)


def rgb555(c):
    r, g, b = c & 31, (c >> 5) & 31, (c >> 10) & 31
    return bytes((r << 3 | r >> 2, g << 3 | g >> 2, b << 3 | b >> 2, 255))


def layer_rgba(m, offs, sizes):
    cells = lzss(m[offs[0]:], sizes[0])
    pal = lzss(m[offs[1]:], sizes[1])
    pal = [rgb555(c) for c in struct.unpack(">%dH" % (len(pal) // 2), pal)]
    names = lzss(m[offs[2]:], sizes[2])
    w, h = struct.unpack(">HH", names[:4])
    names = struct.unpack(">%dH" % (w * h), names[4:4 + 2 * w * h])
    W = w * 8
    img = bytearray(W * h * 8 * 4)
    for ty in range(h):
        for tx in range(w):
            pn = names[ty * w + tx]
            ch, hf, vf, pl = pn & 0x3FF, pn >> 10 & 1, pn >> 11 & 1, pn >> 12
            cell = cells[ch * 32:ch * 32 + 32]
            if len(cell) < 32:
                continue
            for py in range(8):
                row = cell[(7 - py if vf else py) * 4:][:4]
                for px in range(8):
                    sx = 7 - px if hf else px
                    v = row[sx >> 1] >> 4 if sx % 2 == 0 else row[sx >> 1] & 15
                    if v:
                        i = ((ty * 8 + py) * W + tx * 8 + px) * 4
                        img[i:i + 4] = pal[(pl * 16 + v) % len(pal)]
    return W, h * 8, bytes(img)


def main():
    disc, prg_name, map_name, out = sys.argv[1:5]
    pick = [int(x, 0) for x in sys.argv[5:]]
    with open(disc, "rb") as f:
        iso.RAW = f.read(12) == iso.SYNC
        pvd = iso.read(f, 16, iso.S)
        root = pvd[156:190]
        files = {p: (l, s) for p, l, s in iso.walk(f, struct.unpack_from("<I", root, 2)[0],
                                                     struct.unpack_from("<I", root, 10)[0], "", set())}
        prg = iso.read(f, *files[prg_name])
        m = iso.read(f, *files[map_name])
    base = 0x060DC000
    hdr = struct.unpack_from(">10I", prg, 0)
    hlay_at, rooms_at, sizes_at, offs_at, setup_at, cols_at = (hdr[i] - base for i in (2, 4, 5, 6, 7, 8))
    rooms = []  # (left, top, right, bottom, layer set)
    i = rooms_at
    while prg[i] != 0x40:
        l, t, r, b, meta, rec = prg[i:i + 6]
        if rec != 0xFF and meta < 0x40 and (not pick or meta in pick):
            rooms.append((l, t, r, b, meta))
        i += 6
    n = len(rooms)
    res = bytearray(b"SOTNSAT2" + struct.pack("<I", n))
    # NBG2 (layer 1) is drawn first, then NBG3 (layer 2), then NBG1 (layer 0)
    priority = {1: 0, 2: 1, 0: 2}
    for left, top, right, bottom, ls in rooms:
        co, cs = struct.unpack_from(">2I", prg, cols_at + ls * 8)
        col = lzss(m[co:], cs)
        cw = (right - left + 1) * 16
        ch = len(col) // cw
        res += struct.pack("<4B2H", left, top, right, bottom, cw, ch) + col
        layers = []
        for l in range(3):
            o = struct.unpack_from(">3I", prg, offs_at + (ls * 3 + l) * 12)
            sz = struct.unpack_from(">3I", prg, sizes_at + (ls * 3 + l) * 12)
            if sz[2] == 0:
                continue
            W, H, rgba = layer_rgba(m, o, sz)
            dx, dy = prg[setup_at + ls * 9 + l * 3], prg[setup_at + ls * 9 + l * 3 + 1]
            z = zlib.compress(rgba, 9)
            layers.append(struct.pack("<2H4BI", W, H, dx, dy, priority[l], 0, len(z)) + z)
        res += struct.pack("<I", len(layers)) + b"".join(layers)
        ents = []
        e = struct.unpack_from(">I", prg, hlay_at + (ls + 1) * 4)[0] - base
        while True:
            x, y, eid, slot, par = struct.unpack_from(">5H", prg, e)
            e += 10
            if x == 0xFFFF:
                break
            if x != 0xFFFE:
                ents.append(struct.pack("<5H", x, y, eid, slot, par))
        res += struct.pack("<I", len(ents)) + b"".join(ents)
        print(f"room (layer set {ls:#x}): map {left},{top}-{right},{bottom}, collision {cw}x{ch}, {len(layers)} layers, {len(ents)} entities")
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    open(out, "wb").write(res)
    print(f"wrote {out} ({len(res)} bytes)")


if __name__ == "__main__":
    main()
