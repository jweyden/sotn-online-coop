using Sotn;
using System;
using System.Linq;
using System.Numerics;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using ImGuiNET;

// Each friend's name above their head, with their health under it, in their player
// colour. If they're in the room but off screen, the tag sits at the screen edge
// pointing their way. While they're in a menu (a ghost on our screen) the tag fades
// and three dots bob after the name.
// Positions are worked out on the game thread; the overlay (render thread) only draws.
public partial class OnlineCoop
{
    public bool ShowNameTag = true;
    sealed record Tag(float X, float Y, int Edge, string Text, float Hp, bool Ghost, int Player);
    // X, Y: game screen pixels (the tag's centre); Edge: -1 off to the left, 1 off to
    // the right, 0 on screen; Hp 0..1. Replaced whole each frame (the render thread reads it).
    volatile Tag[] _tags = Array.Empty<Tag>();
    bool _overlayHooked;

    // Player colours (name tags, map markers): by player number.
    static readonly (int R, int G, int B)[] PlayerColors =
        { (255, 216, 96), (110, 200, 255), (120, 230, 120), (255, 130, 200), (255, 160, 70), (190, 150, 255) };
    static (int R, int G, int B) PlayerColor(int player) => PlayerColors[Math.Clamp(player, 0, PlayerColors.Length - 1)];

    void HookOverlay(bool on)
    {
        if (on == _overlayHooked) return;
        if (on) ModOverlay.Draw += DrawOverlay; else ModOverlay.Draw -= DrawOverlay;
        _overlayHooked = on;
    }

    // Friends get no game HUD of their own online (it took the screen's right side):
    // the overlay draws a small one for each instead (top right: name, HP, MP, hearts).
    sealed record MiniHud(string Name, int Player, int Hp, int HpMax, int Mp, int MpMax, int Hearts, bool Ghost);
    volatile MiniHud[] _miniHuds = Array.Empty<MiniHud>();
    protected override bool ShowGameHud(Remote r) => false;

    // Once a frame from OnNetFrame (P1 live, the friends in their buffers).
    void UpdateNameTag(IMemory m)
    {
        var huds = new System.Collections.Generic.List<MiniHud>();
        if (Game.InGame)
            foreach (var f in _friends.Where(f => f.Body.Active && f.Valid).OrderBy(f => f.Number))
            {
                var st = f.Peer.Stats;
                huds.Add(new MiniHud(f.Name, f.Number, st[0], st[1], st[4], st[5], st[2], f.Peer.InMenu));
            }
        _miniHuds = huds.ToArray();
        if (!ShowNameTag || !Game.InGame || Paused(m)) { _tags = Array.Empty<Tag>(); return; }
        var tags = new System.Collections.Generic.List<Tag>();
        foreach (var f in _friends)
        {
            if (!f.Body.Active || !f.Valid || f.Body.WantDrop) continue;
            var (x, y) = ScreenOf(m, f.Body);
            y -= 46;                                  // above their head
            float hp = f.Peer.Stats is { } st && st[1] > 0 ? Math.Clamp(st[0] / (float)st[1], 0, 1) : 1;
            tags.Add(new Tag(Math.Clamp(x, 8, 248), Math.Clamp(y, 8, 224), x < 0 ? -1 : x > 255 ? 1 : 0, f.Name, hp, f.Peer.InMenu, f.Number));
        }
        _tags = tags.ToArray();
    }

    static uint Rgba(int r, int g, int b, float a) => (uint)(Math.Clamp(a, 0, 1) * 255) << 24 | (uint)b << 16 | (uint)g << 8 | (uint)r;

    // (on the render thread: an error here would take the game down, so it only logs)
    void DrawOverlay(Vector2 min, Vector2 size)
    {
        try { DrawGate(min, size); DrawTags(min, size); DrawMiniHuds(min, size); }
        catch (Exception e) { if (!_overlayFailed) Console.WriteLine($"[coop-online] name tag drawing failed: {e}"); _overlayFailed = true; }
    }
    bool _overlayFailed;

