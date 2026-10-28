// SPDX-License-Identifier: AGPL-3.0-or-later
// Saturn Maria's step setters (MARIA.PRG 060A914C..060AA048). Same shape as
// Richter's; horizontal speeds are the Saturn's through SATX (5/4 wider there).
#include "ric.h"

void RicSetDebug() { RicSetStep(PL_S_DEBUG); }

void RicSetInit(s32 step_s) {
    PLAYER.step = PL_S_INIT;
    PLAYER.step_s = step_s;
    PLAYER.pose = PLAYER.poseTimer = 0;
    if (step_s & 1) {
        PLAYER.anim = D_80155950;
    } else {
        PLAYER.anim = D_8015591C;
    }
}

// 060A9194
void RicSetCrouch(s32 kind, s32 velocityX) {
    AnimationFrame* anim = ric_anim_crouch;

    RicSetStep(PL_S_CROUCH);
    g_Player.unk44 = 0;
    PLAYER.velocityY = 0;
    if (kind == 1) {
        PLAYER.step_s = 4;
    } else if (kind == 2) {
        PLAYER.step_s = 1;
    } else if (kind == 3) {
        PLAYER.step_s = 0;
        anim = ric_anim_land_from_air_run;
    }
    PLAYER.velocityX = velocityX;
    RicSetAnimation(anim);
}

// 060A9210: at a ledge (MAR_AT_EDGE) she stands teetering
void RicSetStand(s32 velocityX) {
    PLAYER.velocityY = 0;
    g_Player.unk44 = 0;
    PLAYER.velocityX = velocityX;
    RicSetStep(PL_S_STAND);
    RicSetAnimation(
        (g_Player.vram_flag & MAR_AT_EDGE) ? ric_anim_stand_alt : ric_anim_stand);
}

void RicSetRun(void) {
    if (g_Player.unk7A) {
        RicSetWalk(0);
    } else {
        g_Player.unk44 = 0;
        RicSetStep(PL_S_RUN);
        RicSetAnimation(ric_anim_run);
        RicSetSpeedX(FIX(2.25));
        g_Player.timers[PL_T_RUN] = 40;
        PLAYER.velocityY = 0;
        RicCreateEntFactoryFromEntity(
            g_CurrentEntity, FACTORY(BP_SMOKE_PUFF, 5), 0);
    }
}

// 060A926C
void RicSetWalk(s32 arg0) {
    if (g_Player.timers[PL_T_8] && !g_Player.unk7A) {
        RicSetRun();
        return;
    }
    g_Player.timers[PL_T_8] = 12;
    g_Player.timers[PL_T_CURSE] = 12;
    g_Player.unk44 = 0;
    RicSetStep(PL_S_WALK);
    RicSetAnimation(ric_anim_walk);
    RicSetSpeedX(SATX(0x19800));
    PLAYER.velocityY = 0;
}

void RicSetFall(void) {
    if (g_Player.prev_step != PL_S_RUN && g_Player.prev_step != PL_S_SLIDE) {
        PLAYER.velocityX = 0;
    }
    if (g_Player.prev_step != PL_S_WALK && g_Player.prev_step != PL_S_RUN) {
        RicSetAnimation(g_MariaAnimFall);
    }
    if (g_Player.prev_step == PL_S_RUN) {
        g_Player.unk44 = 0x10;
    }
    RicSetStep(PL_S_FALL);
    PLAYER.velocityY = FIX(2);
    g_Player.timers[PL_T_5] = 8;
    g_Player.timers[PL_T_6] = 8;
    g_Player.timers[PL_T_CURSE] = 0;
    g_Player.timers[PL_T_8] = 0;
    if (g_Player.prev_step == PL_S_SLIDE) {
        g_Player.timers[PL_T_5] = g_Player.timers[PL_T_6] = 0;
        PLAYER.pose = 2;
        PLAYER.poseTimer = 0x10;
        PLAYER.velocityX /= 2;
    }
}

// 060A9474
void RicSetJump(void) {
    if (g_Player.unk72) {
        RicSetFall();
        return;
    }
    if (RicCheckFacing() != 0 || PLAYER.step == PL_S_SLIDE) {
        RicSetAnimation(g_MariaAnim_060C2ADC);
        if (PLAYER.step == PL_S_RUN) {
            RicSetSpeedX(FIX(2.25));
            g_Player.unk44 = 0x10;
        } else {
            RicSetSpeedX(SATX(0x19800));
            g_Player.unk44 = 0;
        }
    } else {
        RicSetAnimation(g_MariaAnim_060C2B18);
        PLAYER.velocityX = 0;
        g_Player.unk44 = 4;
    }
    RicSetStep(PL_S_JUMP);
    PLAYER.velocityY = FIX(-4);
}

