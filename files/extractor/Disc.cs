using System.Text;

// A disc image's files (ISO9660): the PSP game's .iso (2048-byte sectors) and the
// PlayStation and Saturn games' raw track 1 .bin (2352-byte sectors: a 16-byte sync and
// header, 8 more bytes of subheader on MODE2, then the 2048 bytes of data).
sealed class Disc : IDisposable
{
    const int S = 2048, Raw = 2352;
    static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
    readonly FileStream _f;
    readonly bool _raw;
    readonly Dictionary<string, (uint Lba, uint Size)> _files = new(StringComparer.OrdinalIgnoreCase);

    public Disc(string path)
    {
        _f = File.OpenRead(path);
        var head = new byte[12];
        _f.ReadExactly(head);
        _raw = head.AsSpan().SequenceEqual(Sync);
        var pvd = Read(16, S);
        if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new InvalidDataException($"{Path.GetFileName(path)} isn't a disc image (no ISO9660 volume)");
        Walk(BitConverter.ToUInt32(pvd, 156 + 2), BitConverter.ToUInt32(pvd, 156 + 10), "", new HashSet<uint>());
    }

    public IEnumerable<string> Files => _files.Keys;

    public byte[] ReadFile(string path)
    {
        if (!_files.TryGetValue(path.TrimStart('/'), out var e)) throw new FileNotFoundException($"{path} isn't on the disc");
        return Read(e.Lba, (int)e.Size);
    }

    byte[] Read(uint lba, int n)
    {
        var out_ = new byte[n];
        if (!_raw)
        {
            _f.Seek((long)lba * S, SeekOrigin.Begin);
            int got = _f.ReadAtLeast(out_, n, throwOnEndOfStream: false);
            return got == n ? out_ : out_[..got];
        }
        int count = (n + S - 1) / S, done = 0;
        var sec = new byte[Raw];
        _f.Seek((long)lba * Raw, SeekOrigin.Begin);
        for (int i = 0; i < count && done < n; i++)
        {
            if (_f.ReadAtLeast(sec, Raw, throwOnEndOfStream: false) < Raw) break;   // (listed past the end of the track)
            int skip = sec[15] == 2 ? 24 : 16, take = Math.Min(S, n - done);
            Array.Copy(sec, skip, out_, done, take);
            done += take;
        }
        return done == n ? out_ : out_[..done];
    }

    void Walk(uint lba, uint size, string prefix, HashSet<uint> seen)
    {
        if (!seen.Add(lba)) return;
        var data = Read(lba, (int)size);
        for (int i = 0; i < data.Length;)
        {
            int len = data[i];
            if (len == 0) { i = (i / S + 1) * S; continue; }
            uint elba = BitConverter.ToUInt32(data, i + 2), esize = BitConverter.ToUInt32(data, i + 10);
            byte flags = data[i + 25];
            int nl = data[i + 32];
            var raw = data.AsSpan(i + 33, nl);
            i += len;
            if (nl == 1 && raw[0] <= 1) continue;   // . and ..
            string name = Encoding.ASCII.GetString(raw).Split(';')[0];
            string path = prefix.Length > 0 ? prefix + "/" + name : name;
            if ((flags & 2) != 0) Walk(elba, esize, path, seen);
            else _files[path] = (elba, esize);
        }
    }

    public void Dispose() => _f.Dispose();
}
