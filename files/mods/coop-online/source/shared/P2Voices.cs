using System;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Every player's voice is their own character's, in every game.
//
// The player's sounds play from vab 1, the voice bank of the character this game runs
// (Alucard's SD_ALK, or SD_RIH for Richter and the Richter-shaped Marias): PlaySfx
// puts the id in a ring buffer, ExecSfx later hands it to func_8013572C, which keys
// on (tone, tone+1) of the vab's program through func_80132A04. Each ring slot is
// tagged with the player whose pass wrote it; when that slot plays and the player's
// character has another voice bank, the two voices are armed with that bank's
// samples (engine patch: Spu bank 2, Ram2), so their key-on plays those instead.
// Volume, pan and envelope stay the game's; the pitch is corrected for the two
// tones' centre notes.
//
// PSP Alucard's lines come from the PSP version (Dracula X Chronicles, the 2007
// recast): res/ps/snd_e/SE/sd_alk.spk on the UMD, from the player's own disc, in
// disc/psp/. It holds the same 18 sounds in the same order as SD_ALK, two entries
// per sound (one per tone of the pair), each a PS-ADPCM VAG. The other banks are VABs
// (VH header + VB samples): the PS1 disc's SD_ALK / SD_RIH. (The Marias play with
// Richter's; characters/Maria.cs swaps in their own lines and effects the same way.)
public abstract partial class CoopCore
{
    protected const string PspVoiceBank = "disc/psp/sd_alk.spk";
    protected const uint SfxWritePosAddr = 0x80139000u; // g_sfxRingBufferWritePos
    protected const uint SfxReadPosAddr = 0x80138FACu;  // g_SfxRingBufferReadPos
    protected const uint PlayableCharacterAddr = 0x8003C9A0u;
    protected const int PlayerVab = 1, VoiceEntries = 36, SfxRingSize = 0x100;
    const uint PspRam2Base = 0x10, VabRam2Base = 0x100000;   // (characters/Maria.cs's sounds use 0x20000..)

    public bool P2PspVoices = true;
    protected (uint Addr, int PitchQ12)[]? _p2Voice; // PSP Alucard, per bank entry
    protected string _p2VoiceStatus = "";
    protected readonly byte[] _sfxWho = new byte[SfxRingSize]; // ring slots: 0 = P1, n = remote player n
    protected int _execWho;

    protected void LoadP2Voices()
    {
        _p2Voice = null;
        var spu = RecompOne.Runtime.Runtime.Spu;
        if (spu == null) { _p2VoiceStatus = "no SPU"; return; }
        if (!File.Exists(PspVoiceBank)) { _p2VoiceStatus = $"{PspVoiceBank} not found"; return; }
        var d = File.ReadAllBytes(PspVoiceBank);
        if (d.Length < 0x20 + VoiceEntries * 0x20 || d[0] != 'S' || d[1] != 'E' || d[2] != 'P' || d[3] != 'K')
        {
            _p2VoiceStatus = $"{PspVoiceBank} isn't a SEPK bank";
            return;
        }

        var voices = new (uint, int)[VoiceEntries];
        var placed = new Dictionary<uint, (uint, int)>();
        uint dst = PspRam2Base;
        for (int e = 0; e < VoiceEntries; e++)
        {
            uint off = BitConverter.ToUInt32(d, 0x20 + e * 0x20);
            int size = (int)BitConverter.ToUInt32(d, 0x24 + e * 0x20);
            if (!placed.TryGetValue(off, out var s))
            {
                if (off + size > d.Length || d[off] != 'V' || d[off + 1] != 'A' || d[off + 2] != 'G')
                {
                    _p2VoiceStatus = $"{PspVoiceBank}: entry {e} isn't a VAG";
                    return;
                }
                int rate = d[off + 0x10] << 24 | d[off + 0x11] << 16 | d[off + 0x12] << 8 | d[off + 0x13];
                var body = d.AsSpan((int)off + 0x30, size - 0x30);
                spu.LoadBank2(dst, body);
                s = (dst, PitchFor(e, rate));
                placed[off] = s;
                dst += (uint)(body.Length + 15) & ~15u;
            }
            voices[e] = s;
        }
        _p2Voice = voices;
        _p2VoiceStatus = $"loaded {placed.Count} samples";
        Console.WriteLine($"[coop] PSP voices: {_p2VoiceStatus}");
    }

