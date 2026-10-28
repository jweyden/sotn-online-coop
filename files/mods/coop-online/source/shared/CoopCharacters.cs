using System;
using RecompOne.Runtime.Memory;

// The six player characters, shared by the co-op mods and the Maria mod's select
// screen (the online mod's source/characters), and how a save remembers the choice.
//
// Two "families": Alucard's (Alucard mode: dra's own player, F_GAME.BIN, ARC_F.BIN
// at 0x8013C000) and Richter's (Richter mode: a player overlay at 0x8013C000,
// F_GAME2.BIN). PSP Alucard is Alucard with the PSP (2007 recast) voice. The others
// are Richter-mode overlays: PS1 Richter, the Saturn's long-coat Richter, and the
// two Marias (build-maria.sh / build-mariasat.sh build them).
//
// The save keeps the choice in saveName[10..11], padding the game never writes
// (names are 8 characters and a terminator) but saves with g_Status:
//   [10] = TagV2, [11] = character | palette << 4.
// TagV1 was the first select's format ([11] = 0 Richter, 1 PSP Maria, 2 Saturn
// Maria, 3 long-coat Richter; Alucard = no tag), still read.
public enum CoopCharacter : byte
{
    Alucard = 0,
    AlucardPsp = 1,
    Richter = 2,
    RichterCoat = 3,
    MariaPsp = 4,
    MariaSaturn = 5,
}

public static class CoopCharacters
{
    public const int Count = 6, Palettes = 3;
    public const uint SaveNameAddr = 0x80097B90u;           // g_Status.saveName[12]
    public const uint PlayableCharacterAddr = 0x8003C9A0u;  // g_PlayableCharacter
    public const byte TagV1 = 0xC5, TagV2 = 0xC6;

    public static readonly string[] Names =
        { "Alucard", "Alucard (PSP)", "Richter", "Richter (long coat)", "Maria (PSP)", "Maria (Saturn)" };

    public static string Name(CoopCharacter c) => (int)c < Count ? Names[(int)c] : "?";
    public static bool IsAlucard(CoopCharacter c) => c is CoopCharacter.Alucard or CoopCharacter.AlucardPsp;
    // g_PlayableCharacter for it: 0 Alucard mode, 1 Richter mode
    public static uint Playable(CoopCharacter c) => IsAlucard(c) ? 0u : 1u;
    public static bool Valid(int c) => c >= 0 && c < Count;

    // The recompiled player overlay that runs it (null: Alucard, in dra).
    public static string? Overlay(CoopCharacter c) => c switch
    {
        CoopCharacter.Richter => "ric",
        CoopCharacter.RichterCoat => "richter2",
        CoopCharacter.MariaPsp => "maria",
        CoopCharacter.MariaSaturn => "mariasat",
        _ => null,
    };

    public static (CoopCharacter Character, int Palette) FromSave(IMemory m)
    {
        byte tag = m.ReadU8(SaveNameAddr + 10), v = m.ReadU8(SaveNameAddr + 11);
        bool richterMode = m.ReadU32(PlayableCharacterAddr) != 0;
        if (tag == TagV2 && Valid(v & 0xF)) return ((CoopCharacter)(v & 0xF), Math.Min(v >> 4, Palettes - 1));
        if (tag == TagV1)
            return (v switch
            {
                1 => CoopCharacter.MariaPsp,
                2 => CoopCharacter.MariaSaturn,
                3 => CoopCharacter.RichterCoat,
                _ => richterMode ? CoopCharacter.Richter : CoopCharacter.Alucard,
            }, 0);
        if (!richterMode) return (CoopCharacter.Alucard, 0);
        if (IsName(m, "satmaria")) return (CoopCharacter.MariaSaturn, 0);
        if (IsName(m, "maria")) return (CoopCharacter.MariaPsp, 0);
        if (IsName(m, "richter2")) return (CoopCharacter.RichterCoat, 0);
        return (CoopCharacter.Richter, 0);
    }

    public static void ToSave(IMemory m, CoopCharacter c, int palette)
    {
        m.WriteU8(SaveNameAddr + 10, TagV2);
        m.WriteU8(SaveNameAddr + 11, (byte)((int)c | Math.Clamp(palette, 0, Palettes - 1) << 4));
    }

    public static bool IsName(IMemory m, string name)
    {
        for (int i = 0; i < name.Length; i++)
            if (char.ToLowerInvariant((char)m.ReadU8(SaveNameAddr + (uint)i)) != name[i]) return false;
        byte next = m.ReadU8(SaveNameAddr + (uint)name.Length);
        return next == ' ' || next == 0;
    }
}
