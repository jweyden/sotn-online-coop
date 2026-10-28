"""Saturn Maria's own sounds -> PS-ADPCM samples for mods/maria (MARIASAT.SND):
her 20 voice lines (tools/mariasat-voice.py), then her 14 effects.

Her effects (sound ids 0x722..0x72F) are sequences 6..19 of her sound package
SD_MARI.CRT (0.BIN's table at 0x06041654: package, then sequence), played on
the Saturn's sound CPU with instruments from bank 0, the resident SD_JO10.CRT
(60 tones), and bank 1, her package's own. The PS1 has neither, so each
sequence is rendered here the way the SCSP plays it, from sotn-decomp's
decoded banks (assets/saturn/sound/*/areas/*.json):
  - notes play their tone's 8-bit sample at 44.1 kHz * 2^((key - base) / 12),
    once or looping (LPCTL), until the gate ends; every tone she uses has an
    instant attack, full sustain and the fastest release
  - gate_add / delta_add add to the next event's gate / delta; reference
    plays a run of events again (as tools/saturn/assets/src/midi.rs)
  - pitch bend (7 bits, centre 64) over the tone's bend range; volume is
    velocity * controller 7 * controller 11
The mod plays them from the SPU's second sample RAM in place of the carrier
sounds the overlay plays (mariasat.c, MarSfx).

Output (little-endian): "MSN2", u32 count, count x {u32 offset, u32 size,
u32 rate} into the ADPCM data that follows (each with a leading zero block),
numbered as the overlay's MarOwnSound.

usage: mariasat-sfx.py DECOMP_DIR OUT
"""
import importlib.util, json, math, os, struct, sys

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("mvoice", os.path.join(here, "mariasat-voice.py"))
mvoice = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mvoice)

FIRST_SFX, FIRST_SONG, COUNT = 0x722, 6, 14
RATE = 22050
SCSP_RATE = 44100
TAIL = RATE // 100  # the release, at the fastest rate
GAIN = 1 / 3  # the SCSP mixes at 18 bits; this keeps the loudest (0x72F) unclipped


class Bank:
    def __init__(self, area):
        tone = json.load(open(area + ".tone.json"))
        self.raw = open(os.path.join(os.path.dirname(area), tone["samples"]["file"]), "rb").read()
        self.voices = tone["voices"]
        sas = [l["source_address"] & 0xFFFFF for v in self.voices for l in v["layers"]]
        self.base = min(sas)

    def layer(self, prog, key):
        v = self.voices[prog]
        for l in v["layers"]:
            if l["start_note"] <= key <= l["end_note"]:
                return v, l
        return v, v["layers"][0]

    def sample(self, l):
        reg0 = l["source_address"] >> 16
        o = (l["source_address"] & 0xFFFFF) - self.base
        n = l["loop_end"]
        if reg0 & 0x10:  # PCM8B
            pcm = [(b - 256 if b > 127 else b) * 256 for b in self.raw[o:o + n]]
        else:
            pcm = list(struct.unpack_from(">%dh" % n, self.raw, o))
        return pcm, (reg0 >> 5) & 3, l["loop_start"]


def events(song):
    """the song's notes and bends, in ticks, as midi.rs plays it"""
    ev = song["events"]
    out, now, gate, delta = [], 0, 0, 0
    cursor, ret, refs, loops = 0, 0, 0, 0
    offsets = None
    for _ in range(100000):
        if cursor >= len(ev):
            break
        e = ev[cursor]
        cursor += 1
        op = e["op"]
        if op == "gate_add":
            gate += e["value"]
            continue
        if op == "delta_add":
            delta += e["value"]
            continue
        if op == "reference":
            if offsets is None:
                offsets = event_offsets(ev)
            ret, refs, cursor = cursor, e["count"], offsets.index(e["target"])
            continue
        if op == "loop":
            loops += 1
            if loops > 1:
                break
            delta += e.get("delta", 0)
            continue
        if op == "end":
            break
        out.append((now, gate, e))
        now += delta + e.get("delta", 0)
        gate = delta = 0
        if refs:
            refs -= 1
            if refs == 0:
                cursor = ret
    return out


