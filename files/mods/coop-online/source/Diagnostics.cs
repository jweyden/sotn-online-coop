using System;
using System.IO;
using System.Linq;
using System.Threading;

// Crash and freeze clues. An error thrown where the game can't recover from it takes
// the whole process down without a word, so:
//  - errors from the mod are logged as they're thrown (even ones caught later), and
//    an unhandled one is written to coop_crash.txt before the process goes;
//  - the last few hundred lines of the log go into coop_crash.txt with it, and what
//    the game was doing (players can't send a log: the terminal's gone);
//  - a watchdog notices when the game stops running frames and logs where the game
//    code was (its last return address), once per stall.
public partial class OnlineCoop
{
    Thread? _watchdog;
    volatile bool _watching;
    int _thrownLogged;

    void StartDiagnostics()
    {
        if (Console.Out is not RecentLines) Console.SetOut(_recent = new RecentLines(Console.Out));
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.FirstChanceException += OnThrown;
        _watching = true;
        _watchdog = new Thread(Watch) { IsBackground = true, Name = "coop watchdog" };
        _watchdog.Start();
    }

    void StopDiagnostics()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        AppDomain.CurrentDomain.FirstChanceException -= OnThrown;
        _watching = false;
    }

    static void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        string text = $"[coop-online] CRASH {DateTime.Now:HH:mm:ss}: {e.ExceptionObject}";
        Console.WriteLine(text);
        string state = "";
        try { state = (I as OnlineCoop)?.CrashState() ?? ""; } catch (Exception x) { state = $"(state: {x.Message})"; }
        string recent = _recent?.Dump() ?? "";
        try { File.AppendAllText("coop_crash.txt", text + Environment.NewLine + state + Environment.NewLine +
                                 "---- the log before it:" + Environment.NewLine + recent + "---- end" + Environment.NewLine + Environment.NewLine); } catch { }
    }

    // What the game was doing, for the crash report.
    string CrashState()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return "";
        return $"state: game state {m.ReadU32(GameStateAddr)}, step {m.ReadU32(GameStepAddr)}, engine step {m.ReadU32(GameEngineStepAddr)}, " +
               $"stage {m.ReadU32(StageIdAddr):X} room {(short)m.ReadU32(TilemapLeft)},{(short)m.ReadU32(TilemapTop)}, " +
               $"us {CoopCharacters.Name(_p1Char)} (playable {m.ReadU32(PlayableCharacterAddr)}), death {_rs}, in a friend's pass {_inP2}, " +
               $"player overlay init {m.ReadU32(0x8013C004u):X8}, active overlays [{string.Join(" ", RecompOne.Runtime.Dispatch.Dispatcher.ActiveNames)}], " +
               $"friends [{string.Join(", ", _friends.Select(f => $"{f.Name} {CoopCharacters.Name((CoopCharacter)f.Peer.Character)}{(f.Body.Active ? " here" : "")} overlays [{string.Join(" ", f.Body.Overlays)}]"))}]";
    }

    // Console output, passed through, with the last lines kept.
    static RecentLines? _recent;
    sealed class RecentLines : TextWriter
    {
        readonly TextWriter _out;
        readonly string[] _lines = new string[400];
        int _next;
        readonly System.Text.StringBuilder _line = new();
        public RecentLines(TextWriter o) => _out = o;
        public override System.Text.Encoding Encoding => _out.Encoding;
        public override void Write(char ch)
        {
            _out.Write(ch);
            lock (_lines)
            {
                if (ch == '\n') { _lines[_next++ % _lines.Length] = _line.ToString(); _line.Clear(); }
                else if (ch != '\r') _line.Append(ch);
            }
        }
        public override void Write(string? s) { if (s != null) foreach (char ch in s) Write(ch); }
        public override void WriteLine(string? s) { Write(s); Write('\n'); }
        public override void Flush() => _out.Flush();
        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            lock (_lines)
                for (int i = Math.Max(0, _next - _lines.Length); i < _next; i++) sb.Append(_lines[i % _lines.Length]).Append('\n');
            return sb.ToString();
        }
    }

    void OnThrown(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        try
        {
            if (_thrownLogged >= 30) return;
            string trace = e.Exception.StackTrace ?? Environment.StackTrace;
            if (!trace.Contains("OnlineCoop") && !trace.Contains("CoopCore") && !trace.Contains("NetSession")) return;
            if (e.Exception is System.Net.Sockets.SocketException) return;   // (network hiccups are normal)
            _thrownLogged++;
            Console.WriteLine($"[coop-online] error: {e.Exception.GetType().Name}: {e.Exception.Message}\n{trace}");
        }
        catch { }
    }

    void Watch()
    {
        long last = -1; int still = 0; bool reported = false;
        while (_watching)
        {
            Thread.Sleep(1000);
            long now = _vsyncs;
            if (now != last) { last = now; still = 0; reported = false; continue; }
            if (++still < 4 || reported || now == 0) continue;
            reported = true;
            var c = RecompOne.Runtime.Runtime.Cpu;
            Console.WriteLine($"[coop-online] FROZEN: no game frame for {still} s (frame {now}, " +
                              $"game code last returning to {(c != null ? $"0x{c.RA:X8}" : "?")}, {_net.Status}, teleport {_tp}, follower {_follower})");
        }
    }
}
