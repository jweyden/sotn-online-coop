// SPDX-License-Identifier: AGPL-3.0-or-later
// Saturn Maria's own systems (MARIA.PRG): the charged attack, the command
// moves, MP, her palettes and sounds.
#include "ric.h"
#include "sfx.h"

s32 g_MarPadSettle;
s32 g_MarDeltaX, g_MarDeltaY, g_MarPrevX, g_MarPrevY;
// frames square has been held (Saturn g_Player 0x3FC); mods/maria reads it
// for the HUD, whose name plate changes colour with it
#define g_MarCharge MAR_CHARGE

extern u16 g_MarEffectPals[5][16];
extern u16 g_MarChargePals[15][16];
extern u16 g_MarFlashPalette[16];
extern u8 g_MarFrameBank[];

// palette p: g_Clut flat p * 16; VRAM: block p >> 8 at x 0x200 / 0 / 0x100,
// row 0xF0 + ((p & 0xFF) >> 4)
static void MarSetPalette(s32 p, u16* colors) {
    static const s16 blockX[] = {0x200, 0, 0x100};
    u16* dst = &g_Clut[0][p * COLORS_PER_PAL];
    RECT rect;
    s32 i;

    for (i = 0; i < COLORS_PER_PAL; i++) {
        dst[i] = colors[i];
    }
    rect.x = blockX[p >> 8] + (p & 15) * COLORS_PER_PAL;
    rect.y = 0xF0 + ((p & 0xFF) >> 4);
    rect.w = COLORS_PER_PAL;
    rect.h = 1;
    LoadImage(&rect, (u_long*)dst);
}

// Her main palette goes where Richter's is; dra may load his over it, so this
// runs every frame and only uploads when something changed it.
void MarLoadPalettes(void) {
    s32 i;

    for (i = 1; i < COLORS_PER_PAL; i++) {
        if (g_Clut[0][PAL_RICHTER * COLORS_PER_PAL + i] != g_MariaSatPalette[i]) {
            break;
        }
    }
    if (i == COLORS_PER_PAL) {
        return;
    }
    MarSetPalette(PAL_RICHTER, g_MariaSatPalette);
    for (i = 0; i < LEN(g_MarEffectPals); i++) {
        MarSetPalette(PAL_MAR_EFFECT + i, g_MarEffectPals[i]);
    }
}

// Some of her frames ask for another palette bank on the Saturn: 1 is a
// brighter copy of hers (she flashes as she attacks), 2..16 the charge
// colours. PS1 sprite frames can't carry one, so this sets her palette from
// the frame she's on, and puts hers back after.
void MarFramePalette(void) {
    s32 bank = g_MarFrameBank[PLAYER.animCurFrame & 0x7FFF];
    u16* colors;
    s32 i;

    if (bank == 0) {
        if (PLAYER.palette == PAL_FLAG(PAL_MAR_FRAME)) {
            PLAYER.palette = PAL_FLAG(PAL_RICHTER);
        }
        return;
    }
    colors = bank == 1 ? g_MarFlashPalette : g_MarChargePals[(bank - 2) % 15];
    for (i = 1; i < COLORS_PER_PAL; i++) {
        if (g_Clut[0][PAL_MAR_FRAME * COLORS_PER_PAL + i] != colors[i]) {
            MarSetPalette(PAL_MAR_FRAME, colors);
            break;
        }
    }
    PLAYER.palette = PAL_FLAG(PAL_MAR_FRAME);
}

// The Saturn refills Maria's MP in her HUD code (UpdateCompactStatusHud): one
// every 16 frames, unless status flag 1 is set; the PS1 has no such HUD.
void MarRegenMp(void) {
    if (!(g_Status.D_80097BF8 & 1) && !(g_Timer & 0xF) &&
        g_Status.mp < g_Status.mpMax) {
        g_Status.mp++;
    }
}

// 060AAA2C
s32 MarChargeLevel(void) {
    if (g_MarCharge > 300) {
        return 3;
    }
    if (g_MarCharge > 200) {
        return 2;
    }
    if (g_MarCharge > 100) {
        return 1;
    }
    return 0;
}

// 060A5AD4: holding square charges her next attack (released in
// RicCheckInput); she flashes through the level's colours.
void MarCharge(s32 allowed) {
    static const u8 flash[] = {0, 1, 2, 3, 4, 3, 2, 1};
    s32 level;

    if (!(g_Player.padPressed & PAD_SQUARE) || !allowed) {
        if (g_MarCharge) {
            PLAYER.palette = PAL_FLAG(PAL_RICHTER);
            g_MarCharge = 0;
        }
        return;
    }
    level = MarChargeLevel();
    if (level) {
        MarSetPalette(PAL_MAR_CHARGE,
                      g_MarChargePals[(level - 1) * 5 + flash[(g_Timer >> 1) & 7]]);
        PLAYER.palette = PAL_FLAG(PAL_MAR_CHARGE);
        DisableAfterImage(1, 4);
    }
    if (g_MarCharge == 370) {
        RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_MAR_CHARGE_FULL, 0);
    }
    if (g_MarCharge < 400) {
        g_MarCharge++;
    }
}

