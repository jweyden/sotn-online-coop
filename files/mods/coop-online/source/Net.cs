using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// The online session: up to MaxPlayers games on one UDP socket each.
//  - The host is player 0. A joiner knocks (Hello) on the host's address; the host
//    gives it a number and the roster (Welcome), and tells everyone else about it.
//  - Every pair of players has its own link (NetLink): one Frame packet per game
//    frame each way with the sender's recent pad inputs (redundant, so a lost packet
//    costs nothing), an optional state snapshot, and any unacknowledged reliable
//    messages. Links between two guests go straight between them when the host's
//    introduction gets a direct path open ("hole punching"), and through the host
//    (which forwards them) until then or if it never does.
//  - Dropouts: after TimeoutFrames of silence a link is gone; a guest whose host
//    link went quiet knocks again, and the host keeps its number for it.
//  - Relay (for routers nothing gets through): everything to and from the host goes
//    through server/coop_server.py, which knows each joiner by a tag.
// The receive loop runs on a background thread; the game thread calls Poll() once a
// frame and only ever touches the decoded data from there.
public sealed partial class NetSession : IDisposable
{
    public const ushort ProtocolVersion = 4;   // 4: snapshot Flags2 (in a cutscene)
    public const int TimeoutFrames = 300; // 5 s of silence: the connection is gone
    public const int MaxPlayersLimit = 6;   // what the system handles (test bots: COOP_PLAYERS)
    public const int PublicMaxPlayers = 3;  // what a host can choose: capped on purpose
    internal const ushort Magic = 0x4F53; // "SO"
    internal const byte PkHello = 1, PkWelcome = 2, PkFrame = 3, PkBye = 4, PkRoom = 5, PkPunch = 6, PkRoster = 7, PkPair = 8;
    internal const byte Nobody = 0xFF;
    const int PairDirectAfterFrames = 180;   // a guest pair still on the host's forwarding after this: stays there

    public enum State { Off, Hosting, Joining, Connected }
    public State Status { get; private set; } = State.Off;
    public string LastError { get; private set; } = "";
    public int MaxPlayers = 3;

    public string LocalName = "Player";
    public byte LocalCharacter, LocalPalette;   // CoopCharacter, 0..2
    public uint LocalAvailable = 1;             // bit n: this machine can run CoopCharacter n
    public int LocalNumber { get; private set; } = 0;
    readonly uint _installId = (uint)Random.Shared.Next(1, int.MaxValue);   // the same player across reconnects

    // Everyone else in the session, connected or (for a guest) still being reached.
    readonly List<NetLink> _links = new();
    public IReadOnlyList<NetLink> Links => _links;
    public IEnumerable<NetLink> Peers => _links.Where(l => l.Connected);
    public NetLink? Link(int number) => _links.Find(l => l.Number == number);
    public int PlayerCount => 1 + _links.Count(l => l.Connected);
    public string PeerName => string.Join(", ", Peers.Select(l => l.Name));
    public int RttMs => Peers.Select(l => l.RttMs).DefaultIfEmpty(0).Max();
    public int Reconnects { get; private set; }

    UdpClient? _udp;
    bool _isHost;
    public bool IsHost => _isHost;
    IPEndPoint? _hostEp;               // guest: where the host is (direct, or the relay)
    List<IPEndPoint>? _joinTargets;    // guest, joining through the lobby: every address to knock on
    Thread? _rx;
    volatile bool _running;
    readonly ConcurrentQueue<(byte[] Data, IPEndPoint From)> _inbox = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    internal long _frame;
    uint _session;                     // guest: this join attempt
    long _lastHostHeard;

    // our recent inputs (the same for every link)
    readonly List<(uint Frame, ushort Pad)> _sentInputs = new();

    IPEndPoint? _relay; byte[]? _relayHello;   // relay mode: the server, and our sign-up for it
    string _relayTag = "";                     // guest via the relay: how the server knows us
    public string RelayCode { get; private set; } = "";
    public bool ViaRelay => Peers.Any(l => l.ViaRelay);

    // ---------------------------------------------------------------- starting

    public void Host(int port)
    {
        Close();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _isHost = true;
        LocalNumber = 0;
        Status = State.Hosting;
        Start();
    }

