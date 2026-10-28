using System;
using System.Linq;
using System.IO;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

// Starting a new game together. Each player picks New Game on the title screen as
// usual; then:
//  - the opening movie and the Richter prologue are skipped: the game goes straight
//    to the castle entrance (what the end of the prologue does itself);
//  - the "Now Loading" screen holds until the friend's game has loaded the entrance
//    too, then both start on the same frame (the host says go, and waits half the
//    ping itself), so the two Alucards run in side by side (Intro.cs).
// Start skips the wait and goes in alone. Loading a save is unchanged.
public partial class OnlineCoop
{
    public bool SkipPrologue = true;
    const uint GameStateAddr = 0x8003C734u, GameStepAddr = 0x80073060u;   // g_GameState, g_GameStep
    const uint GameState_NowLoading = 4, LoadStep_Last = 16;             // Game_NowLoading, Play_16
    const uint GameState_Play = 2, SpawnPointAddr = 0x8006C374u, RichterModeSpawn = 0x11;   // Game_Play, D_8006C374
    const uint PrologueComplete = 52;                                    // g_CastleFlags[PROLOGUE_COMPLETE]
    bool _insideLogged;
    System.Collections.Generic.HashSet<int>? _startedWith;   // host: who was in when the new game started
    const uint Stage_Prologue = 0x1F, Stage_Entrance = 0x41;              // STAGE_ST0, STAGE_NO3 (first visit)

    enum Gate { None, Waiting, Released }
    Gate _gate;
    long _goAt = -1;          // (host) frame to release on, once the friend has been told
    volatile string _gateText = "";
    bool AtGate => _gate == Gate.Waiting;   // snapshot flag 128

    const uint FnEndVideo = 0x800E4970u;   // func_800E4970: what the movie does when it ends

