using System;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

/*
 * The Saturn's character select, after name entry on a new game.
 *
 * On the Saturn, confirming a name goes to a "Player select" screen: Richter,
 * Alucard and Maria in frames, left/right to choose, the chosen frame pulsing;
 * picking Richter while holding up gives the long-coat Richter (TITLE.PRG,
 * title states 0x1b..0x1d). Here it's drawn over the PS1's name entry from the
 * player's own Saturn disc (tools/charselect-convert.py -> disc/charsel/
 * CHARSEL.BIN, built by build-mariasat.sh), as host images.
 *
 * The name entry's START runs func_801AD66C (which picks Alucard or, for
 * RICHTER on a cleared save, Richter) and moves on to its fade-out step; the
 * select holds that fade (func_801ACEC0) until a choice is made, then sets
 * g_PlayableCharacter and the first stage the same way, and lets it go on.
 * Six characters (CoopCharacters.cs): holding up on Alucard picks PSP Alucard (the
 * PSP voice), on Richter the long-coat Richter, on Maria the PSP one instead of the
 * Saturn one.
 *
 * Then (when ColourSets is on: off for now) a second window, styled like the game's
 * menu boxes, offers the character's three colour sets (CoopPalettes.cs) as
 * recoloured portraits: left/right choose, cross/circle/start confirm, triangle goes
 * back to the portraits.
 *
 * Online, the co-op lobby picks the character (NetplayMenu.cs): no select then.
 * The art itself is SaturnSelect.cs (shared with the lobby).
 *
 * The choice is kept in the save: saveName[10..11] are padding the game never
 * writes (names are 8 characters and a terminator) but saves with g_Status, so
 * a loaded game gets the same character and colours back (CoopCharacters.ToSave).
 */
public partial class MariaMod
{
    const uint PadsAddr = 0x80097490u;           // g_pads[0]: pressed, previous, tapped, repeat
    const uint EngineStepAddr = 0x8003C9A4u;     // g_GameEngineStep
    const uint StageIdAddr = 0x800974A0u;        // g_StageId
    const uint UpdEng17 = 0x11, UpdEng18 = 0x12; // name entry, its fade-out
    const uint StageSt0 = 0x1F, StageNo3 = 0x41;
    const byte SelectTag = 0xC5;                 // saveName[10] when [11] holds the choice
    const ushort PadUp = 0x1000, PadLeft = 0x8000, PadRight = 0x2000;
    const ushort PadTriangle = 0x10, PadCircle = 0x20, PadCross = 0x40, PadStart = 0x800;
    const uint SfxUiConfirm = 0x633, SfxUiMove = 0x67B, SfxUiError = 0x686;
    const int SelOrder = SaturnSelect.Order;

    public bool CharacterSelect = true;
    public bool ColourSets = false;   // the colour window (off until the flow is settled)

    SaturnSelect? _selArt;
    string _selStatus = "not loaded";
    // 0: off; 1: choosing; 2: chosen, black until the game moves on;
    // 3: cancelled, back to name entry next frame; 4: choosing colours
    int _selPhase, _selChoice, _selTimer;
    CoopCharacter _selCharacter;
    int _selPalette;

    void LoadCharSel() => _selArt = SaturnSelect.Load(out _selStatus);

    bool MariaAvailable => _satFiles.Length > 0 || _files.Length > 0;

    // The select's choice for this save: -1 if it was named instead, else
    // 0 Richter, 1 PSP Maria, 2 Saturn Maria, 3 long-coat Richter (WantMaria's numbers).
    static int SelectedVariant(IMemory m)
    {
        byte tag = m.ReadU8(SaveNameAddr + 10);
        if (tag == SelectTag) return m.ReadU8(SaveNameAddr + 11);   // (the first select's saves)
        if (tag != CoopCharacters.TagV2) return -1;
        return (CoopCharacter)(m.ReadU8(SaveNameAddr + 11) & 0xF) switch
        {
            CoopCharacter.MariaPsp => 1,
            CoopCharacter.MariaSaturn => 2,
            CoopCharacter.RichterCoat => 3,
            _ => 0,
        };
    }

    // Names that pick a character themselves skip the select, as STAFF does on the Saturn.
    static bool SkipsSelect(IMemory m) =>
        IsMariaName(m) || IsName(m, "richter") || IsName(m, "axearmor");

    void StartSelect(IMemory m)
    {
        // (online the co-op mod has picked already, and written it here: keep it)
        _coopNewGame = CoopPicksCharacters();
        if (_coopNewGame) { _satNewGame = false; return; }
        m.WriteU8(SaveNameAddr + 10, 0);
        m.WriteU8(SaveNameAddr + 11, 0);
        if (!CharacterSelect || _selArt == null || SkipsSelect(m) || CoopPicksCharacters()) return;
        _satNewGame = false;
        _selPhase = 1;
        _selChoice = 0;
        _selTimer = 0;
    }

