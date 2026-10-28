"""Saturn Maria's player graphics -> PS1 player-overlay data.

Reads what sotn-decomp's Saturn asset tools extracted from your own Saturn disc
(assets/saturn/player/MARIA, src/saturn/maria/gen/frames.h, hitboxes.c) and
writes:
  OUT_SPR      sprite blob, loaded by mods/maria at SPR_BASE (high RAM; it's too
               big for the player overlay slot). PS1 player-sprite format: u8 w, h,
               pivotX, pivotY, then 4bpp pixels (w a multiple of 4).
  OUT_HDR      C data for the overlay: sprite pointer table, spritebank0 frames,
               palette, frame properties (hitboxes).

The Saturn draws everything 5/4 wider than the PS1 (320px screen): Maria's art
is squashed back 5 -> 4 columns, and x offsets and hitboxes are divided by 5/4.
Frame offsets on the Saturn are image centres (calibrated against Richter, whose
Saturn frames are PS1 frames stretched).

  OUT_FX       her effect graphics (MAR_W.CHR, sprite packages 1..12, and 13:
               the shared fire her fire spell throws), drawn by
               mods/maria as host images: per package its palette banks,
               images (8-bit indices) and frames (parts: attr, x, y, image),
               all in Saturn pixels.

usage: mariasat-convert.py DECOMP_DIR OUT_SPR OUT_HDR OUT_FX
"""
import json, re, struct, sys, zlib

SPR_BASE = 0x80600000

def read_png8(path):
    """8-bit indexed, non-interlaced PNG -> (width, height, index bytes)"""
    d = open(path, "rb").read()
    w, h, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", d[16:29])
    assert depth == 8 and ctype == 3 and not interlace, path
    idat = b""
    o = 8
    while o < len(d):
        n, kind = struct.unpack(">I4s", d[o:o + 8])
        if kind == b"IDAT":
            idat += d[o + 8:o + 8 + n]
        o += 12 + n
    raw = zlib.decompress(idat)
    out = bytearray()
    prev = bytearray(w)
    for y in range(h):
        f = raw[y * (w + 1)]
        line = bytearray(raw[y * (w + 1) + 1:(y + 1) * (w + 1)])
        for x in range(w):
            a = line[x - 1] if x else 0
            b = prev[x]
            c = prev[x - 1] if x else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        out += line
        prev = line
    return w, h, bytes(out)

def ps1_color(c, index):
    """Saturn RGB555 (bit 15 = opaque) -> PS1: colour 0 is transparent, and
    black keeps the STP bit so it stays opaque."""
    if index == 0:
        return 0
    c &= 0x7FFF
    return c if c else 0x8000

BEASTS = 6
# Her fire spell's fireballs are 0.BIN's shared sprite bank 11, whose pixels
# every stage's CHR carries; they're coloured with bank 7's palettes 12 (fire)
# and 5 (a double-damage volley). 060BB334 / g_EntitySpriteBank07.
FIRE_PKG = 13
ZERO_BASE = 0x06004080
FIRE_BANK7_PALETTES = 0x06046854
FIRE_PALETTES = (12, 5)
# Images some objects draw directly as a quad rather than through a frame:
# each gets a frame of its own after the package's (one part, centred). The
# dragon (060BB868): head image 3, body image 4 -> package 12 frames 19, 20.
EXTRA_FRAMES = {12: [3, 4]}
# Her HUD (GAME.PRG func_0607672C / UpdateCompactStatusHud) is package 5; its
# subweapon icon is from 0.BIN's shared bank 0 (images below, for subweapons
# 1..9), added to the package as images 18..26.
HUD_PKG = 5
HUD_SUBWEAPON_ICONS = [0x33, 0x37, 0x34, 0x38, 0x35, 0x36, 0x24, 0x3A, 0x39]
CHR_ORDER = [1, 2, 3, 8, 4, 6, 5, 7, 9, 10, 11, 12]


