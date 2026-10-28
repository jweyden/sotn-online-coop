// Saturn Maria's effect graphics (MARIASAT.FX), drawn by the mod as host images: her
// weapon sprite packages 1..12 (frames and palettes in MARIA.PRG, pixels in MAR_W.CHR) and
// 13, the shared fire her fire spell throws (0.BIN's sprite bank 11, whose pixels every
// stage's CHR carries). A port of tools/mariasat-convert.py's write_fx, output byte for
// byte the same.
//   MSFX: u32 magic, u32 count, count x u32 package offsets (0 = none); package: u16 banks,
//   u16 images, u16 frames, u16 0; banks x 16 u16 colours (Saturn RGB555); images: u16 w,
//   u16 h, w*h u8 indices; frames: u16 parts, parts x (u16 attr, s16 x, s16 y, u16 image).
//   Little-endian; in Saturn pixels.
static class MariaFx
{
    const int Resources = 0x21D68;   // MARIA.PRG: 12-byte entries (image table, palette, ...)
    // each package's frames (one after another, then their pointer table, which can list
    // more: frame 0 again) and how many of each
    static readonly (uint Address, int Frames, int Table)[] Packages = [
        (0, 0, 0), (0x060C5714, 55, 55), (0x060C5D70, 2, 2), (0x060C5DF0, 4, 4), (0x060C5EAC, 13, 13), (0x060C5FD8, 38, 38),
        (0x060C63E0, 5, 5), (0x060C64C0, 9, 9), (0x060C6590, 16, 17), (0x060C680C, 19, 20), (0x060C6978, 9, 10),
        (0x060C6ABC, 14, 14), (0x060C6BF0, 18, 19)];
    // the packages aren't in MAR_W.CHR in index order: 8 comes before 4, and 6 before 5
    static readonly int[] ChrOrder = [1, 2, 3, 8, 4, 6, 5, 7, 9, 10, 11, 12];
    const int Beasts = 6, Hud = 5, Fire = 13;
    // her HUD's subweapon icons (subweapons 1..9), from 0.BIN's shared bank 0, added to
    // package 5 as images 18..26
    static readonly int[] HudSubweaponIcons = [0x33, 0x37, 0x34, 0x38, 0x35, 0x36, 0x24, 0x3A, 0x39];
    // images some objects draw directly as a quad: each gets a frame of its own (the
    // dragon's head and body, package 12)
    static readonly Dictionary<int, int[]> ExtraFrames = new() { [12] = [3, 4] };
    // 0.BIN: loaded at 0x06004080; its shared sprite banks' image tables (0..11); the fire
    // (bank 11) frames; bank 7's palettes, of which 12 (fire) and 5 (a double-damage volley)
    const uint ZeroBase = 0x06004080, FireFrames = 0x06047420, FireBank7Palettes = 0x06046854;
    static readonly uint[] ZeroImageTables = [0x06045344, 0x06045F24, 0x06045FF4, 0x060461A8, 0x06046238, 0x06046658,
        0x060466EC, 0x06046818, 0x06046CDC, 0x06046DFC, 0x06047120, 0x060474C0];
    static readonly int[] FirePalettes = [12, 5];

    public static byte[] Build(byte[] prg, byte[] marW, byte[] zero, byte[] stage00Chr)
    {
        // where each package's pixels start in MAR_W.CHR
        var start = new Dictionary<int, int>();
        int at = 0;
        foreach (int n in ChrOrder)
        {
            start[n] = at;
            at += Saturn.ImageTable(prg, ImagesOf(prg, n), marW, 0).Size;
        }
        if (at != marW.Length) throw new InvalidDataException("MAR_W.CHR isn't the expected size");
        // 0.BIN's shared banks: their pixels in the stage CHR, one after another
        var shared = new List<List<Saturn.Image>>();
        int sharedAt = 0;
        foreach (uint t in ZeroImageTables)
        {
            var (ims, size) = Saturn.ImageTable(zero, Saturn.Prg(t, ZeroBase), stage00Chr, sharedAt);
            shared.Add(ims);
            sharedAt += size;
        }

        var packages = new Dictionary<int, byte[]>();
        for (int n = 1; n < Packages.Length; n++)
        {
            var colors = Palette(prg, Saturn.Prg(Saturn.U32(prg, Resources + n * 12 + 4)));
            var images = Saturn.ImageTable(prg, ImagesOf(prg, n), marW, start[n]).Images;
            if (n == Hud) images.AddRange(HudSubweaponIcons.Select(i => shared[0][i]));
            var frames = Saturn.FrameGroup(prg, Packages[n].Address, Packages[n].Frames, Packages[n].Table);
            if (n == Beasts)   // (the Saturn draws them as a quad centred on the object)
                frames = frames.Select(f => f?.Select(p => p with { X = 0, Y = 0 }).ToArray()).ToList();
            foreach (int i in ExtraFrames.GetValueOrDefault(n, []))
                frames.Add([new Saturn.Part(0, 0, 0, (ushort)i)]);
            packages[n] = Package(colors, images, frames);
        }
        var fire = new List<ushort>();
        foreach (int k in FirePalettes)
            for (int i = 0; i < 16; i++) fire.Add(Saturn.U16(zero, Saturn.Prg(FireBank7Palettes, ZeroBase) + 2 + k * 32 + i * 2));
        packages[Fire] = Package(fire.ToArray(), shared[11], Saturn.FrameGroup(zero, FireFrames, 8, 8, ZeroBase));

        const int Count = Fire + 1;
        var body = new List<byte>();
        var table = new uint[Count];
        for (int n = 0; n < Count; n++)
        {
            if (!packages.TryGetValue(n, out var p)) continue;
            table[n] = (uint)(8 + 4 * Count + body.Count);
            body.AddRange(p);
            while (body.Count % 4 != 0) body.Add(0);
        }
        var out_ = new List<byte>();
        out_.AddRange(BitConverter.GetBytes(0x5846534Du));
        out_.AddRange(BitConverter.GetBytes((uint)Count));
        foreach (uint o in table) out_.AddRange(BitConverter.GetBytes(o));
        out_.AddRange(body);
        return out_.ToArray();
    }

    static int ImagesOf(byte[] prg, int n) => Saturn.Prg(Saturn.U32(prg, Resources + n * 12));

    // a palette object: a u16 count (banks of 16 in the low 14 bits), then the colours
    static ushort[] Palette(byte[] d, int o) =>
        Enumerable.Range(0, (Saturn.U16(d, o) & 0x3FFF) * 16).Select(i => Saturn.U16(d, o + 2 + i * 2)).ToArray();

    static byte[] Package(ushort[] colors, List<Saturn.Image> images, List<Saturn.Part[]?> frames)
    {
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        U16(colors.Length / 16); U16(images.Count); U16(frames.Count); U16(0);
        foreach (var c in colors) U16(c);
        foreach (var im in images) { U16(im.Width); U16(im.Height); b.AddRange(im.Pixels); }
        foreach (var f in frames)
        {
            U16(f?.Length ?? 0);
            foreach (var p in f ?? []) { U16(p.Attr); U16(p.X); U16(p.Y); U16(p.Image); }
        }
        return b.ToArray();
    }
}
