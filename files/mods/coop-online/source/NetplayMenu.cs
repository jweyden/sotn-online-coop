using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Netplay on the title screen's main menu, in the game's own look: the button tips at
// the bottom get "△ Netplay" under "↕ Select" and "✕ Decide", and Triangle opens a
// Netplay screen drawn with the menu's own fonts (the big naming-screen letters for
// the title, the small 8x8 font the tips use for the lines). Your name first (typed
// on the game's own name-entry screen), then host a game or find one and join it.
// Paired, the host goes back to the menu to start a new game or load a save, and the
// guest's game follows (TitleFlow.cs).
//
// The menu is the SEL stage's Update, a switch on g_GameEngineStep. The Netplay screen
// is a step of our own (the switch ignores steps it doesn't know); everything is drawn
// right after the game's own drawing for the frame.
public partial class OnlineCoop
{
    // SEL (title screen) functions and data
    const uint FnDrawString16 = 0x801B29C0u;   // DrawString16x16(str, x, y, tge): the naming-screen letters
    const uint FnDrawImages8 = 0x801B2AFCu;    // DrawImages8x8(imgs, x, y, tge): the tips' font (char - 0x20, 0xFF ends)
    const uint FnMenuHideAllGfx = 0x801ACC3Cu;
    const uint FnSetGfxDrawMode = 0x801ACBE4u; // (gfxId, drawMode)
    const uint FnNavigationTips = 0x801AD260u; // DrawNavigationTips(mode)
    const uint FnMainMenuTips = 0x801AD490u;   // the main menu's tips and button highlight
    const uint FnShowMainMenu = 0x801AE9A8u;   // back from a sub-screen to the main menu
    const uint FnNameEntryInit = 0x801AEA8Cu;  // the name-entry screen's set-up
    const uint MenuGfxTable = 0x801BAF18u;     // D_801BAF18[gfx][0]: each graphic's prim index
    const uint TipsLabels = 0x801803A8u;       // STR_SELECT, STR_DECIDE, ...
    const uint InputNameAddr = 0x801D6B14u, InputCursorAddr = 0x801D6B08u;   // g_InputSaveName, g_InputCursorPos
    const uint EngMainMenuFadeIn = 1, EngMainMenuIdle = 2, EngNameEntry = 0x11, EngNetplay = 0x3E0;
    const uint TipsMenuNavigation = 4, GfxTips = 6, GfxNewGameHeader = 1;
    const ushort PadTri = 0x10, PadX = 0x40, PadSel = 0x100, PadSta = 0x800, PadU = 0x1000, PadD = 0x4000, PadL = 0x8000, PadR = 0x2000;
    const uint SfxConfirm = 0x633, SfxMove = 0x67D, SfxError = 0x686;

    bool _npNaming;
    int _npCursor;

