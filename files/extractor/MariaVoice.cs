using System.Text;

// PSP Maria's voice as a PlayStation voice bank (SD_RIH.VH / .VB), from the PSP game's
// sd_mar2.spk: her 16 voice lines (descriptors 10..25), silence trimmed, at 8 kHz, re-encoded
// as PS ADPCM, in a VAB laid out the way dra expects for Richter's bank. A port of the build
// path of sotn-decomp's tools/maria-sfx (Rust), output byte for byte the same.
static class MariaVoice
{
    const int VbSlotSize = 0xE190, VhSize = 0x1020, TargetRate = 8000;
    const byte CenterNote = 90, PitchShift = 64;
    const int VoiceFirst = 10, VoiceCount = 16, Descriptors = 26;
    static readonly (int F0, int F1)[] Filters = [(0, 0), (60, 0), (115, -52), (98, -55), (122, -60)];

    public static (byte[] Vh, byte[] Vb) Build(byte[] spk)
    {
        if (Encoding.ASCII.GetString(spk, 0, 8) != "SEPK1.20") throw new InvalidDataException("sd_mar2.spk isn't the SEPK1.20 bank this needs");
        int count = BitConverter.ToInt32(spk, 8);
        if (count != Descriptors) throw new InvalidDataException($"sd_mar2.spk should have {Descriptors} sounds, it has {count}");
        var encoded = new List<byte[]>();
        for (int i = VoiceFirst; i < VoiceFirst + VoiceCount; i++)
        {
            int vagOff = BitConverter.ToInt32(spk, 0x20 + i * 0x20);
            if (Encoding.ASCII.GetString(spk, vagOff, 4) != "VAGp") throw new InvalidDataException($"sound {i}: no VAGp");
            int size = BE32(spk, vagOff + 0x0C), rate = BE32(spk, vagOff + 0x10);
            var pcm = Decode(spk.AsSpan(vagOff + 0x30, size));
            encoded.Add(Encode(Resample(TrimSilence(pcm, rate), rate, TargetRate)));
        }
        int used = encoded.Sum(e => e.Length);
        if (used > VbSlotSize) throw new InvalidDataException($"her voice comes to {used} bytes; the bank holds {VbSlotSize}");
        var vb = new byte[VbSlotSize];
        int at = 0;
        foreach (var e in encoded) { e.CopyTo(vb, at); at += e.Length; }
        return (BuildVh(encoded.Select(e => e.Length).ToArray()), vb);
    }

    static int BE32(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];

