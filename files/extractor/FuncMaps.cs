using System.Text.Json.Nodes;

// The characters' function names that clash with the game's get a prefix (the recompiler
// would suffix every copy), the same on every machine: the co-op mod's hooks use them.
//   maria.json     mar_  names the base game's maps have
//   mariasat.json  mas_  names any other map has (PSP Maria's included)
//   richter2.json  r2_   likewise
static class FuncMaps
{
    static readonly (string File, string Prefix)[] Characters = [("maria.json", "mar_"), ("mariasat.json", "mas_"), ("richter2.json", "r2_")];

    public static void Prefix(string dir)
    {
        var mine = Characters.Select(c => c.File).ToHashSet();
        foreach (var (file, prefix) in Characters)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            var others = Directory.GetFiles(dir, "*.json").Where(f => Path.GetFileName(f) != file);
            if (file == "maria.json") others = others.Where(f => !mine.Contains(Path.GetFileName(f)));
            var taken = new HashSet<string>();
            foreach (var f in others)
                foreach (var fn in JsonNode.Parse(File.ReadAllText(f))!["functions"]!.AsArray())
                    if (fn?["name"]?.GetValue<string>() is { } n) taken.Add(n);
            var map = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var fn in map["functions"]!.AsArray())
            {
                string n = fn!["name"]!.GetValue<string>();
                if (taken.Contains(n) && !n.StartsWith(prefix)) fn["name"] = prefix + n;
            }
            File.WriteAllText(path, map.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
