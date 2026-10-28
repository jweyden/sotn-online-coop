using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;
using ImGuiNET;

// Online co-op: up to six independent games. Each one simulates every other player
// as one of its remote players (all of CoopCore's machinery; Friends.cs), driven by
// that friend's streamed pad input and pulled toward their real state from their
// snapshots. A friend only exists in your game while you're both in the same stage
// and room.
//
// Phase 1 (presence): connect, see each other, move/attack/cast together.
public partial class OnlineCoop : CoopCore
{
    readonly NetSession _net = new();

    public string HostPort = "7777";
    public string JoinAddress = "127.0.0.1:7777";
    public string PlayerName = "Player";   // set by the Netplay menu's name entry
    public string RelayServer = "";      // optional join-code relay, host:port (server/coop_server.py)
    public string JoinCode = "";
    // The lobby (server/coop_server.py): COOP_LOBBY, or the first line of mods/coop-online/lobby.txt
    public string LobbyServer = "";
    public string GameName = "";
    bool _autoJoinFirst;
    readonly System.Collections.Concurrent.ConcurrentQueue<Action> _uiActions = new();   // settings-panel clicks, run on the game thread
    long _lastLobbyRefresh;
    // P2 rollback: run the friend on predicted input (their last buttons) with no
    // delay, a little ahead of what we've received; when their real input arrives
    // and differs, rewind P2 and replay it. InputDelay is only used with rollback off.
    public bool Rollback = true;
    public int MaxRollback = 8;          // frames we can rewind
    public int InputDelay = 3;
    public int SnapDistance = 48;        // px: past this the remote P2 snaps to the friend's real spot

    public int Rollbacks, RollbackFrames, RollbacksRefused;
    readonly byte[] _savedPad0 = new byte[8];
    long _vsyncs;
    NetSession.State _lastStatus = NetSession.State.Off;

    const uint StageIdAddr = 0x800974A0u, TilemapLeft = 0x800730B0u, TilemapTop = 0x800730B4u;
    const uint EStepOff = 0x2C, EStepSOff = 0x2E, EFacing = 0x14;

    struct Snap
    {
        public byte Character, Palette, Flags; // Flags: 1 in game, 2 in menu/map/pause, 4 dead, 8 PvP on (host), 16 beaming in, 32 room locked, 64 room keeps running in their menu, 128 waiting to start a new game
        public ushort Stage; public short RoomLeft, RoomTop;
        public int WorldX, WorldY, VelX, VelY;
        public byte Facing; public ushort Step, StepS; public uint Status;
        public int[] Stats;                    // hp, hpMax, hearts, heartsMax, mp, mpMax
        public uint[] Equip;                   // equipment[7]
        public uint Subweapon, Servant;
        public ushort[] Cape;
        public byte[]? Familiar;               // the owner's familiar entity (FamiliarFields), world coords
        public short ScrollX, ScrollY; public byte Role; // their camera; Role 1 = they run our shared room
        public int RoomFrames;                 // how long they've been in their room
        public byte Flags2;                    // 1 in a story cutscene
        public bool InGame => (Flags & 1) != 0;
        public bool InMenu => (Flags & 2) != 0;
        public bool Beaming => (Flags & 16) != 0;
        public bool RoomLocked => (Flags & 32) != 0;
        public bool InScene => (Flags2 & 1) != 0;
        public bool Frozen => InMenu && (Flags & 64) == 0;   // in the menu, and their room stands still
    }

    // In a menu, the map or paused: the world (and their Alucard) stands still for them.
    // A cutscene (or the intro script) has our Alucard: it drives him (demo control).
    // Our buttons aren't his then, so they aren't sent (or their copy of us would
    // swing at them), and the friend can't hit us (PvP). In the castle entrance's
    // scripted scenes pausing is off too; elsewhere that flag means other things
    // (save rooms turn it off while you're in them), so it only counts there.
    const uint DemoTimerAddr = 0x80072EFCu, PauseAllowedFlagAddr = 0x8003C8B8u;   // g_Player.demo_timer, g_PauseAllowed
    static bool InCutscene(IMemory m) => Game.InGame && (m.ReadU32(DemoTimerAddr) != 0 ||
        ((ushort)m.ReadU32(StageIdAddr) == Stage_Entrance && m.ReadU8(PauseAllowedFlagAddr) == 0));

    bool Paused(IMemory m) => m.ReadU32(GameEngineStepAddr) is EngineMenu or EngineMap or EnginePause;
    const uint EngineMap = 20, EnginePause = 0x70, EngineFrozen = 5;   // (Engine_5: the spell / level-up freeze)

    // The friend is in their menu: show them as a still, see-through ghost.
    protected override bool P2Ghost => F.Valid && F.Peer.InMenu;

    // Whoever picked PSP Alucard has the PSP voice, in every game.
    protected override string PspVoiceLabel => "PSP Alucard uses PSP voices";
    protected override bool RemoteAlucardsUsePsp => false;   // (online, who picked PSP Alucard decides)