    // the new-game wait, over the "Now Loading" screen
    void DrawGate(Vector2 min, Vector2 size)
    {
        string text = _gateText;
        if (text.Length == 0) return;
        var font = ImGui.GetFont();
        float fontSize = Math.Clamp(size.Y / 22f, 14f, 56f), y = min.Y + size.Y * 0.68f;
        var dl = ImGui.GetWindowDrawList();
        foreach (var line in text.Split('\n'))
        {
            float fs = line.StartsWith("(") || line.StartsWith("They") ? fontSize * 0.7f : fontSize;
            var ts = font.CalcTextSizeA(fs, float.MaxValue, 0, line);
            var p = new Vector2(min.X + (size.X - ts.X) / 2, y);
            dl.AddText(font, fs, p + new Vector2(2, 2), 0xC0000000, line);
            dl.AddText(font, fs, p, Rgba(255, 216, 96, 1), line);
            y += ts.Y * 1.25f;
        }
    }

    void DrawMiniHuds(Vector2 min, Vector2 size)
    {
        var huds = _miniHuds;
        if (huds.Length == 0) return;
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        int dh = gpu?.DisplayHeight ?? 240;
        if (dh <= 0) return;
        float u = size.Y / dh;   // one game pixel
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        float fontSize = Math.Clamp(size.Y / 34f, 11f, 40f);
        float w = 64 * u, x1 = min.X + size.X - 6 * u, x0 = x1 - w, y = min.Y + 6 * u;
        foreach (var h in huds)
        {
            var (cr, cg, cb) = PlayerColor(h.Player);
            float a = h.Ghost ? 0.55f : 1f;
            string hearts = $"♥{h.Hearts}";
            var ts = font.CalcTextSizeA(fontSize, float.MaxValue, 0, h.Name);
            float rowH = ts.Y + 7 * u;
            dl.AddRectFilled(new(x0 - 3 * u, y - u), new(x1 + 2 * u, y + rowH), Rgba(14, 10, 24, 0.7f * a), 2 * u);
            dl.AddText(font, fontSize, new(x0, y), Rgba(cr, cg, cb, a), h.Name);
            var hs = font.CalcTextSizeA(fontSize * 0.85f, float.MaxValue, 0, hearts);
            dl.AddText(font, fontSize * 0.85f, new(x1 - hs.X, y), Rgba(240, 200, 200, a), hearts);
            float by = y + ts.Y + u;
            void Bar(float frac, uint col, float yy)
            {
                dl.AddRectFilled(new(x0, yy), new(x1, yy + 2 * u), Rgba(40, 10, 14, 0.9f * a));
                if (frac > 0) dl.AddRectFilled(new(x0, yy), new(x0 + w * Math.Clamp(frac, 0, 1), yy + 2 * u), col);
            }
            Bar(h.HpMax > 0 ? h.Hp / (float)h.HpMax : 0, Rgba(220, 40, 50, a), by);
            Bar(h.MpMax > 0 ? h.Mp / (float)h.MpMax : 0, Rgba(60, 110, 240, a), by + 3 * u);
            y += rowH + 3 * u;
        }
    }

    void DrawTags(Vector2 min, Vector2 size)
    {
        foreach (var t in _tags) DrawTag(min, size, t);
    }

