// sotn-extract: the player kit's character builder, in .NET (no Docker, Python or
// compilers). Setup runs it; by hand:
//   sotn-extract list IMAGE                       the files on a disc image
//   sotn-extract extract IMAGE OUTDIR [PART ...]  copy them out (only paths containing a PART)
//   sotn-extract fill RECIPE BLANK OUT --ps1 BIN --psp ISO --saturn BIN
//                                                 a character's program from its blank build
//   sotn-extract psp-maria ISO OUTDIR             PSP Maria's voice bank and the PSP Alucard voice
//   sotn-extract funcmap-prefix FUNCMAPS_DIR      prefix the characters' clashing function names

static int Usage()
{
    Console.Error.WriteLine("usage: sotn-extract list IMAGE | extract IMAGE OUTDIR [PART ...] | fill RECIPE BLANK OUT --ps1 BIN --psp ISO --saturn BIN | psp-maria ISO OUTDIR | funcmap-prefix DIR");
    return 1;
}

static Dictionary<string, Disc> OpenDiscs(string[] args)
{
    var discs = new Dictionary<string, Disc>();
    for (int i = 0; i + 1 < args.Length; i++)
        if (args[i] is "--ps1" or "--psp" or "--saturn") discs[args[i][2..]] = new Disc(args[++i]);
    return discs;
}

try
{
    if (args.Length < 2) return Usage();
    switch (args[0])
    {
        case "list":
        {
            using var d = new Disc(args[1]);
            foreach (var f in d.Files.Order()) Console.WriteLine(f);
            return 0;
        }
        case "extract" when args.Length >= 3:
        {
            using var d = new Disc(args[1]);
            var parts = args[3..];
            int n = 0;
            foreach (var f in d.Files)
            {
                if (parts.Length > 0 && !parts.Any(p => f.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                string dst = Path.Combine(args[2], f);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, d.ReadFile(f));
                n++;
            }
            Console.WriteLine($"extracted {n} files");
            return 0;
        }
        case "fill" when args.Length >= 4:
        {
            var r = Recipes.Load(args[1]);
            var discs = OpenDiscs(args[4..]);
            try
            {
                int missing = Recipes.Fill(r, File.ReadAllBytes(args[2]), discs, made: null, out var program);
                File.WriteAllBytes(args[3], program);
                Console.WriteLine(missing == 0 ? $"{r.File}: built from your discs" : $"{r.File}: built, {missing} bytes still to make (converters not done yet)");
                return missing == 0 ? 0 : 2;
            }
            finally { foreach (var d in discs.Values) d.Dispose(); }
        }
        case "psp-maria" when args.Length >= 3:
        {
            // her voice bank and the PSP Alucard voice, from the PSP game
            using var psp = new Disc(args[1]);
            Directory.CreateDirectory(Path.Combine(args[2], "maria"));
            Directory.CreateDirectory(Path.Combine(args[2], "psp"));
            var (vh, vb) = MariaVoice.Build(psp.ReadFile("PSP_GAME/USRDIR/res/ps/snd_e/SE/sd_mar2.spk"));
            File.WriteAllBytes(Path.Combine(args[2], "maria", "SD_RIH.VH"), vh);
            File.WriteAllBytes(Path.Combine(args[2], "maria", "SD_RIH.VB"), vb);
            File.WriteAllBytes(Path.Combine(args[2], "psp", "sd_alk.spk"), psp.ReadFile("PSP_GAME/USRDIR/res/ps/snd_e/SE/sd_alk.spk"));
            Console.WriteLine("PSP Maria's voice and the PSP Alucard voice: done");
            return 0;
        }
        case "funcmap-prefix":
            FuncMaps.Prefix(args[1]);
            return 0;
        default:
            return Usage();
    }
}
catch (Exception e) when (e is InvalidDataException or FileNotFoundException or IOException or KeyNotFoundException)
{
    Console.Error.WriteLine("sotn-extract: " + e.Message);
    return 1;
}