// 060A955C: her second jump in the air (unk44 0x100 once used)
void MarSetDoubleJump(void) {
    if (!(g_Player.unk44 & 1)) {
        g_Player.unk44 = (g_Player.unk44 & ~2) | 0x21;
    } else {
        g_Player.unk44 = (g_Player.unk44 & ~0x20) | 0x100;
    }
    RicSetAnimation(g_MariaAnim_060C2ADC);
    if (RicCheckFacing()) {
        RicSetSpeedX(SATX(0x19000));
    }
    PLAYER.step = PL_S_JUMP;
    PLAYER.step_s = 0;
    PLAYER.velocityY = -0x4C000;
    if (g_Player.unk72) {
        PLAYER.velocityY = 0;
    }
}

// 060A9608: a running high jump when a direction is held
void RicSetHighJump(void) {
    s32 speed = SATX(0x19800);
    AnimationFrame* anim = g_MariaAnim_060C2BB8;

    RicSetStep(PL_S_HIGHJUMP);
    if (RicCheckFacing()) {
        speed = SATX(0x4C800);
        anim = g_MariaAnim_060C2B04;
    }
    RicSetSpeedX(speed);
    PLAYER.velocityY = FIX(-7.5);
    g_Player.high_jump_timer = 0;
    RicSetAnimation(anim);
    func_8015CC28();
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_HIGH_JUMP, 0);
    MarSfx(0x71D);
    g_Player.timers[PL_T_12] = 4;
    if (g_Player.unk72) {
        PLAYER.velocityY = 0;
    }
}

static s32 RicCheckSubwpnChainLimit(s16 subwpnId, s16 limit) {
    Entity* entity;
    s32 i;
    s32 nFound;
    s32 nEmpty;

    entity = &g_Entities[32];
    for (i = 0, nFound = 0, nEmpty = 0; i < 16; i++, entity++) {
        if (!entity->entityId) {
            nEmpty++;
        }
        if (entity->ext.subweapon.subweaponId &&
            entity->ext.subweapon.subweaponId == subwpnId) {
            nFound++;
        }
        if (nFound >= limit) {
            return -1;
        }
    }
    if (nEmpty) {
        return 0;
    }
    return -1;
}

// 060A973C: up + attack throws the subweapon
s32 RicDoSubweapon() {
    SubweaponDef subweapon;
    s16 subweaponId;
    AnimationFrame* anim;

    if (!(g_Player.padPressed & PAD_UP)) {
        return 1;
    }
    subweaponId = RicCheckSubweapon(&subweapon, false, false);
    if (subweaponId <= 0) {
        return 1;
    }
    if (subweapon.blueprintNum == 0) {
        return 4;
    }
    if (RicCheckSubwpnChainLimit(subweaponId, subweapon.chainLimit) < 0) {
        return 2;
    }
    if (g_Player.unk72) {
        return 5;
    }
    subweaponId = RicCheckSubweapon(&subweapon, false, true);
    if (subweaponId <= 0) {
        return 3;
    }
    RicCreateEntFactoryFromEntity(g_CurrentEntity, subweapon.blueprintNum, 0);
    g_Player.timers[PL_T_10] = 4;
    if (subweaponId == SUBWPN_DAGGER || subweaponId == 9) {
        anim = (g_Player.vram_flag & MAR_AT_EDGE) ? g_MariaAnim_060C2F00
                                                  : g_MariaAnim_060C2ED8;
    } else {
        anim = (g_Player.vram_flag & MAR_AT_EDGE) ? g_MariaAnim_060C2EA8
                                                  : g_MariaAnim_060C2E78;
    }
    switch (PLAYER.step) {
    case PL_S_STAND:
    case PL_S_WALK:
    case PL_S_CROUCH:
        PLAYER.step = PL_S_STAND;
        RicSetAnimation(anim);
        break;
    case PL_S_FALL:
    case PL_S_JUMP:
        PLAYER.step = PL_S_JUMP;
        RicSetAnimation(g_MariaAnim_060C2F00);
        break;
    case PL_S_RUN:
        PLAYER.step = PL_S_STAND;
        RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_SKID_SMOKE, 0);
        RicSetAnimation(anim);
        break;
    }
    PLAYER.step_s = 0x42;
    g_Player.unk46 = 3;
    g_Player.timers[PL_T_10] = 4;
    return 0;
}

