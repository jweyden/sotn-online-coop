// Saturn Maria's own sounds as PS-ADPCM samples for the mod (MARIASAT.SND): her 20 voice
// lines, then her 14 effects. A port of tools/mariasat-sfx.py and mariasat-voice.py (and
// the parts of sotn-decomp's Saturn sound tools they use), output byte for byte the same.
//
// Her lines: her sound package SD_MARI.CRT's 6 samples, then clips 1 and 3..15 of the
// streamed SD/SDE7.PCM (0.BIN's voice table at 0x0603A8BC), all 8-bit PCM at 11025 Hz.
// Her effects (sound ids 0x722..0x72F) are sequences 6..19 of SD_MARI.CRT, played on the
// Saturn's sound CPU with tones from bank 0 (the resident SD_JO10.CRT) and bank 1 (her
// package's own): rendered here the way the SCSP plays them.
//   "MSN2", u32 count, count x {u32 offset, u32 size, u32 rate} into the ADPCM data that
//   follows (each with a leading zero block). Little-endian.
static class MariaSatSound
{
    const int SrcRate = 11025, Rate = 22050, ScspRate = 44100, Tail = Rate / 100;
    const double Gain = 1.0 / 3;   // (the SCSP mixes at 18 bits; this keeps the loudest unclipped)
    const int FirstSong = 6, Count = 14;
    // the packages' areas: SD_MARI.CRT's sequences and tones, SD_JO10.CRT's tones
    const int MariSeq = 0, MariSeqSize = 2168, MariTone = 2168, MariToneSize = 42896, Jo10Tone = 22460, Jo10ToneSize = 207716;
    static readonly (int Start, int Length)[] Sde7Clips = [
        (0, 4424), (4424, 3896), (8320, 13352), (21672, 11840), (33512, 15040),
        (48552, 6148), (54700, 6148), (60848, 6376), (67224, 6148), (73372, 6148),
        (79520, 1992), (81512, 3556), (85068, 4464), (89532, 3808), (93340, 6148)];
    static readonly int[] ClipsUsed = [1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    public static byte[] Build(byte[] mari, byte[] jo10, byte[] sde7)
    {
        if (mari.Length != MariToneSize + MariTone || jo10.Length != Jo10Tone + Jo10ToneSize)
            throw new InvalidDataException("SD_MARI.CRT / SD_JO10.CRT aren't the expected size");
        var her = new ToneBank(mari.AsSpan(MariTone, MariToneSize).ToArray());
        var banks = new[] { new ToneBank(jo10.AsSpan(Jo10Tone, Jo10ToneSize).ToArray()), her };
        var songs = Songs(mari.AsSpan(MariSeq, MariSeqSize).ToArray());

        var sounds = new List<(int[] Pcm, int Rate)>();
        long lineBase = her.Voices.Min(v => (long)v.Layers[0].SourceAddress);
        foreach (var v in her.Voices)
        {
            var l = v.Layers[0];
            int o = (int)(l.SourceAddress - lineBase);
            sounds.Add((Trim(Pcm8(her.Samples, o, l.LoopEnd), SrcRate), SrcRate));
        }
        foreach (int clip in ClipsUsed)
        {
            var (o, n) = Sde7Clips[clip - 1];
            sounds.Add((Trim(Pcm8(sde7, o, n), SrcRate), SrcRate));
        }
        for (int n = 0; n < Count; n++) sounds.Add((Render(songs[FirstSong + n], banks), Rate));

        var blobs = sounds.Select(s => (Data: new byte[16].Concat(Encode(s.Pcm)).ToArray(), s.Rate)).ToList();
        var out_ = new List<byte>();
        out_.AddRange("MSN2"u8.ToArray());
        out_.AddRange(BitConverter.GetBytes(blobs.Count));
        int off = 8 + 12 * blobs.Count;
        foreach (var (data, rate) in blobs)
        {
            out_.AddRange(BitConverter.GetBytes(off)); out_.AddRange(BitConverter.GetBytes(data.Length)); out_.AddRange(BitConverter.GetBytes(rate));
            off += data.Length;
        }
        foreach (var (data, _) in blobs) out_.AddRange(data);
        return out_.ToArray();
    }

    static int[] Pcm8(byte[] raw, int o, int n) => Enumerable.Range(o, n).Select(i => (sbyte)raw[i] * 256).ToArray();

    static int[] Trim(int[] pcm, int rate)
    {
        int first = Array.FindIndex(pcm, x => Math.Abs(x) > 64), last = Array.FindLastIndex(pcm, x => Math.Abs(x) > 64);
        if (first < 0) return pcm[..1];
        int m = rate / 50;
        return pcm[Math.Max(0, first - m)..Math.Min(pcm.Length, last + 1 + m)];
    }

    // --- tone banks (sotn-decomp tone.rs): u16 offsets (mixer, ...), the voice offsets
    // before the mixer; voices end to end (u8 mode/bend range, u8, s8 layers - 1, s8, then
    // 0x20-byte layers); the samples after the last voice. Big-endian.
    sealed record Layer(int StartNote, int EndNote, uint SourceAddress, int LoopStart, int LoopEnd, int TotalLevel, int BaseNote, int FineTune);
    sealed record Voice(int ModeAndBendRange, Layer[] Layers);
    sealed class ToneBank
    {
        public readonly List<Voice> Voices = [];
        public readonly byte[] Samples;
        readonly uint _base;
        public ToneBank(byte[] d)
        {
            int count = (Saturn.U16(d, 0) - 8) / 2, at = Saturn.U16(d, 8);
            for (int i = 0; i < count; i++)
            {
                if (Saturn.U16(d, 8 + i * 2) != at) throw new InvalidDataException("a tone bank's voices aren't end to end");
                int layers = (sbyte)d[at + 2] + 1;
                Voices.Add(new Voice(d[at], Enumerable.Range(0, layers).Select(k => at + 4 + k * 0x20).Select(b => new Layer(
                    d[b], d[b + 1], Saturn.U32(d, b + 2), Saturn.U16(d, b + 6), Saturn.U16(d, b + 8), d[b + 0x0F], d[b + 0x19], (sbyte)d[b + 0x1A])).ToArray()));
                at += 4 + layers * 0x20;
            }
            Samples = d[at..];
            _base = Voices.SelectMany(v => v.Layers).Min(l => l.SourceAddress & 0xFFFFF);
        }
        public (Voice, Layer) Layer(int prog, int key)
        {
            var v = Voices[prog];
            return (v, v.Layers.FirstOrDefault(l => l.StartNote <= key && key <= l.EndNote) ?? v.Layers[0]);
        }
        public (int[] Pcm, int Loop, int LoopStart) Sample(Layer l)
        {
            uint reg0 = l.SourceAddress >> 16;
            int o = (int)((l.SourceAddress & 0xFFFFF) - _base), n = l.LoopEnd;
            var pcm = (reg0 & 0x10) != 0 ? Pcm8(Samples, o, n) : Enumerable.Range(0, n).Select(i => (int)Saturn.S16(Samples, o + i * 2)).ToArray();
            return (pcm, (int)(reg0 >> 5) & 3, l.LoopStart);
        }
    }

    // --- sequence banks (seq.rs): u16 count, u32 song offsets; a song: u16 resolution, u16
    // tracks, u16 track offset, u16 tempo offset, per track (u32 delta, u32 us/quarter), one
    // track of events
    enum Op { Note, Reference, Loop, End, GateAdd, DeltaAdd, Aftertouch, Control, Program, Pressure, Bend }
    sealed record Ev(Op Op, int Offset, int Channel = 0, int Key = 0, int Velocity = 0, int Gate = 0, int Delta = 0,
        int Controller = 0, int Value = 0, int Program = 0, int Target = 0, int Count = 0);
    sealed record Song(int Resolution, uint Tempo, List<Ev> Events);
    static readonly int[] GateAdds = [0x200, 0x800, 0x1000, 0x2000], DeltaAdds = [0x100, 0x200, 0x800, 0x1000];

    static List<Song> Songs(byte[] d)
    {
        int count = Saturn.U16(d, 0);
        var songs = new List<Song>();
        for (int s = 0; s < count; s++)
        {
            int o = (int)Saturn.U32(d, 2 + s * 4);
            int tracks = Saturn.U16(d, o + 2), start = o + Saturn.U16(d, o + 4);
            uint tempo = tracks > 0 ? Saturn.U32(d, o + 8 + 4) : 500000;
            var ev = new List<Ev>();
            for (int at = start; ;)
            {
                int rel = at - start, op = d[at++];
                if (op <= 0x7F)
                {
                    ev.Add(new Ev(Op.Note, rel, Channel: op & 15, Key: d[at], Velocity: d[at + 1],
                        Gate: d[at + 2] | ((op & 0x40) != 0 ? 0x100 : 0), Delta: d[at + 3] | ((op & 0x20) != 0 ? 0x100 : 0)));
                    at += 4;
                }
                else if (op == 0x81) { ev.Add(new Ev(Op.Reference, rel, Target: Saturn.U16(d, at), Count: d[at + 2])); at += 3; }
                else if (op == 0x82) { ev.Add(new Ev(Op.Loop, rel, Delta: d[at])); at += 1; }
                else if (op == 0x83) { ev.Add(new Ev(Op.End, rel)); break; }
                else if (op is >= 0x88 and <= 0x8B) ev.Add(new Ev(Op.GateAdd, rel, Value: GateAdds[op - 0x88]));
                else if (op is >= 0x8C and <= 0x8F) ev.Add(new Ev(Op.DeltaAdd, rel, Value: DeltaAdds[op - 0x8C]));
                else if (op is >= 0xA0 and <= 0xEF)
                {
                    int kind = op & 0xF0, operands = kind is 0xA0 or 0xB0 ? 2 : 1, ch = op & 15;
                    int delta = d[at + operands], data1 = d[at] & 0x7F;
                    ev.Add(kind switch
                    {
                        0xA0 => new Ev(Op.Aftertouch, rel, Channel: ch, Key: data1, Delta: delta),
                        0xB0 => new Ev(Op.Control, rel, Channel: ch, Controller: data1, Value: d[at + 1], Delta: delta),
                        0xC0 => new Ev(Op.Program, rel, Channel: ch, Program: data1, Delta: delta),
                        0xD0 => new Ev(Op.Pressure, rel, Channel: ch, Delta: delta),
                        _ => new Ev(Op.Bend, rel, Channel: ch, Value: d[at], Delta: delta),
                    });
                    at += operands + 1;
                }
                else throw new InvalidDataException($"SD_MARI.CRT: song {s} uses opcode {op:X2}, which this doesn't know");
            }
            songs.Add(new Song(Saturn.U16(d, o), tempo, ev));
        }
        return songs;
    }

    // the song's notes and bends, in ticks, as sotn-decomp's midi.rs plays it
    static List<(long Now, int Gate, Ev E)> Events(Song song)
    {
        var ev = song.Events;
        var out_ = new List<(long, int, Ev)>();
        long now = 0;
        int gate = 0, delta = 0, cursor = 0, ret = 0, refs = 0, loops = 0;
        for (int step = 0; step < 100000 && cursor < ev.Count; step++)
        {
            var e = ev[cursor++];
            switch (e.Op)
            {
                case Op.GateAdd: gate += e.Value; continue;
                case Op.DeltaAdd: delta += e.Value; continue;
                case Op.Reference:
                    ret = cursor; refs = e.Count;
                    cursor = ev.FindIndex(x => x.Offset == e.Target);
                    if (cursor < 0) throw new InvalidDataException("a sequence refers to no event");
                    continue;
                case Op.Loop:
                    if (++loops > 1) return out_;
                    delta += e.Delta;
                    continue;
                case Op.End: return out_;
            }
            out_.Add((now, gate, e));
            now += delta + e.Delta;
            gate = delta = 0;
            if (refs != 0 && --refs == 0) cursor = ret;
        }
        return out_;
    }

    sealed class Channel { public int Bank, Prog, Vol = 127, Expr = 127; public readonly List<(double T, int V)> Bends = []; }

    static int[] Render(Song song, ToneBank[] banks)
    {
        double tick = song.Tempo / 1e6 / song.Resolution;
        var ch = new Dictionary<int, Channel>();
        var notes = new List<(double Start, double Gate, int Key, int Vel, int Bank, int Prog, double Vol, Channel C)>();
        foreach (var (t, extraGate, e) in Events(song))
        {
            if (!ch.TryGetValue(e.Channel, out var c)) ch[e.Channel] = c = new Channel();
            switch (e.Op)
            {
                case Op.Control when e.Controller == 32: c.Bank = e.Value; break;
                case Op.Control when e.Controller == 7: c.Vol = e.Value; break;
                case Op.Control when e.Controller == 11: c.Expr = e.Value; break;
                case Op.Program: c.Prog = e.Program; break;
                case Op.Bend: c.Bends.Add((t * tick, e.Value & 0x7F)); break;
                case Op.Note:
                    notes.Add((t * tick, (extraGate + e.Gate) * tick, e.Key, e.Velocity, c.Bank, c.Prog, (double)(c.Vol * c.Expr) / (127 * 127), c));
                    break;
            }
        }
        double length = (notes.Count > 0 ? notes.Max(n => n.Start + n.Gate) : 0) + 0.05;
        var mix = new double[(int)(length * Rate + Tail + 1)];
        foreach (var (start, gate, key, vel, bank, prog, vol, c) in notes)
        {
            var b = banks[bank];
            var (v, l) = b.Layer(prog, key);
            var (pcm, loop, loopStart) = b.Sample(l);
            int rng = (v.ModeAndBendRange & 0xF) is var r && r != 0 ? r : 2;
            double amp = Gain * vel / 127 * vol * Math.Pow(2, -l.TotalLevel * 0.375 / 6);
            double semis = key - l.BaseNote + l.FineTune / 128.0;
            int i0 = (int)(start * Rate), held = (int)(gate * Rate);
            double pos = 0.0;
            for (int i = 0; i < held + Tail; i++)
            {
                double bend = 0;
                foreach (var (bt, bv) in c.Bends)
                    if (bt <= start + (double)i / Rate) bend = (bv - 64) / 64.0 * rng;
                int p = (int)pos;
                if (p >= pcm.Length)
                {
                    if (loop != 0 && pcm.Length > loopStart)
                    {
                        pos = loopStart + (pos - loopStart) % (pcm.Length - loopStart);
                        p = (int)pos;
                    }
                    else break;
                }
                double env = i < held ? 1.0 : 1 - (double)(i - held) / Tail;
                if (i0 + i >= mix.Length) break;
                mix[i0 + i] += pcm[p] * amp * env;
                pos += ScspRate * Math.Pow(2, (semis + bend) / 12) / Rate;
            }
        }
        int end = mix.Length;
        while (end > 1 && Math.Abs(mix[end - 1]) < 1) end--;
        return mix[..end].Select(x => (int)Math.Clamp(x, -32768, 32767)).ToArray();
    }

    // PS ADPCM, the best filter and shift per 28-sample block (rounding half to even, as
    // the Python encoder did)
    static readonly (int F0, int F1)[] Filters = [(0, 0), (60, 0), (115, -52), (98, -55), (122, -60)];
    static byte[] Encode(int[] pcm)
    {
        int blocks = Math.Max(1, (pcm.Length + 27) / 28);
        var out_ = new byte[blocks * 16];
        int h1 = 0, h2 = 0;
        var src = new int[28];
        var qs = new int[28];
        var bestQ = new int[28];
        for (int bi = 0; bi < blocks; bi++)
        {
            Array.Clear(src);
            for (int j = 0; j < 28 && bi * 28 + j < pcm.Length; j++) src[j] = pcm[bi * 28 + j];
            long bestErr = long.MaxValue;
            int bestF = 0, bestShift = 0, bestH1 = 0, bestH2 = 0;
            for (int fi = 0; fi < Filters.Length; fi++)
                for (int shift = 0; shift <= 12; shift++)
                {
                    int scale = 1 << (12 - shift), a = h1, b = h2;
                    long err = 0;
                    bool done = true;
                    for (int j = 0; j < 28; j++)
                    {
                        int pred = ((a * Filters[fi].F0) >> 6) + ((b * Filters[fi].F1) >> 6);
                        int d = src[j] - pred;
                        int q = d >= 0 ? (int)Math.Round((double)d / scale, MidpointRounding.ToEven) : -(int)Math.Round((double)-d / scale, MidpointRounding.ToEven);
                        q = Math.Clamp(q, -8, 7);
                        int rec = Math.Clamp(pred + q * scale, -32768, 32767);
                        err += (long)(src[j] - rec) * (src[j] - rec);
                        if (bestErr != long.MaxValue && err >= bestErr) { done = false; break; }
                        qs[j] = q;
                        b = a; a = rec;
                    }
                    if (done) { bestErr = err; bestF = fi; bestShift = shift; qs.CopyTo(bestQ, 0); bestH1 = a; bestH2 = b; }
                }
            h1 = bestH1; h2 = bestH2;
            int o = bi * 16;
            out_[o] = (byte)(bestF << 4 | bestShift);
            out_[o + 1] = (byte)(bi + 1 == blocks ? 1 : 0);
            for (int j = 0; j < 14; j++) out_[o + 2 + j] = (byte)((bestQ[j * 2] & 15) | ((bestQ[j * 2 + 1] & 15) << 4));
        }
        return out_;
    }
}