    // The game's pitch plays the PS1 sample at 44.1 kHz * 2^((note - centre) / 12):
    // 25 semitones down for the voices, 24 for the wolf bark, 30 for the "whoa"
    // (notes from g_SfxData, centres from the PS1 sd_alk header). Rescale that to
    // the PSP sample's own rate.
    static int PitchFor(int entry, int rate)
    {
        int semis = entry < 32 ? -25 : entry < 34 ? -24 : -30;
        double ps1Rate = 44100.0 * Math.Pow(2, semis / 12.0);
        return (int)Math.Round(4096 * rate / ps1Rate);
    }

    // vab 1's (program, tone) -> PSP bank entry: programs 0 and 1 hold 8 sounds each on
    // tones 0..15, program 2 the wolf bark (tones 2, 3) and the "whoa" (tones 6, 7).
    static int VoiceEntry(int prog, int tone) => prog switch
    {
        0 or 1 when tone is >= 0 and < 16 => prog * 16 + tone,
        2 when tone is 2 or 3 => 30 + tone,
        2 when tone is 6 or 7 => 28 + tone,
        _ => -1,
    };

    // ---------------------------------------------------------------- VAB voice banks

    sealed class VoiceBank
    {
        // [prog * 16 + tone]: the sample in Ram2 and the tone's centre note / fine tune
        public readonly (uint Addr, int Centre, int Fine)?[] Tones = new (uint, int, int)?[128 * 16];
    }
    readonly Dictionary<string, VoiceBank?> _voiceBanks = new();
    uint _ram2Next = VabRam2Base;

    // Which bank a character's voice is in.
    static string VoiceBankOf(CoopCharacter c) => c switch
    {
        CoopCharacter.Alucard or CoopCharacter.AlucardPsp => "alk",
        // (both Marias play with Richter's bank; characters/Maria.cs swaps in their own lines)
        _ => "rih",
    };

    VoiceBank? Bank(string key)
    {
        if (_voiceBanks.TryGetValue(key, out var b)) return b;
        byte[]? vh, vb;
        switch (key)
        {
            case "alk": vh = ReadDiscFile("VAB/SD_ALK.VH"); vb = ReadDiscFile("VAB/SD_ALK.VB"); break;
            case "rih": vh = ReadDiscFile("VAB/SD_RIH.VH"); vb = ReadDiscFile("VAB/SD_RIH.VB"); break;
            default:
                string dir = $"disc/{key}";
                vh = File.Exists($"{dir}/SD_RIH.VH") ? File.ReadAllBytes($"{dir}/SD_RIH.VH") : null;
                vb = File.Exists($"{dir}/SD_RIH.VB") ? File.ReadAllBytes($"{dir}/SD_RIH.VB") : null;
                break;
        }
        b = vh != null && vb != null ? LoadVab(key, vh, vb) : null;
        _voiceBanks[key] = b;
        return b;
    }