    // The name entry's fade-out: held while choosing.
    [PreHook("sel", "func_801ACEC0")]
    public static bool BeforeNameFade(CpuContext c, IMemory m)
    {
        if (I == null || I._selPhase is not (1 or 3 or 4)) return true;
        c.V0 = 0;
        return false;
    }

    // Runs every frame of name entry and its fade-out (after its own drawing).
    [PostHook("sel", "func_801AD78C")]
    public static void AfterNameScreen(CpuContext c, IMemory m)
    {
        var self = I;
        if (self == null || self._selPhase == 0) return;
        if (m.ReadU32(EngineStepAddr) != UpdEng18)
        {
            self._selPhase = 0;
            return;
        }
        if (self._selPhase == 1) self.UpdateSelect(c, m);
        else if (self._selPhase == 4) self.UpdateColours(c, m);
        if (self._selPhase != 3) self.DrawSelect(m);
    }

    void UpdateSelect(CpuContext c, IMemory m)
    {
        _selTimer++;
        if (_selTimer < 16) return; // fading in
        ushort pressed = m.ReadU16(PadsAddr), tapped = m.ReadU16(PadsAddr + 4);
        if ((tapped & PadLeft) != 0)
        {
            _selChoice = (_selChoice + 1) % 3;
            Sfx(c, m, SfxUiMove);
        }
        else if ((tapped & PadRight) != 0)
        {
            _selChoice = (_selChoice + 2) % 3;
            Sfx(c, m, SfxUiMove);
        }
        else if ((tapped & PadTriangle) != 0)
        {
            // back to name entry (the fade is still held this frame)
            _selPhase = 3;
            m.WriteU32(EngineStepAddr, UpdEng17);
            Sfx(c, m, SfxUiError);
        }
        else if ((tapped & (PadCross | PadCircle | PadStart)) != 0)
        {
            if (_selChoice == 2 && !MariaAvailable)
            {
                Sfx(c, m, SfxUiError);
                return;
            }
            bool up = (pressed & PadUp) != 0;
            _selCharacter = _selChoice switch
            {
                1 => up && _r2Files.Length > 0 ? CoopCharacter.RichterCoat : CoopCharacter.Richter,
                2 => _satFiles.Length > 0 && !(up && _files.Length > 0) ? CoopCharacter.MariaSaturn : CoopCharacter.MariaPsp,
                _ => up ? CoopCharacter.AlucardPsp : CoopCharacter.Alucard,
            };
            _selPalette = 0;
            if (ColourSets) { _selPhase = 4; Sfx(c, m, SfxUiConfirm); }
            else Choose(c, m);
        }
    }

    // The choice is made: set the game up for it, and fade out.
    void Choose(CpuContext c, IMemory m)
    {
        bool alucard = CoopCharacters.IsAlucard(_selCharacter);
        m.WriteU32(PlayableCharacterAddr, CoopCharacters.Playable(_selCharacter));
        m.WriteU32(StageIdAddr, alucard ? StageSt0 : StageNo3);
        CoopCharacters.ToSave(m, _selCharacter, _selPalette);
        _satNewGame = _selCharacter == CoopCharacter.MariaSaturn;
        _selPhase = 2;
        _selTimer = 0;
        Sfx(c, m, SfxUiConfirm);
    }

    // Online co-op picks the character in its lobby.
    static bool CoopPicksCharacters() => OnlineCoop.PicksNow;

    // The colour window: left/right pick one of three, confirm, or back to the portraits.
    void UpdateColours(CpuContext c, IMemory m)
    {
        _selTimer++;
        ushort tapped = m.ReadU16(PadsAddr + 4);
        if ((tapped & PadLeft) != 0) { _selPalette = (_selPalette + CoopCharacters.Palettes - 1) % CoopCharacters.Palettes; Sfx(c, m, SfxUiMove); }
        else if ((tapped & PadRight) != 0) { _selPalette = (_selPalette + 1) % CoopCharacters.Palettes; Sfx(c, m, SfxUiMove); }
        else if ((tapped & PadTriangle) != 0) { _selPhase = 1; Sfx(c, m, SfxUiError); }
        else if ((tapped & (PadCross | PadCircle | PadStart)) != 0) Choose(c, m);
    }

    const uint FnPlaySfx = 0x801347F8u; // dra PlaySfx

    static void Sfx(CpuContext c, IMemory m, uint id)
    {
        var regs = c.Snapshot();
        c.A0 = id;
        Dispatcher.Call(c, m, FnPlaySfx);
        c.Restore(regs);
    }