// 060A9064: the command moves' steps
void MarStepCommand(void) {
    if (PLAYER.poseTimer < 0) {
        if (PLAYER.step == PL_S_MAR_CMD0 + 5) {
            MarSfx(0x727);
        }
        if (PLAYER.step > PL_S_MAR_CMD0 && PLAYER.step < PL_S_MAR_CMD0 + 6) {
            MarSfx(MAR_SFX_RESTORE_MUSIC);
        }
        RicSetStand(0);
        return;
    }
    RicSetInvincibilityFrames(1, 0x32);
    func_8015CC28();
    if (PLAYER.step != PL_S_MAR_CMD0) {
        return;
    }
    RicDecelerateX(SATX(0x1C00));
    if (PLAYER.pose == 0x14 && PLAYER.poseTimer == 1) {
        RicSetSpeedX(SATX(0x50000));
        MarSfx(0x725);
    }
}

// Command moves: Street Fighter style motions + an attack button, for MP.
// The motions are matched on the d-pad in the Saturn's bit order (up, down,
// left, right in bits 28..31), so these read like the Saturn code.
#define SAT_UP 0x10000000
#define SAT_DOWN 0x20000000
#define SAT_LEFT 0x40000000
#define SAT_RIGHT 0x80000000

static u32 SatDirs(void) {
    u32 p = g_Player.padPressed;
    u32 d = 0;

    if (p & PAD_UP) {
        d |= SAT_UP;
    }
    if (p & PAD_DOWN) {
        d |= SAT_DOWN;
    }
    if (p & PAD_LEFT) {
        d |= SAT_LEFT;
    }
    if (p & PAD_RIGHT) {
        d |= SAT_RIGHT;
    }
    return d;
}

static s16 g_MarCmdMp[] = {40, 80, 100, 120, 60, 150}; // 0x060C7B30

typedef struct {
    s16 progress;
    s16 timer;
    u16 facing;
} MarMotion;

static MarMotion g_MarMotions[6];

#define CMD_ATTACK (PAD_SQUARE | MAR_PAD_SUBWPN)
#define STANDING (PLAYER.step <= PL_S_CROUCH)

static s32 MarTrySpend(s32 cmd) {
    s32 cost = g_MarCmdMp[cmd];

    if (cost > g_Status.mp || cost == 0) {
        return 0;
    }
    g_Status.mp -= cost;
    return 1;
}

// motion done: the timer runs out unless an attack fires it while standing
static s32 MarMotionFinish(MarMotion* m, s32 cmd, s32 runOk) {
    s16 t = m->timer - 1;

    if (t > 0) {
        if (!STANDING && !(runOk && PLAYER.step == PL_S_RUN)) {
            m->timer = t;
            return 0;
        }
        if (!g_Player.unk72) {
            m->timer = t;
            if (g_Player.unk46 || !(g_Player.padTapped & CMD_ATTACK)) {
                return 0;
            }
            if (!MarTrySpend(cmd)) {
                return 0;
            }
            MarSetCommand(cmd);
            return 1;
        }
    }
    m->timer--;
    m->progress = 0;
    return 0;
}

static s32 MarMotionAdvance(MarMotion* m) {
    m->timer = 0x14;
    m->progress++;
    return 0;
}

static s32 MarMotionWait(MarMotion* m) {
    if ((s16)(m->timer - 1) > 0) {
        m->timer--;
        return 0;
    }
    m->timer--;
    m->progress = 0;
    return 0;
}

// 060B9FE0: down, down-forward, forward + attack
static s32 MarCmd0(void) {
    MarMotion* m = &g_MarMotions[0];
    u32 want = m->facing ? (SAT_DOWN | SAT_LEFT) : (SAT_DOWN | SAT_RIGHT);

    switch (m->progress) {
    case 0:
        m->facing = PLAYER.facingLeft;
        if (g_Player.padTapped & PAD_DOWN) {
            return MarMotionAdvance(m);
        }
        return 0;
    case 2:
        want = m->facing ? SAT_LEFT : SAT_RIGHT;
    case 1:
        if (SatDirs() == want) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    default:
        return MarMotionFinish(m, 0, 0);
    }
}

