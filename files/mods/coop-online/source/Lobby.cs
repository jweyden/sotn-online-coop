using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

// The lobby (server/coop_server.py): hosts list their game there (until it's full),
// players fetch the list and pick one. Joining asks the server to introduce us: it
// sends each side the other's public and LAN address, and both start sending to each
// other at once, which opens a direct path through home routers ("hole punching").
// Game traffic then goes straight between the players. If nothing gets through for a
// few seconds, the joiner falls back to the server's relay (the host is always signed
// up for it; each joiner signs up under its own tag). The other guests are introduced
// by the host (Net.cs). Everything uses the one game socket, since the server has to
// see the same port the game traffic will use.
public sealed partial class NetSession
{
    // Free: Free Select (else Normal Mode); Locked: private (a password to join)
    public sealed record LobbyGame(string Id, string Game, string Host, string Info, bool Free, bool Locked);
    public IReadOnlyList<LobbyGame> LobbyGames { get; private set; } = Array.Empty<LobbyGame>();
    public string LobbyStatus { get; private set; } = "";
    public string LobbyGameId { get; private set; } = "";   // the game we host in the lobby
    public string LobbyInfo = "";                             // shown in the list next to our game (set by the mod)
    public bool LobbyOpen => _lobby != null;
    public string KitVersion = "";   // this kit's version (the mod's version.txt; "" in a dev build)
    public string LatestKit = "";    // a newer kit's version, if the lobby says there is one
    public long LobbyListFrame { get; private set; } = -1;
    public bool ForceRelay;   // testing: skip the direct path (COOP_FORCE_RELAY=1)

    IPEndPoint? _lobby;
    string _lobbyGameName = "";
    bool _listed;
    readonly List<(IPEndPoint To, long Until)> _punchTo = new(); // host: joiners to open a path toward
    string _joinId = "";
    long _joinAsked, _peerInfoAt;
    bool _joinViaRelay;
    const int RelayAfterFrames = 180;   // 3 s with no direct path: use the relay

