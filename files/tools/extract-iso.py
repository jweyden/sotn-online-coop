"""Minimal ISO9660 reader (macOS bsdtar can't read the PSP UMD image). Takes
plain 2048-byte-sector images and raw MODE1/MODE2 2352 tracks (the Saturn
and PS1 bins).
usage: extract-iso.py ISO list
       extract-iso.py ISO extract OUTDIR [PATH-SUBSTRING ...]
"""
import os, struct, sys

S = 2048
SYNC = b"\x00" + b"\xff" * 10 + b"\x00"
RAW = False  # raw 2352-byte sectors: sync/header (16 bytes, +8 subheader on MODE2), 2048 of data, EDC/ECC

def read(f, lba, n):
    if not RAW:
        f.seek(lba * S)
        return f.read(n)
    out = bytearray()
    count = (n + S - 1) // S
    f.seek(lba * 2352)
    raw = f.read(count * 2352)
    for i in range(count):
        sec = raw[i * 2352:(i + 1) * 2352]
        if len(sec) < 2352:  # file listed past the end of the track
            break
        skip = 24 if sec[15] == 2 else 16
        out += sec[skip:skip + S]
    return bytes(out[:n])

def walk(f, lba, size, prefix, seen):
    if lba in seen:
        return
    seen.add(lba)
    data = read(f, lba, size)
    i = 0
    while i < len(data):
        ln = data[i]
        if ln == 0:
            i = (i // S + 1) * S
            continue
        rec = data[i:i + ln]
        elba, esize = struct.unpack_from("<I", rec, 2)[0], struct.unpack_from("<I", rec, 10)[0]
        flags = rec[25]
        nl = rec[32]
        name = rec[33:33 + nl]
        i += ln
        if name in (b"\x00", b"\x01"):
            continue
        name = name.decode("ascii", "replace").split(";")[0]
        path = prefix + "/" + name if prefix else name
        if flags & 2:
            yield from walk(f, elba, esize, path, seen)
        else:
            yield path, elba, esize

def main():
    global RAW
    iso, cmd = sys.argv[1], sys.argv[2]
    with open(iso, "rb") as f:
        RAW = f.read(12) == SYNC
        pvd = read(f, 16, S)
        assert pvd[1:6] == b"CD001", "not ISO9660"
        root = pvd[156:156 + 34]
        rlba, rsize = struct.unpack_from("<I", root, 2)[0], struct.unpack_from("<I", root, 10)[0]
        files = list(walk(f, rlba, rsize, "", set()))
        if cmd == "list":
            for p, l, s in files:
                print(f"{s:>10} {l:>8} {p}")
            return
        out, subs = sys.argv[3], sys.argv[4:]
        for p, l, s in files:
            if subs and not any(x.lower() in p.lower() for x in subs):
                continue
            dst = os.path.join(out, p)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, "wb") as o:
                left, lba = s, l
                while left:
                    n = min(left, 512 * S)
                    o.write(read(f, lba, n))
                    left -= n
                    lba += n // S
        print(f"extracted {sum(1 for _ in files)} entries scanned")

if __name__ == "__main__":
    main()
