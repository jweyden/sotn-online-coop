using Sotn;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Live menu: while your menu is open the room keeps going: the spawner and every
// room object update as usual (following the friend's room stream if they run it),
// so nothing has to catch up when you come back. Your Alucard stands still and the
// objects see no buttons (menu presses aren't for them).
//  - the setting (experimental, off by default): everywhere, and nothing can hit you.
//  - boss fights (locked rooms) while online: always, and the fight goes on: hit
//    detection runs too, so the boss can hit you, and you take the hit as you close
//    the menu. No pausing your way out of a co-op boss fight.
public partial class OnlineCoop
{
    public bool LiveMenu;
    public bool BossMenuLive = true;
    const uint ApiUpdateRoomPosition = 0x8003C77Cu;
    const int MenuStepOpen = 0x10;

    bool BossMenu(IMemory m) => BossMenuLive && _net.Status == NetSession.State.Connected && RoomLocked(m);

    // The room keeps running in our menu right now (snapshot flag 64: the friend's
    // game mustn't take the room over just because we're in the menu).
    bool MenuLive(IMemory m) => m.ReadU32(GameEngineStepAddr) == EngineMenu && (LiveMenu || BossMenu(m));

    [PostHook("dra", "MenuHandle")]
    public static void PostMenuHandleLive(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || !Game.InGame) return;
        bool boss = self.BossMenu(m);
        if (!self.LiveMenu && !boss) return;
        if (m.ReadU32(GameEngineStepAddr) != EngineMenu || (int)m.ReadU32(MenuStepAddr) < MenuStepOpen) return;
        uint roomPos = m.ReadU32(ApiUpdateRoomPosition), update = m.ReadU32(ApiUpdate), hits = m.ReadU32(ApiHitDetection);
        if (roomPos == 0 || update == 0) return;

        var pads = new byte[16];
        for (uint i = 0; i < 16; i++) { pads[i] = m.ReadU8(PadsAddr + i); m.WriteU8(PadsAddr + i, 0); }
        try
        {
            // (Alucard's own update doesn't run in the menu: a hit waits in his hit
            // flags until it does, as the menu closes)
            if (boss && hits != 0) Call(c, m, hits);
            Call(c, m, roomPos);
            Call(c, m, update);
        }
        finally { for (uint i = 0; i < 16; i++) m.WriteU8(PadsAddr + i, pads[i]); }
    }
}