// 060BA148: back, up-back, up, up-forward, forward + attack
static s32 MarCmd1(void) {
    MarMotion* m = &g_MarMotions[1];
    u32 d = SatDirs();
    u32 upBack = m->facing ? (SAT_UP | SAT_RIGHT) : (SAT_UP | SAT_LEFT);
    u32 upFwd = m->facing ? (SAT_UP | SAT_LEFT) : (SAT_UP | SAT_RIGHT);
    u32 fwd = m->facing ? SAT_LEFT : SAT_RIGHT;

    switch (m->progress) {
    case 0:
        m->facing = PLAYER.facingLeft;
        if (g_Player.padTapped & (m->facing ? PAD_RIGHT : PAD_LEFT)) {
            return MarMotionAdvance(m);
        }
        return 0;
    case 1:
        if ((d & upBack) == upBack) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 2:
        if (d & SAT_UP) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 3:
        if ((d & upFwd) == upFwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 4:
        if (d == fwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    default:
        return MarMotionFinish(m, 1, 0);
    }
}

// 060BA32C: up, down, down-forward, forward + attack
static s32 MarCmd2(void) {
    MarMotion* m = &g_MarMotions[2];
    u32 d = SatDirs();
    u32 downFwd = m->facing ? (SAT_DOWN | SAT_LEFT) : (SAT_DOWN | SAT_RIGHT);
    u32 fwd = m->facing ? SAT_LEFT : SAT_RIGHT;

    switch (m->progress) {
    case 0:
        m->facing = PLAYER.facingLeft;
        if (g_Player.padTapped & PAD_UP) {
            return MarMotionAdvance(m);
        }
        return 0;
    case 1:
        if (d & SAT_DOWN) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 2:
        if ((d & downFwd) == downFwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 3:
        if (d == fwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    default:
        return MarMotionFinish(m, 2, 0);
    }
}

// 060BA4BC: back, forward, down-forward, down, down-back, back, forward +
// attack (also from a run)
static s32 MarCmd3(void) {
    MarMotion* m = &g_MarMotions[3];
    u32 d = SatDirs();
    u32 fwd = m->facing ? SAT_LEFT : SAT_RIGHT;
    u32 back = m->facing ? SAT_RIGHT : SAT_LEFT;
    u32 downFwd = SAT_DOWN | fwd;
    u32 downBack = SAT_DOWN | back;

    switch (m->progress) {
    case 0:
        m->facing = PLAYER.facingLeft;
        if (g_Player.padTapped & (m->facing ? PAD_RIGHT : PAD_LEFT)) {
            return MarMotionAdvance(m);
        }
        return 0;
    case 1:
        if (d == fwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 2:
        if ((d & downFwd) == downFwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 3:
        if (d == SAT_DOWN) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 4:
        if ((d & downBack) == downBack) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 5:
        if (d == back) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 6:
        if (d == fwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    default:
        return MarMotionFinish(m, 3, 1);
    }
}

// 060BA6E8: hold up for a second, then up-back(?) ... down + attack; only
// while fewer than two of its objects (entity 0x4D) are out
static s32 MarCmd4(void) {
    MarMotion* m = &g_MarMotions[4];
    u32 d = SatDirs();
    u32 upFwd = m->facing ? (SAT_UP | SAT_LEFT) : (SAT_UP | SAT_RIGHT);
    u32 downFwd = m->facing ? (SAT_DOWN | SAT_LEFT) : (SAT_DOWN | SAT_RIGHT);
    u32 fwd = m->facing ? SAT_LEFT : SAT_RIGHT;
    Entity* e;
    s32 n;

    switch (m->progress) {
    case 0:
        m->facing = PLAYER.facingLeft;
        if (!(g_Player.padPressed & PAD_UP)) {
            m->timer = 0;
        } else {
            m->timer++;
        }
        if (m->timer > 60) {
            m->progress++;
        }
        return 0;
    case 1:
        if (!(g_Player.padPressed & PAD_UP)) {
            m->progress = m->timer = 0;
            return 0;
        }
        if (d != upFwd) {
            return 0;
        }
        return MarMotionAdvance(m);
    case 2:
        if (d == fwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 3:
        if (d == downFwd) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    case 4:
        if (d == SAT_DOWN) {
            return MarMotionAdvance(m);
        }
        return MarMotionWait(m);
    }
    if ((s16)(m->timer - 1) > 0 && STANDING && !g_Player.unk72) {
        m->timer--;
        if (g_Player.unk46 || !(g_Player.padTapped & CMD_ATTACK)) {
            return 0;
        }
        for (n = 0, e = &g_Entities[16]; e < &g_Entities[31]; e++) {
            if (e->entityId == 0x4D) {
                n++;
            }
        }
        if (!MarTrySpend(4)) {
            return 0;
        }
        if (n < 2) {
            MarSetCommand(4);
        }
        return 1;
    }
    if ((s16)(m->timer - 1) > 0 && !STANDING) {
        m->timer--;
        return 0;
    }
    m->timer--;
    m->progress = 0;
    return 0;
}

// 060BA914: up, then a full circle (up-forward ... down ... up-back), hold
// up for a second, release, down + attack (also from a run)
static s32 MarCmd5(void) {
    MarMotion* m = &g_MarMotions[5];
    u32 d = SatDirs();
    u32 fwd = m->facing ? SAT_LEFT : SAT_RIGHT;
    u32 back = m->facing ? SAT_RIGHT : SAT_LEFT;
    u32 seq[] = {0, SAT_UP | fwd, fwd, SAT_DOWN | fwd, SAT_DOWN, SAT_DOWN | back,
                 back, SAT_UP | back};

    switch (m->progress) {
    case 0:
        m->facing = PLAYER.facingLeft;
        if (g_Player.padTapped != PAD_UP) {
            return 0;
        }
        break;
    case 1:
    case 2:
    case 3:
    case 5:
    case 6:
    case 7:
        if (d != seq[m->progress]) {
            if (--m->timer < 1) {
                m->progress = 0;
            }
            return 0;
        }
        break;
    case 4:
    case 10:
        if (d != SAT_DOWN) {
            if (--m->timer < 1) {
                m->progress = 0;
            }
            return 0;
        }
        break;
    case 8:
        if (!(g_Player.padPressed & PAD_UP)) {
            m->progress = 0;
            return 0;
        }
        if ((s16)(m->timer + 1) > 60) {
            m->progress++;
            m->timer = 0x14;
        } else {
            m->timer++;
        }
        return 0;
    case 9:
        if (!(g_Player.padPressed & PAD_UP)) {
            m->progress++;
            m->timer = 0x14;
        }
        return 0;
    default:
        if (--m->timer > 0) {
            if (!STANDING && PLAYER.step != PL_S_RUN) {
                return 0;
            }
            if (g_Player.unk72) {
                m->progress = 0;
                return 0;
            }
            if (g_Player.unk46 || g_Player.timers[PL_T_INVINCIBLE_SCENE] ||
                !(g_Player.padTapped & CMD_ATTACK)) {
                return 0;
            }
            if (MarTrySpend(5)) {
                MarSetCommand(5);
                return 1;
            }
            return 0;
        }
        m->progress = 0;
        return 0;
    }
    m->timer = 0x14;
    m->progress++;
    return 0;
}

// 060BABE0
void MarCheckCommands(void) {
    if (MarCmd2() || MarCmd1() || MarCmd3() || MarCmd0() || MarCmd4()) {
        return;
    }
    MarCmd5();
}

// Her own sounds (her 20 voice lines and 14 effects) aren't in any PS1 bank:
// mods/maria plays them from the SPU's second sample RAM (MARIASAT.SND, from
// tools/mariasat-sfx.py). Each is played as one of Richter's voice sounds, a
// carrier, with MAR_SOUND saying which of hers it is: the mod tags the queued
// sound and swaps her sample in when it's keyed on. Richter's bank and dra's
// sound table stay as they are, so every player's sounds stay their own.
// (The carriers' tones have full volume in Richter's and Alucard's banks;
// they take turns, as dra cuts a sound short when the same id plays again.)
static const u16 g_MarCarriers[] = {0x6F9, 0x6FA, 0x6FB, 0x6FC, 0x6FF, 0x700};
static s32 g_MarCarrier;

// Saturn sound id -> her sound (voice lines 0..19: SD_MARI.CRT's 6 samples,
// then SDE7.PCM clips 1 and 3..15; effects 20..33: SD_MARI.CRT sequences 6..19)
static s32 MarOwnSound(s32 sfx) {
    if (sfx >= 0x71C && sfx <= 0x721) {
        return sfx - 0x71C;
    }
    if (sfx == 0x5CA) {
        return 6;
    }
    if (sfx >= 0x5CC && sfx <= 0x5D8) {
        return 7 + sfx - 0x5CC;
    }
    if (sfx >= 0x722 && sfx <= 0x72F) {
        return 20 + sfx - 0x722;
    }
    return -1;
}

void MarSfx(s32 sfx) {
    s32 n;

    if ((u32)sfx >= 0xF0000000) {
        return; // Saturn music commands (dim/restore around command moves)
    }
    n = MarOwnSound(sfx);
    if (n < 0) {
        g_api.PlaySfx(MarMapSfx(sfx));
        return;
    }
    MAR_SOUND = n + 1;
    g_api.PlaySfx(g_MarCarriers[g_MarCarrier]);
    MAR_SOUND = 0;
    g_MarCarrier = (g_MarCarrier + 1) % LEN(g_MarCarriers);
}

// Saturn sound ids -> PS1 (below Richter's voices they're the same)
s32 MarMapSfx(s32 sfx) {
    switch (sfx) {
    case 0x71A:
        return SFX_WEAPON_SWISH_C;
    }
    return sfx;
}
