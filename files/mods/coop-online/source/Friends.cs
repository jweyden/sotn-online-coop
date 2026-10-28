using System;
using System.Collections.Generic;
using System.Linq;
using RecompOne.Runtime.Memory;

// Everyone else in the session. Each friend has a link (Net.cs), their decoded
// snapshot, and a stand-in body in our game: one of CoopCore's remote players (with
// its own VRAM bank), driven by their streamed input with rollback. F is the friend
// whose body R is, so code that runs inside a remote pass (or WithFriend) just uses F.
public partial class OnlineCoop
{
    sealed class Friend
    {
        public readonly NetLink Link;
        public readonly Remote Body;
        public Snap Peer;
        public bool Valid;            // a fresh snapshot of theirs
        public int AbsentFrames;      // their body is here but they aren't (left the room)
        // rollback: per local frame of their body, which of their frames we fed and the
        // input we used, plus their body's state just before that frame ran
        public uint RemoteCursor;
        public ushort RemotePrev;
        public uint BodyFrame;
        public readonly (uint Remote, ushort Input, ushort Prev)[] Fed = new (uint, ushort, ushort)[64];
        public readonly P2Checkpoint?[] Checkpoints = new P2Checkpoint?[16];
        public ushort ResimInput, ResimPrev;
        public ushort Pressed, Prev;  // their buttons this frame, for objects they drive (lifts)
        public long JoinedAt = -1000;
        public long ArrivedAt = -1000;   // when they last appeared in our room (PvP spawn protection)
        public long FamiliarAppliedFrame = -1;
        public bool Vanished;            // their body went away mid-teleport (FriendFx)
        public int IntroGap;
        // PvP: a hit their game says they took (shown on their body after its hit
        // detection), and which way they ended up facing
        public (byte Flags, ushort Element, short Damage, short Dx, short Dy, int VelX, ushort Facing, short OffX, byte VictimFacing, long At)? PvpHit;
        public int PvpFacingWant = -1;

        public Friend(NetLink link, Remote body) { Link = link; Body = body; }
        public string Name => Link.Name;
        public int Number => Link.Number;
    }

    readonly List<Friend> _friends = new();
    static readonly Friend NoFriend = new(new NetLink(-1), new Remote(0));
    Friend F => FriendOf(R);
    Friend FriendOf(Remote r) => _friends.Find(f => f.Body == r) ?? NoFriend;
    Friend? FriendOf(NetLink? l) => l == null ? null : _friends.Find(f => f.Link == l);
    IEnumerable<Friend> ValidFriends => _friends.Where(f => f.Valid);

    // Run `body` with that friend's body as R (outside the passes: P1 live).
    void WithFriend(Friend f, Action body) => WithRemote(f.Body, body);
    void ForEachFriend(Action<Friend> body) { foreach (var f in _friends.ToList()) WithFriend(f, () => body(f)); }

    // Once a frame before anything else (P1 live, no pass running): a friend for every
    // link, a body for every friend; bodies of friends who left are dropped, then let go.
    void SyncFriends()
    {
        foreach (var l in _net.Links)
        {
            if (!l.Connected || FriendOf(l) != null) continue;
            var body = _remotes.Find(r => !r.Active && _friends.All(f => f.Body != r));
            if (body == null)
            {
                int n = 1;
                while (_remotes.Exists(r => r.Number == n)) n++;
                if (n >= RecompOne.Runtime.Hle.VramShadow.MaxBanks) continue;   // no bank left for them
                body = new Remote(n);
                _remotes.Add(body);
            }
            _friends.Add(new Friend(l, body));
            Console.WriteLine($"[coop-online] {l.Name} (player {l.Number + 1}) uses body {body.Number}");
        }
        foreach (var f in _friends.ToList())
        {
            bool gone = !f.Link.Connected || !_net.Links.Contains(f.Link);
            if (gone)
            {
                if (f.Body.Active) { f.Body.WantDrop = true; continue; }
                _friends.Remove(f);
                if (_remotes.Count > 1) _remotes.Remove(f.Body);
                continue;
            }
            // who they play (from the roster; their snapshot says too)
            var ch = (CoopCharacter)(f.Valid ? f.Peer.Character : f.Link.Character);
            if (CoopCharacters.Valid((int)ch) && ch != f.Body.Character)
            {
                if (f.Body.Active) f.Body.WantDrop = true;   // rejoins as the new one
                else f.Body.Character = ch;
            }
            f.Body.Palette = f.Valid ? f.Peer.Palette : f.Link.Palette;
        }
        if (!_remotes.Contains(R)) R = _remotes[0];
    }
}