    void DrawTag(Vector2 min, Vector2 size, Tag tag)
    {
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        int dw = gpu?.DisplayWidth ?? 256, dh = gpu?.DisplayHeight ?? 240;
        if (dw <= 0 || dh <= 0) return;
        float sx = size.X / dw, sy = size.Y / dh, u = sy;   // u: one game pixel
        double now = ImGui.GetTime();
        bool ghost = tag.Ghost;
        var (cr, cg, cb) = PlayerColor(tag.Player);
        float alpha = ghost ? 0.55f : 1f;

        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        float fontSize = Math.Clamp(size.Y / 30f, 12f, 44f);
        var ts = font.CalcTextSizeA(fontSize, float.MaxValue, 0, tag.Text);
        float dotsW = ghost ? font.CalcTextSizeA(fontSize, float.MaxValue, 0, " ...").X : 0;
        ts.X += dotsW;
        float padX = 5 * u, padY = 2 * u, barH = Math.Max(2, 2 * u);
        var boxSize = new Vector2(ts.X + padX * 2, ts.Y + padY * 2 + barH + u);
        var centre = new Vector2(min.X + tag.X * sx, min.Y + tag.Y * sy);
        var p0 = centre - boxSize / 2;
        p0.X = Math.Clamp(p0.X, min.X + 2, min.X + size.X - boxSize.X - 2);
        var p1 = p0 + boxSize;
        float r = 3 * u;

        // drop shadow, plate, gold rim
        dl.AddRectFilled(p0 + new Vector2(u, u), p1 + new Vector2(u, u), Rgba(0, 0, 0, 0.35f * alpha), r);
        dl.AddRectFilled(p0, p1, Rgba(14, 10, 24, 0.78f * alpha), r);
        uint rim = Rgba(cr * 4 / 5, cg * 4 / 5, cb * 4 / 5, 0.9f * alpha);
        dl.AddRect(p0, p1, rim, r, ImDrawFlags.None, Math.Max(1, 0.75f * u));
        // pointer: down at their head, or out to the screen edge they're past
        if (tag.Edge == 0)
        {
            float cx = Math.Clamp(centre.X, p0.X + r + 3 * u, p1.X - r - 3 * u);
            dl.AddTriangleFilled(new(cx - 3 * u, p1.Y), new(cx + 3 * u, p1.Y), new(cx, p1.Y + 4 * u), rim);
        }
        else
        {
            float ex = tag.Edge < 0 ? p0.X - 5 * u : p1.X + 5 * u, bx = tag.Edge < 0 ? p0.X - u : p1.X + u, my = (p0.Y + p1.Y) / 2;
            dl.AddTriangleFilled(new(bx, my - 4 * u), new(bx, my + 4 * u), new(ex, my), rim);
        }
        // name (gold, or pale while they're a ghost)
        var tp = new Vector2(p0.X + padX, p0.Y + padY);
        dl.AddText(font, fontSize, tp + new Vector2(u * 0.6f, u * 0.6f), Rgba(0, 0, 0, 0.8f * alpha), tag.Text);
        dl.AddText(font, fontSize, tp, ghost ? Rgba(225, 225, 240, alpha) : Rgba(cr, cg, cb, 1), tag.Text);
        // health
        var b0 = new Vector2(p0.X + padX, tp.Y + ts.Y + u * 0.5f);
        var b1 = new Vector2(p1.X - padX, b0.Y + barH);
        dl.AddRectFilled(b0, b1, Rgba(60, 8, 12, 0.9f * alpha));
        if (tag.Hp > 0)
        {
            var hpCol = tag.Hp > 0.5f ? Rgba(220, 40, 50, alpha) : tag.Hp > 0.25f ? Rgba(240, 120, 40, alpha) : Rgba(255, 60, 60, alpha * (0.6f + 0.4f * (float)Math.Abs(Math.Sin(now * 6))));
            dl.AddRectFilled(b0, new Vector2(b0.X + (b1.X - b0.X) * tag.Hp, b1.Y), hpCol);
        }

        // "thinking" dots, bobbing one after another
        if (ghost)
        {
            float x0 = tp.X + ts.X - dotsW * 0.62f, dy = tp.Y + ts.Y * 0.62f;
            for (int i = 0; i < 3; i++)
            {
                float phase = (float)((now * 2.4 - i * 0.28) % 1.6);
                float jump = phase < 0.5f ? (float)Math.Sin(phase / 0.5f * Math.PI) : 0;
                dl.AddCircleFilled(new Vector2(x0 + i * dotsW * 0.24f, dy - jump * 2.5f * u), Math.Max(1.2f, 0.9f * u), Rgba(225, 225, 240, alpha), 10);
            }
        }
    }

}
