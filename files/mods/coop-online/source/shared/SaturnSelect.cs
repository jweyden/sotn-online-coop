using System;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

// The Saturn's "Player select" art (Richter / Alucard / Maria in frames, the chosen
// frame pulsing), from the player's own Saturn disc: tools/charselect-convert.py ->
// disc/charsel/CHARSEL.BIN (build-mariasat.sh). Drawn as host images in the title
// screen's 384x240 menu space. Used by the select after name entry (characters/CharSelect.cs) and
// the co-op lobby (NetplayMenu.cs).
//
// CHARSEL.BIN: "CSEL", w, h; the page (8bpp, w x h), its 256 colours, the glow ramp
// (20 steps of 16 colours); then 3 portraits: w, h, pixels, 256 colours.
public sealed class SaturnSelect
{
    public const string FileName = "disc/charsel/CHARSEL.BIN";
    const uint CurrentBufferPtr = 0x8006C37Cu, OtOfs = 0x474;
    const int OtSize = 0x200;
    public const int Order = 0x1FF;

    // Saturn order: 0 Alucard (middle), 1 Richter (left), 2 Maria (right)
    static readonly int[] FramePalette = { 13, 7, 14 };

    // Each frame (its pixels' bounding box, name included) and its opening, measured
    // from the page (Saturn pixels). The portraits sit behind the frames, centred in
    // the openings, as on the Saturn.
    public (int X0, int Y0, int X1, int Y1)[] Frame = new (int, int, int, int)[3];
    public (int X0, int Y0, int X1, int Y1)[] Opening = new (int, int, int, int)[3];
    readonly int[] _frameTex = { -1, -1, -1 };

    public int W, H;
    public byte[] Page = [];
    public ushort[] PagePalette = [], Glow = [];
    public (int W, int H, byte[] Px, ushort[] Pal)[] Portraits = [];
    readonly Dictionary<int, int> _pageTex = new();
    readonly int[] _portraitTex = { -1, -1, -1 };

    public static SaturnSelect? Load(out string status)
    {
        if (!File.Exists(FileName)) { status = "not built (build-mariasat.sh)"; return null; }
        try
        {
            var art = Read(File.ReadAllBytes(FileName));
            status = art != null ? "ready" : "bad file";
            return art;
        }
        catch (Exception e)
        {
            status = "bad file: " + e.Message;
            return null;
        }
    }

    static SaturnSelect? Read(byte[] d)
    {
        if (d.Length < 8 || BitConverter.ToUInt32(d, 0) != 0x4C455343) return null; // "CSEL"
        var a = new SaturnSelect { W = BitConverter.ToUInt16(d, 4), H = BitConverter.ToUInt16(d, 6) };
        int o = 8;
        a.Page = d.AsSpan(o, a.W * a.H).ToArray();
        o += a.W * a.H;
        a.PagePalette = U16s(d, ref o, 256);
        a.Glow = U16s(d, ref o, 20 * 16);
        a.Portraits = new (int, int, byte[], ushort[])[3];
        for (int k = 0; k < 3; k++)
        {
            int w = BitConverter.ToUInt16(d, o), h = BitConverter.ToUInt16(d, o + 2);
            o += 4;
            var px = d.AsSpan(o, w * h).ToArray();
            o += w * h;
            a.Portraits[k] = (w, h, px, U16s(d, ref o, 256));
        }
        for (int k = 0; k < 3; k++) a.MeasureFrame(k);
        return a;
    }

    void MeasureFrame(int k)
    {
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = Page[y * W + x];
                if ((v & 15) == 0 || v >> 4 != FramePalette[k]) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
        Frame[k] = (x0, y0, x1, y1);
        // the opening: rows inside the frame with a long see-through run
        int ox0 = W, ox1 = -1, oy0 = H, oy1 = -1;
        for (int y = y0; y <= y1; y++)
        {
            int best = 0, bestStart = 0, run = 0;
            for (int x = x0; x <= x1; x++)
            {
                run = (Page[y * W + x] & 15) == 0 ? run + 1 : 0;
                if (run > best) { best = run; bestStart = x - run + 1; }
            }
            if (best < (x1 - x0) * 3 / 4) continue;
            ox0 = Math.Min(ox0, bestStart); ox1 = Math.Max(ox1, bestStart + best);
            oy0 = Math.Min(oy0, y); oy1 = Math.Max(oy1, y + 1);
        }
        Opening[k] = ox1 > ox0 ? (ox0, oy0, ox1, oy1) : (x0, y0, x1 + 1, y1 + 1);
    }