    void DrawSelect(IMemory m)
    {
        var a = _selArt!;
        SaturnSelect.BeginDraw(m);   // (host prims get the buffer's drawing offset added already)
        // fade in on choosing, out once chosen
        int t = _selPhase == 1 ? Math.Min(_selTimer, 16) : _selPhase == 4 ? 16 : Math.Max(16 - _selTimer++, 0);
        a.DrawScreen(_selChoice, _selTimer, (byte)(t * 8), !MariaAvailable);
        if (_selPhase == 4) DrawColourWindow(a);
    }

    // ---------------------------------------------------------------- the colour window

    // Box like the game's menu windows: a light frame around a dark blue gradient.
    const float WinX = 48, WinY = 58, WinW = 288, WinH = 132;
    volatile string _winTitle = "", _winSet = "";

    void DrawColourWindow(SaturnSelect a)
    {
        Rect(WinX - 2, WinY - 2, WinW + 4, WinH + 4, 0xB0);
        Rect(WinX - 1, WinY - 1, WinW + 2, WinH + 2, 0x30);
        GpuPrims.Quad(SelOrder,
            new PrimVertex(WinX, WinY, 0x10, 0x10, 0x68), new PrimVertex(WinX + WinW, WinY, 0x10, 0x10, 0x68),
            new PrimVertex(WinX, WinY + WinH, 0x00, 0x00, 0x20), new PrimVertex(WinX + WinW, WinY + WinH, 0x00, 0x00, 0x20), raw: false);
        int k = _selChoice;
        var (w, h, _, _) = a.Portraits[k];
        float pw = w * 0.85f, ph = h * 0.85f, py = WinY + 24;
        for (int set = 0; set < CoopCharacters.Palettes; set++)
        {
            float cx = WinX + WinW * (set + 0.5f) / CoopCharacters.Palettes;
            if (set == _selPalette)
            {
                // the chosen one: a gold frame, pulsing
                byte g = (byte)(0xA0 + 0x50 * Math.Abs(Math.Sin(_selTimer / 10.0)));
                GpuPrims.Quad(SelOrder, new PrimVertex(cx - pw / 2 - 3, py - 3, g, (byte)(g * 3 / 4), 0x20), new PrimVertex(cx + pw / 2 + 3, py - 3, g, (byte)(g * 3 / 4), 0x20),
                    new PrimVertex(cx - pw / 2 - 3, py + ph + 3, g, (byte)(g * 3 / 4), 0x20), new PrimVertex(cx + pw / 2 + 3, py + ph + 3, g, (byte)(g * 3 / 4), 0x20), raw: false);
            }
            int tex = RecolouredPortrait(a, k, _selCharacter, set);
            if (tex >= 0) Image(tex, cx - pw / 2, py, pw, ph, (byte)(set == _selPalette ? 0x80 : 0x60));
        }
        _winTitle = $"{CoopCharacters.Name(_selCharacter)}: colours";
        _winSet = CoopPalettes.SetNames[_selPalette];
    }

    readonly Dictionary<(int, CoopCharacter, int), int> _recoloured = new();

    int RecolouredPortrait(SaturnSelect a, int k, CoopCharacter ch, int set)
    {
        if (set == 0) return a.PortraitTexture(k);
        if (_recoloured.TryGetValue((k, ch, set), out int t)) return t;
        var (w, h, px, pal) = a.Portraits[k];
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
            if (px[i] != 0) SaturnSelect.Rgba(rgba, i, CoopPalettes.Recolor(ch, set, pal[px[i]]));
        t = GpuPrims.RegisterImage(rgba, w, h);
        if (t >= 0) _recoloured[(k, ch, set)] = t;
        return t;
    }

    // The window's words (the overlay draws text; the box itself is the game's prims).
    void DrawSelectText(System.Numerics.Vector2 min, System.Numerics.Vector2 size)
    {
        if (_selPhase != 4) return;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        float fs = Math.Clamp(size.Y / 26f, 12f, 48f);
        void Text(string s, float x, float y, uint col)
        {
            var ts = font.CalcTextSizeA(fs, float.MaxValue, 0, s);
            var p = new System.Numerics.Vector2(min.X + x / 384f * size.X - ts.X / 2, min.Y + y / 240f * size.Y);
            dl.AddText(font, fs, p + new System.Numerics.Vector2(2, 2), 0xC0000000, s);
            dl.AddText(font, fs, p, col, s);
        }
        Text(_winTitle, WinX + WinW / 2, WinY + 6, 0xFF60D8FF);
        Text(_winSet, WinX + WinW / 2, WinY + WinH - 22, 0xFFFFFFFF);
    }

    static void Rect(float x, float y, float w, float h, byte grey) => SaturnSelect.Rect(x, y, w, h, grey);
    static void Image(int tex, float x, float y, float w, float h, byte level) => SaturnSelect.Image(tex, x, y, w, h, level);
}

