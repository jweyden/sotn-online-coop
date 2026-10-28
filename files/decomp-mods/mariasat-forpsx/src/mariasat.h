// SPDX-License-Identifier: AGPL-3.0-or-later
// Saturn Maria on the PS1 Richter overlay: what her code adds to Richter's.
#ifndef MARIASAT_H
#define MARIASAT_H

// Saturn horizontal values are 5/4 of the PS1's (320px screen)
#define SATX(x) ((x) * 4 / 5)

// The Saturn's second attack button (0x200): her kick
#define MAR_PAD_SUBWPN PAD_CIRCLE

// g_Player.vram_flag: standing near a ledge (set in CheckStageCollision)
#define MAR_AT_EDGE 0x20

// her command moves (steps 0x30..0x35)
#define PL_S_MAR_CMD0 0x30

// blueprints past Richter's
#define BP_MAR_ATTACK 0x4F
#define BP_MAR_KICK 0x50
#define BP_MAR_DIVE_KICK 0x51
#define BP_MAR_CMD0 0x53
#define BP_MAR_CHARGED 0x5C
#define BP_MAR_CHARGE_FULL 0x63
#define BP_MAR_BEAM_SPARK 0x60
#define BP_MAR_FIREBALL 0x59
#define BP_MAR_BEAST 0x58
#define BP_MAR_DRAGON 0x5A
#define BP_MAR_STARBURST 0x5B
#define BP_MAR_FAMILIAR_TRAIL 0x5C
#define BP_MAR_SPARK 0x64
#define BP_MAR_CMD5_EXTRA 0x66

// her palettes, in Alucard's player slots (unused in Richter mode)
#define PAL_MAR_CHARGE 0x104
#define PAL_MAR_EFFECT 0x105 // 5 of them
#define PAL_MAR_FRAME 0x10A  // a frame's own palette bank (MarFramePalette)
#define PAL_MAR_BLINK_A PAL_RICHTER
#define PAL_MAR_BLINK_B PAL_FILL_WHITE

#define MAR_NUM_BLUEPRINTS 105
#define MAR_NUM_ENTITIES 93

// Her effects are drawn by mods/maria from her Saturn sprite packages (as
// host images): an object that has one sets its entry here, and the mod draws
// the package frame animCurFrame picks, with the object's position, facing,
// rotation/scale and zPriority. Entries count only while entityId matches.
typedef struct {
    u16 entityId;
    u8 pkg;   // sprite package 1..12 (0: none)
    s8 bank;  // palette bank override (-1: the frame's own)
    u8 blend; // 0 opaque, 1 additive, 2 50%, 3 subtractive
    u8 bright; // 0x80 = normal
    u8 brightB; // blue's own brightness (0: same as bright)
    u8 pad;
} MarFx;
#define MAR_FX ((MarFx*)0x80680000)

// The Saturn's colour offset (SCL_SetColorOffset on everything but RBG0), in
// 8-bit colour steps: mods/maria darkens or brightens the whole screen by it
#define MAR_COLOR_OFFSET (*(s16*)0x80680200)
#define MAR_CHARGE (*(s32*)0x80680204)
// her sound being played (1 + MarSfx's number), for mods/maria
#define MAR_SOUND (*(u16*)0x80680208)

#define MAR_SFX_DIM_MUSIC 0xF0000001
#define MAR_SFX_RESTORE_MUSIC 0xF0000002

extern s32 g_MarPadSettle;
extern s32 g_MarDeltaX, g_MarDeltaY, g_MarPrevX, g_MarPrevY;
extern u16 g_MariaSatPalette[16];

void MarSfx(s32 sfx);
void MarFxSet(Entity* e, s32 pkg);
void MarSetAttackParams(Entity* e, const s16* def);
void MarEntityAttack(Entity* self);
void MarEntityKick(Entity* self);
void MarEntityDiveKick(Entity* self);
void MarEntity22(Entity* self);
void MarEntity72(Entity* self);
void MarEntity73(Entity* self);
void MarEntity74(Entity* self);
void MarEntity75(Entity* self);
void MarEntity76(Entity* self);
void MarEntity77(Entity* self);
void MarEntity78(Entity* self);
void MarEntity79(Entity* self);
void MarEntity80(Entity* self);
void MarEntity81(Entity* self);
void MarEntity82(Entity* self);
void MarEntity83(Entity* self);
void MarEntity85(Entity* self);
void MarEntity86(Entity* self);
void MarEntity87(Entity* self);
void MarEntity88(Entity* self);
void MarEntity89(Entity* self);
void MarEntity90(Entity* self);
void MarEntity91(Entity* self);
void MarEntity92(Entity* self);
s32 MarMapSfx(s32 sfx);
void MarLoadPalettes(void);
void MarFramePalette(void);
void MarRegenMp(void);
void MarSetDoubleJump(void);
void MarSubweaponButton(void);
void MarSetBackSlide(void);
void MarSetCommand(s32 n);
s32 MarChargeLevel(void);
void MarStepCommand(void);
void MarCheckCommands(void);
void MarCharge(s32 allowed);
void func_8015FA5C(s32 arg0);
void DisableAfterImage(s32 resetAnims, s32 arg1);
void RicSetInvincibilityFrames(s32 kind, s16 invincibilityFrames);
void func_8015CC28(void);

#endif