    protected override void OnLoaded()
    {
        _net.MaxPlayers = Math.Clamp(MaxPlayers, 2, HostCap);
        SharedCamera = false;       // each player's camera follows their own Alucard
        TeleportOnSpawn = false;    // the friend is simply there; the beam is for "teleport to friend"
        Event.AddListener<VSyncEvent>(OnNetFrame);
        Event.AddListener<Recompiled.PlayerLoadedEvent>(OnPlayerLoaded);
        Event.AddListener<KeyboardEvent>(OnKey);
        HookOverlay(true);
        StartDiagnostics();

        SetUpTestTools();
        _net.MaxPlayers = Math.Clamp(MaxPlayers, 2, HostCap);
        LobbyServer = Environment.GetEnvironmentVariable("COOP_LOBBY") ?? "";
        _net.ForceRelay = Environment.GetEnvironmentVariable("COOP_FORCE_RELAY") == "1";
        try { if (LobbyServer.Length == 0 && File.Exists(LobbyFile)) LobbyServer = File.ReadAllLines(LobbyFile)[0].Trim(); } catch { }

        // For testing two instances on one machine: COOP_ONLINE=host:7777 or join:127.0.0.1:7777
        // (or lobbyhost:server:port / lobbyjoin:server:port, which joins the first game listed)
        var auto = Environment.GetEnvironmentVariable("COOP_ONLINE");
        var name = Environment.GetEnvironmentVariable("COOP_NAME");
        if (!string.IsNullOrEmpty(name)) PlayerName = name;
        GameName = $"{PlayerName}'s game";
        if (!string.IsNullOrEmpty(auto))
        {
            var parts = auto.Split(':');
            if (parts[0] == "host" && parts.Length > 1) { HostPort = parts[1]; Host(); }
            if (parts[0] == "join" && parts.Length > 2) { JoinAddress = parts[1] + ":" + parts[2]; Join(); }
            if (parts[0] is "lobbyhost" or "lobbyjoin" && parts.Length > 2)
            {
                LobbyServer = parts[1] + ":" + parts[2];
                if (parts[0] == "lobbyhost") HostLobby(); else { _autoJoinFirst = true; BrowseLobby(); }
            }
            if (parts[0] is "hostrelay" or "joinrelay" && parts.Length > 3)
            {
                RelayServer = parts[1] + ":" + parts[2]; JoinCode = parts[3];
                if (parts[0] == "hostrelay") HostRelay(); else JoinRelay();
            }
        }
    }

    protected override void OnUnloading()
    {
        Event.RemoveListener<VSyncEvent>(OnNetFrame);
        Event.RemoveListener<Recompiled.PlayerLoadedEvent>(OnPlayerLoaded);
        Event.RemoveListener<KeyboardEvent>(OnKey);
        HookOverlay(false);
        StopDiagnostics();
        _net.Close();
        _disc?.Dispose();
    }

    protected override bool LeashP2 => false;

    // Hotkeys (keyboard): F6 teleport to the friend, F7 resync. GLFW/Silk key codes.
    const int KeyF6 = 295, KeyF7 = 296;
    void OnKey(KeyboardEvent e)
    {
        if (!e.Pressed || e.Repeat) return;
        if (e.Key == KeyF6) _teleportKey = true;
        if (e.Key == KeyF7) _resyncKey = true;
    }

    // A save was loaded (or a new game started): re-baseline and re-merge progress.
    void OnPlayerLoaded(Recompiled.PlayerLoadedEvent e) { _progressReady = false; _fullSyncSent = false; _newGameStarted = false; }

    // We changed room: the friends stay where they are (in the room we left), so take
    // their bodies away now; they come back if they really are in our new room.
    protected override void OnLocalRoomChange(int layer)
    {
        foreach (var f in _friends) { if (f.Body.Active) f.Body.WantDrop = true; f.AbsentFrames = 0; }
        if (_tp == Tp.Arriving) _tp = Tp.Placing;   // in the friend's room: the exact spot next
    }

    // Our name, character and colours for the session (Hello, the roster).
    void PrepareSession()
    {
        _net.LocalName = PlayerName;
        _net.MaxPlayers = Math.Clamp(MaxPlayers, 2, HostCap);
        var m = RecompOne.Runtime.Runtime.Mem;
        // in a game: who we are in it; otherwise the pick (not the last game's character)
        if (m != null && Game.InGame) { RefreshLocalCharacter(m); _net.LocalCharacter = (byte)_p1Char; _net.LocalPalette = (byte)_p1Palette; }
        else { var (ch, pal) = MyPick(); _net.LocalCharacter = (byte)ch; _net.LocalPalette = (byte)pal; }
        _net.LocalAvailable = AvailableCharacters();
    }

    // Which characters this machine can run (its own PSP / Saturn builds).
    uint AvailableCharacters()
    {
        uint mask = 0;
        for (int i = 0; i < CoopCharacters.Count; i++)
            if (CanPlay((CoopCharacter)i, out _)) mask |= 1u << i;
        return mask;
    }

    void Host()
    {
        try { PrepareSession(); _net.Host(int.Parse(HostPort)); Console.WriteLine($"[coop-online] hosting on port {HostPort}"); }
        catch (Exception e) { Console.WriteLine($"[coop-online] host failed: {e.Message}"); }
    }

    void Join()
    {
        try
        {
            int colon = JoinAddress.LastIndexOf(':');
            string host = colon > 0 ? JoinAddress[..colon] : JoinAddress;
            int port = colon > 0 ? int.Parse(JoinAddress[(colon + 1)..]) : 7777;
            PrepareSession();
            _net.Join(host, port);
            Console.WriteLine($"[coop-online] joining {host}:{port}");
        }
        catch (Exception e) { Console.WriteLine($"[coop-online] join failed: {e.Message}"); }
    }

    const string LobbyFile = "mods/coop-online/lobby.txt";

    void HostLobby()
    {
        if (LobbyServer.Trim().Length == 0) { Console.WriteLine("[coop-online] no lobby server set"); return; }
        try
        {
            PrepareSession();
            _net.HostLobby(LobbyServer.Trim(), GameName.Trim().Length > 0 ? GameName.Trim() : $"{PlayerName}'s game");
            Console.WriteLine($"[coop-online] hosting \"{GameName}\" in the lobby at {LobbyServer} (id {_net.LobbyGameId})");
        }
        catch (Exception e) { Console.WriteLine($"[coop-online] lobby host failed: {e.Message}"); }
    }

    void BrowseLobby()
    {
        if (LobbyServer.Trim().Length == 0) { Console.WriteLine("[coop-online] no lobby server set"); return; }
        try { PrepareSession(); _net.OpenLobby(LobbyServer.Trim()); _lastLobbyRefresh = _vsyncs; }
        catch (Exception e) { Console.WriteLine($"[coop-online] lobby failed: {e.Message}"); }
    }