    static ushort[] U16s(byte[] d, ref int o, int n)
    {
        var r = new ushort[n];
        for (int i = 0; i < n; i++, o += 2) r[i] = BitConverter.ToUInt16(d, o);
        return r;
    }

    public static void Rgba(byte[] rgba, int i, ushort c)
    {
        rgba[i * 4] = (byte)((c & 31) << 3);
        rgba[i * 4 + 1] = (byte)(((c >> 5) & 31) << 3);
        rgba[i * 4 + 2] = (byte)(((c >> 10) & 31) << 3);
        rgba[i * 4 + 3] = 255;
    }

    // The page with the chosen frame's palette at a step of the glow ramp.
    public int PageTexture(int choice, int step)
    {
        int key = choice * 32 + step;
        if (_pageTex.TryGetValue(key, out int t)) return t;
        var pal = (ushort[])PagePalette.Clone();
        for (int k = 0; k < 3; k++)
            Array.Copy(Glow, (k == choice ? step : 0) * 16, pal, FramePalette[k] * 16, 16);
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < Page.Length; i++)
            if ((Page[i] & 15) != 0) Rgba(rgba, i, pal[Page[i]]);
        t = GpuPrims.RegisterImage(rgba, W, H);
        if (t >= 0) _pageTex[key] = t;
        return t;
    }

    public int PortraitTexture(int k)
    {
        if (_portraitTex[k] >= 0) return _portraitTex[k];
        var (w, h, px, pal) = Portraits[k];
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
            if (px[i] != 0) Rgba(rgba, i, pal[px[i]]);
        return _portraitTex[k] = GpuPrims.RegisterImage(rgba, w, h);
    }

    // Host prims go into this frame's ordering table (call once a frame before drawing).
    public static void BeginDraw(IMemory m) => GpuPrims.SetOrderingTable(m.ReadU32(CurrentBufferPtr) + OtOfs, OtSize);

    // The whole screen: the portraits, then the page over them with `choice` glowing
    // (`timer` steps the glow); `level` fades it all (0x80 = full); a dimmed Maria if she
    // can't be picked. `backdrop`: black behind it (else whatever the menu shows).
    public void DrawScreen(int choice, int timer, byte level, bool mariaDim, bool backdrop = true)
    {
        float sx = 384f / W, sy = 240f / H;   // the Saturn's 320x224 over the PS1 menu's 384x240
        if (backdrop) Rect(0, 0, 384, 240, 0);
        if (level == 0) return;
        for (int k = 0; k < 3; k++)
            DrawPortraitIn(k, 0, 0, sx, sy, k == 2 && mariaDim ? (byte)(level / 3) : level);
        int page = PageTexture(choice, timer / 2 % 20);
        if (page >= 0) Image(page, 0, 0, W * sx, H * sy, level);
    }

    // A portrait in its frame's opening (a little under the frame's edge), with the page
    // placed at (ox, oy) and scaled by (sx, sy).
    void DrawPortraitIn(int k, float ox, float oy, float sx, float sy, byte level)
    {
        int tex = PortraitTexture(k);
        if (tex < 0) return;
        var (w, h, _, _) = Portraits[k];
        var o = Opening[k];
        float cx = (o.X0 + o.X1) / 2f, cy = (o.Y0 + o.Y1) / 2f;
        // the part of the portrait that shows: the opening, plus 2 pixels under the frame
        float vx0 = Math.Max(cx - w / 2f, o.X0 - 2), vx1 = Math.Min(cx + w / 2f, o.X1 + 2);
        float vy0 = Math.Max(cy - h / 2f, o.Y0 - 2), vy1 = Math.Min(cy + h / 2f, o.Y1 + 2);
        float u0 = (vx0 - (cx - w / 2f)) / w, u1 = (vx1 - (cx - w / 2f)) / w;
        float v0 = (vy0 - (cy - h / 2f)) / h, v1 = (vy1 - (cy - h / 2f)) / h;
        ImageUV(tex, ox + vx0 * sx, oy + vy0 * sy, (vx1 - vx0) * sx, (vy1 - vy0) * sy, u0, v0, u1, v1, level);
    }

    // One character in its frame (name and all), its top left at (x, y) in menu space,
    // `scale` of the select screen's size.
    public void DrawFramed(int k, float x, float y, float scale, byte level)
    {
        var f = Frame[k];
        float sx = 384f / W * scale, sy = 240f / H * scale;
        DrawPortraitIn(k, x - f.X0 * sx, y - f.Y0 * sy, sx, sy, level);
        int tex = FrameTexture(k);
        if (tex >= 0) Image(tex, x, y, (f.X1 - f.X0 + 1) * sx, (f.Y1 - f.Y0 + 1) * sy, level);
    }

    // Size of a framed character in menu space at `scale`.
    public (float W, float H) FramedSize(int k, float scale)
    {
        var f = Frame[k];
        return ((f.X1 - f.X0 + 1) * 384f / W * scale, (f.Y1 - f.Y0 + 1) * 240f / H * scale);
    }

    // Just that frame's pixels from the page (not glowing).
    int FrameTexture(int k)
    {
        if (_frameTex[k] >= 0) return _frameTex[k];
        var f = Frame[k];
        int fw = f.X1 - f.X0 + 1, fh = f.Y1 - f.Y0 + 1;
        if (fw <= 0 || fh <= 0) return -1;
        var pal = (ushort[])PagePalette.Clone();
        for (int j = 0; j < 3; j++) Array.Copy(Glow, 0, pal, FramePalette[j] * 16, 16);
        var rgba = new byte[fw * fh * 4];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                byte v = Page[(f.Y0 + y) * W + f.X0 + x];
                if ((v & 15) != 0 && (v >> 4 == FramePalette[k] || v >> 4 == 2)) Rgba(rgba, y * fw + x, pal[v]);
            }
        return _frameTex[k] = GpuPrims.RegisterImage(rgba, fw, fh);
    }

    public static void Rect(float x, float y, float w, float h, byte grey) =>
        GpuPrims.Quad(Order, new PrimVertex(x, y, grey, grey, grey), new PrimVertex(x + w, y, grey, grey, grey),
            new PrimVertex(x, y + h, grey, grey, grey), new PrimVertex(x + w, y + h, grey, grey, grey), raw: false);

    static void ImageUV(int tex, float x, float y, float w, float h, float u0, float v0, float u1, float v1, byte level) =>
        GpuPrims.Quad(Order,
            new PrimVertex(x, y, u0, v0, level, level, level), new PrimVertex(x + w, y, u1, v0, level, level, level),
            new PrimVertex(x, y + h, u0, v1, level, level, level), new PrimVertex(x + w, y + h, u1, v1, level, level, level),
            tex, raw: false);

    public static void Image(int tex, float x, float y, float w, float h, byte level) =>
        GpuPrims.Quad(Order,
            new PrimVertex(x, y, 0, 0, level, level, level), new PrimVertex(x + w, y, 1, 0, level, level, level),
            new PrimVertex(x, y + h, 0, 1, level, level, level), new PrimVertex(x + w, y + h, 1, 1, level, level, level),
            tex, raw: false);

    // Which portrait (Saturn order) a character's is.
    public static int PortraitOf(CoopCharacter c) => c switch
    {
        CoopCharacter.Richter or CoopCharacter.RichterCoat => 1,
        CoopCharacter.MariaPsp or CoopCharacter.MariaSaturn => 2,
        _ => 0,
    };
}