    // A new game first plays the opening movie (unskippable until you've beaten the
    // game): end it before it starts, straight to loading.
    [Replace("dra", "HandleVideoPlayback")]
    public static void HandleVideoPlaybackHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is OnlineCoop self && self.SkipPrologue && m.ReadU32(StageIdAddr) == Stage_Prologue && m.ReadU32(GameStepAddr) == 0)
        {
            Console.WriteLine("[coop-online] new game: skipping the opening movie");
            Call(c, m, FnEndVideo);
            return;
        }
        orig(c, m);
    }

    [Replace("dra", "HandleNowLoading")]
    public static void HandleNowLoadingHook(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self) { orig(c, m); return; }
        if (self._ownCharacterPending) self.ApplyOwnCharacterNow(m);

        // a new game heads for the prologue: go to the castle entrance instead
        if (self.SkipPrologue && m.ReadU32(StageIdAddr) == Stage_Prologue)
        {
            m.WriteU32(StageIdAddr, Stage_Entrance);
            Console.WriteLine("[coop-online] new game: skipping the prologue");
        }

        bool last = m.ReadU32(GameStepAddr) == LoadStep_Last && m.ReadU32(StageIdAddr) == Stage_Entrance;
        uint stateBefore = m.ReadU32(GameStateAddr);
        orig(c, m);
        // Online, an Alucard starts where Richter mode does (inside the entrance, with the
        // prologue counted as done, so no door push either): everyone in the same room.
        if (self.SkipEntranceRun && stateBefore != GameState_Play && m.ReadU32(GameStateAddr) == GameState_Play &&
            m.ReadU32(StageIdAddr) == Stage_Entrance && m.ReadU32(CoopCharacters.PlayableCharacterAddr) == 0)
        {
            if (!self._insideLogged) Console.WriteLine("[coop-online] new game: starting inside the entrance, with everyone");
            self._insideLogged = true;
            m.WriteU32(SpawnPointAddr, RichterModeSpawn);
            m.WriteU8(CastleFlagsAddr + PrologueComplete, 1);
        }
        if (!last) return;
        if (self.HoldAtGate(m))
        {
            // stay on the last loading step (its "Now Loading" animation keeps going)
            m.WriteU32(GameStateAddr, GameState_NowLoading);
            m.WriteU32(GameStepAddr, LoadStep_Last);
        }
    }

    // Each frame on the last loading step of a new game: true = keep waiting.
    bool HoldAtGate(IMemory m)
    {
        var st = _net.Status;
        if (_gate == Gate.Released) { _gate = Gate.None; }
        if (st == NetSession.State.Off) { _gate = Gate.None; _gateText = ""; return false; }   // not playing online
        if (!_startingTogether) return false;   // (a save loading into the entrance: no one to wait for)
        if (_gate == Gate.None)
        {
            _gate = Gate.Waiting; _goAt = -1;
            Console.WriteLine("[coop-online] new game: loaded, waiting for the friend");
        }
        if ((m.ReadU16(PadsAddr + 4) & PadStart) != 0) { Release("going in alone (Start)", together: false); return false; }

        string who = _net.PeerName.Length > 0 ? _net.PeerName : "your friends";
        if (st != NetSession.State.Connected) { _gateText = "Waiting for a friend to connect...\n(Start: go in alone)"; return true; }
        // everyone in the session is loaded and waiting too
        // (only those who were in when the host started: anyone joining since gets the
        // game as it is once it's going, TitleFlow.cs)
        var waitingFor = _net.Peers.Where(l => _startedWith == null || _startedWith.Contains(l.Number))
            .Where(l => !(FriendOf(l) is { Valid: true } f && (f.Peer.Flags & 128) != 0)).Select(l => l.Name).ToList();
        bool peerHere = waitingFor.Count == 0;
        _gateText = peerHere ? "Starting..." : $"Waiting for {string.Join(", ", waitingFor)}...\nThey pick New Game too.  (Start: go in alone)";

        if (_net.IsHost)
        {
            if (_goAt < 0 && peerHere)
            {
                Send(Msg.NewGameGo, _ => { });
                _goAt = _vsyncs + Math.Clamp(_net.RttMs * 60 / 2000, 0, 30);   // half the ping: they get it then
            }
            if (_goAt >= 0 && _vsyncs >= _goAt) { Release($"starting with {who}", together: true); return false; }
            return true;
        }
        // (reliable messages are normally read in game; the go comes while loading)
        while (_net.TryReceiveReliable(out var from, out var body))
        {
            if (from.Number == 0 && body.Length >= 2 && body[0] == 0x50 && body[1] == (byte)Msg.NewGameGo) _goNow = true;
            else _heldMessages.Enqueue((from, body));   // the rest once we're in
        }
        if (_goNow) { _goNow = false; Release($"starting with {who}", together: true); return false; }
        return true;
    }

    readonly System.Collections.Generic.Queue<(NetLink From, byte[] Body)> _heldMessages = new();
    bool _goNow;
    void OnNewGameGo() { if (_gate == Gate.Waiting) _goNow = true; }

    // We started a new game together with the friend, and they're still in the castle
    // entrance too: only then does the intro push their copy (Intro.cs). (Going in
    // alone, e.g. starting over after dying while the friend plays on, it mustn't:
    // their Alucard isn't in any intro.)
    bool _newGameStarted;
    // Every online game skips the entrance's run (Intro.cs): it starts on its first screen.
    protected override bool SkipEntranceRun => _net.Status != NetSession.State.Off;
    protected override bool OwnTimeStops => true;
    protected override bool IntroAllowed => _newGameStarted && F.Valid && F.Peer.Stage == Stage_Entrance;

    // A new game everyone starts at once (the host's Start, or its HostNewGame), until
    // it's released.
    bool _startingTogether;

    void Release(string why, bool together)
    {
        _startingTogether = false;
        _insideLogged = false;
        _newGameStarted = together;
        _gate = Gate.Released; _goAt = -1; _gateText = "";
        Console.WriteLine($"[coop-online] new game: {why}");
    }
}