    public void Join(string host, int port)
    {
        Close();
        _hostEp = new IPEndPoint(Resolve(host), port);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        BeginJoin();
        Start();
    }

    // Through a relay: host or join under a code. The relay is the only address the
    // host's link to a joiner (and a joiner's to the host) uses.
    public void HostRelay(string relayHost, int relayPort, string code) => StartRelay(relayHost, relayPort, code, host: true);
    public void JoinRelay(string relayHost, int relayPort, string code) => StartRelay(relayHost, relayPort, code, host: false);

    void StartRelay(string relayHost, int relayPort, string code, bool host)
    {
        Close();
        code = code.Trim().ToUpperInvariant();
        _relay = new IPEndPoint(Resolve(relayHost), relayPort);
        RelayCode = code;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        if (host)
        {
            _isHost = true;
            LocalNumber = 0;
            Status = State.Hosting;
            _relayHello = RelaySignUp('H', code, "");
        }
        else
        {
            BeginJoin();
            _hostEp = _relay;
            _relayTag = _session.ToString("X8");
            _relayHello = RelaySignUp('J', code, _relayTag);
        }
        Start();
        SendRelayHello();
    }

    void BeginJoin()
    {
        _isHost = false;
        LocalNumber = Nobody;
        Status = State.Joining;
        _session = (uint)Random.Shared.Next(1, int.MaxValue);
    }

    void SendRelayHello()
    {
        if (_relay == null || _relayHello == null || _udp == null) return;
        try { _udp.Send(_relayHello, _relayHello.Length, _relay); } catch (Exception e) { LastError = e.Message; }
    }