    static short[] Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length % 16 != 0) throw new InvalidDataException("ADPCM isn't block aligned");
        int h1 = 0, h2 = 0;
        var pcm = new List<short>(data.Length / 16 * 28);
        for (int b = 0; b < data.Length; b += 16)
        {
            var block = data.Slice(b, 16);
            int filter = block[0] >> 4, shift = block[0] & 15;
            if (filter >= Filters.Length || shift > 12) throw new InvalidDataException("bad ADPCM block header");
            int scale = 1 << (12 - shift);
            for (int i = 0; i < 28; i++)
            {
                int n = (i & 1) == 0 ? block[2 + i / 2] & 15 : block[2 + i / 2] >> 4;
                int q = (n & 8) != 0 ? n - 16 : n;
                int pred = ((h1 * Filters[filter].F0) >> 6) + ((h2 * Filters[filter].F1) >> 6);
                int s = Math.Clamp(q * scale + pred, -32768, 32767);
                pcm.Add((short)s);
                h2 = h1; h1 = s;
            }
            if ((block[1] & 1) != 0) break;   // (a block of 0x07/0x77 may follow: stop at the end flag)
        }
        return pcm.ToArray();
    }

    static short[] Resample(short[] input, int source, int target)
    {
        if (input.Length == 0 || source == target) return input;
        long len = ((long)input.Length * target + source / 2) / source;
        var out_ = new short[len];
        for (long i = 0; i < len; i++)
        {
            long posNum = i * source;
            long a = posNum / target, frac = posNum % target;
            long x = input[Math.Min(a, input.Length - 1)], y = input[Math.Min(a + 1, input.Length - 1)];
            out_[i] = (short)Math.Clamp(x + ((y - x) * frac + target / 2) / target, -32768, 32767);
        }
        return out_;
    }

    static short[] TrimSilence(short[] input, int rate)
    {
        const int Threshold = 64;
        int margin = rate / 50;   // keep 20 ms around what you can hear
        int first = Array.FindIndex(input, x => Math.Abs((int)x) > Threshold); if (first < 0) first = 0;
        int lastIdx = Array.FindLastIndex(input, x => Math.Abs((int)x) > Threshold);
        int last = lastIdx < 0 ? input.Length : lastIdx + 1;
        int start = Math.Max(0, first - margin), end = Math.Min(last + margin, input.Length);
        return input[start..Math.Max(end, start)];
    }

    static int DivRound(int n, int d) => n >= 0 ? (n + d / 2) / d : (n - d / 2) / d;

    static byte[] Encode(short[] pcm)
    {
        int blocks = Math.Max(1, (pcm.Length + 27) / 28);
        var out_ = new byte[blocks * 16];
        int hist1 = 0, hist2 = 0;
        var source = new short[28];
        var qs = new sbyte[28];
        for (int bi = 0; bi < blocks; bi++)
        {
            Array.Clear(source);
            int start = bi * 28, take = Math.Min(Math.Max(pcm.Length - start, 0), 28);
            Array.Copy(pcm, start, source, 0, take);
            ulong bestErr = ulong.MaxValue; int bestF = 0, bestShift = 0, bestH1 = hist1, bestH2 = hist2;
            var bestQ = new sbyte[28];
            for (int fi = 0; fi < Filters.Length; fi++)
                for (int shift = 0; shift <= 12; shift++)
                {
                    int scale = 1 << (12 - shift), h1 = hist1, h2 = hist2;
                    ulong err = 0;
                    for (int j = 0; j < 28; j++)
                    {
                        int pred = ((h1 * Filters[fi].F0) >> 6) + ((h2 * Filters[fi].F1) >> 6);
                        int q = Math.Clamp(DivRound(source[j] - pred, scale), -8, 7);
                        int rec = Math.Clamp(pred + q * scale, -32768, 32767);
                        long delta = source[j] - (long)rec;
                        ulong sq = (ulong)(delta * delta);
                        err = err > ulong.MaxValue - sq ? ulong.MaxValue : err + sq;
                        qs[j] = (sbyte)q;
                        h2 = h1; h1 = rec;
                    }
                    if (err < bestErr) { bestErr = err; bestF = fi; bestShift = shift; qs.CopyTo(bestQ, 0); bestH1 = h1; bestH2 = h2; }
                }
            int o = bi * 16;
            out_[o] = (byte)(bestF << 4 | bestShift);
            out_[o + 1] = (byte)(bi + 1 == blocks ? 1 : 0);
            for (int j = 0; j < 14; j++)
                out_[o + 2 + j] = (byte)(((byte)bestQ[j * 2] & 15) | (((byte)bestQ[j * 2 + 1] & 15) << 4));
            hist1 = bestH1; hist2 = bestH2;
        }
        return out_;
    }

    static byte[] BuildVh(int[] sizes)
    {
        var vh = new byte[VhSize];
        Encoding.ASCII.GetBytes("pBAV").CopyTo(vh, 0);
        Put32(vh, 4, 7); Put32(vh, 8, 1); Put32(vh, 12, VhSize + VbSlotSize);
        Put16(vh, 16, 0xEEEE); Put16(vh, 18, 3); Put16(vh, 20, VoiceCount); Put16(vh, 22, VoiceCount);
        vh[24] = 127; vh[25] = 64;
        Put32(vh, 28, 0xFFFFFFFF);
        for (int prog = 1; prog <= 2; prog++)
        {
            int p = 0x20 + prog * 16;
            vh[p] = 16; vh[p + 1] = 127; vh[p + 2] = 80; vh[p + 4] = 64;   // (the PSP's table uses even tones)
        }
        for (int i = 0; i < VoiceCount; i++)
        {
            int prog = 1 + i / 8, tone = (i % 8) * 2;
            int o = 0x820 + (prog - 1) * 0x200 + tone * 0x20;
            vh[o] = (byte)(i == VoiceCount - 1 ? 90 : 80);
            vh[o + 2] = 127; vh[o + 3] = 64;
            vh[o + 4] = CenterNote; vh[o + 5] = PitchShift;   // (about 8 kHz)
            vh[o + 6] = 0; vh[o + 7] = 127;
            vh[o + 12] = 12; vh[o + 13] = 12;
            Put16(vh, o + 16, 0x80FF); Put16(vh, o + 18, 0x5FC0);
            Put16(vh, o + 20, (uint)prog); Put16(vh, o + 22, (uint)(i + 1));
        }
        int used = sizes.Sum();
        Put16(vh, 0xE20, 0);
        for (int i = 0; i < sizes.Length; i++)
        {
            int extent = i + 1 == sizes.Length ? sizes[i] + VbSlotSize - used : sizes[i];   // (dra has the bank's size built in: pad)
            Put16(vh, 0xE20 + (i + 1) * 2, (uint)(extent / 8));
        }
        return vh;
    }

    static void Put16(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    static void Put32(byte[] d, int o, uint v) { Put16(d, o, v); Put16(d, o + 2, v >> 16); }
}
