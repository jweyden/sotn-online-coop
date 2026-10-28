// sotn-extract: the player kit's character builder, in .NET (no Docker, Python or
// compilers). Setup runs it; by hand:
//   sotn-extract list IMAGE                       the files on a disc image
//   sotn-extract extract IMAGE OUTDIR [PART ...]  copy them out (only paths containing a PART)
//   sotn-extract fill RECIPE BLANK OUT --ps1 BIN --psp ISO --saturn BIN
//                                                 a character's program from its blank build
//                                                 (the Saturn players: and their sprites, OUT's .SPR;
//                                                 Saturn Maria's effects and sounds, .FX, .SND)
//   sotn-extract characters RECIPES BLANKS OUTDIR --ps1 BIN --psp ISO --saturn BIN
//                                                 all of the above, into OUTDIR/maria, mariasat, ...
//   sotn-extract psp-maria ISO OUTDIR             PSP Maria's voice bank and the PSP Alucard voice
//   sotn-extract charsel SATURN_BIN OUT           the Saturn's character select screen (CHARSEL.BIN)
//   sotn-extract funcmap-prefix FUNCMAPS_DIR      prefix the characters' clashing function names

static int Usage()
{
    Console.Error.WriteLine("usage: sotn-extract list IMAGE | extract IMAGE OUTDIR [PART ...] | fill RECIPE BLANK OUT --ps1 BIN --psp ISO --saturn BIN | characters RECIPES BLANKS OUTDIR --ps1 BIN --psp ISO --saturn BIN | psp-maria ISO OUTDIR | charsel SATURN_BIN OUT | funcmap-prefix DIR");
    return 1;
}

static Dictionary<string, Disc> OpenDiscs(string[] args)
{
    var discs = new Dictionary<string, Disc>();
    for (int i = 0; i + 1 < args.Length; i++)
        if (args[i] is "--ps1" or "--psp" or "--saturn") discs[args[i][2..]] = new Disc(args[++i]);
    return discs;
}

// A character's program from its recipe and blank build (the Saturn players: and their
// other files, next to it). Returns how many bytes are still missing.
static int Fill(string recipe, string blankPath, string outPath, Dictionary<string, Disc> discs)
{
    var r = Recipes.Load(recipe);
    var blank = File.ReadAllBytes(blankPath);
    Func<Recipes.Piece, byte[]?>? made = null;
    // the Saturn players: their sprites, and their converted tables written into a copy of
    // the blank
    if (r.File is "MARIASAT.BIN" or "RICHTER2.BIN" && discs.TryGetValue("saturn", out var saturn))
    {
        string who = r.File == "MARIASAT.BIN" ? "MARIA" : "RICHTER2";
        var prg = saturn.ReadFile(who + ".PRG");
        var chr = saturn.ReadFile(who + ".CHR");
        var res = who == "MARIA" ? SaturnPlayers.Maria(prg, chr) : SaturnPlayers.Richter2(prg, chr);
        File.WriteAllBytes(Path.ChangeExtension(outPath, ".SPR"), res.Spr);
        if (who == "MARIA")
        {
            File.WriteAllBytes(Path.ChangeExtension(outPath, ".FX"), MariaFx.Build(prg, saturn.ReadFile("MAR_W.CHR"), saturn.ReadFile("0.BIN"), saturn.ReadFile("STAGE_00.CHR")));
            File.WriteAllBytes(Path.ChangeExtension(outPath, ".SND"), MariaSatSound.Build(saturn.ReadFile("SD/SD_MARI.CRT"), saturn.ReadFile("SD/SD_JO10.CRT"), saturn.ReadFile("SD/SDE7.PCM")));
        }
        var converted = (byte[])blank.Clone();
        SaturnPlayers.Apply(res, converted, r.Symbols);
        made = p => converted[p.At..(p.At + p.Len)];
    }
    int missing = Recipes.Fill(r, blank, discs, made, out var program);
    File.WriteAllBytes(outPath, program);
    Console.WriteLine(missing == 0 ? $"{r.File}: built from your discs" : $"{r.File}: built, {missing} bytes still to make");
    return missing;
}

// PSP Maria's voice bank (VH, and the VB next to it) and the PSP Alucard voice
static void PspMaria(Disc psp, string vhPath, string alkPath)
{
    const string Se = "PSP_GAME/USRDIR/res/ps/snd_e/SE/";
    var (vh, vb) = MariaVoice.Build(psp.ReadFile(Se + "sd_mar2.spk"));
    File.WriteAllBytes(vhPath, vh);
    File.WriteAllBytes(Path.ChangeExtension(vhPath, ".VB"), vb);
    File.WriteAllBytes(alkPath, psp.ReadFile(Se + "sd_alk.spk"));
    Console.WriteLine("PSP Maria's voice and the PSP Alucard voice: done");
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
            var discs = OpenDiscs(args[4..]);
            try
            {
                int missing = Fill(args[1], args[2], args[3], discs);
                return missing == 0 ? 0 : 2;
            }
            finally { foreach (var d in discs.Values) d.Dispose(); }
        }
        case "characters" when args.Length >= 4:
        {
            // everything, as Setup puts it into the game's disc folder
            var discs = OpenDiscs(args[4..]);
            try
            {
                foreach (var k in new[] { "ps1", "psp", "saturn" })
                    if (!discs.ContainsKey(k)) throw new FileNotFoundException($"no --{k} game given");
                string Out(string dir, string name) { Directory.CreateDirectory(Path.Combine(args[3], dir)); return Path.Combine(args[3], dir, name); }
                int missing = 0;
                foreach (var (dir, file) in new[] { ("maria", "MARIA.BIN"), ("mariasat", "MARIASAT.BIN"), ("richter2", "RICHTER2.BIN") })
                    missing += Fill(Path.Combine(args[1], dir + ".json"), Path.Combine(args[2], file), Out(dir, file), discs);
                PspMaria(discs["psp"], Out("maria", "SD_RIH.VH"), Out("psp", "sd_alk.spk"));
                var saturn = discs["saturn"];
                File.WriteAllBytes(Out("charsel", "CHARSEL.BIN"), CharSelect.Build(saturn.ReadFile("TITLE.PRG"), saturn.ReadFile("TITLE.MAP"), saturn.ReadFile("TITLE.CHR")));
                Console.WriteLine("the character select screen: done");
                return missing == 0 ? 0 : 2;
            }
            finally { foreach (var d in discs.Values) d.Dispose(); }
        }
        case "psp-maria" when args.Length >= 3:
        {
            using var psp = new Disc(args[1]);
            Directory.CreateDirectory(Path.Combine(args[2], "maria"));
            Directory.CreateDirectory(Path.Combine(args[2], "psp"));
            PspMaria(psp, Path.Combine(args[2], "maria", "SD_RIH.VH"), Path.Combine(args[2], "psp", "sd_alk.spk"));
            return 0;
        }
        case "charsel" when args.Length >= 3:
        {
            using var saturn = new Disc(args[1]);
            File.WriteAllBytes(args[2], CharSelect.Build(saturn.ReadFile("TITLE.PRG"), saturn.ReadFile("TITLE.MAP"), saturn.ReadFile("TITLE.CHR")));
            Console.WriteLine("the character select screen: done");
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