static void MarAttackGrunt(s32 grunt) {
    if (grunt == 0) {
        MarSfx(0x71C);
    }
    if (grunt == 1) {
        MarSfx(0x71D);
    }
    if (grunt == 2) {
        MarSfx(0x71E);
    }
}

// MP each attack costs, by charge level (0x060C1980)
static s8 g_MarAttackMp[] = {5, 10, 30, 60};

// 060A9990: attack (charged != 0: square released after a charge). Her attack
// is an object (blueprint 0x4F, charged 0x5C + level) and costs MP.
s32 RicDoAttack(s32 charged) {
    s32 grunt;
    s32 level;
    s32 cost;
    AnimationFrame* anim;

    grunt = rand() % 6;
    level = 0;
    if (!charged) {
        if (RicDoSubweapon() == 0) {
            MarAttackGrunt(grunt);
            return 1;
        }
    } else {
        PLAYER.palette = PAL_FLAG(PAL_RICHTER);
        level = MarChargeLevel();
        if (level == 0) {
            return 0;
        }
    }
    cost = g_MarAttackMp[level];
    if (cost > g_Status.mp || cost == 0) {
        return level;
    }
    g_Status.mp -= cost;
    if (!RicCreateEntFactoryFromEntity(
            g_CurrentEntity, charged ? BP_MAR_CHARGED + level : BP_MAR_ATTACK,
            0)) {
        return 0;
    }
    MarAttackGrunt(grunt);
    switch (PLAYER.step) {
    case PL_S_CROUCH:
        if (PLAYER.step_s != 2) {
            anim = g_MariaAnim_060C30B0;
            break;
        }
    case PL_S_STAND:
    case PL_S_WALK:
        anim = g_MariaAnim_060C3760;
        PLAYER.step = PL_S_STAND;
        break;
    case PL_S_FALL:
    case PL_S_JUMP:
        PLAYER.step = PL_S_JUMP;
        RicSetAnimation((g_Player.padPressed & PAD_DOWN) ? g_MariaAnim_060C3114
                                                         : g_MariaAnim_060C30EC);
        g_Player.timers[PL_T_ATTACK] = 4;
        g_Player.unk46 = 1;
        PLAYER.step_s = 0x40;
        PLAYER.pose = 3;
        return 1;
    case PL_S_RUN:
        PLAYER.step = PL_S_STAND;
        RicSetAnimation(g_MariaAnim_060C3760);
        RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_SKID_SMOKE, 0);
        g_Player.timers[PL_T_ATTACK] = 4;
        g_Player.unk46 = 1;
        PLAYER.step_s = 0x40;
        PLAYER.pose = 3;
        return 1;
    default:
        return 0;
    }
    RicSetAnimation(anim);
    g_CurrentEntity->velocityX = 0;
    PLAYER.pose = 3;
    PLAYER.step_s = 0x40;
    g_Player.unk46 = 1;
    g_Player.timers[PL_T_ATTACK] = 4;
    return 1;
}

// 060A9BC4: the second attack button (Saturn 0x200, circle here), a kick:
// stand/crouch/air up/air down versions; while running it's the slide kick
void MarSubweaponButton(void) {
    s32 kind;

    switch (PLAYER.step) {
    case PL_S_CROUCH:
        if (PLAYER.step_s != 2) {
            RicSetAnimation(g_MariaAnim_060C3080);
            kind = 1;
            g_CurrentEntity->velocityX = 0;
            break;
        }
    case PL_S_STAND:
    case PL_S_WALK:
        PLAYER.step = PL_S_STAND;
        RicSetAnimation(g_MariaAnim_060C2FA4);
        kind = 0;
        g_CurrentEntity->velocityX = 0;
        break;
    case PL_S_FALL:
    case PL_S_JUMP:
        PLAYER.step = PL_S_JUMP;
        if (PLAYER.velocityY <= 0) {
            RicSetAnimation(g_MariaAnim_060C3734);
            kind = 2;
        } else {
            RicSetAnimation(g_MariaAnim_060C2F18);
            kind = 3;
        }
        break;
    case PL_S_RUN:
        RicSetSlideKick();
        return;
    default:
        return;
    }
    RicCreateEntFactoryFromEntity(
        g_CurrentEntity, FACTORY(BP_MAR_KICK, kind), 0);
    MarSfx(0x71F);
    g_Player.unk46 = 2;
    PLAYER.step_s = 0x41;
    g_Player.timers[PL_T_ATTACK] = 4;
}