# byte offsets of the events (reference targets), from seq.rs's sizes
def event_offsets(ev):
    sizes = {"note": 5, "control": 4, "program": 3, "bend": 3, "pressure": 3,
             "aftertouch": 4, "gate_add": 1, "delta_add": 1, "reference": 4,
             "loop": 2, "end": 1}
    out, o = [], 0
    for e in ev:
        out.append(o)
        o += sizes[e["op"]]
    return out


def render(song, banks):
    tempo = song["tempo"][0]["microseconds_per_quarter"] if song["tempo"] else 500000
    tick = tempo / 1e6 / song["resolution"]
    ch = {}
    notes = []
    for t, extra_gate, e in events(song):
        c = ch.setdefault(e.get("channel", 0), {"bank": 0, "prog": 0, "vol": 127, "expr": 127, "bends": []})
        op = e["op"]
        if op == "control":
            if e["controller"] == 32:
                c["bank"] = e["value"]
            elif e["controller"] == 7:
                c["vol"] = e["value"]
            elif e["controller"] == 11:
                c["expr"] = e["value"]
        elif op == "program":
            c["prog"] = e["program"]
        elif op == "bend":
            c["bends"].append((t * tick, e["value"] & 0x7F))
        elif op == "note":
            notes.append((t * tick, (extra_gate + e["gate"]) * tick, e["key"], e["velocity"],
                          c["bank"], c["prog"], c["vol"] * c["expr"] / (127 * 127), c))
    length = max((s + g for s, g, *_ in notes), default=0) + 0.05
    mix = [0.0] * int(length * RATE + TAIL + 1)
    for start, gate, key, vel, bank, prog, vol, c in notes:
        b = banks[bank]
        v, l = b.layer(prog, key)
        pcm, loop, loop_start = b.sample(l)
        rng = (v["play_mode_and_bend_range"] & 0xF) or 2
        amp = GAIN * vel / 127 * vol * (2 ** (-l["total_level"] * 0.375 / 6))
        semis = key - l["base_note"] + (l["fine_tune"] - 256 if l["fine_tune"] > 127 else l["fine_tune"]) / 128
        i0 = int(start * RATE)
        held = int(gate * RATE)
        pos = 0.0
        for i in range(held + TAIL):
            bend = 0
            for bt, bv in c["bends"]:
                if bt <= start + i / RATE:
                    bend = (bv - 64) / 64 * rng
            p = int(pos)
            if p >= len(pcm):
                if loop and len(pcm) > loop_start:
                    pos = loop_start + (pos - loop_start) % (len(pcm) - loop_start)
                    p = int(pos)
                else:
                    break
            env = 1.0 if i < held else 1 - (i - held) / TAIL
            if i0 + i >= len(mix):
                break
            mix[i0 + i] += pcm[p] * amp * env
            pos += SCSP_RATE * 2 ** ((semis + bend) / 12) / RATE
    end = len(mix)
    while end > 1 and abs(mix[end - 1]) < 1:
        end -= 1
    return [int(max(-32768, min(32767, x))) for x in mix[:end]]


def main():
    decomp, out = sys.argv[1:3]
    snd = decomp + "/assets/saturn/sound/"
    banks = {0: Bank(snd + "SD_JO10.CRT/areas/00"), 1: Bank(snd + "SD_MARI.CRT/areas/01")}
    songs = json.load(open(snd + "SD_MARI.CRT/areas/11.seq.json"))["songs"]
    sounds = [(p, mvoice.SRC_RATE) for p in mvoice.lines(decomp)]
    sounds += [(render(songs[FIRST_SONG + n], banks), RATE) for n in range(COUNT)]
    blobs = [(bytes(16) + mvoice.encode_adpcm(p), r) for p, r in sounds]
    head = struct.pack("<4sI", b"MSN2", len(blobs))
    off = len(head) + 12 * len(blobs)
    table = b""
    for b, r in blobs:
        table += struct.pack("<III", off, len(b), r)
        off += len(b)
    open(out, "wb").write(head + table + b"".join(b for b, _ in blobs))
    print("sounds: %d voice lines, %d effects, %d bytes" % (len(sounds) - COUNT, COUNT, off))


if __name__ == "__main__":
    main()