    // A short code that's easy to read out: no 0/O or 1/I.
    public static string NewCode()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var c = new char[6];
        for (int i = 0; i < c.Length; i++) c[i] = letters[Random.Shared.Next(letters.Length)];
        return new string(c);
    }

    static IPAddress Resolve(string host)
    {
        var addrs = Dns.GetHostAddresses(host);
        return Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
    }

    void Start()
    {
        _running = true;
        var udp = _udp!;
        // never block for long: closing a socket another thread is stuck receiving on can
        // hang the closer (macOS), and the closer is the game thread
        udp.Client.ReceiveTimeout = 100;
        _rx = new Thread(() => ReceiveLoop(udp)) { IsBackground = true, Name = "coop-online rx" };
        _rx.Start();
    }

    // (each thread keeps its own socket: a new one replaces _udp, and this one ends)
    void ReceiveLoop(UdpClient udp)
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_running && ReferenceEquals(udp, _udp))
        {
            try
            {
                var ep = any;
                byte[] data = udp.Receive(ref ep);
                _inbox.Enqueue((data, ep));
            }
            catch (SocketException) when (_running) { /* timeout, ICMP unreachable etc: keep going */ }
            catch { return; }
        }
    }

    public void Close()
    {
        if (_udp != null)
            foreach (var l in Peers) try { SendTo(l, Packet(PkBye, l.Number).ToArray()); } catch { }
        LobbyClosing();
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null; _rx = null; _hostEp = null; _joinTargets = null;
        _relay = null; _relayHello = null; _relayTag = ""; RelayCode = "";
        Status = State.Off;
        _links.Clear();
        _sentInputs.Clear();
        LocalNumber = 0;
    }

    public void Dispose() => Close();

    // ---------------------------------------------------------------- what the mod uses

    // To everyone (each link keeps its own numbering and resends).
    public void SendReliable(byte[] body) { foreach (var l in Peers) l.SendReliable(body); }
    public void SendReliable(NetLink to, byte[] body) => to.SendReliable(body);

    // Messages from anyone, oldest first per sender.
    public bool TryReceiveReliable(out NetLink from, out byte[] body)
    {
        foreach (var l in _links)
            if (l.TryReceiveReliable(out body)) { from = l; return true; }
        from = null!; body = null!;
        return false;
    }

    public void SendRoom(NetLink to, uint frame, byte[] payload)
    {
        if (!to.Connected) return;
        var w = Packet(PkRoom, to.Number);
        w.Write(frame);
        w.Write(payload);
        SendTo(to, w.ToArray());
    }

    // Once a frame on the game thread: drain packets, then send ours.
    public void Poll(ushort localPad, byte[]? snapshot)
    {
        _frame++;
        FlushSim();
        while (_inbox.TryDequeue(out var pkt)) Handle(pkt.Data, pkt.From);
        if (_udp == null) return;

        foreach (var l in _links) l.Tick(_frame);
        foreach (var l in _links.ToList())
        {
            // (a guest's other links go through the host if need be: the host's roster
            // says when someone has left)
            if (!_isHost || !l.Connected || l.FramesSinceHeard <= TimeoutFrames) continue;
            LastError = $"lost {l.Name}";
            Console.WriteLine($"[coop-online] lost {l.Name} (player {l.Number + 1})");
            l.Lost();
            if (_isHost) { _links.Remove(l); BroadcastRoster(); }
        }
        if (!_isHost && Status == State.Connected && _frame - _lastHostHeard > TimeoutFrames)
        {
            // the host went quiet: knock again until it's back
            LastError = "lost the host: reconnecting";
            Status = State.Joining;
            foreach (var l in _links) l.Lost();
            _links.Clear();
            _session = (uint)Random.Shared.Next(1, int.MaxValue);
        }
        if (_relay != null && _frame % 120 == 1) SendRelayHello();   // keeps our spot at the relay (and the NAT path) open
        TickLobby();
        if (Status == State.Joining && _frame % 15 == 0) SendHello();
        if (!_isHost) TickPairs();
        if (_isHost) Status = Peers.Any() ? State.Connected : State.Hosting;
        if (Status != State.Connected) return;

        _sentInputs.Add(((uint)_frame, localPad));
        if (_sentInputs.Count > 8) _sentInputs.RemoveAt(0);
        foreach (var l in Peers) SendFrame(l, snapshot);
    }

    void SendFrame(NetLink l, byte[]? snapshot)
    {
        var w = Packet(PkFrame, l.Number);
        w.Write((uint)_frame);
        w.Write(NowMs());
        w.Write(l.LastSendTime);                                     // echo, for RTT
        w.Write((uint)(_frame - l.LastSendTimeAt));                  // frames we held it
        w.Write(l.LastReliableIn);                                   // ack
        w.Write((byte)_sentInputs.Count);
        foreach (var (f, p) in _sentInputs) { w.Write(f); w.Write(p); }
        w.Write((ushort)(snapshot?.Length ?? 0));
        if (snapshot != null) w.Write(snapshot);
        l.WriteReliables(w);
        SendTo(l, w.ToArray());
        l.PruneInputs();
    }

    // ---------------------------------------------------------------- packets

    // [magic][type][from][to] ...
    BinaryWriter Packet(byte type, int to)
    {
        var w = new BinaryWriter(new MemoryStream());
        w.Write(Magic); w.Write(type); w.Write((byte)LocalNumber); w.Write((byte)to);
        return w;
    }

    void Handle(byte[] data, IPEndPoint from)
    {
        if (data.Length >= 2 && data[0] == 'L' && data[1] == 'B') { HandleLobby(data, from); return; }
        // through the relay, to the host: "RF" + joiner tag + 0 + packet
        string relayTag = "";
        if (_isHost && _relay != null && from.Equals(_relay) && data.Length > 3 && data[0] == 'R' && data[1] == 'F')
        {
            int z = Array.IndexOf(data, (byte)0, 2);
            if (z < 0) return;
            relayTag = System.Text.Encoding.ASCII.GetString(data, 2, z - 2);
            data = data[(z + 1)..];
        }
        if (data.Length < 5 || BitConverter.ToUInt16(data, 0) != Magic) return;
        byte type = data[2], src = data[3], dst = data[4];
        // the host passes on packets between two guests that have no direct path
        if (_isHost && dst != LocalNumber && dst != Nobody && type is PkFrame or PkRoom or PkPair or PkBye)
        {
            var to = Link(dst);
            if (to != null && to.Connected) SendTo(to, data);
            return;
        }
        var r = new BinaryReader(new MemoryStream(data, 5, data.Length - 5));
        try
        {
            switch (type)
            {
                case PkHello when _isHost: HandleHello(r, from, relayTag); break;
                case PkWelcome when !_isHost && Status == State.Joining: HandleWelcome(r, from); break;
                case PkRoster when !_isHost && Status == State.Connected && src == 0: HandleRoster(r); break;
                case PkPair when !_isHost: HandlePair(r, src, from); break;
                case PkBye:
                {
                    var l = Link(src);
                    if (l == null) return;
                    Console.WriteLine($"[coop-online] {l.Name} left");
                    l.Lost();
                    _links.Remove(l);
                    if (_isHost) BroadcastRoster();
                    else if (src == 0) { Status = State.Off; foreach (var x in _links) x.Lost(); _links.Clear(); }
                    break;
                }
                case PkRoom:
                {
                    var l = Link(src);
                    if (l == null || !l.Connected || data.Length <= 9) return;
                    l.NoteDirect(from, _isHost || src == 0 || !from.Equals(_hostEp));
                    uint f = BitConverter.ToUInt32(data, 5);
                    l.ReceiveRoom(f, data[9..]);
                    break;
                }
                case PkFrame:
                {
                    var l = Link(src);
                    if (l == null || !l.Connected) return;
                    if (_isHost || src == 0 || !from.Equals(_hostEp)) l.NoteDirect(from, true);
                    l.ReadFrame(r, _frame, NowMs());
                    if (src == 0) _lastHostHeard = _frame;
                    break;
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (Exception e) { if (_handleErrors++ < 5) Console.WriteLine($"[coop-online] bad packet (type {type} from {src}): {e.GetType().Name}: {e.Message}"); }
    }
    int _handleErrors;

    // ---------------------------------------------------------------- joining (host side)

    // Hello: [ver][install id][session][name][character][palette][available][lan]
    void SendHello()
    {
        var w = Packet(PkHello, 0);
        w.Write(ProtocolVersion);
        w.Write(_installId);
        w.Write(_session);
        w.Write(LocalName);
        w.Write(LocalCharacter); w.Write(LocalPalette); w.Write(LocalAvailable);
        w.Write(LanAddress());
        var pkt = w.ToArray();
        // joining through the lobby: knock on every address we were given at once
        if (_joinTargets != null) foreach (var t in _joinTargets) SendRaw(pkt, t, "");
        else if (_hostEp != null) SendRaw(pkt, _hostEp, "");
    }

    // Our pick changed after connecting: a guest says Hello again (the host takes the
    // new pick from it), the host sends everyone the roster.
    public void LocalPickChanged()
    {
        if (_isHost) BroadcastRoster();
        else if (Status == State.Connected) SendHello();
    }

    void HandleHello(BinaryReader r, IPEndPoint from, string relayTag)
    {
        ushort ver = r.ReadUInt16();
        if (ver != ProtocolVersion) { LastError = $"a player runs protocol v{ver}, we run v{ProtocolVersion}"; return; }
        uint install = r.ReadUInt32(), session = r.ReadUInt32();
        string name = r.ReadString();
        byte ch = r.ReadByte(), pal = r.ReadByte();
        uint avail = r.ReadUInt32();
        string lan = r.ReadString();

        var l = _links.Find(x => x.InstallId == install);
        if (l == null)
        {
            if (PlayerCount >= MaxPlayers) return;   // full: no answer (they keep knocking)
            int n = 1;
            while (_links.Exists(x => x.Number == n)) n++;
            l = new NetLink(n) { InstallId = install };
            _links.Add(l);
            Console.WriteLine($"[coop-online] {name} joins as player {n + 1}");
        }
        // a new attempt (or the first): start this link over. Repeats of the same Hello
        // (sent until our Welcome arrives) change nothing.
        if (session != l.Session || !l.Connected)
        {
            if (l.Session != 0) Reconnects++;
            l.Reset();
            l.Session = session;
            // (the same Hello can arrive by several paths - direct, LAN, relay: the
            // first one that got through is where we answer)
            l.Endpoint = from;
            l.RelayTag = relayTag;
            l.Connect(_frame);
            l.Name = name; l.Character = ch; l.Palette = pal; l.Available = avail;
            l.PublicAddr = relayTag.Length > 0 ? "" : $"{from.Address}:{from.Port}";
            l.LanAddr = lan;
            BroadcastRoster();
        }
        else if (l.Character != ch || l.Palette != pal)
        {
            // their pick changed (LocalPickChanged): everyone's list shows it
            l.Character = ch; l.Palette = pal;
            BroadcastRoster();
        }
        l.Heard(_frame);
        var w = Packet(PkWelcome, l.Number);
        w.Write(ProtocolVersion);
        w.Write((byte)l.Number);
        w.Write((byte)MaxPlayers);
        WriteRoster(w);
        SendTo(l, w.ToArray());
    }

    // Everyone: [count] then [number][name][character][palette][available][public][lan]
    void WriteRoster(BinaryWriter w)
    {
        var all = _links.Where(x => x.Connected).ToList();
        w.Write((byte)(all.Count + 1));
        w.Write((byte)0); w.Write(LocalName); w.Write(LocalCharacter); w.Write(LocalPalette); w.Write(LocalAvailable); w.Write(""); w.Write("");
        foreach (var x in all)
        {
            w.Write((byte)x.Number); w.Write(x.Name); w.Write(x.Character); w.Write(x.Palette); w.Write(x.Available);
            w.Write(x.PublicAddr); w.Write(x.LanAddr);
        }
    }

    long _rosterSentAt;
    void BroadcastRoster()
    {
        _rosterSentAt = _frame;
        foreach (var l in Peers)
        {
            var w = Packet(PkRoster, l.Number);
            WriteRoster(w);
            SendTo(l, w.ToArray());
        }
        if (_lobby != null) SendHost();   // the lobby's player count
    }

    // ---------------------------------------------------------------- joining (guest side)

    void HandleWelcome(BinaryReader r, IPEndPoint from)
    {
        if (r.ReadUInt16() != ProtocolVersion) return;
        LocalNumber = r.ReadByte();
        MaxPlayers = r.ReadByte();
        _hostEp = from;   // whichever way got through (direct, LAN or relay)
        _joinTargets = null;
        Status = State.Connected;
        _lastHostHeard = _frame;
        HandleRoster(r);
        Console.WriteLine($"[coop-online] joined as player {LocalNumber + 1} of {MaxPlayers}");
    }

    void HandleRoster(BinaryReader r)
    {
        _lastHostHeard = _frame;
        int count = r.ReadByte();
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int n = r.ReadByte();
            string name = r.ReadString();
            byte ch = r.ReadByte(), pal = r.ReadByte();
            uint avail = r.ReadUInt32();
            string pub = r.ReadString(), lan = r.ReadString();
            if (n == LocalNumber) continue;
            seen.Add(n);
            var l = Link(n);
            if (l == null)
            {
                l = new NetLink(n);
                _links.Add(l);
                if (n == 0) { l.Endpoint = _hostEp; l.RelayTag = ""; l.Direct = true; }
                else
                {
                    // until a direct path opens, through the host
                    l.Endpoint = _hostEp; l.Direct = false;
                    l.Candidates = new[] { pub, lan }.Where(a => TryEndpoint(a, out _)).Select(a => { TryEndpoint(a, out var ep); return ep; }).ToList();
                    l.KnockSince = _frame;
                }
                l.Connect(_frame);
                Console.WriteLine($"[coop-online] {name} is player {n + 1}");
            }
            l.Name = name; l.Character = ch; l.Palette = pal; l.Available = avail;
        }
        foreach (var gone in _links.Where(x => !seen.Contains(x.Number)).ToList())
        {
            Console.WriteLine($"[coop-online] {gone.Name} left");
            gone.Lost();
            _links.Remove(gone);
        }
    }

    // Guest pairs: knock on each other's addresses (both sides at once) until a packet
    // arrives straight from the other; meanwhile the host forwards everything.
    void TickPairs()
    {
        if (Status != State.Connected || _frame % 15 != 0) return;
        foreach (var l in _links)
        {
            if (l.Number == 0 || l.Direct || l.Candidates == null || _frame - l.KnockSince > PairDirectAfterFrames * 4) continue;
            var pkt = Packet(PkPair, l.Number).ToArray();
            foreach (var ep in l.Candidates) SendRaw(pkt, ep, "");
        }
    }

    void HandlePair(BinaryReader r, int src, IPEndPoint from)
    {
        var l = Link(src);
        if (l == null || from.Equals(_hostEp)) return;
        if (!l.Direct)
        {
            l.NoteDirect(from, true);
            Console.WriteLine($"[coop-online] direct path to {l.Name} at {from}");
            SendRaw(Packet(PkPair, l.Number).ToArray(), from, "");   // so they see it too
        }
    }

    // ---------------------------------------------------------------- sending

    void SendTo(NetLink l, byte[] pkt)
    {
        if (l.Endpoint == null) return;
        // a guest pair without a direct path goes through the host (dst says who for)
        SendRaw(pkt, l.Endpoint, _isHost ? l.RelayTag : "");
    }

    // Testing (COOP_NETSIM): a bad connection on the way out.
    public int SimLagMs, SimJitterMs, SimLossPercent;
    readonly List<(long Due, byte[] Pkt, IPEndPoint To)> _simQueue = new();

    void FlushSim()
    {
        if (_simQueue.Count == 0 || _udp == null) return;
        long now = _clock.ElapsedMilliseconds;
        for (int i = 0; i < _simQueue.Count; i++)
        {
            if (_simQueue[i].Due > now) continue;
            try { _udp.Send(_simQueue[i].Pkt, _simQueue[i].Pkt.Length, _simQueue[i].To); } catch { }
            _simQueue.RemoveAt(i--);
        }
    }

    void SendRaw(byte[] pkt, IPEndPoint to, string relayTag)
    {
        if (_udp == null) return;
        if (SimLossPercent > 0 && Random.Shared.Next(100) < SimLossPercent) return;
        if (relayTag.Length > 0)
        {
            // through the relay, to one joiner: "RT" + tag + 0 + packet
            var tag = System.Text.Encoding.ASCII.GetBytes(relayTag);
            var wrapped = new byte[3 + tag.Length + pkt.Length];
            wrapped[0] = (byte)'R'; wrapped[1] = (byte)'T';
            tag.CopyTo(wrapped, 2);
            pkt.CopyTo(wrapped, 3 + tag.Length);
            pkt = wrapped;
        }
        if (SimLagMs > 0 || SimJitterMs > 0)
        {
            _simQueue.Add((_clock.ElapsedMilliseconds + SimLagMs / 2 + Random.Shared.Next(SimJitterMs + 1), pkt, to));   // (each way: half the round trip)
            return;
        }
        try { _udp.Send(pkt, pkt.Length, to); } catch (Exception e) { LastError = e.Message; }
    }

    internal uint NowMs() => (uint)_clock.ElapsedMilliseconds | 1;
}

// One other player: their inputs, snapshots, room stream and the reliable channel
// both ways, and the address that reaches them.
public sealed class NetLink
{
    public readonly int Number;   // their player number (0 = host)
    public string Name = "";
    public byte Character, Palette;
    public uint Available;
    public bool Connected { get; private set; }
    public int RttMs { get; private set; }
    public long FramesSinceHeard => _frame - _lastHeard;

    // how to reach them
    internal IPEndPoint? Endpoint;
    internal string RelayTag = "";          // host: a joiner that came through the relay
    internal bool Direct;                   // guest pair: a packet arrived straight from them
    internal List<IPEndPoint>? Candidates;  // guest pair: addresses to knock on
    internal long KnockSince;
    internal string PublicAddr = "", LanAddr = "";
    internal uint InstallId, Session;
    public bool ViaRelay => RelayTag.Length > 0;
    public bool ViaHost => Number != 0 && !Direct;

    long _frame, _lastHeard;

    // their inputs by their frame number
    readonly SortedDictionary<uint, ushort> _inputs = new();
    public uint LatestInputFrame { get; private set; }

    public byte[]? Snapshot { get; private set; }
    public uint SnapshotFrame { get; private set; }
    public long SnapshotLocalFrame { get; private set; }

    // room stream: one compressed datagram per frame from the room authority;
    // unreliable, only the newest one matters
    public byte[]? Room { get; private set; }
    public uint RoomFrame { get; private set; }
    public void ClearRoom() { Room = null; RoomFrame = 0; }

    // reliable channel
    uint _nextReliable = 1, _peerAcked;
    internal uint LastReliableIn;
    readonly List<(uint Seq, byte[] Body)> _unacked = new();
    readonly Queue<byte[]> _reliableIn = new();
    internal uint LastSendTime; internal long LastSendTimeAt;

    public NetLink(int number) => Number = number;

    internal void Connect(long frame) { Connected = true; _frame = frame; _lastHeard = frame; }
    internal void Heard(long frame) { _frame = frame; _lastHeard = frame; }
    internal void Lost() { Connected = false; Snapshot = null; }

    // Everything that belongs to one connection (a reconnect starts over).
    internal void Reset()
    {
        Snapshot = null; SnapshotFrame = 0; Room = null; RoomFrame = 0;
        _inputs.Clear(); _unacked.Clear(); _reliableIn.Clear();
        LatestInputFrame = 0;
        _nextReliable = 1; _peerAcked = 0; LastReliableIn = 0;
        LastSendTime = 0;
    }

    internal void NoteDirect(IPEndPoint from, bool direct)
    {
        if (!direct || Number == 0 || Direct) return;
        Endpoint = from;
        Direct = true;
    }

    public void SendReliable(byte[] body) => _unacked.Add((_nextReliable++, body));
    public bool TryReceiveReliable(out byte[] body) => _reliableIn.TryDequeue(out body!);

    // Their input for their frame `f`, or their nearest earlier one.
    public ushort Input(uint f)
    {
        ushort last = 0;
        foreach (var (k, v) in _inputs) { if (k > f) break; last = v; }
        return last;
    }

    internal void PruneInputs()
    {
        while (_inputs.Count > 120) { foreach (var k in _inputs.Keys) { _inputs.Remove(k); break; } }
    }

    // (no more than ~1.1 KB of them per packet: a big one, like a save, goes in pieces,
    // and oversized UDP packets get dropped on the way)
    internal void WriteReliables(BinaryWriter w)
    {
        int count = 0, bytes = 0;
        while (count < _unacked.Count && count < 16 && (count == 0 || bytes + _unacked[count].Body.Length <= 1100))
            bytes += _unacked[count++].Body.Length;
        w.Write((byte)count);
        for (int i = 0; i < count; i++) { w.Write(_unacked[i].Seq); w.Write((ushort)_unacked[i].Body.Length); w.Write(_unacked[i].Body); }
    }

    internal void ReceiveRoom(uint f, byte[] payload)
    {
        if (f > RoomFrame || f + 600 < RoomFrame) { RoomFrame = f; Room = payload; }
    }

    internal void ReadFrame(BinaryReader r, long now, uint nowMs)
    {
        _frame = now; _lastHeard = now;
        uint peerFrame = r.ReadUInt32();
        uint sendTime = r.ReadUInt32();
        uint echo = r.ReadUInt32();
        uint held = r.ReadUInt32();
        uint ack = r.ReadUInt32();
        LastSendTime = sendTime; LastSendTimeAt = now;
        if (echo != 0) RttMs = (int)Math.Max(0, nowMs - echo - held * 1000 / 60);

        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            uint f = r.ReadUInt32(); ushort p = r.ReadUInt16();
            _inputs[f] = p;
            if (f > LatestInputFrame) LatestInputFrame = f;
        }
        int snapLen = r.ReadUInt16();
        if (snapLen > 0)
        {
            var snap = r.ReadBytes(snapLen);
            if (peerFrame >= SnapshotFrame) { Snapshot = snap; SnapshotFrame = peerFrame; SnapshotLocalFrame = now; }
        }
        int rel = r.ReadByte();
        for (int i = 0; i < rel; i++)
        {
            uint seq = r.ReadUInt32();
            var body = r.ReadBytes(r.ReadUInt16());
            if (seq == LastReliableIn + 1) { _reliableIn.Enqueue(body); LastReliableIn = seq; }
        }
        if (ack > _peerAcked) { _peerAcked = ack; _unacked.RemoveAll(u => u.Seq <= ack); }
    }

    internal void Tick(long frame) => _frame = frame;
}

static class NetPacketExtensions
{
    // a Packet()'s bytes so far
    public static byte[] ToArray(this BinaryWriter w) => ((MemoryStream)w.BaseStream).ToArray();
}