    [Replace("sel", "SEL_Update")]
    public static void SelUpdateHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self) { orig(c, m); return; }
        if (self._bot && IsMenuStep(m.ReadU32(GameEngineStepAddr))) self.BotPads(m);
        self.PumpTitleMessages(m);
        if (self.GuestPaired) self.GuestFollowsHost(c, m);
        uint step = m.ReadU32(GameEngineStepAddr);
        if (self._botWantsStart)
        {
            self._botWantsStart = false;
            if (step == EngNetplay && self._net.IsHost && self._net.PlayerCount >= 2) { self.StartNewGameFromTitle(c, m); step = EngNameEntry; }
        }
        if (self._pressStartOnName && step == EngNameEntry)
        {
            self._pressStartOnName = false;
            m.WriteU16(PadsAddr + 4, (ushort)(m.ReadU16(PadsAddr + 4) | PadSta));
        }
        // a paired guest waits for the host on the Netplay screen (the host picks the game)
        if (self.GuestPaired && step == EngMainMenuIdle) { self.OpenNetplay(c, m); step = EngNetplay; }
        ushort tapped = m.ReadU16(PadsAddr + 4);

        if (step == EngMainMenuIdle && (tapped & PadTri) != 0)
        {
            m.WriteU16(PadsAddr + 4, (ushort)(tapped & ~PadTri));
            self.OpenNetplay(c, m);
        }
        else if (self._npNaming && step == EngNameEntry)
            self.NamingInput(c, m, tapped);

        orig(c, m);

        step = m.ReadU32(GameEngineStepAddr);
        if (step == EngMainMenuIdle || step == EngMainMenuFadeIn) self.DrawNetplayTip(c, m, 0xD8, "Netplay");
        else if (step == EngNetplay) self.NetplayFrame(c, m);
        else if (self._npNaming && step == EngNameEntry) DrawSmall(c, m, "Your name", 0x18, 0x1C);
        // (read again: the screen may have just switched to naming)
        if (m.ReadU32(GameEngineStepAddr) != EngNameEntry) self._npNaming = false;
    }

    // ---------------------------------------------------------------- the screen

    void OpenNetplay(CpuContext c, IMemory m)
    {
        Call(c, m, FnPlaySfx, SfxConfirm);
        Call(c, m, FnMenuHideAllGfx);
        m.WriteU32(GameEngineStepAddr, EngNetplay);
        _npCursor = 0;
        if (_net.Status == NetSession.State.Off && !_net.LobbyOpen) BrowseLobby();   // the list straight away
        if (!_npNamed && _net.Status == NetSession.State.Off) StartNaming(c, m);     // who are you? (once)
    }
    bool _npNamed;

    void CloseNetplay(CpuContext c, IMemory m)
    {
        Call(c, m, FnPlaySfx, SfxConfirm);
        Call(c, m, FnShowMainMenu);
        Call(c, m, FnMainMenuTips);
        m.WriteU32(GameEngineStepAddr, EngMainMenuIdle);
    }

    // The pages of the screen. Off line: the main page (your character, host, join) and
    // the list of games to join; hosting: who's in, and Start; joined: waiting for the
    // host. The character select is a page of its own, full screen (Saturn art).
    enum NpPage { Main, Join, Select }
    NpPage _npPage;
    const string Rule = "";   // a blank line between groups
    const uint EngFileSelect = 0x30, MainMenuCursorAddr = 0x801D6B0Cu;

    // What the page offers right now: (label, action); no action = just a line
    List<(string Text, Action<CpuContext, IMemory>? Do)> NetplayItems()
    {
        var items = new List<(string, Action<CpuContext, IMemory>?)>();
        switch (_net.Status)
        {
            case NetSession.State.Connected when !_net.IsHost:
                items.Add(($"Waiting for {Clip(_net.Link(0)?.Name ?? "the host", 14)} to start", null));
                AddPlayers(items);
                items.Add((Rule, null));
                items.Add(("Leave", (c, m) => { _net.Close(); DropAllFriends(); _npPage = NpPage.Main; }));
                break;
            case NetSession.State.Hosting:
            case NetSession.State.Connected:
                items.Add(_net.PlayerCount >= 2 ? ("Start", (c, m) => StartNewGameFromTitle(c, m)) : ("Waiting for players...", null));
                AddPlayers(items);
                items.Add((Rule, null));
                items.Add(("Cancel", (c, m) => { _net.Close(); DropAllFriends(); _npPage = NpPage.Main; }));
                break;
            case NetSession.State.Joining:
                items.Add(("Joining...", null));
                items.Add((Rule, null));
                items.Add(("Cancel", (c, m) => { _net.Close(); _npPage = NpPage.Join; BrowseLobby(); }));
                break;
            case NetSession.State.Off when _npPage == NpPage.Join:
                foreach (var g in _net.LobbyGames)
                {
                    var game = g;
                    items.Add(($"{Clip(g.Host, 14)}  {Clip(g.Info, 14)}", (c, m) => JoinLobbyGame(game)));
                }
                if (_net.LobbyGames.Count == 0) items.Add(("No games yet", null));
                items.Add((Rule, null));
                items.Add(("Refresh", (c, m) => BrowseLobby()));
                items.Add(("Back", (c, m) => _npPage = NpPage.Main));
                break;
            default:
                items.Add(("Select Character", (c, m) => OpenSelect()));
                items.Add((Rule, null));
                items.Add(("Host", (c, m) => { GameName = PlayerName; HostLobby(); }));
                items.Add((Rule, null));
                items.Add(("Join", (c, m) => { _npPage = NpPage.Join; BrowseLobby(); }));
                break;
        }
        return items;
    }

    // who's in (and as whom)
    void AddPlayers(List<(string Text, Action<CpuContext, IMemory>? Do)> items)
    {
        items.Add(($"  {Clip(PlayerName, 10)}: {CoopCharacters.Name(MyPick().Item1)}", null));
        foreach (var l in _net.Peers.OrderBy(l => l.Number))
            items.Add(($"  {Clip(l.Name, 10)}: {CoopCharacters.Name((CoopCharacter)l.Character)}", null));
    }

    string NetplayStatus() => _net.Status switch
    {
        NetSession.State.Connected => $"{_net.PlayerCount} of {_net.MaxPlayers} players" + (_net.ViaRelay ? " (relay)" : $"  {_net.RttMs} ms"),
        NetSession.State.Hosting => $"Hosting: up to {_net.MaxPlayers} players",
        NetSession.State.Joining => "Joining...",
        _ => LobbyServer.Trim().Length == 0 ? "No lobby server set (F1 > Online Co-op)"
           : _npPage != NpPage.Join ? ""
           : _net.LobbyGames.Count == 0 ? (_net.LobbyStatus.Length > 0 ? Clip(_net.LobbyStatus, 34) : "No games yet: host one!")
           : $"{_net.LobbyGames.Count} game{(_net.LobbyGames.Count == 1 ? "" : "s")} found",
    };

    // ---------------------------------------------------------------- starting the game

    // The host's Start, and the guest following it: the game's own new-game path (the
    // name-entry screen with our name, then Start). It's always a new game; the
    // prologue and the entrance's run are skipped (NewGame.cs, Intro.cs), and everyone
    // comes in together.
    bool _pressStartOnName;

    void StartNewGameFromTitle(CpuContext c, IMemory m)
    {
        Call(c, m, FnMenuHideAllGfx);
        Call(c, m, FnNameEntryInit, 0);
        string name = NameEntryText(PlayerName);
        for (int i = 0; i < 8; i++) m.WriteU8(InputNameAddr + (uint)i, (byte)name[i]);
        m.WriteU32(GameEngineStepAddr, EngNameEntry);
        _pressStartOnName = true;
        _startingTogether = true;
        HostStartsNewGame();   // (the guests' games start one too)
    }

    // ---------------------------------------------------------------- the character select

    // The Saturn select (left/right, hold up for the alternate: PSP Alucard's voice,
    // the long coat, PSP Maria), or without its art (from the Saturn disc) a plain cycle.
    // No colour sets yet.
    SaturnSelect? _selArt;
    bool _selArtTried;
    int _selChoice, _selTimer;

    SaturnSelect? SelectArt()
    {
        if (!_selArtTried) { _selArtTried = true; _selArt = SaturnSelect.Load(out _); }
        return _selArt;
    }

    void OpenSelect()
    {
        if (SelectArt() == null) { SetPick(NextCharacter(MyPick().Item1), 0); return; }
        _npPage = NpPage.Select;
        _selChoice = SaturnSelect.PortraitOf(MyPick().Item1);
        _selTimer = 0;
    }

    // Characters every player here can run (their own PSP / Saturn builds).
    uint EveryoneCanPlay()
    {
        uint everyone = AvailableCharacters();
        foreach (var l in _net.Peers) everyone &= l.Available;
        return everyone;
    }

    CoopCharacter NextCharacter(CoopCharacter ch)
    {
        uint everyone = EveryoneCanPlay();
        for (int k = 1; k <= CoopCharacters.Count; k++)
        {
            var next = (CoopCharacter)(((int)ch + k) % CoopCharacters.Count);
            if ((everyone & (1u << (int)next)) != 0) return next;
        }
        return CoopCharacter.Alucard;
    }

    void SelectFrame(CpuContext c, IMemory m)
    {
        var art = SelectArt()!;
        _selTimer++;
        ushort pressed = m.ReadU16(PadsAddr), tapped = m.ReadU16(PadsAddr + 4);
        uint can = EveryoneCanPlay();
        bool Can(CoopCharacter ch) => (can & (1u << (int)ch)) != 0;
        if (_selTimer >= 16)
        {
            if ((tapped & PadL) != 0) { _selChoice = (_selChoice + 1) % 3; Call(c, m, FnPlaySfx, SfxMove); }
            else if ((tapped & PadR) != 0) { _selChoice = (_selChoice + 2) % 3; Call(c, m, FnPlaySfx, SfxMove); }
            else if ((tapped & PadTri) != 0) { _npPage = NpPage.Main; Call(c, m, FnPlaySfx, SfxError); return; }
            else if ((tapped & (PadX | PadSta)) != 0)
            {
                bool up = (pressed & PadU) != 0;
                CoopCharacter ch = _selChoice switch
                {
                    1 => up && Can(CoopCharacter.RichterCoat) ? CoopCharacter.RichterCoat : CoopCharacter.Richter,
                    2 => up || !Can(CoopCharacter.MariaSaturn) ? CoopCharacter.MariaPsp : CoopCharacter.MariaSaturn,
                    _ => up ? CoopCharacter.AlucardPsp : CoopCharacter.Alucard,
                };
                if (!Can(ch)) Call(c, m, FnPlaySfx, SfxError);
                else { SetPick(ch, 0); _npPage = NpPage.Main; Call(c, m, FnPlaySfx, SfxConfirm); return; }
            }
        }
        HideTips(m);   // (the Netplay page's button icons: its tips draw shows them again)
        SaturnSelect.BeginDraw(m);
        // (over the menu's own background)
        art.DrawScreen(_selChoice, _selTimer, (byte)(Math.Min(_selTimer, 16) * 8),
            !Can(CoopCharacter.MariaPsp) && !Can(CoopCharacter.MariaSaturn), backdrop: false);
    }

    void SetPick(CoopCharacter ch, int pal)
    {
        _myPick = (ch, pal);
        // (test bots on one machine share the file: they keep theirs to themselves)
        if (!_bot) try { File.WriteAllText(CharacterFile, $"{(int)ch} {pal}"); } catch { }
        _net.LocalCharacter = (byte)ch; _net.LocalPalette = (byte)pal;
        _net.LocalPickChanged();
    }

    void NetplayFrame(CpuContext c, IMemory m)
    {
        if (_npPage == NpPage.Select)
        {
            if (_net.Status == NetSession.State.Off) { SelectFrame(c, m); return; }
            _npPage = NpPage.Main;   // (hosting / joined: who you play is set)
        }
        var items = NetplayItems();
        // the cursor only stops on lines that do something
        int Step(int from, int dir)
        {
            for (int k = 1; k <= items.Count; k++)
            {
                int i = ((from + dir * k) % items.Count + items.Count) % items.Count;
                if (items[i].Do != null) return i;
            }
            return from;
        }
        if (_npCursor >= items.Count || _npCursor < 0 || items[_npCursor].Do == null) _npCursor = Step(-1, 1);

        ushort tapped = m.ReadU16(PadsAddr + 4);
        if ((tapped & PadU) != 0) { int n = Step(_npCursor, -1); if (n != _npCursor) { _npCursor = n; Call(c, m, FnPlaySfx, SfxMove); } }
        if ((tapped & PadD) != 0) { int n = Step(_npCursor, 1); if (n != _npCursor) { _npCursor = n; Call(c, m, FnPlaySfx, SfxMove); } }
        if ((tapped & PadX) != 0 && _npCursor >= 0 && _npCursor < items.Count && items[_npCursor].Do is { } act)
        {
            Call(c, m, FnPlaySfx, SfxConfirm);
            var page = _npPage;
            var status = _net.Status;
            act(c, m);
            if (m.ReadU32(GameEngineStepAddr) != EngNetplay || _npPage == NpPage.Select) return;   // (it left the screen)
            if (_npPage != page || _net.Status != status) _npCursor = -1;
            items = NetplayItems();
            if (_npCursor < 0 || _npCursor >= items.Count || items[_npCursor].Do == null) _npCursor = Step(-1, 1);
        }
        if ((tapped & PadTri) != 0 && _net.Status == NetSession.State.Off)
        {
            if (_npPage == NpPage.Join) { _npPage = NpPage.Main; _npCursor = -1; Call(c, m, FnPlaySfx, SfxError); }
            else { CloseNetplay(c, m); return; }
        }

        DrawBig(c, m, _npPage == NpPage.Join && _net.Status == NetSession.State.Off ? "join" : "netplay", 0x18, 0x18);
        if (_npNamed) DrawSmall(c, m, PlayerName, 0x1C, 0x2C);
        DrawPickPortrait(m);
        int y = 0x48, first = Math.Max(0, Math.Min(_npCursor - 4, items.Count - 7));
        for (int i = first; i < items.Count && i < first + 7; i++, y += 0x10)
        {
            DrawSmall(c, m, items[i].Text, 0x38, y);
            if (i == _npCursor && (_vsyncs & 16) == 0) DrawSmall(c, m, ">", 0x28, y);
        }
        DrawSmall(c, m, NetplayStatus(), 0x28, 0xB0);

        Call(c, m, FnNavigationTips, TipsMenuNavigation);
        DrawImages(c, m, m.ReadU32(TipsLabels + 0), 0x34, 0xBC);   // Select
        DrawImages(c, m, m.ReadU32(TipsLabels + 4), 0x34, 0xCC);   // Decide
        DrawNetplayTip(c, m, 0xD8, "Back");
    }

    // Your character in its frame (as on the select screen), in the top right.
    void DrawPickPortrait(IMemory m)
    {
        var art = SelectArt();
        if (art == null) return;
        int k = SaturnSelect.PortraitOf(MyPick().Item1);
        const float scale = 0.55f;
        var (w, _) = art.FramedSize(k, scale);
        SaturnSelect.BeginDraw(m);
        art.DrawFramed(k, 384 - 32 - w, 12, scale, 0x80);
    }

    // The Triangle button icon (the third of the tips' icons) with a label beside it.
    void DrawNetplayTip(CpuContext c, IMemory m, int y, string label)
    {
        uint prim = TipsPrim(m, 2);
        if (prim == 0) return;
        SetPrimRect(m, prim, 0x20, y, 0x10, 0x10);
        m.WriteU16(prim + 0x32, 0);   // drawMode: shown
        DrawSmall(c, m, label, 0x34, y + 4);
    }

    static uint TipsPrim(IMemory m, int n)
    {
        int idx = (int)m.ReadU32(MenuGfxTable + GfxTips * 8);
        if (idx <= 0 || idx >= MaxPrims) return 0;
        uint p = PrimBufAddr + (uint)idx * 0x34;
        for (int k = 0; k < n && p != 0; k++) p = m.ReadU32(p);
        return p;
    }

    static void HideTips(IMemory m)
    {
        for (uint p = TipsPrim(m, 0), k = 0; p != 0 && k < 8; p = m.ReadU32(p), k++)
            m.WriteU16(p + 0x32, 8);   // DRAW_HIDE
    }

    static void SetPrimRect(IMemory m, uint p, int x, int y, int w, int h)
    {
        void Pt(uint off, int px, int py) { m.WriteU16(p + off, (ushort)px); m.WriteU16(p + off + 2, (ushort)py); }
        Pt(0x08, x, y); Pt(0x14, x + w, y); Pt(0x20, x, y + h); Pt(0x2C, x + w, y + h);
    }

    // ---------------------------------------------------------------- your name

    void StartNaming(CpuContext c, IMemory m)
    {
        // the game's own name-entry screen, empty
        Call(c, m, FnNameEntryInit, 0);
        Call(c, m, FnSetGfxDrawMode, GfxNewGameHeader, 8);   // (no "New Game" header)
        for (int i = 0; i < 8; i++) m.WriteU8(InputNameAddr + (uint)i, (byte)' ');
        m.WriteU8(InputNameAddr + 8, 0);
        m.WriteU32(InputCursorAddr, 0);
        m.WriteU32(GameEngineStepAddr, EngNameEntry);
        _npNaming = true;
    }

    // Before the name-entry step runs: Start means "that's my name", Select (or
    // Triangle on an empty name) means "never mind". Both would otherwise start a new
    // game or open the file screen, so take the press away from the game.
    void NamingInput(CpuContext c, IMemory m, ushort tapped)
    {
        bool empty = true;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i++) { byte b = m.ReadU8(InputNameAddr + (uint)i); sb.Append((char)b); if (b != ' ') empty = false; }
        bool cancel = (tapped & PadSel) != 0 || ((tapped & PadTri) != 0 && empty && m.ReadU32(InputCursorAddr) == 0);
        if ((tapped & PadSta) == 0 && !cancel) return;
        m.WriteU16(PadsAddr + 4, (ushort)(tapped & ~(PadSta | PadSel | PadTri)));
        if (!cancel && empty) { Call(c, m, FnPlaySfx, SfxError); m.WriteU16(PadsAddr + 4, (ushort)(tapped & ~PadSta)); return; }   // a name, please
        _npNaming = false;
        Call(c, m, FnMenuHideAllGfx);
        m.WriteU32(GameEngineStepAddr, EngNetplay);
        if (cancel)
        {
            if (!_npNamed) CloseNetplay(c, m);   // (no name yet: no Netplay)
            return;
        }
        Call(c, m, FnPlaySfx, SfxConfirm);
        PlayerName = sb.ToString().Trim();
        _npNamed = true;
        _npCursor = 0;
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + ".";

    // ---------------------------------------------------------------- the game's fonts

    // The strings go on the game's stack, below its stack pointer, for the call.
    static void DrawSmall(CpuContext c, IMemory m, string text, int x, int y)
    {
        var bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++) bytes[i] = (byte)Math.Clamp(text[i] - 0x20, 0, 0x5E);
        bytes[^1] = 0xFF;
        CallWithBytes(c, m, bytes, ptr => Call(c, m, FnDrawImages8, ptr, (uint)x, (uint)y, 1));
    }

    static void DrawImages(CpuContext c, IMemory m, uint imgs, int x, int y)
    {
        if (imgs >= 0x80000000u && imgs < 0x80200000u) Call(c, m, FnDrawImages8, imgs, (uint)x, (uint)y, 1);
    }

    static void DrawBig(CpuContext c, IMemory m, string text, int x, int y)
    {
        var bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
        CallWithBytes(c, m, bytes, ptr => Call(c, m, FnDrawString16, ptr, (uint)x, (uint)y, 1));
    }

    static void CallWithBytes(CpuContext c, IMemory m, byte[] bytes, Action<uint> call)
    {
        uint sp = c.SP;
        uint buf = (sp - 0x100u - (uint)bytes.Length) & ~7u;
        for (int i = 0; i < bytes.Length; i++) m.WriteU8(buf + (uint)i, bytes[i]);
        c.SP = buf - 0x20;
        try { call(buf); }
        finally { c.SP = sp; }
    }
}
