"""Saturn Maria's voice lines, for tools/mariasat-sfx.py (MARIASAT.SND).

Her lines on the Saturn come from two places:
  - her sound package SD_MARI.CRT (sotn-decomp extracts it to
    assets/saturn/sound/SD_MARI.CRT): 6 samples, 8-bit PCM played at
    11025 Hz (sound ids 0x71C..0x721);
  - streamed clips: sound ids 0x5CA..0x5D8 are group 0x17 of 0.BIN's voice
    table, which is SD/SDE7.PCM, at 11025 Hz, clips listed at
    0x0603A8BC (offset, length), plain signed 8-bit PCM like the package's.
That's 20 lines (lines()), in the order the overlay numbers them (mariasat.c,
MarOwnSound). Also the PS1 ADPCM encoder.
"""
import json

SRC_RATE = 11025

# (start, length) in SDE7.PCM for clips 1..15 (0x0603A8BC)
SDE7_CLIPS = [
    (0, 4424), (4424, 3896), (8320, 13352), (21672, 11840), (33512, 15040),
    (48552, 6148), (54700, 6148), (60848, 6376), (67224, 6148), (73372, 6148),
    (79520, 1992), (81512, 3556), (85068, 4464), (89532, 3808), (93340, 6148),
]
# her lines: 6 package samples, then clips (1, 3..15)
CLIPS_USED = [1] + list(range(3, 16))

def bank_samples(decomp):
    d = decomp + "/assets/saturn/sound/SD_MARI.CRT/areas/"
    tone = json.load(open(d + "01.tone.json"))
    raw = open(d + "01.samples.bin", "rb").read()
    layers = [v["layers"][0] for v in tone["voices"]]
    base = min(l["source_address"] for l in layers)
    out = []
    for l in layers:
        o = l["source_address"] - base
        n = l["loop_end"]
        out.append([(b - 256 if b > 127 else b) * 256 for b in raw[o:o + n]])
    return out

def sde7_clips(decomp):
    raw = open(decomp + "/disks/saturn/SD/SDE7.PCM", "rb").read()
    out = []
    for clip in CLIPS_USED:
        o, n = SDE7_CLIPS[clip - 1]
        out.append([(b - 256 if b > 127 else b) * 256 for b in raw[o:o + n]])
    return out

def trim(pcm, rate):
    loud = [i for i, x in enumerate(pcm) if abs(x) > 64]
    if not loud:
        return pcm[:1]
    m = rate // 50
    return pcm[max(0, loud[0] - m):min(len(pcm), loud[-1] + 1 + m)]

def resample(pcm, src, dst):
    n = max(1, round(len(pcm) * dst / src))
    out = []
    for i in range(n):
        p = i * src / dst
        a = int(p)
        f = p - a
        x = pcm[min(a, len(pcm) - 1)]
        y = pcm[min(a + 1, len(pcm) - 1)]
        out.append(int(max(-32768, min(32767, round(x + (y - x) * f)))))
    return out

FILTERS = [(0, 0), (60, 0), (115, -52), (98, -55), (122, -60)]

def encode_adpcm(pcm):
    blocks = max(1, (len(pcm) + 27) // 28)
    out = bytearray()
    h1 = h2 = 0
    for bi in range(blocks):
        src = pcm[bi * 28:bi * 28 + 28]
        src += [0] * (28 - len(src))
        best = None
        for fi, (f0, f1) in enumerate(FILTERS):
            for shift in range(13):
                scale = 1 << (12 - shift)
                a, b, err, qs = h1, h2, 0, []
                for s in src:
                    pred = ((a * f0) >> 6) + ((b * f1) >> 6)
                    d = s - pred
                    q = int(round(d / scale)) if d >= 0 else -int(round(-d / scale))
                    q = max(-8, min(7, q))
                    r = max(-32768, min(32767, pred + q * scale))
                    err += (s - r) ** 2
                    if best is not None and err >= best[0]:
                        break
                    qs.append(q)
                    b, a = a, r
                else:
                    best = (err, fi, shift, qs, a, b)
        _, fi, shift, qs, h1, h2 = best
        block = bytearray(16)
        block[0] = (fi << 4) | shift
        block[1] = 1 if bi + 1 == blocks else 0
        for j in range(14):
            block[2 + j] = (qs[j * 2] & 15) | ((qs[j * 2 + 1] & 15) << 4)
        out += block
    return bytes(out)

def lines(decomp):
    return [trim(p, SRC_RATE) for p in bank_samples(decomp) + sde7_clips(decomp)]
