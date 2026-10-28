using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// The friends on the map screen: the game's own blinking map cursor, drawn again at
// each friend's spot (their room + their position in it), tinted their player colour.
public partial class OnlineCoop
{
    const uint FnDrawMapCursor = 0x800F2658u;
    bool _drawingFriendCursor;

    [PostHook("dra", "DrawMapCursor")]
    public static void PostDrawMapCursor(CpuContext c, IMemory m)
    {
        if (I is not OnlineCoop self || self._drawingFriendCursor) return;
        foreach (var f in self.ValidFriends) self.DrawFriendCursor(c, m, f);
    }

    void DrawFriendCursor(CpuContext c, IMemory m, Friend f)
    {
        if (!f.Peer.InGame) return;
        // only on the map of the castle we're both in
        if (((f.Peer.Stage ^ (ushort)m.ReadU32(StageIdAddr)) & InvertedCastle) != 0) return;

        uint left = m.ReadU32(TilemapLeft), top = m.ReadU32(TilemapTop);
        uint px = m.ReadU32(PlayerXAddr), py = m.ReadU32(PlayerYAddr);
        uint before = m.ReadU32(GpuUsageGt4);
        m.WriteU32(TilemapLeft, (uint)f.Peer.RoomLeft); m.WriteU32(TilemapTop, (uint)f.Peer.RoomTop);
        m.WriteU32(PlayerXAddr, (uint)f.Peer.WorldX); m.WriteU32(PlayerYAddr, (uint)f.Peer.WorldY);
        _drawingFriendCursor = true;
        try { Call(c, m, FnDrawMapCursor); }
        finally
        {
            _drawingFriendCursor = false;
            m.WriteU32(TilemapLeft, left); m.WriteU32(TilemapTop, top);
            m.WriteU32(PlayerXAddr, px); m.WriteU32(PlayerYAddr, py);
        }
        if (m.ReadU32(GpuUsageGt4) != before + 1) return;
        // their colour instead of white: POLY_GT4 vertex colours at 0x04, 0x10, 0x1C, 0x28 (r, g, b),
        // scaled by the cursor's blink
        var (cr, cg, cb) = PlayerColor(f.Number);
        uint poly = m.ReadU32(CurrentBufferAddr) + PolyGt4Offset + before * PolyGt4Size;
        foreach (uint v in new uint[] { 0x04, 0x10, 0x1C, 0x28 })
        {
            int lum = m.ReadU8(poly + v);
            m.WriteU8(poly + v, (byte)(lum * cr / 255));
            m.WriteU8(poly + v + 1, (byte)(lum * cg / 255));
            m.WriteU8(poly + v + 2, (byte)(lum * cb / 255));
        }
    }
}