// 060A9CE8: Saturn Maria has no item crashes
bool RicDoCrash(void) { return 1; }

void RicSetDeadPrologue() { RicSetStep(PL_S_DEAD_PROLOGUE); }

// 060A9D10
void RicSetSlide(void) {
    RicCheckFacing();
    RicSetStep(PL_S_SLIDE);
    RicSetAnimation(g_MariaAnim_060C2E4C);
    g_CurrentEntity->velocityY = 0;
    RicSetSpeedX(SATX(0x6A000));
    func_8015CC28();
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_25, 0);
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_SKID_SMOKE, 0);
    MarSfx(SFX_RIC_SLIDE_SKID);
    g_Player.timers[PL_T_12] = 4;
}

// 060A9DA4
void RicSetSlideKick(void) {
    g_Player.unk44 = 0;
    RicSetStep(PL_S_SLIDE_KICK);
    RicSetAnimation(g_MariaAnim_060C2F8C);
    g_CurrentEntity->velocityY = FIX(-2);
    RicSetSpeedX(SATX(0x6A000));
    func_8015CC28();
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_25, 0);
    MarSfx(0x71D);
    g_Player.timers[PL_T_12] = 4;
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_31, 0);
}

// 060A9E40: R1, a quick hop backwards
void MarSetBackSlide(void) {
    PLAYER.step = PL_S_STAND;
    PLAYER.step_s = 3;
    RicSetSpeedX(-SATX(0x46000));
    PLAYER.velocityY = 0;
    RicSetAnimation(g_MariaAnim_060C2E04);
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_SKID_SMOKE, 0);
    g_Player.unk46 = 0;
    MarSfx(0x616);
}

// 060A9EC4
void RicSetBladeDash(void) {
    RicSetStep(PL_S_BLADEDASH);
    RicSetAnimation(g_MariaAnim_060C2ADC);
    g_CurrentEntity->velocityY = 0;
    RicSetSpeedX(SATX(0x6A000));
    g_Player.unk46 = 5;
    g_Player.timers[PL_T_12] = 4;
    RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_BLADE_DASH, 0);
    func_8015CC28();
    MarSfx(0x71F);
    MarSfx(0x71A);
}

// Command moves (060A9F54 table, 060A9F84): animation, step, blueprint
static struct {
    AnimationFrame* anim;
    u8 step;
    u8 blueprint;
} g_MarCommands[] = {
    {g_MariaAnim_060C3518, PL_S_MAR_CMD0, BP_MAR_CMD0},
    {g_MariaAnim_060C31AC, PL_S_MAR_CMD0 + 1, BP_MAR_CMD0 + 1},
    {g_MariaAnim_060C33D8, PL_S_MAR_CMD0 + 2, BP_MAR_CMD0 + 2},
    {g_MariaAnim_060C32BC, PL_S_MAR_CMD0 + 3, BP_MAR_CMD0 + 3},
    {g_MariaAnim_060C3258, PL_S_MAR_CMD0 + 4, BP_MAR_CMD0 + 4},
    {g_MariaAnim_060C3258, PL_S_MAR_CMD0 + 5, BP_MARIA_POWERS_APPLIED},
};

void MarSetCommand(s32 n) {
    if (n > 5) {
        n = 0;
    }
    RicSetAnimation(g_MarCommands[n].anim);
    RicSetStep(g_MarCommands[n].step);
    func_8015FA5C(n + 5);
    g_CurrentEntity->velocityX = g_CurrentEntity->velocityY = 0;
    if (PLAYER.step > PL_S_MAR_CMD0 && PLAYER.step < PL_S_MAR_CMD0 + 6) {
        MarSfx(MAR_SFX_DIM_MUSIC);
    }
    switch (n) {
    case 0:
        MarSfx(0x5CC);
        break;
    case 1:
        MarSfx(0x5D3);
        break;
    case 2:
        MarSfx(0x5D2);
        break;
    case 3:
        MarSfx(0x5D1);
        DisableAfterImage(1, 1);
        break;
    case 4:
        MarSfx(0x5D0);
        break;
    case 5:
        RicCreateEntFactoryFromEntity(g_CurrentEntity, BP_MAR_CMD5_EXTRA, 0);
        break;
    }
    RicCreateEntFactoryFromEntity(g_CurrentEntity, g_MarCommands[n].blueprint, 0);
}