    // VH: a 0x20 header ('pBAV', programs at 0x12, VAGs at 0x16), 128 program
    // attributes of 0x10 (tone count +0), then 16 tone attributes of 0x20 per program
    // that has tones (centre note +4, fine tune +5, VAG +0x16), then 256 VAG sizes / 8.
    // VB: the VAGs back to back, raw ADPCM. Programs are looked up as libsnd does
    // (SsVabOpenHead: a program's tone block is the count of earlier programs with
    // tones), so a program a bank lacks reads the next one's block, blank tones and all.
    VoiceBank? LoadVab(string key, byte[] vh, byte[] vb)
    {
        var spu = RecompOne.Runtime.Runtime.Spu;
        if (spu == null || vh.Length < 0x820 || BitConverter.ToUInt32(vh, 0) != 0x56414270) return null;
        int programs = BitConverter.ToUInt16(vh, 0x12), vags = BitConverter.ToUInt16(vh, 0x16);
        int toneBase = 0x20 + 128 * 0x10, sizes = toneBase + programs * 16 * 0x20;
        if (sizes + 512 > vh.Length) return null;
        var vagAddr = new uint[257];
        uint vbOff = 0;
        for (int n = 0; n <= Math.Min(vags, 255); n++)
        {
            int len = BitConverter.ToUInt16(vh, sizes + n * 2) * 8;
            if (len > 0 && vbOff + len <= vb.Length && _ram2Next + len <= RecompOne.Runtime.Spu.Ram2Size)
            {
                spu.LoadBank2(_ram2Next, vb.AsSpan((int)vbOff, len));
                vagAddr[n] = _ram2Next;
                _ram2Next += (uint)(len + 15) & ~15u;
            }
            vbOff += (uint)len;
        }
        var bank = new VoiceBank();
        int block = 0;
        for (int prog = 0; prog < 128; prog++)
        {
            bool hasTones = vh[0x20 + prog * 0x10] != 0;
            if (block < programs)
                for (int t = 0; t < 16; t++)
                {
                    int at = toneBase + (block * 16 + t) * 0x20, vag = BitConverter.ToInt16(vh, at + 0x16);
                    uint addr = hasTones && vag > 0 && vag <= 256 ? vagAddr[vag] : 0;
                    bank.Tones[prog * 16 + t] = (addr, vh[at + 4], vh[at + 5]);
                }
            if (hasTones) block++;
        }
        Console.WriteLine($"[coop] voice bank {key}: {vags} samples in Ram2");
        return bank;
    }

    // The pitch libsnd sets for `note` on a tone (note2pitch, into a u16), and the one
    // the tone's own sample wants (the same, without the u16's overflow).
    static readonly ushort[] NotePitch = BuildNotePitch();
    static ushort[] BuildNotePitch()
    {
        var t = new ushort[12 * 16];
        for (int i = 0; i < t.Length; i++) t[i] = (ushort)Math.Floor(4096 * Math.Pow(2, i / 192.0));
        return t;
    }
    static long NoteToPitch(int note, int centre, int fine)
    {
        int n = note + 60 - centre;
        if (n < 0) return 0;
        long p = NotePitch[n % 12 * 16 + Math.Min(fine / 8, 15)];
        int octave = n / 12 - 5;
        return octave >= 0 ? p << octave : p >> -octave;
    }

    // What to scale the game's pitch by so `own`'s sample plays at `note` as its own
    // bank would play it, given the `loaded` tone the game keyed (0x1000: leave it).
    static int ScaleForNote((uint Addr, int Centre, int Fine) own, (uint Addr, int Centre, int Fine)? loaded, int note)
    {
        long want = NoteToPitch(note, own.Centre, own.Fine);
        long game = loaded is { } l ? (ushort)NoteToPitch(note, l.Centre, l.Fine) : 0;
        if (want <= 0 || game <= 0) return 0x1000;
        return (int)Math.Clamp(want * 4096 / game, 1, 0x7FFFFFFF);
    }

    // ---------------------------------------------------------------- whose sound

    // after PlaySfx/PlaySfxVolPan: remember who wrote the ring slot at `pos`
    protected void TagSfx(IMemory m, int pos)
    {
        if (m.ReadU16(SfxWritePosAddr) != pos) _sfxWho[pos & (SfxRingSize - 1)] = (byte)(_inP2 ? R.Number : 0);
    }

    CoopCharacter CharacterOf(int who)
    {
        if (who == 0) return _p1Char;
        foreach (var r in _remotes) if (r.Number == who) return r.Character;
        return _p1Char;
    }

    // Local co-op: player 2's Alucard has the PSP voice when P2PspVoices is on.
    protected virtual bool RemoteAlucardsUsePsp => true;

    bool UsesPspVoice(int who)
    {
        var c = CharacterOf(who);
        return c == CoopCharacter.AlucardPsp || (who != 0 && c == CoopCharacter.Alucard && RemoteAlucardsUsePsp && P2PspVoices);
    }

