using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Sotn;

// Things a friend's own game plays on their player that our copy of them wouldn't
// do by itself, because they don't come from button presses:
//  - their teleport cast: the Hellfire pose and pillar, then they vanish (and, if
//    they reappear in this room, the arrival beam);
//  - their death: our copy only gets their HP of 0, and the game plays the death
//    only when a hit takes Alucard to 0, so put it into the death step ourselves;
//  - their level-ups: our copy never levels up by itself (LevelUp.cs), so their game
//    says when, and we play the "LEVEL UP" animation on it then.
// The cast, the death step and the guard are Alucard's; the level-up text is anyone's.
// Called from AfterP2Update (their body live in slot 0).
public partial class OnlineCoop
{
    const ushort StepPlayerKill = 16;   // Player_Kill

    // online, a friend who died is gone until they continue (their game decides)
    protected override bool AutoRevive => false;

    sealed class FriendFxState
    {
        public bool CastWant, Casting, DeathShown, GuardWant, LevelUpWant, RespawnFxWant;
        public long CastAt, VanishedAt, CastWantAt = -1000, LevelUpAt = -1000, GuardAt = -1000, RespawnFxAt, RespawnFxUntil;
        public int HealWant;
        public long WingsAt = -1;   // PSP Maria's respawn wings on our copy of her (MariaWings.cs)
    }
    readonly Dictionary<int, FriendFxState> _fx = new();
    FriendFxState Fx(Friend f) => _fx.TryGetValue(f.Number, out var s) ? s : _fx[f.Number] = new FriendFxState();

    void OnFriendCast() { if (FromFriend is { } f) { Fx(f).CastWant = true; Fx(f).CastWantAt = _vsyncs; } }

    // their defence turned our attack away: the game's guard flash on their copy
    void OnFriendGuard() { if (FromFriend is { } f) { Fx(f).GuardWant = true; Fx(f).GuardAt = _vsyncs; } }
    const uint BpBlinkWhite = 44, BpGuard = 47;

    int _myLevel = -1;
    void OnFriendLevelUp() { if (FromFriend is { } f) { Fx(f).LevelUpWant = true; Fx(f).LevelUpAt = _vsyncs; } }

    // Once a frame (P1 live): did we just level up? (A save loading isn't a level-up.)
    void TickOwnLevel(IMemory m)
    {
        if (!Game.InGame || Game.IsLoading || _net.Status != NetSession.State.Connected) { _myLevel = -1; return; }
        int level = (int)m.ReadU32(LevelAddr);
        if (_myLevel > 0 && level > _myLevel && level - _myLevel <= 3) Send(Msg.LevelUp, _ => { });
        _myLevel = level;
    }

    void TickFriendFx(CpuContext c, IMemory m)
    {
        var f = F;
        var fx = Fx(f);
        bool alucard = CoopCharacters.IsAlucard(R.Character);
        ushort step = m.ReadU16(EntitiesAddr + EStep);
        // (only as it happens: not when we next see them, if they were elsewhere)
        if (_vsyncs - fx.LevelUpAt > 30) fx.LevelUpWant = false;
        if (_vsyncs - fx.CastWantAt > 30) fx.CastWant = false;

        // death
        bool dead = (f.Peer.Flags & 4) != 0;
        if (!dead && fx.DeathShown)
        {
            // back (revived, or respawned): a fresh copy of them, not one stuck dying (Respawn.cs)
            fx.DeathShown = false;
            f.Body.WantDrop = true;
            return;
        }
        if (dead && !fx.DeathShown)
        {
            fx.DeathShown = true; fx.Casting = false; fx.CastWant = false;
            if (alucard && step != StepPlayerKill)
            {
                m.WriteU16(EntitiesAddr + EStep, StepPlayerKill);
                m.WriteU16(EntitiesAddr + EStepS, 0);
                R.Discontinuity = true;
            }
            return;
        }
        if (dead)
        {
            // their own death step, as their game plays it (any character): our copy
            // took no killing hit, so left alone it showed falls and stumbles
            if (f.Peer.Step != step && f.Peer.Step != 0)
            {
                m.WriteU16(EntitiesAddr + EStep, f.Peer.Step);
                m.WriteU16(EntitiesAddr + EStepS, 0);
                R.Discontinuity = true;
            }
            return;
        }

        TickFriendRespawnFx(c, m, f, fx);

        // guard (as EntityAlucard does it: the effect and two white blinks)
        if (fx.GuardWant)
        {
            fx.GuardWant = false;
            if (alucard && _vsyncs - fx.GuardAt <= 30)
            {
                Call(c, m, FnCreateEntFactory, EntitiesAddr, BpGuard, 0);
                Call(c, m, FnCreateEntFactory, EntitiesAddr, BpBlinkWhite | (0x43u << 16), 0);
                Call(c, m, FnCreateEntFactory, EntitiesAddr, BpBlinkWhite | (0x51u << 16), 0);
            }
        }

        // level-up (spawned on their body: it follows them, and doesn't freeze our game)
        if (fx.LevelUpWant)
        {
            fx.LevelUpWant = false;
            Call(c, m, FnCreateEntFactory, EntitiesAddr, BpLevelUpText, 0);
        }

        // teleport cast
        if (fx.CastWant)
        {
            fx.CastWant = false;
            if (alucard && R.BeamState == 0 && step != StepPlayerHit && step != StepSpellHellfire)
            {
                m.WriteU16(EntitiesAddr + EStep, StepSpellHellfire);
                m.WriteU16(EntitiesAddr + EStepS, 0);
                fx.Casting = true; fx.CastAt = _vsyncs;
                R.Discontinuity = true;
                return;
            }
        }
        if (fx.Casting)
        {
            uint beam = 0;
            for (int i = 4; i < PlayerEntityCount && beam == 0; i++)
                if (m.ReadU16(EntityAddr(i) + EEntityId) == EHellfire) beam = EntityAddr(i);
            if (step != StepSpellHellfire || (beam == 0 && _vsyncs - fx.CastAt > 10)) { fx.Casting = false; return; }
            if (beam != 0 && m.ReadU16(beam + EStep) >= HfhPlayerDisappear)
            {
                // gone, like in their game: no fireballs, hidden until they land
                Call(c, m, FnDestroyEntity, beam);
                Call(c, m, FnStand, 0);
                m.WriteU16(EntitiesAddr + EPalette, PalPlayerHidden);
                fx.Casting = false; f.Vanished = true; fx.VanishedAt = _vsyncs;
                R.Discontinuity = true;
            }
            return;
        }
        // they vanished but stayed in this room: they come down in the beam (below);
        // if that never comes, show them again anyway
        if (f.Vanished && _vsyncs - fx.VanishedAt > 180)
        {
            m.WriteU16(EntitiesAddr + EPalette, PalAlucard);
            f.Vanished = false;
        }
    }
}
