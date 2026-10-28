using System.Security.Cryptography;
using System.Text.Json;

// A character's player program from its blank build (GitHub: no disc data) and its recipe
// (files/decomp-mods/recipes): each listed byte range is either copied from a file on one
// of the player's discs, or made by a converter (MadeBytes).
static class Recipes
{
    public sealed record Piece(int At, int Len, string? Disc, string? Path, int From, string? Make);
    public sealed record Recipe(string File, int Size, string Sha1Blank, string Sha1, Dictionary<string, int> Symbols, List<Piece> Fill);

    public static Recipe Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = doc.RootElement;
        var fill = r.GetProperty("fill").EnumerateArray().Select(p => new Piece(
            p.GetProperty("at").GetInt32(), p.GetProperty("len").GetInt32(),
            p.TryGetProperty("disc", out var d) ? d.GetString() : null,
            p.TryGetProperty("path", out var pa) ? pa.GetString() : null,
            p.TryGetProperty("from", out var f) ? f.GetInt32() : 0,
            p.TryGetProperty("make", out var mk) ? mk.GetString() : null)).ToList();
        var symbols = r.TryGetProperty("symbols", out var sy)
            ? sy.EnumerateObject().ToDictionary(s => s.Name, s => s.Value.GetInt32()) : [];
        return new Recipe(r.GetProperty("file").GetString()!, r.GetProperty("size").GetInt32(),
            r.GetProperty("sha1_blank").GetString()!, r.GetProperty("sha1").GetString()!, symbols, fill);
    }

    // made: the converters' bytes by symbol, or null (left blank). Returns how many bytes
    // are still missing (0: the real program, checked against the recipe).
    public static int Fill(Recipe r, byte[] blank, IReadOnlyDictionary<string, Disc> discs, Func<Piece, byte[]?>? made, out byte[] program)
    {
        if (blank.Length != r.Size || Sha1(blank) != r.Sha1Blank)
            throw new InvalidDataException($"{r.File}: the blank program isn't the one its recipe is for");
        program = (byte[])blank.Clone();
        var files = new Dictionary<(string, string), byte[]>();
        int missing = 0;
        foreach (var p in r.Fill)
        {
            if (p.Disc != null)
            {
                if (!files.TryGetValue((p.Disc, p.Path!), out var src))
                    files[(p.Disc, p.Path!)] = src = discs[p.Disc].ReadFile(p.Path!);
                Array.Copy(src, p.From, program, p.At, p.Len);
            }
            else if (made?.Invoke(p) is { } bytes && bytes.Length == p.Len) Array.Copy(bytes, 0, program, p.At, p.Len);
            else missing += p.Len;
        }
        if (missing == 0 && Sha1(program) != r.Sha1)
            throw new InvalidDataException($"{r.File}: built, but it isn't the right program (a different disc?)");
        return missing;
    }

    public static string Sha1(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();
}