def write_fx(decomp, out_fx):
    """MSFX: u32 magic, u32 count, count x u32 package offsets (0 = none);
    package: u16 banks, u16 images, u16 frames, u16 0; banks x 16 u16 colours
    (Saturn RGB555); images: u16 w, u16 h, w*h u8 indices; frames: u16 parts,
    parts x (u16 attr, s16 x, s16 y, u16 image). Little-endian."""
    man = json.load(open(decomp + "/assets/saturn/weapon/MAR_W/manifest.json"))
    chr_ = open(decomp + "/disks/saturn/MAR_W.CHR", "rb").read()
    # The packages aren't stored in MAR_W.CHR in index order (the asset tool
    # assumes they are, so its PNGs for 4..8 are the wrong bytes): found by
    # looking at the data, 8 comes before 4, and 6 (the beasts) before 5.
    sizes = {p["resource_index"]: p["byte_count"] for p in man["packages"]}
    base, ofs = 0, {}
    for n in CHR_ORDER:
        ofs[n] = base
        base += sizes[n]
    assert base == len(chr_), "MAR_W.CHR isn't the expected size"
    pkgs = {}
    for p in man["packages"]:
        n = p["resource_index"]
        h = open(decomp + "/src/saturn/maria/gen/sprpkg%d.h" % n).read()
        pal = re.search(r"\} g_MariaSpritePackage%dPalette = \{\s*0x([0-9A-F]+),(.*?)\{0x0000\}" % n, h, re.S)
        colors = [int(c, 16) for c in re.findall(r"0x([0-9A-F]{4})", pal.group(2))]
        banks = len(colors) // 16
        frames = {}
        for typ, cnt, idx, body in re.findall(
                r"(SaturnSpriteFrameHeader|SaturnSpriteFrame(\d+)) g_MariaSpritePackage%dFrame(\d+) = \{(.*?)\};" % n, h, re.S):
            parts = [tuple(int(v, 0) for v in m) for m in re.findall(r"\{(0x[0-9A-F]+), (-?\d+), (-?\d+), (\d+)\}", body)]
            frames[int(idx)] = parts
        order = [int(x) for x in re.findall(r"&g_MariaSpritePackage%dFrame(\d+)" % n, h)]
        imgs = []
        for im in p["images"]:
            w, hh = im["pixel_width"], im["pixel_height"]
            raw = chr_[ofs[n] + im["offset"]:][:w * hh // 2]
            imgs.append((w, hh, bytes(v for x in raw for v in (x >> 4, x & 15))))
        if n == HUD_PKG:
            imgs += shared_images(decomp, 0, HUD_SUBWEAPON_ICONS)
        blob = struct.pack("<HHHH", banks, len(imgs), len(order), 0)
        blob += struct.pack("<%dH" % len(colors), *colors)
        for w, hh, px in imgs:
            blob += struct.pack("<HH", w, hh) + px
        extra = EXTRA_FRAMES.get(n, [])
        blob = struct.pack("<HHHH", banks, len(imgs), len(order) + len(extra), 0) + blob[8:]
        for f in order:
            parts = frames.get(f, [])
            blob += struct.pack("<H", len(parts))
            for a, x, y, i in parts:
                if n == BEASTS:
                    x = y = 0  # the Saturn draws them as a quad centred on the object
                blob += struct.pack("<HhhH", a, x, y, i)
        for i in extra:
            blob += struct.pack("<HHhhH", 1, 0, 0, 0, i)
        pkgs[n] = blob
    pkgs[FIRE_PKG] = fire_package(decomp)
    count = FIRE_PKG + 1
    out = struct.pack("<II", 0x5846534D, count)
    off = 8 + 4 * count
    table, body = [], b""
    for n in range(count):
        if n in pkgs:
            table.append(off + len(body))
            body += pkgs[n]
            body += b"\0" * (-len(body) % 4)
        else:
            table.append(0)
    open(out_fx, "wb").write(out + struct.pack("<%dI" % count, *table) + body)
    print("fx: %d packages, %d bytes" % (len(pkgs), len(body)))

def shared_images(decomp, bank, indices=None):
    """0.BIN shared sprite bank `bank`'s images (as (w, h, 8-bit indices)); their
    pixels are in every stage's CHR"""
    stage = decomp + "/assets/saturn/stage/STAGE_00.CHR/"
    man = json.load(open(stage + "manifest.json"))
    res = next(i for i, r in enumerate(man["resources"]) if bank in r["banks"])
    chr_ = open(decomp + "/disks/saturn/" + man["source"]["chr"], "rb").read()
    ims = {i["index"]: i for i in man["images"] if i["resource"] == res}
    out = []
    for n in (sorted(ims) if indices is None else indices):
        im = ims[n]
        w, hh = im["pixel_width"], im["pixel_height"]
        raw = chr_[im["file_offset"]:][:w * hh // 2]
        out.append((w, hh, bytes(v for x in raw for v in (x >> 4, x & 15))))
    return out

def fire_package(decomp):
    zero = open(decomp + "/disks/saturn/0.BIN", "rb").read()
    colors = []
    for k in FIRE_PALETTES:
        o = FIRE_BANK7_PALETTES - ZERO_BASE + 2 + k * 32
        colors += struct.unpack_from(">16H", zero, o)
    h = open(decomp + "/src/saturn/zero/gen/stgspr11.h").read()
    frames = {}
    for typ, cnt, idx, body in re.findall(
            r"(SaturnSpriteFrameHeader|SaturnSpriteFrame(\d+)) g_SaturnSharedSpriteBank11Frame(\d+) = \{(.*?)\};", h, re.S):
        frames[int(idx)] = [tuple(int(v, 0) for v in m) for m in re.findall(r"\{(0x[0-9A-F]+), (-?\d+), (-?\d+), (\d+)\}", body)]
    order = [int(x) for x in re.findall(r"&g_SaturnSharedSpriteBank11Frame(\d+)", h)]
    imgs = shared_images(decomp, 11)
    blob = struct.pack("<HHHH", len(FIRE_PALETTES), len(imgs), len(order), 0)
    blob += struct.pack("<%dH" % len(colors), *colors)
    for w, hh, px in imgs:
        blob += struct.pack("<HH", w, hh) + px
    for f in order:
        parts = frames.get(f, [])
        blob += struct.pack("<H", len(parts))
        for a, x, y, i in parts:
            blob += struct.pack("<HhhH", a, x, y, i)
    return blob

def main():
    decomp, out_spr, out_hdr, out_fx = sys.argv[1:5]
    write_fx(decomp, out_fx)
    man = json.load(open(decomp + "/assets/saturn/player/MARIA/manifest.json"))
    words = [int(w, 16) for w in man["palette"]["words"]]
    frames_h = open(decomp + "/src/saturn/maria/gen/frames.h").read()
    frames = {}
    for n, body in re.findall(
            r"SaturnSpriteFrame1 g_MariaFrame(\d+) = \{.*?\{\s*\{(.*?)\},", frames_h, re.S):
        a, x, y, i = [int(v, 0) for v in body.split(",")]
        frames[int(n)] = (a, x, y, i)
    order = [int(n) for n in re.findall(r"&g_MariaFrame(\d+)", frames_h)]
    props = re.findall(r"\{(-?\d+), (-?\d+), (\d+), (\d+)\}",
                       open(decomp + "/src/saturn/maria/hitboxes.c").read())

    # sprites: one per Saturn image, plus mirrored copies for flipped frames
    images = {}
    def sprite(idx, flip):
        key = (idx, flip)
        if key in images:
            return images[key][0]
        sw, sh, px = read_png8(decomp + "/assets/saturn/player/MARIA/sprites/%03d.png" % idx)
        w = (sw * 4 + 4) // 5
        w = (w + 3) & ~3
        rows = []
        for y in range(sh):
            row = []
            for j in range(w):
                sx = int((j + 0.5) * 5 / 4)
                row.append(px[y * sw + sx] & 15 if sx < sw else 0)
            if flip:
                row.reverse()
            rows.append(row)
        data = bytes([w, sh, 0, 0])
        for row in rows:
            for j in range(0, w, 2):
                data += bytes([row[j] | (row[j + 1] << 4)])
        images[key] = (len(images), data, sw, sh, w)
        return images[key][0]

    bank = []  # spritebank0 entries: (sprite, left, top)
    for n in order:
        if n not in frames:
            bank.append(None)
            continue
        a, sx, sy, idx = frames[n]
        s = sprite(idx, bool(a & 0x10))
        _, _, sw, sh, w = images[(idx, bool(a & 0x10))]
        # (sx, sy) is the image centre in Saturn pixels; checked against
        # Saturn Richter, whose frames are PS1 frames stretched
        left = int(round((sx - sw / 2) / 1.25))
        top = sy - sh // 2 - 1
        bank.append((s, left, top))

    blob = b""
    offsets = []
    for key, (k, data, *_ ) in sorted(images.items(), key=lambda kv: kv[1][0]):
        offsets.append(len(blob))
        blob += data
        blob += b"\0" * (-len(blob) % 4)
    open(out_spr, "wb").write(blob)

    pal = [ps1_color(w, i) for i, w in enumerate(words[:16])]
    flash = [ps1_color(w, i) for i, w in enumerate(words[16:32])]
    fbanks = [(frames[n][0] >> 8) & 31 if n in frames else 0 for n in order]
    out = ["// Generated by tools/mariasat-convert.py from your Saturn disc; do not edit.",
           "#define MARIASAT_SPR_BASE 0x%08X" % SPR_BASE,
           "#define MARIASAT_SPRITE_COUNT %d" % len(images),
           "__attribute__((section(\".header.1\"))) u8* richter_sprites[MARIASAT_SPRITE_COUNT] = {"]
    out += ["    (u8*)0x%08X," % (SPR_BASE + o) for o in offsets]
    out += ["};", ""]
    for i, e in enumerate(bank):
        if i == 0 or e is None:
            continue
        out.append("static s16 spritebank0_%d[] = {0x%02X | 0x8000, %d, %d, 0};" % (i, e[0], e[1], e[2]))
    out.append("s16* spritebank0[] = {")
    out += ["    %s," % ("0" if i == 0 or e is None else "spritebank0_%d" % i) for i, e in enumerate(bank)]
    out += ["};", "",
            "u16 g_MariaSatPalette[16] = {" + ", ".join("0x%04X" % c for c in pal) + "};",
            "// her flash (palette bank 1 of her frames)",
            "u16 g_MarFlashPalette[16] = {" + ", ".join("0x%04X" % c for c in flash) + "};",
            "// the palette bank each frame asks for (0: her own; 1: the flash; 2..16: a",
            "// charge colour)",
            "u8 g_MarFrameBank[%d] = {" % len(fbanks) + ", ".join(str(b) for b in fbanks) + "};", "",
            "FrameProperty g_MariaSatFrameProps[] = {"]
    for x, y, w, h in props:
        x, y, w, h = int(x), int(y), int(w), int(h)
        out.append("    {%d, %d, %d, %d}," % (round(x / 1.25), y, max(1, round(w / 1.25)) if w else 0, h))
    out.append("};")

    # her blueprints (0x060C1F24, 105 x 6 bytes; same layout as the PS1's)
    prg = open(decomp + "/disks/saturn/MARIA.PRG", "rb").read()
    bp = prg[0x060C1F24 - 0x060A5000:][:105 * 6]
    out.append("u8 g_MarBlueprints[105][6] = {")
    for i in range(105):
        out.append("    {" + ", ".join("0x%02X" % b for b in bp[i * 6:i * 6 + 6]) + "},")
    out.append("};")

    # effect palettes (5) and charge-flash palettes (15): src/saturn/maria/colpal.c
    colpal = re.sub(r"//.*", "", open(decomp + "/src/saturn/maria/colpal.c").read())
    def words(name):
        m = re.search(name + r"\[\](?:\[16\])?\s*=\s*\{(.*?)\};", colpal, re.S)
        return [int(w, 16) for w in re.findall(r"0x[0-9A-Fa-f]+", m.group(1))]
    for name, src, n in [("g_MarEffectPals", "g_MariaEffectPaletteColors", 5),
                         ("g_MarChargePals", "g_MariaEffectPaletteVariants", 15)]:
        w = words(src)
        out.append("u16 %s[%d][16] = {" % (name, n))
        for i in range(n):
            out.append("    {" + ", ".join("0x%04X" % ps1_color(c, j) for j, c in enumerate(w[i * 16:i * 16 + 16])) + "},")
        out.append("};")
    open(out_hdr, "w").write("\n".join(out) + "\n")
    print("%d sprites (%d bytes), %d frames, %d frame props" % (len(images), len(blob), len(bank), len(props)))

if __name__ == "__main__":
    main()