    // ExecSfx plays the ring slot at the read position
    [Replace("dra", "func_8013572C")]
    public static void PlayQueuedSfxHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null) { orig(c, m); return; }
        int pos = m.ReadU16(SfxReadPosAddr) & (SfxRingSize - 1);
        self._execWho = self._sfxWho[pos];
        self._sfxWho[pos] = 0;
        try { orig(c, m); }
        finally { self._execWho = 0; }
    }

    // func_80132A04(voice, vabId, prog, tone, note, volume, distance) keys on
    // (voice, tone) and (voice + 1, tone + 1)
    [Replace("dra", "func_80132A04")]
    public static void KeyOnPairHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var spu = RecompOne.Runtime.Runtime.Spu;
        int voice = (short)c.A0;
        var self = I;
        if (self != null && spu != null && voice is >= 0 and < 23 && (short)c.A1 == PlayerVab)
        {
            try { self.ArmVoice(spu, voice, (short)c.A2, (short)c.A3, (short)m.ReadU16(c.SP + 0x10)); }   // (note: 5th argument)
            catch (Exception e) { Console.WriteLine($"[coop] voice: {e.Message}"); }
        }
        // (nothing is disarmed here: characters/Maria.cs clears the pair before this runs,
        // and arms voices too)
        orig(c, m);
    }

    // libsnd keys on only a tone that has a sample (SsUtKeyOnV), so only those voices
    // are armed: an arm left on a voice that wasn't keyed plays on its next sound.
    // A tone of the loaded bank with no counterpart in the player's own is armed
    // with silence, not left playing the loaded bank's sample (e.g. Saturn Maria's
    // bank has no second tone for her lines; Alucard's does, a grunt that would
    // play 30 semitones up).
    const uint SilenceAddr = 0;   // (below the PSP voices)
    static readonly byte[] Silence = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    bool _silenceLoaded;

    static bool Keyed(VoiceBank? loaded, int idx) => loaded == null || loaded.Tones[idx] is { Addr: > 0 };

    void ArmVoice(RecompOne.Runtime.Spu spu, int voice, int prog, int tone, int note)
    {
        int who = _execWho;
        var ch = CharacterOf(who);
        string loadedKey = VoiceBankOf(_p1Char), ownKey = VoiceBankOf(ch);
        if (prog < 0 || prog >= 128 || tone < 0 || tone >= 15) return;
        int idx = prog * 16 + tone;
        if (!_silenceLoaded) { spu.LoadBank2(SilenceAddr, Silence); _silenceLoaded = true; }

        if (UsesPspVoice(who) && _p2Voice is { } v && VoiceEntry(prog, tone) is int entry and >= 0)
        {
            // PSP Alucard: the PSP sample, pitched as for Alucard's PS1 tone (and on from
            // the loaded bank's tone if that isn't Alucard's)
            int q = 0x1000, q1 = 0x1000;
            var ld = Bank(loadedKey);
            if (loadedKey != "alk" && Bank("alk") is { } alk && ld != null)
            {
                if (alk.Tones[idx] is { } a) q = ScaleForNote(a, ld.Tones[idx], note);
                if (alk.Tones[idx + 1] is { } a1) q1 = ScaleForNote(a1, ld.Tones[idx + 1], note);
            }
            if (Keyed(ld, idx)) spu.ArmBank2(voice, v[entry].Addr, (int)((long)v[entry].PitchQ12 * q >> 12));
            if (Keyed(ld, idx + 1)) spu.ArmBank2(voice + 1, v[entry + 1].Addr, (int)((long)v[entry + 1].PitchQ12 * q1 >> 12));
            return;
        }
        if (ownKey == loadedKey) return;   // the loaded bank is theirs
        var own = Bank(ownKey);
        if (own == null) return;
        var loaded = Bank(loadedKey);
        for (int i = 0; i < 2; i++)
        {
            if (!Keyed(loaded, idx + i)) continue;
            if (own.Tones[idx + i] is { Addr: > 0 } t) spu.ArmBank2(voice + i, t.Addr, ScaleForNote(t, loaded?.Tones[idx + i], note));
            else if (loaded != null) spu.ArmBank2(voice + i, SilenceAddr, 0x1000);
        }
    }
}