    void JoinLobbyGame(NetSession.LobbyGame g)
    {
        PrepareSession();
        _net.JoinLobby(g.Id);
        Console.WriteLine($"[coop-online] joining \"{g.Game}\" ({g.Host}) through the lobby");
    }

    // Once a frame (game thread): settings clicks, the list refresh, our line in the list.
    void TickLobbyUi(IMemory m)
    {
        while (_uiActions.TryDequeue(out var a)) a();
        _net.LobbyInfo = Game.InGame ? $"Lv {m.ReadU32(LevelAddr)}" : "on the title screen";
        if (_net.LobbyOpen && _net.Status == NetSession.State.Off && _vsyncs - _lastLobbyRefresh > (_autoJoinFirst ? 60 : 180))
        {
            _lastLobbyRefresh = _vsyncs;
            _net.RefreshLobby();
        }
        if (_autoJoinFirst && _net.Status == NetSession.State.Off && _net.LobbyGames.Count > 0)
        {
            // COOP_JOIN_HOST=name: only that host's game (on a public lobby the first one
            // listed may be anybody's); otherwise the first
            var want = Environment.GetEnvironmentVariable("COOP_JOIN_HOST");
            var game = string.IsNullOrEmpty(want) ? _net.LobbyGames[0]
                : _net.LobbyGames.FirstOrDefault(g => g.Host.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (game == null) return;   // (not listed yet: the next refresh)
            _autoJoinFirst = false;
            JoinLobbyGame(game);
        }
    }

    bool RelayAddress(out string host, out int port)
    {
        int colon = RelayServer.LastIndexOf(':');
        host = colon > 0 ? RelayServer[..colon].Trim() : RelayServer.Trim();
        port = 7777;
        if (colon > 0 && !int.TryParse(RelayServer[(colon + 1)..], out port)) return false;
        return host.Length > 0;
    }

    void HostRelay()
    {
        if (!RelayAddress(out var host, out int port)) { Console.WriteLine("[coop-online] set the relay server (host:port) first"); return; }
        if (JoinCode.Trim().Length == 0) JoinCode = NetSession.NewCode();
        try { PrepareSession(); _net.HostRelay(host, port, JoinCode); Console.WriteLine($"[coop-online] hosting through {host}:{port}, code {_net.RelayCode}"); }
        catch (Exception e) { Console.WriteLine($"[coop-online] relay host failed: {e.Message}"); }
    }

    void JoinRelay()
    {
        if (!RelayAddress(out var host, out int port) || JoinCode.Trim().Length == 0) { Console.WriteLine("[coop-online] need the relay server and a code"); return; }
        try { PrepareSession(); _net.JoinRelay(host, port, JoinCode); Console.WriteLine($"[coop-online] joining code {_net.RelayCode} through {host}:{port}"); }
        catch (Exception e) { Console.WriteLine($"[coop-online] relay join failed: {e.Message}"); }
    }

    // The settings panel only shows the connection; hosting, joining and names are on
    // the title screen's Netplay menu, and everything else is fixed at its default.
    protected override bool ShowCoreSettings => false;
    protected override void DrawModeSettings()
    {
        ImGui.Text($"Online: {_net.Status}, {_net.PlayerCount} of {_net.MaxPlayers} players (you are player {_net.LocalNumber + 1}, {CoopCharacters.Name(_p1Char)})");
        foreach (var l in _net.Peers)
            ImGui.Text($"  player {l.Number + 1}: {l.Name}, {CoopCharacters.Name((CoopCharacter)l.Character)}, ping {l.RttMs} ms{(l.ViaRelay ? " (relay)" : l.ViaHost ? " (through the host)" : "")}");
        if (_net.Status == NetSession.State.Off) ImGui.SliderInt("Players (when you host)", ref MaxPlayers, 2, NetSession.PublicMaxPlayers);
        if (_net.LastError.Length > 0) ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), _net.LastError);
        if (_net.Status == NetSession.State.Off) ImGui.Text("Host or join from the title screen: Triangle (Netplay).");
        if (_net.Status == NetSession.State.Connected && _net.ViaRelay) ImGui.Text("(connected through the lobby server's relay: a bit more delay than direct)");
        if (_net.Status == NetSession.State.Connected)
        {
            if (ImGui.Button("Teleport to the nearest friend (F6)")) RequestTeleport();
            ImGui.SameLine();
            if (ImGui.Button("Resync (F7)")) RequestResync();
            if (TeleportStatus.Length > 0) ImGui.Text(TeleportStatus);
        }
        if (_net.Status != NetSession.State.Off && ImGui.Button("Disconnect")) { _net.Close(); DropAllFriends(); }
        if (_net.Reconnects > 0) ImGui.Text($"reconnected {_net.Reconnects}x");
        foreach (var f in ValidFriends)
            ImGui.Text(f.Peer.InGame ? $"{f.Name}: stage {f.Peer.Stage} room {f.Peer.RoomLeft},{f.Peer.RoomTop}{(f.Peer.InMenu ? " (menu)" : "")}" : $"{f.Name}: not in game");
        ImGui.Checkbox("Name tag over your friend", ref ShowNameTag);
        ImGui.Checkbox("Live menu (experimental): the room keeps moving while you're in the menu", ref LiveMenu);
        ImGui.Checkbox("Boss fights don't pause: in a locked room the fight goes on while you're in the menu", ref BossMenuLive);
        ImGui.Checkbox("Share progress (flags, map, relics, items, gold)", ref ShareProgress);
        ImGui.Checkbox("Room sync (fight the same enemies)", ref RoomSync);
        if (_net.IsHost || _net.Status != NetSession.State.Connected) ImGui.Checkbox("PvP: players can hurt each other", ref Pvp);
        else ImGui.Text($"PvP: {(PvpOn ? "on" : "off")} (the host decides)");
        ImGui.Text($"room: {RoomStatus}");
        ImGui.Checkbox("Rollback (friend's moves with no delay)", ref Rollback);
        if (Rollback) ImGui.SliderInt("Max rollback frames", ref MaxRollback, 2, 15);
        else ImGui.SliderInt("Input delay (frames)", ref InputDelay, 0, 10);
        ImGui.Text($"rollbacks {Rollbacks} ({RollbackFrames} frames replayed, {RollbacksRefused} refused)");
    }

    // ---------------------------------------------------------------- network frame

    // Once per frame, outside any swap (P1 is live): send our input + snapshot, read theirs.
    void OnNetFrame(VSyncEvent e)
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;
        _vsyncs++;
        // menu presses aren't moves; nor are presses while our world is frozen (a
        // level-up or a spell's freeze: our Alucard stands still, so their copy must too)
        ushort pad = (ushort)(Game.InGame && !Paused(m) && m.ReadU32(GameEngineStepAddr) != EngineFrozen && !InCutscene(m) && !RespawnLocked(m) ? m.ReadU16(PadsAddr) : 0);
        _net.Poll(pad, _vsyncs % 3 == 0 ? BuildSnapshot(m) : null);

        if (_net.Status != _lastStatus)
        {
            Console.WriteLine($"[coop-online] {_lastStatus} -> {_net.Status}{(_net.Status == NetSession.State.Connected ? $" ({_net.PeerName})" : "")}{(_net.LastError.Length > 0 ? $" [{_net.LastError}]" : "")}");
            _lastStatus = _net.Status;
        }
        // a new friend gets everything we have (and sends us theirs)
        int peers = _net.Peers.Count();
        if (peers > _lastPeerCount) { _fullSyncSent = false; _progressReady = false; }
        _lastPeerCount = peers;
        SyncFriends();
        foreach (var f in _friends)
        {
            var l = f.Link;
            f.Valid = l.Connected && l.Snapshot != null && l.FramesSinceHeard < 120;
            if (f.Valid) f.Peer = ReadSnapshot(l.Snapshot!);
        }
        TickLobbyUi(m);
        if (Game.InGame) { RefreshLocalCharacter(m); RememberPick(m); }
        TickProgress(m);
        TickOwnLevel(m);
        CheckPrims(m, "frame end (after the friend's update and drawing)");
        if (_resyncKey) { _resyncKey = false; if (_net.Status == NetSession.State.Connected) RequestResync(); }
        if (Game.InGame) TickRoom(m);
        TickTeleport(m);
        UpdateNameTag(m);
        TickSyncLog(m);

        // A friend left the room (or the game, or the session): take their body away.
        foreach (var f in _friends)
        {
            if (!f.Body.Active) continue;
            f.AbsentFrames = Present(m, f) ? 0 : f.AbsentFrames + 1;
            if (f.AbsentFrames > 20) { f.Body.WantDrop = true; f.AbsentFrames = 0; }
        }
    }
    int _lastPeerCount;

    public int MaxPlayers = 3;   // when hosting (2..3; the test bots can go to 6)
    int HostCap => _testPlayers ? NetSession.MaxPlayersLimit : NetSession.PublicMaxPlayers;

    void DropAllFriends() { foreach (var f in _friends) if (f.Body.Active) f.Body.WantDrop = true; }

    // Character mode counts everyone in the session, not just who's in this room.
    protected override bool CharacterMode =>
        base.CharacterMode || _net.Peers.Any(l => !CoopCharacters.IsAlucard((CoopCharacter)l.Character));

    // ---------------------------------------------------------------- our own character

    // A guest plays the host's save as their own character: the last one they picked
    // (on the select screen, or in a game of their own), kept in a file.
    const string CharacterFile = "mods/coop-online/character.txt";
    (CoopCharacter Character, int Palette)? _myPick;

    (CoopCharacter, int) MyPick()
    {
        if (_myPick is { } p) return p;
        try
        {
            var parts = File.ReadAllText(CharacterFile).Split(' ');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int c) && CoopCharacters.Valid(c) && int.TryParse(parts[1], out int pal))
                return (_myPick = ((CoopCharacter)c, Math.Clamp(pal, 0, CoopCharacters.Palettes - 1))).Value;
        }
        catch { }
        return (CoopCharacter.Alucard, 0);
    }

    // In game (P1 live): remember what this save's player is.
    void RememberPick(IMemory m)
    {
        if (_bot || !Game.InGame || m.ReadU8(CoopCharacters.SaveNameAddr + 10) != CoopCharacters.TagV2) return;
        var pick = (_p1Char, _p1Palette);
        if (_myPick == pick) return;
        _myPick = pick;
        try { File.WriteAllText(CharacterFile, $"{(int)pick.Item1} {pick.Item2}"); } catch { }
    }

    // A new game with no Saturn select screen (it needs the Saturn disc's art): the
    // Netplay menu's pick, set the way the select sets it, after the name entry.
    const string CharSelArtFile = "disc/charsel/CHARSEL.BIN";
    const uint StageSt0 = 0x1F, StageNo3 = 0x41;

    public override bool PicksCharacters => _net.Status != NetSession.State.Off;

    // For the characters' code (characters/: Maria, the Saturn select), which used to be
    // a mod of its own: co-op is loaded, and online it picks the character in the lobby.
    internal static bool Running => I is OnlineCoop;
    internal static bool PicksNow => I is OnlineCoop { PicksCharacters: true };

    // A friend Saturn Maria's effects, in her render pass (Characters.cs).
    protected override void DrawSaturnFx(IMemory m) => MariaMod.DrawPlayerFx(m);

    [PostHook("sel", "func_801AD66C")]
    public static void AfterNameEntryPick(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || (!self.PicksCharacters && File.Exists(CharSelArtFile))) return;
        if (CoopCharacters.IsName(m, "richter") || CoopCharacters.IsName(m, "maria") || CoopCharacters.IsName(m, "satmaria") || CoopCharacters.IsName(m, "richter2")) return;
        var (ch, pal) = self.MyPick();
        if (!self.CanPlay(ch, out _)) ch = CoopCharacter.Alucard;
        if (!CoopPalettes.Enabled) pal = 0;
        m.WriteU32(CoopCharacters.PlayableCharacterAddr, CoopCharacters.Playable(ch));
        // online everyone goes straight to the castle entrance (no prologue), the way
        // Richter mode does, so all start in the same room
        m.WriteU32(StageIdAddr, self.PicksCharacters || !CoopCharacters.IsAlucard(ch) ? StageNo3 : StageSt0);
        CoopCharacters.ToSave(m, ch, pal);
        Console.WriteLine($"[coop-online] new game as {CoopCharacters.Name(ch)} ({CoopPalettes.SetNames[pal]})");
    }

    // The host's save is in: make it ours (character, colours, and the game mode for it),
    // once its loading starts (NewGame.cs): the title screen still draws with whatever
    // the last game left, and switching the mode under it there breaks its drawing.
    bool _ownCharacterPending;
    void ApplyOwnCharacter(IMemory m) => _ownCharacterPending = true;

    void ApplyOwnCharacterNow(IMemory m)
    {
        _ownCharacterPending = false;
        var (ch, pal) = MyPick();
        if (!CanPlay(ch, out _)) ch = CoopCharacters.IsAlucard(ch) ? CoopCharacter.Alucard : CoopCharacter.Richter;
        if (!CoopPalettes.Enabled) pal = 0;
        CoopCharacters.ToSave(m, ch, pal);
        m.WriteU32(CoopCharacters.PlayableCharacterAddr, CoopCharacters.Playable(ch));
        Console.WriteLine($"[coop-online] playing the host's save as {CoopCharacters.Name(ch)}");
    }

    bool Present(IMemory m, Friend f) =>
        f.Valid && f.Peer.InGame && Game.InGame &&
        f.Peer.Stage == (ushort)m.ReadU32(StageIdAddr) &&
        f.Peer.RoomLeft == (short)m.ReadU32(TilemapLeft) && f.Peer.RoomTop == (short)m.ReadU32(TilemapTop);

    byte[] BuildSnapshot(IMemory m)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        byte flags = (byte)((Game.InGame ? 1 : 0) | (Paused(m) ? 2 : 0) |
                            (DeadToFriends(m) ? 4 : 0) |
                            (_net.IsHost && Pvp ? 8 : 0) |
                            (_p1BeamState != 0 ? 16 : 0) |
                            (RoomLocked(m) ? 32 : 0) |
                            (MenuLive(m) ? 64 : 0) |
                            (AtGate ? 128 : 0));
        w.Write((byte)_p1Char);
        w.Write((byte)_p1Palette);
        w.Write(flags);
        w.Write((ushort)m.ReadU32(StageIdAddr));
        w.Write((short)m.ReadU32(TilemapLeft));
        w.Write((short)m.ReadU32(TilemapTop));
        w.Write((int)m.ReadU32(PlayerXAddr));
        w.Write((int)m.ReadU32(PlayerYAddr));
        w.Write((int)m.ReadU32(EntitiesAddr + EVelX));
        w.Write((int)m.ReadU32(EntitiesAddr + EVelY));
        w.Write((byte)m.ReadU16(EntitiesAddr + EFacing));
        w.Write(m.ReadU16(EntitiesAddr + EStepOff));
        w.Write(m.ReadU16(EntitiesAddr + EStepSOff));
        w.Write(m.ReadU32(PlayerStatusAddr));
        for (int i = 0; i < 6; i++) w.Write((int)m.ReadU32(StatusStatsAddr + (uint)(i * 4)));
        for (int i = 0; i < 7; i++) w.Write(m.ReadU32(StatusEquipAddr + (uint)(i * 4)));
        w.Write(m.ReadU32(StatusGearAddr));                 // subweapon
        w.Write(m.ReadU32(ServantAddr));
        for (int i = 0; i < CapeColors; i++)
            w.Write(m.ReadU16(ClutAddr + (uint)((AlucardPalette * 16 + CapeFirstColor + i) * 2)));

        w.Write((short)m.ReadU16(ScrollXAddr));   // our camera, for object handoff
        w.Write((short)m.ReadU16(ScrollYAddr));
        w.Write((byte)(_authority ? 1 : 0));
        w.Write((ushort)_roomFrames);
        w.Write((byte)(InStoryScene(m) ? 1 : 0));

        // our familiar (servant entity slot 4), so the friend's copy can follow it
        uint fam = EntityAddr(FamiliarSlot);
        ushort famId = m.ReadU16(fam + EEntityId);
        bool hasFam = m.ReadU32(ServantAddr) != 0 && famId >= 0xD0 && famId <= 0xDF;
        w.Write(hasFam);
        if (hasFam)
        {
            int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
            w.Write(famId);
            foreach (var (off, size) in FamiliarFields)
            {
                uint v = size == 4 ? m.ReadU32(fam + off) : size == 2 ? m.ReadU16(fam + off) : m.ReadU8(fam + off);
                if (off == EPosX) v = (uint)((int)v + (sx << 16));      // to world
                if (off == EPosY) v = (uint)((int)v + (sy << 16));
                if (size == 4) w.Write(v); else if (size == 2) w.Write((ushort)v); else w.Write((byte)v);
            }
        }
        return ms.ToArray();
    }

    static Snap ReadSnapshot(byte[] b)
    {
        var r = new BinaryReader(new MemoryStream(b));
        var s = new Snap
        {
            Character = r.ReadByte(), Palette = r.ReadByte(), Flags = r.ReadByte(),
            Stage = r.ReadUInt16(), RoomLeft = r.ReadInt16(), RoomTop = r.ReadInt16(),
            WorldX = r.ReadInt32(), WorldY = r.ReadInt32(), VelX = r.ReadInt32(), VelY = r.ReadInt32(),
            Facing = r.ReadByte(), Step = r.ReadUInt16(), StepS = r.ReadUInt16(), Status = r.ReadUInt32(),
            Stats = new int[6], Equip = new uint[7], Cape = new ushort[4],
        };
        for (int i = 0; i < 6; i++) s.Stats[i] = r.ReadInt32();
        for (int i = 0; i < 7; i++) s.Equip[i] = r.ReadUInt32();
        s.Subweapon = r.ReadUInt32();
        s.Servant = r.ReadUInt32();
        for (int i = 0; i < 4; i++) s.Cape[i] = r.ReadUInt16();
        s.ScrollX = r.ReadInt16(); s.ScrollY = r.ReadInt16();
        s.Role = r.ReadByte();
        s.RoomFrames = r.ReadUInt16();
        s.Flags2 = r.ReadByte();
        if (r.BaseStream.Position < r.BaseStream.Length && r.ReadBoolean())
            s.Familiar = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
        return s;
    }

    // ---------------------------------------------------------------- P2 = the friend

    protected override bool ShouldJoin(IMemory m)
    {
        var f = F;
        if (f == NoFriend || !Present(m, f)) return false;
        // dead, or still on the way back from a death (DeadToFriends): they appear once
        // they're standing at the respawn spot, not dropping into it
        if ((f.Peer.Flags & 4) != 0) return false;
        if (!CanPlay(R.Character, out string why))
        {
            if (_vsyncs % 600 == 0) Console.WriteLine($"[coop-online] can't show {f.Name} as {CoopCharacters.Name(R.Character)}: {why}");
            return false;
        }
        f.JoinedAt = _vsyncs; f.ArrivedAt = _vsyncs;
        return true;
    }

    // ---------------------------------------------------------------- resync

    // Start the shared state over on both sides: progress is re-sent and re-merged, the
    // friend's body rejoins from their real state, and who runs the room is decided again.
    void RequestResync()
    {
        Send(Msg.Resync, w => w.Write((byte)0));
        Resync($"resync requested");
    }

    void OnResyncRequest(System.IO.BinaryReader r) => Resync($"{From} asked for a resync");

    bool _resyncRoom;
    void Resync(string why)
    {
        Console.WriteLine($"[coop-online] {why}");
        _fullSyncSent = false; _progressReady = false;
        DropAllFriends();
        _resyncRoom = true;
        foreach (var f in _friends) f.PvpHit = null;
        TeleportStatus = why;
    }

    protected override (uint X, uint Y) SpawnPoint(IMemory m, uint p1x, uint p1y)
    {
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        return ((uint)((F.Peer.WorldX - sx) << 16), (uint)((F.Peer.WorldY - sy) << 16));
    }

    // ---------------------------------------------------------------- input + rollback

    // Before P2's frame: if real input has arrived for frames we ran on a guess and
    // the guess was wrong, rewind P2 to the first wrong frame and replay up to now.
    protected override void BeforeP2Pass(CpuContext c, IMemory m)
    {
        var fr = F;
        CheckPrims(m, $"before {fr.Name}'s update");
        RemoveFriendAttacks(m);   // PvP copies: the friend's own frame mustn't see them
        if (!Rollback || R.Discontinuity || !fr.Valid || P2Ghost)
        {
            Array.Clear(fr.Checkpoints);
            R.Discontinuity = false;
        }
        else
        {
            uint latest = fr.Link.LatestInputFrame;
            int window = Math.Min(MaxRollback, fr.Checkpoints.Length - 1);
            for (uint f = fr.BodyFrame >= (uint)window ? fr.BodyFrame - (uint)window + 1 : 1; f <= fr.BodyFrame; f++)
            {
                var fed = fr.Fed[f % 64];
                var cp = fr.Checkpoints[f % 16];
                if (cp == null || fed.Remote > latest || fr.Link.Input(fed.Remote) == fed.Input) continue;

                // first mispredicted frame: rewind and replay f..BodyFrame
                if (!RestoreP2(m, cp)) { RollbacksRefused++; Array.Clear(fr.Checkpoints); break; }
                Rollbacks++;
                ushort prev = fed.Prev;
                for (uint g = f; g <= fr.BodyFrame; g++)
                {
                    fr.Checkpoints[g % 16] = CaptureP2(m, fr.Checkpoints[g % 16]);
                    var e = fr.Fed[g % 64];
                    fr.ResimInput = fr.Link.Input(e.Remote);
                    fr.ResimPrev = prev;
                    fr.Fed[g % 64] = (e.Remote, fr.ResimInput, prev);
                    ResimulateP2Frame(c, m);
                    prev = fr.ResimInput;
                    RollbackFrames++;
                }
                fr.RemotePrev = prev;
                break;
            }
        }
        fr.BodyFrame++;
        fr.Checkpoints[fr.BodyFrame % 16] = Rollback ? CaptureP2(m, fr.Checkpoints[fr.BodyFrame % 16]) : null;
    }

    // P2's pad: with rollback, the friend's input for "now" (a guess when it hasn't
    // arrived yet); without, their input from InputDelay frames ago.
    protected override void BeginP2Input(IMemory m)
    {
        for (uint i = 0; i < 8; i++) _savedPad0[i] = m.ReadU8(PadsAddr + i);

        var f = F;
        ushort pressed, prev;
        if (_resim)
        {
            pressed = f.ResimInput;
            prev = f.ResimPrev;
        }
        else
        {
            uint latest = f.Link.LatestInputFrame;
            // with rollback, run about half a round trip ahead of what's arrived
            int lead = Rollback ? Math.Clamp(f.Link.RttMs / 2 / 17 + 1, 0, MaxRollback - 1) : -InputDelay;
            uint target = (uint)Math.Max(0, (long)latest + lead);
            f.RemoteCursor++;
            if (f.RemoteCursor + 12 < target || f.RemoteCursor > target + 2) f.RemoteCursor = target;
            pressed = f.Valid && !f.Peer.InMenu ? f.Link.Input(f.RemoteCursor) : (ushort)0;
            prev = f.RemotePrev;
            f.Fed[f.BodyFrame % 64] = (f.RemoteCursor, pressed, prev);
            f.RemotePrev = pressed;
        }

        if (!_resim) { f.Pressed = pressed; f.Prev = prev; }
        WritePad0(m, pressed, prev);
    }

    static void WritePad0(IMemory m, ushort pressed, ushort prev)
    {
        m.WriteU16(PadsAddr + 0, pressed);
        m.WriteU16(PadsAddr + 2, prev);
        m.WriteU16(PadsAddr + 4, (ushort)(pressed & ~prev));
        m.WriteU16(PadsAddr + 6, (ushort)(pressed & ~prev));
    }

    // The friend's buttons this frame, for objects they drive (lifts).
    protected override void WithP2Pad(IMemory m, Action body)
    {
        Span<byte> saved = stackalloc byte[8];
        for (uint i = 0; i < 8; i++) saved[(int)i] = m.ReadU8(PadsAddr + i);
        WritePad0(m, F.Pressed, F.Prev);
        try { body(); }
        finally { for (uint i = 0; i < 8; i++) m.WriteU8(PadsAddr + i, saved[(int)i]); }
    }

    protected override void EndP2Input(IMemory m)
    {
        for (uint i = 0; i < 8; i++) m.WriteU8(PadsAddr + i, _savedPad0[i]);
    }

    // Pull the simulated friend toward the friend's real state (P2 is live here).
    protected override void AfterP2Update(CpuContext c, IMemory m)
    {
        var f = F;
        CheckPrims(m, $"after {f.Name}'s update");
        RemoveFriendAttacks(m);   // the stand-in attacker of a PvP hit, if any
        FixPvpFacing(m);
        if (!f.Valid) return;
        var peer = f.Peer;
        TickFriendFx(c, m);

        // Position: extrapolate their last snapshot by its age, then snap or ease.
        long age = Math.Min(10, _vsyncs - f.Link.SnapshotLocalFrame + f.Link.RttMs / 33);
        int tx = peer.WorldX + (int)(peer.VelX / 65536f * age);
        // all are pushed along the same intro path: keep ours a step behind, and let
        // that gap close gently once the push is over
        f.IntroGap = IntroRunning && IntroAllowed ? (f.IntroGap > IntroBehind ? f.IntroGap - 2 : IntroBehind) : System.Math.Max(0, f.IntroGap - 2);
        tx -= f.IntroGap;
        int ty = peer.WorldY + (int)(peer.VelY / 65536f * age);
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        int px = (short)m.ReadU16(EntitiesAddr + EPosXHi) + sx, py = (short)m.ReadU16(EntitiesAddr + EPosYHi) + sy;
        int ex = tx - px, ey = ty - py;
        bool snap = Math.Abs(ex) > SnapDistance || Math.Abs(ey) > SnapDistance;
        int mx = snap ? ex : ex / 4 + Math.Sign(ex), my = snap ? ey : ey / 4 + Math.Sign(ey);
        if (mx != 0 || my != 0) MoveP2Body(m, mx, my);
        if (Math.Abs(ex) > SnapDistance) m.WriteU16(EntitiesAddr + EFacing, peer.Facing);

        // Their HP/MP/hearts are theirs: the owner's numbers win.
        for (int i = 0; i < 6; i++) m.WriteU32(StatusStatsAddr + (uint)(i * 4), (uint)peer.Stats[i]);
        if (!CoopCharacters.IsAlucard(R.Character))
        {
            m.WriteU32(StatusGearAddr, peer.Subweapon);
            return;   // (gear, weapons, familiars and the cloak are Alucard's)
        }

        // Their gear and cloak.
        bool gearChanged = m.ReadU32(StatusGearAddr) != peer.Subweapon;
        for (int i = 0; i < 7 && !gearChanged; i++) gearChanged = m.ReadU32(StatusEquipAddr + (uint)(i * 4)) != peer.Equip[i];
        if (gearChanged)
        {
            m.WriteU32(StatusGearAddr, peer.Subweapon);
            for (int i = 0; i < 7; i++) m.WriteU32(StatusEquipAddr + (uint)(i * 4), peer.Equip[i]);
            Call(c, m, FnCalcDefense);
            Call(c, m, FnCalcAttack);
        }
        // they just teleported in: they come down in the beam on our screen too
        if (peer.Beaming && R.BeamState == 0 && (_vsyncs - f.JoinedAt < 40 || f.Vanished))
        {
            f.JoinedAt = -1000; f.Vanished = false;
            R.Discontinuity = true;
            StartBeam(c, m, ref R.BeamState, ref R.BeamWait);
        }
        EnsureWeapons(c, m);
        EnsureP2Familiar(c, m);
        SyncP2Familiar(m);
        for (int i = 0; i < CapeColors; i++) R.Cape[i] = peer.Cape[i];
    }

    // Move P2 and its afterimage trail together, so a correction doesn't leave the
    // afterimages behind as a streak (slots 1..3 and slot 1's trail prims).
    static void MoveP2Body(IMemory m, int dx, int dy)
    {
        for (int i = 0; i <= 3; i++)
        {
            uint e = EntityAddr(i);
            if (i > 0 && m.ReadU16(e + EAnimSet) == 0) continue;
            m.WriteU16(e + EPosXHi, (ushort)(m.ReadU16(e + EPosXHi) + dx));
            m.WriteU16(e + EPosYHi, (ushort)(m.ReadU16(e + EPosYHi) + dy));
        }
        // The afterimage trail lives in slot 1's prims (InitPlayerAfterImage): x0/y0
        // are where P2 was; x1/x2/y2 hold its frame/facing/palette, so leave those.
        int first = (int)m.ReadU32(EntityAddr(1) + EPrimIndex);
        if (first <= 0) return;
        uint p = PrimBufAddr + (uint)first * 0x34;
        for (int n = 0; p != 0 && n < 16; n++, p = m.ReadU32(p))
        {
            m.WriteU16(p + 0x08, (ushort)(m.ReadU16(p + 0x08) + dx));
            m.WriteU16(p + 0x0A, (ushort)(m.ReadU16(p + 0x0A) + dy));
        }
    }

    // ---------------------------------------------------------------- the friend's familiar, from disc

    DiscFs? _disc;

    // The friend's familiar, the same way: SERVANT/FT_00n.BIN is its graphics (0x6000:
    // 256x128 to 0x2C0,0x100 then 128x128 to 0x2C0,0x180) and TT_00n.BIN its code
    // for 0x80170000 (cd.c func_801078C4). Then the game's InitializeServant spawns it.
    void EnsureP2Familiar(CpuContext c, IMemory m)
    {
        uint want = F.Peer.Servant, have = m.ReadU32(ServantAddr);
        if (want == have || want > 7) return;

        // dismiss the current one (ServantWorkClear: servant entities are 0xD0..0xDF)
        for (int i = 4; i < PlayerEntityCount; i++)
        {
            ushort id = m.ReadU16(EntityAddr(i) + EEntityId);
            if (id >= 0xD0 && id <= 0xDF) Call(c, m, FnDestroyEntity, EntityAddr(i));
        }
        m.WriteU32(ServantAddr, want);
        R.Discontinuity = true;
        if (want == 0) return;

        int n = (int)want - 1;
        if (m.ReadU32(ServantLoadedAddr) != want)
        {
            var disc = Disc();
            byte[]? chr = null, prg = null;
            try { chr = disc?.ReadFile($"SERVANT/FT_{n:D3}.BIN"); prg = disc?.ReadFile($"SERVANT/TT_{n:D3}.BIN"); }
            catch (Exception e) { Console.WriteLine($"[coop-online] can't read familiar {n}: {e.Message}"); }
            if (chr == null || prg == null || chr.Length < 0x6000) { m.WriteU32(ServantAddr, 0); return; }

            UploadVram(0x2C0, 0x100 + R.BankY, 0x40, 0x80, chr.AsSpan(0, 0x4000));
            UploadVram(0x2C0, 0x180 + R.BankY, 0x20, 0x80, chr.AsSpan(0x4000, 0x2000));
            prg.AsSpan(0, Math.Min(prg.Length, 0xA000)).CopyTo(MemoryMarshal.AsBytes(Ram(m, FamiliarOvlAddr, 0xA000)));
            Dispatcher.Load($"tt_{n:D3}");
            m.WriteU32(ServantLoadedAddr, want);
        }
        Call(c, m, FnInitializeServant, 1 /* MENU_SWITCH_SERVANT */);
        Console.WriteLine($"[coop-online] {F.Name}'s familiar {n} is out");
    }

    // Familiars think for themselves (and randomly), so each game's copy of the
    // friend's familiar would wander off on its own. Make ours follow theirs: every
    // snapshot sets its motion and animation to the owner's (P2 is live). Not its AI
    // step: that would desync from the familiar's own internal state and crash.
    const int FamiliarSlot = 4; // SERVANT_ENTITY_INDEX
    static readonly (uint Off, int Size)[] FamiliarFields =
    {
        (0x00, 4), (0x04, 4), (0x08, 4), (0x0C, 4),   // pos, velocity
        (0x14, 2), (0x16, 2), (0x18, 1), (0x19, 1),   // facing, palette, blend, draw flags
        (0x1A, 2), (0x1C, 2), (0x1E, 2), (0x24, 2),   // scale, rotate, z
        (0x4C, 4), (0x50, 2), (0x52, 2),              // anim, pose, pose timer
        (0x54, 2), (0x56, 2), (0x5A, 2),              // anim set, frame, draw slot
    };

    void SyncP2Familiar(IMemory m)
    {
        var f = F;
        var data = f.Peer.Familiar;
        if (data == null || f.FamiliarAppliedFrame == f.Link.SnapshotLocalFrame) return;
        uint fam = EntityAddr(FamiliarSlot);
        var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadUInt16() != m.ReadU16(fam + EEntityId)) return; // not the same familiar (yet)
        if (m.ReadU32(fam + 0x28) == 0) return;                    // hasn't run its first update yet
        f.FamiliarAppliedFrame = f.Link.SnapshotLocalFrame;
        int sx = (short)m.ReadU16(ScrollXAddr), sy = (short)m.ReadU16(ScrollYAddr);
        foreach (var (off, size) in FamiliarFields)
        {
            uint v = size == 4 ? r.ReadUInt32() : size == 2 ? r.ReadUInt16() : r.ReadByte();
            if (off == EPosX) v = (uint)((int)v - (sx << 16));          // to our screen
            if (off == EPosY) v = (uint)((int)v - (sy << 16));
            if (size == 4) m.WriteU32(fam + off, v); else if (size == 2) m.WriteU16(fam + off, (ushort)v); else m.WriteU8(fam + off, (byte)v);
        }
    }

    DiscFs? Disc()
    {
        if (_disc != null) return _disc;
        foreach (var pattern in new[] { "*.cue", "*.chd" })
            foreach (var path in Directory.GetFiles(Path.GetFullPath("disc"), pattern))
                return _disc = DiscFs.Open(path);
        return null;
    }
}