    // Browse the lobby (a socket for talking to the server; no game yet).
    public void OpenLobby(string server)
    {
        var ep = ParseEndpoint(server);
        if (_udp == null) { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)); Start(); }
        _lobby = ep;
        LobbyStatus = "asking for games...";
        RefreshLobby();
    }

    // (only games of our protocol version; not our own)
    public void RefreshLobby() => SendLobby("list", ("ver", ProtocolVersion.ToString()), ("install", _installId.ToString("X8")), ("x", Chaos ? "1" : "0"), ("kit", KitVersion));

    public void HostLobby(string server, string gameName, string password)
    {
        Close();
        _lobby = ParseEndpoint(server);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _isHost = true;
        Status = State.Hosting;
        LobbyGameId = NewCode();
        Key = PasswordKey(LobbyGameId, password);
        _lobbyGameName = gameName;
        // the relay fallback: the server passes a joiner's packets on to us if they can't reach us
        _relay = _lobby;
        _relayHello = RelaySignUp('H', LobbyGameId, "");
        Start();
        SendHost();
        SendRelayHello();
        LobbyStatus = "listing your game...";
    }

    public void JoinLobby(string id, string password)
    {
        if (_lobby == null || _udp == null) return;
        Key = PasswordKey(id, password);
        _links.Clear();
        BeginJoin();
        _hostEp = null;
        _joinId = id;
        _joinTargets = new();
        _joinAsked = _frame; _peerInfoAt = 0; _joinViaRelay = false;
        LobbyStatus = "asking the host...";
        SendJoin();
    }

    // Once a frame from Poll.
    void TickLobby()
    {
        if (_lobby == null) return;
        if (_isHost)
        {
            if (_frame % 120 == 0) SendHost();   // still here (a full game says so: the list leaves it out)
            _punchTo.RemoveAll(p => p.Until < _frame);
            if (_frame % 10 == 0)
                foreach (var (to, _) in _punchTo) SendRaw(PunchPacket(), to, "");
            return;
        }
        if (Status != State.Joining || _joinId.Length == 0 || _joinTargets == null) return;
        if (_peerInfoAt == 0 && _frame - _joinAsked > 60) { SendJoin(); _joinAsked = _frame; }   // (lost on the way?)
        if (_peerInfoAt != 0 && !_joinViaRelay && _frame - _peerInfoAt > RelayAfterFrames)
        {
            _joinViaRelay = true;
            _relay = _lobby;
            _relayTag = _session.ToString("X8");
            _relayHello = RelaySignUp('J', _joinId, _relayTag);
            SendRelayHello();
            _joinTargets.Add(_lobby);
            LobbyStatus = "no direct path: connecting through the server";
            Console.WriteLine("[coop-online] lobby: no direct path, using the relay");
        }
    }

    void HandleLobby(byte[] data, IPEndPoint from)
    {
        if (_lobby == null || !from.Equals(_lobby)) return;
        var lines = Encoding.UTF8.GetString(data, 2, data.Length - 2).Split('\n');
        string op = lines[0].Trim();
        var f = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq > 0) f[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        string Get(string k) => f.TryGetValue(k, out var v) ? v : "";

        switch (op)
        {
            case "games":
            {
                var games = new List<LobbyGame>();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("latest=")) { LatestKit = lines[i][7..].Split('\t')[0].Trim(); continue; }
                    var c = lines[i].Split('\t');
                    if (c.Length >= 4 && c[0] != LobbyGameId)
                        games.Add(new LobbyGame(c[0], c[1], c[2], c[3], c.Length < 5 || c[4] != "normal", c.Length >= 6 && c[5] == "1"));
                }
                LobbyGames = games;
                LobbyListFrame = _frame;
                LobbyStatus = games.Count == 0 ? "no games right now" : $"{games.Count} game{(games.Count == 1 ? "" : "s")}";
                break;
            }
            case "hosted":
                _listed = true;
                if (Status == State.Hosting) LobbyStatus = "your game is listed: waiting for someone to join";
                break;
            case "badkey":
                if (!_isHost && Status == State.Joining) { Status = State.Off; _joinId = ""; _joinTargets = null; }
                Refused = "wrong password";
                LobbyStatus = "wrong password";
                break;
            case "nogame":
                if (!_isHost && Status == State.Joining) { Status = State.Off; _joinId = ""; _joinTargets = null; }
                LobbyStatus = "that game isn't there any more";
                RefreshLobby();
                break;
            case "peer":
            {
                var addrs = new List<IPEndPoint>();
                foreach (var a in new[] { Get("addr"), Get("lan") })
                    if (TryEndpoint(a, out var ep) && !addrs.Exists(x => x.Equals(ep))) addrs.Add(ep);
                if (_isHost && (PlayerCount < MaxPlayers || ChaosSeat(Get("x") == "1")))
                {
                    // open our side toward them while they knock on ours
                    foreach (var ep in addrs) { _punchTo.Add((ep, _frame + 600)); SendRaw(PunchPacket(), ep, ""); }
                    Console.WriteLine($"[coop-online] lobby: {Get("name")} is joining");
                }
                else if (!_isHost && Status == State.Joining && Get("id") == _joinId && _joinTargets != null && _peerInfoAt == 0)
                {
                    if (!ForceRelay) _joinTargets.AddRange(addrs);
                    _peerInfoAt = _frame;
                    LobbyStatus = $"connecting to {Get("name")}...";
                    SendHello();
                }
                break;
            }
        }
    }

    void LobbyClosing()
    {
        // (three times: one lost packet shouldn't leave the game listed)
        if (_lobby != null && _isHost && LobbyGameId.Length > 0)
            for (int i = 0; i < 3; i++) SendLobby("unhost", ("id", LobbyGameId));
        _lobby = null; _listed = false;
        LobbyGameId = ""; LobbyStatus = "";
        _joinId = ""; _joinTargets = null; _punchTo.Clear();
    }

    byte[] PunchPacket() => Packet(PkPunch, Nobody).ToArray();

    void SendHost()
    {
        if (!_isHost || LobbyGameId.Length == 0) return;
        string info = $"{Math.Min(PlayerCount, MaxPlayers)}/{MaxPlayers}" + (LobbyInfo.Length > 0 ? $"  {LobbyInfo}" : "");
        SendLobby("host", ("id", LobbyGameId), ("name", LocalName), ("game", _lobbyGameName), ("info", info), ("lan", LanAddress()),
            ("ver", ProtocolVersion.ToString()), ("mode", FreeSelect ? "free" : "normal"), ("lock", Key), ("install", _installId.ToString("X8")),
            ("players", PlayerCount.ToString()), ("playing", Playing ? "1" : "0"), ("full", PlayerCount >= MaxPlayers ? "1" : "0"));
    }
    void SendJoin() => SendLobby("join", ("id", _joinId), ("name", LocalName), ("lan", LanAddress()), ("key", Key), ("install", _installId.ToString("X8")), ("x", Chaos ? "1" : "0"));

    // The host's game has started (for the lobby's list and log)
    public bool Playing;

    void SendLobby(string op, params (string Key, string Value)[] fields)
    {
        if (_lobby == null) return;
        var sb = new StringBuilder(op).Append('\n');
        foreach (var (k, v) in fields) sb.Append(k).Append('=').Append(v.Replace('\n', ' ').Replace('\t', ' ')).Append('\n');
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var pkt = new byte[2 + body.Length];
        pkt[0] = (byte)'L'; pkt[1] = (byte)'B';
        body.CopyTo(pkt, 2);
        SendRaw(pkt, _lobby, "");
    }

    // "RL" + H|J + id (+ ":" + tag for a joiner)
    static byte[] RelaySignUp(char role, string id, string tag)
    {
        var b = new List<byte> { (byte)'R', (byte)'L', (byte)role };
        b.AddRange(Encoding.ASCII.GetBytes(tag.Length > 0 ? $"{id}:{tag}" : id));
        return b.ToArray();
    }

    // Our address on the home network (for a friend behind the same router), with our game port.
    string LanAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(_lobby ?? new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));   // no packet is sent
            var ip = ((IPEndPoint)s.LocalEndPoint!).Address;
            int port = ((IPEndPoint)_udp!.Client.LocalEndPoint!).Port;
            return $"{ip}:{port}";
        }
        catch { return ""; }
    }

    static IPEndPoint ParseEndpoint(string hostPort)
    {
        int colon = hostPort.LastIndexOf(':');
        string host = colon > 0 ? hostPort[..colon].Trim() : hostPort.Trim();
        int port = colon > 0 ? int.Parse(hostPort[(colon + 1)..]) : 7777;
        return new IPEndPoint(Resolve(host), port);
    }

    static bool TryEndpoint(string s, out IPEndPoint ep)
    {
        ep = null!;
        int colon = s.LastIndexOf(':');
        if (colon <= 0 || !IPAddress.TryParse(s[..colon], out var ip) || !int.TryParse(s[(colon + 1)..], out int port)) return false;
        ep = new IPEndPoint(ip, port);
        return true;
    }
}
