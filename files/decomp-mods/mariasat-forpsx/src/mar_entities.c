// SPDX-License-Identifier: AGPL-3.0-or-later
// Saturn Maria's own objects (entity slots 69..92, MARIA.PRG). Their graphics
// are her Saturn sprite packages, drawn by mods/maria (MAR_FX); horizontal
// values are the Saturn's through SATX.
#include "ric.h"
#include "sfx.h"

#define EXT8(e, i) (((u8*)&(e)->ext)[i])
#define MAR_TODO(n)                                                            \
    void MarEntity##n(Entity* self) { DestroyEntity(self); }


typedef struct {
    s16 attack;
    u8 hitboxWidth;
    u8 hitboxHeight;
    s8 hitboxOffX;
    s8 hitboxOffY;
    u16 attackElement;
    u16 hitboxState;
    u8 nFramesInvincibility;
    u8 stunFrames;
    u16 hitEffect;
    u16 entityRoomIndex;
} MarAttackDef;

void MarFxSet(Entity* e, s32 pkg) {
    MarFx* fx = &MAR_FX[e - g_Entities];

    fx->entityId = e->entityId;
    fx->pkg = pkg;
    fx->bank = -1;
    fx->blend = 0;
    fx->bright = 0x80;
    fx->brightB = 0;
    e->animSet = 0; // dra doesn't draw it; the mod does
}

// 060ABE94: hit slots 3..6 for the low entities, 7..10 for the rest
extern s32 D_80174F80[11];
static void MarAllocEnemyId(Entity* e) {
    s32 i, id, lo, hi;

    lo = e < &g_Entities[32] ? 3 : 7;
    hi = lo + 4;
    for (i = 0;; i++) {
        for (id = lo; id < hi; id++) {
            if (D_80174F80[id] == i) {
                D_80174F80[id] = i + 1;
                e->enemyId = id;
                return;
            }
        }
    }
}

// A factory's parameter reaches what it spawns as params << 8 (the low byte
// is the spawn index). The Saturn code reads it as *(u8*)&params, which on
// the big-endian SH-2 is that high byte too.

// 060ABFF0
static void MarApplyAttackDef(Entity* e, const MarAttackDef* d) {
    e->attack = d->attack;
    e->hitboxWidth = SATX(d->hitboxWidth);
    e->hitboxHeight = d->hitboxHeight;
    e->hitboxOffX = SATX(d->hitboxOffX);
    e->hitboxOffY = d->hitboxOffY;
    e->attackElement = d->attackElement;
    e->hitboxState = d->hitboxState;
    e->nFramesInvincibility = d->nFramesInvincibility;
    e->stunFrames = d->stunFrames;
    e->hitEffect = d->hitEffect;
    e->entityRoomIndex = d->entityRoomIndex;
    e->attack = g_api.func_800FD664(e->attack);
    MarAllocEnemyId(e);
}

void MarSetAttackParams(Entity* e, const s16* def) {
    MarApplyAttackDef(e, (const MarAttackDef*)def);
}

// which of her attack poses she's in: stand, crouch, air, air (down)
static s32 MarAttackKind(void) {
    if (PLAYER.anim == g_MariaAnim_060C3760) {
        return 0;
    }
    if (PLAYER.anim == g_MariaAnim_060C30B0) {
        return 1;
    }
    if (PLAYER.anim == g_MariaAnim_060C30EC) {
        return 2;
    }
    return 3;
}

static MarAttackDef g_MarAttackDef = {15, 16, 8, 0, 0, 0x20, 2, 16, 4, 0x81, 0x20};
// where it starts, per kind (hand while winding up: kind + 4)
static s8 g_MarAttackOffsets[8][2] = {
    {30, -11}, {30, 7}, {34, -16}, {38, 3},
    {16, -11}, {16, 7}, {16, -16}, {16, -8},
};
static AnimationFrame g_MarAttackAnim0[] = {
    POSE(4, 1, 0), POSE(4, 2, 0), POSE(4, 3, 0), POSE(4, 4, 0), POSE_LOOP(0),
};
static AnimationFrame g_MarAttackAnim1[] = {
    POSE(4, 5, 0), POSE(4, 6, 0), POSE(4, 7, 0), POSE(4, 8, 0), POSE_LOOP(0),
};

static void MarAtPlayer(Entity* self, s32 i) {
    s32 dx = SATX(g_MarAttackOffsets[i][0]);

    self->posX.i.hi = PLAYER.posX.i.hi + (self->facingLeft ? -dx : dx);
    self->posY.i.hi = PLAYER.posY.i.hi + g_MarAttackOffsets[i][1];
    self->posY.val += PLAYER.velocityY;
}

// 060B9404: her attack, a projectile (sprite package 7) thrown straight,
// or down-forward from the air
void MarEntityAttack(Entity* self) {
    s32 kind;

    switch (self->step) {
    case 0:
        EXT8(self, 4) = MarAttackKind();
        MarApplyAttackDef(self, &g_MarAttackDef);
        self->step++;
    case 1:
        self->facingLeft = PLAYER.facingLeft;
        if (PLAYER.anim == g_MariaAnim_060C3760 ||
            PLAYER.anim == g_MariaAnim_060C30B0 ||
            PLAYER.anim == g_MariaAnim_060C30EC ||
            PLAYER.anim == g_MariaAnim_060C3114) {
            MarAtPlayer(self, EXT8(self, 4) + 4);
            if (PLAYER.pose < 4) {
                return;
            }
        }
        break;
    case 2:
        MarFxSet(self, 7);
        self->zPriority = PLAYER.zPriority - 2;
        MarSfx(0x728);
        self->facingLeft = PLAYER.facingLeft;
        if (EXT8(self, 4) == 3) {
            self->velocityX = self->facingLeft ? -SATX(0x27991) : SATX(0x27991);
            self->velocityY = 0x27991;
            self->anim = g_MarAttackAnim1;
            self->flags = FLAG_UNK_100000;
        } else {
            self->velocityX = self->facingLeft ? -SATX(0x38000) : SATX(0x38000);
            self->anim = g_MarAttackAnim0;
            self->flags = FLAG_POS_CAMERA_LOCKED | FLAG_UNK_100000;
        }
        self->pose = 0;
        self->poseTimer = 0;
        MarAtPlayer(self, EXT8(self, 4));
        break;
    case 3:
        self->hitboxWidth = SATX(6);
        self->hitboxHeight = 4;
        self->step++;
    default:
        if (self->hitFlags) {
            DestroyEntity(self);
            return;
        }
        self->posX.val += self->velocityX;
        self->posY.val += self->velocityY;
        return;
    }
    self->step++;
}

// her kicks: stand, crouch, air up, air down (0x060C21AC), and the frames of
// her kick each is live for (0x060C21EC)
static MarAttackDef g_MarKickDefs[] = {
    {30, 16, 5, 28, -3, 32, 2, 26, 4, 1, 8},
    {30, 16, 6, 23, 23, 32, 2, 26, 4, 1, 8},
    {30, 16, 5, 26, 6, 32, 2, 26, 4, 1, 8},
    {30, 13, 5, 20, 20, 32, 2, 26, 4, 1, 8},
};
static s16 g_MarKickPoses[4][2] = {{3, 8}, {3, 5}, {3, 7}, {2, 5}};

// 060AD0E8: the kick's hitbox, live for some of her kick frames
void MarEntityKick(Entity* self) {
    s32 kind = (self->params >> 8) & 3;

    if (PLAYER.step_s != 0x41) {
        DestroyEntity(self);
        return;
    }
    self->posX.i.hi = PLAYER.posX.i.hi;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->facingLeft = PLAYER.facingLeft;
    if (self->step == 0) {
        self->flags = FLAG_KEEP_ALIVE_OFFCAMERA | FLAG_POS_PLAYER_LOCKED | FLAG_UNK_20000;
        if (PLAYER.pose < g_MarKickPoses[kind][0]) {
            return;
        }
        MarApplyAttackDef(self, &g_MarKickDefs[kind]);
        self->step++;
        return;
    }
    if (PLAYER.pose > g_MarKickPoses[kind][1]) {
        DestroyEntity(self);
    }
}

static MarAttackDef g_MarDiveKickDef = {30, 8, 8, 26, 26, 32, 2, 8, 4, 1, 8};

// 060AD248: the dive kick's hitbox; a hit makes her bounce (unk44 0x80)
void MarEntityDiveKick(Entity* self) {
    if (PLAYER.step_s != 0x70) {
        DestroyEntity(self);
        return;
    }
    self->flags = FLAG_POS_PLAYER_LOCKED | FLAG_UNK_20000;
    self->facingLeft = PLAYER.facingLeft;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->posX.i.hi = PLAYER.posX.i.hi;
    g_Player.unk44 &= ~0x80;
    if (self->step == 0) {
        MarApplyAttackDef(self, &g_MarDiveKickDef);
        if (self->params) {
            // straight down
            self->hitboxOffX = 1;
            self->hitboxOffY = 0x1B;
        }
        self->step++;
    } else if (self->hitFlags == 1) {
        g_Player.unk44 |= 0x80;
    }
}

static MarAttackDef g_MarCharged1Def = {30, 4, 8, 0, 0, 0x8000, 2, 16, 4, 0x81, 0x20};

// 060B9B88: charged attack, level 1: a projectile (package 1) that grows
void MarEntity82(Entity* self) {
    s32 kind;

    if (self->step == 0) {
        MarFxSet(self, 1);
        self->zPriority = PLAYER.zPriority - 2;
        self->flags = FLAG_POS_CAMERA_LOCKED;
        MarSfx(0x722);
        kind = MarAttackKind();
        EXT8(self, 4) = kind;
        MarAtPlayer(self, kind);
        // the art faces left
        self->facingLeft = PLAYER.facingLeft == 0;
        self->velocityX = self->facingLeft ? SATX(0x44000) : -SATX(0x44000);
        self->drawFlags = ENTITY_SCALEX | ENTITY_SCALEY;
        self->scaleX = self->scaleY = 0x40;
        self->animCurFrame = 1;
        MarApplyAttackDef(self, &g_MarCharged1Def);
        self->step++;
    } else {
        if (self->hitFlags) {
            DestroyEntity(self);
            return;
        }
        self->posX.val += self->velocityX;
        self->scaleX += 0x10;
        if (self->scaleX > 0x180) {
            self->scaleX = 0x180;
        }
        self->scaleY += 0x10;
        if (self->scaleY > 0x100) {
            self->scaleY = 0x100;
        }
    }
    if (++self->animCurFrame > 8) {
        self->animCurFrame = 1;
    }
}

static MarAttackDef g_MarCharged2Def = {40, 9, 9, 0, 0, 0x20, 2, 16, 4, 0x181, 8};
static AnimationFrame g_MarCharged2Anim[] = {
    POSE(2, 9, 0),  POSE(1, 16, 0), POSE(2, 10, 0), POSE(1, 16, 0),
    POSE(2, 11, 0), POSE(1, 16, 0), POSE(2, 12, 0), POSE_LOOP(0),
};

// 060B9D88: charged attack, level 2: a projectile leaving fading copies of
// itself (params: the copy's frame)
void MarEntity83(Entity* self) {
    s32 kind;
    s16 t;

    if (self->step == 0) {
        MarFxSet(self, 1);
        if (self->params == 0) {
            self->zPriority = PLAYER.zPriority - 2;
            self->flags = FLAG_POS_CAMERA_LOCKED | FLAG_UNK_100000;
            self->anim = g_MarCharged2Anim;
            self->pose = self->poseTimer = 0;
            self->velocityX = self->facingLeft ? -SATX(0x44000) : SATX(0x44000);
            kind = MarAttackKind();
            EXT8(self, 4) = kind;
            MarAtPlayer(self, kind);
            MarApplyAttackDef(self, &g_MarCharged2Def);
        } else {
            self->zPriority--;
            self->flags = FLAG_POS_CAMERA_LOCKED;
            self->animCurFrame = self->params >> 8;
            self->ext.timer.t = 12;
            MAR_FX[self - g_Entities].blend = 2; // half-transparent (0x80)
        }
        MarSfx(0x723);
        self->step++;
    } else if (self->params == 0) {
        self->posX.val += self->velocityX;
        if (!(self->pose & 1) && self->poseTimer == 1) {
            RicCreateEntFactoryFromEntity(
                self, FACTORY(BP_MAR_CHARGED + 2, self->animCurFrame), 0);
        }
    } else if (g_GameTimer & 1) {
        t = --self->ext.timer.t;
        if (t == -1) {
            DestroyEntity(self);
            return;
        }
    }
    if (self->params != 0) {
        // Saturn gouraud t * 0x21 - 0x4000: red and green t - 16 of 31
        // darker, blue left alone, so the copies go dim and blue
        t = self->ext.timer.t;
        MAR_FX[self - g_Entities].bright = 0x80 * (t > 0 ? t + 4 : 4) / 20;
        MAR_FX[self - g_Entities].brightB = 0x80;
    }
}

static MarAttackDef g_MarBeamDef = {45, 16, 8, 0, 0, 0x20, 2, 16, 4, 0x81, 0x20};

// 060B96C0: charged attack, level 3 (and command move 4's): a beam from her
// hand to the edge of the screen, thinning out over a few frames
void MarEntity72(Entity* self) {
    Primitive* prim;
    s32 kind, i, t;

    if (self->step == 0) {
        self->primIndex = g_api.AllocPrimitives(PRIM_G4, 2);
        if (self->primIndex == -1) {
            DestroyEntity(self);
            return;
        }
        self->flags = FLAG_POS_CAMERA_LOCKED | FLAG_HAS_PRIMS;
        EXT8(self, 5) = 4;
        self->facingLeft = PLAYER.facingLeft;
        kind = MarAttackKind();
        EXT8(self, 4) = kind;
        MarAtPlayer(self, kind);
        if ((u16)self->posX.i.hi > 255) {
            DestroyEntity(self);
            return;
        }
        MarApplyAttackDef(self, &g_MarBeamDef);
        if (self->facingLeft) {
            self->hitboxWidth = self->posX.i.hi / 2;
        } else {
            self->hitboxWidth = (256 - self->posX.i.hi) / 2;
        }
        self->hitboxOffX = self->hitboxWidth;
        for (prim = &g_PrimBuf[self->primIndex], i = 0; prim; prim = prim->next, i++) {
            prim->x0 = prim->x2 = self->facingLeft ? 0 : self->posX.i.hi;
            prim->x1 = prim->x3 = self->facingLeft ? self->posX.i.hi : 255;
            // white at the edges, pink in the middle (Saturn gouraud 0xC9DF)
            prim->r0 = prim->g0 = prim->b0 = prim->r1 = prim->g1 = prim->b1 = 0xF8;
            prim->r2 = prim->r3 = 0xF8;
            prim->g2 = prim->g3 = 0x70;
            prim->b2 = prim->b3 = 0x90;
            prim->priority = PLAYER.zPriority + 2;
            prim->drawMode = DRAW_DEFAULT;
        }
        RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_BEAM_SPARK, 1), 0);
        RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_BEAM_SPARK, 9), 0);
        self->step++;
    }
    if (!(g_GameTimer & 3) && (s8)(--EXT8(self, 5)) <= 0) {
        DestroyEntity(self);
        return;
    }
    t = (s8)EXT8(self, 5);
    prim = &g_PrimBuf[self->primIndex];
    // top half: edge above, middle at her hand; bottom half mirrored. The
    // Saturn's polygons include their edge rows, so both halves draw row y.
    prim->y0 = prim->y1 = self->posY.i.hi - t;
    prim->y2 = prim->y3 = self->posY.i.hi + 1;
    prim = prim->next;
    prim->y0 = prim->y1 = self->posY.i.hi + t + 1;
    prim->y2 = prim->y3 = self->posY.i.hi;
}

#define EXT16(e, i) (*(s16*)&((u8*)&(e)->ext)[i])
#define EXT32(e, i) (*(s32*)&((u8*)&(e)->ext)[i])
#define FX(e) (&MAR_FX[(e) - g_Entities])

// shared by the command moves: some freeze everything else while they play
static void MarTimeStop(s32 on) { g_unkGraphicsStruct.unk28 = on ? 4 : 0; }

// --- command move 0 (060BADA8): sparks gather in her hand, then a holy
// blast in front of her (package 8)
static u8 g_MarCmd0Frames[44] = {
    16, 16, 16, 16, 16, 16, 16, 16, 16, 1,  2,  3,  4,  1,  2,
    3,  4,  1,  2,  3,  4,  5,  6,  7,  8,  9,  10, 11, 12, 13,
    14, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 0,  0,  0,
};
static MarAttackDef g_MarCmd0Def = {50, 32, 16, 32, -8, 0x1000, 2, 16, 4, 1, 0x30};
static u8 g_MarSparkColors[4][3] = {
    {0x80, 0x80, 0xF8}, {0xC0, 0xC0, 0xF8}, {0x40, 0x40, 0xF8}, {0x80, 0x80, 0xF8}};

// 060BAC78: each line spark: angle (u0/v0), radius (u1), delay (v1); going
// in towards (x, y), or out once the blast fires
static void MarSparks(Primitive* prim, s32 x, s32 y, s32 out) {
    s32 r, a, c, s;

    for (; prim; prim = prim->next) {
        if (prim->drawMode & DRAW_HIDE) {
            if (--prim->v1 > 0) {
                continue;
            }
            prim->drawMode &= ~DRAW_HIDE;
            a = rand() & 0xFFF;
            prim->u0 = a & 0xFF;
            prim->v0 = a >> 8;
            prim->u1 = out ? 1 : ((rand() & 7) + 14) * 2;
            continue;
        }
        a = prim->u0 | (prim->v0 << 8);
        r = prim->u1;
        c = rcos(a);
        s = rsin(a);
        prim->x0 = x + SATX((r / 2) * c >> 12);
        prim->y0 = y + ((r / 2) * s >> 12);
        prim->x1 = x + SATX((r / 2 + 3) * c >> 12);
        prim->y1 = y + ((r / 2 + 3) * s >> 12);
        if (!out) {
            if (--prim->u1 < 4) {
                prim->drawMode |= DRAW_HIDE;
                prim->v1 = rand() & 7;
            }
        } else {
            prim->u1 += 3;
            if (prim->u1 > 0x28) {
                prim->drawMode |= DRAW_HIDE;
                prim->v1 = rand() & 7;
            }
        }
    }
}

void MarEntity73(Entity* self) {
    Primitive* prim;
    s32 hx, hy, c;

    if (PLAYER.anim != g_MariaAnim_060C3518) {
        DestroyEntity(self);
        return;
    }
    if (PLAYER.pose < 0x15) {
        hx = PLAYER.facingLeft ? SATX(8) : -SATX(8);
        hy = PLAYER.posY.i.hi - 12;
    } else {
        hx = PLAYER.facingLeft ? -SATX(0x23) : SATX(0x23);
        hy = PLAYER.posY.i.hi - 8;
    }
    hx += PLAYER.posX.i.hi;
    if (self->step == 0) {
        self->primIndex = g_api.AllocPrimitives(PRIM_LINE_G2, 16);
        if (self->primIndex == -1) {
            DestroyEntity(self);
            return;
        }
        for (prim = &g_PrimBuf[self->primIndex]; prim; prim = prim->next) {
            c = rand();
            prim->priority = PLAYER.zPriority + 2;
            prim->v1 = c & 7;
            prim->u0 = c & 0xFF;
            prim->v0 = (c >> 8) & 0xF;
            prim->u1 = ((c & 7) + 14) * 2;
            prim->r0 = prim->r1 = g_MarSparkColors[c & 3][0];
            prim->g0 = prim->g1 = g_MarSparkColors[c & 3][1];
            prim->b0 = prim->b1 = g_MarSparkColors[c & 3][2];
            prim->drawMode = DRAW_HIDE;
        }
        MarFxSet(self, 8);
        self->zPriority = PLAYER.zPriority + 4;
        self->flags = FLAG_HAS_PRIMS | FLAG_UNK_20000;
        self->step++;
    } else if (self->step == 1 && PLAYER.pose > 0x14) {
        MarApplyAttackDef(self, &g_MarCmd0Def);
        self->step++;
    }
    self->posX.i.hi = PLAYER.posX.i.hi;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->facingLeft = PLAYER.facingLeft;
    self->animCurFrame = g_MarCmd0Frames[PLAYER.pose];
    MarSparks(&g_PrimBuf[self->primIndex], hx, hy, self->step > 1);
}

// --- command move 1 (060BAFF0): a pillar of green light heals a quarter of
// her max HP (package 9)
static u8 g_MarCmd1Frames[32] = {
    19, 9, 8, 7, 6, 5, 4, 3, 2, 1, 1, 2, 3, 4, 1, 2,
    3,  4, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 0, 0,
};

void MarEntity74(Entity* self) {
    Primitive* prim;
    s32 cx, w, left;

    if (PLAYER.anim != g_MariaAnim_060C31AC &&
        PLAYER.anim != g_MariaAnim_060C31E8) {
        RicCreateEntFactoryFromEntity(self, FACTORY(BP_RIC_BLINK, 0x58), 0);
        DestroyEntity(self);
        return;
    }
    if (++EXT16(self, 0x1C) == 0x38) {
        MarSfx(0x5CF);
    }
    switch (self->step) {
    case 0:
        self->primIndex = g_api.AllocPrimitives(PRIM_G4, 2);
        if (self->primIndex == -1) {
            DestroyEntity(self);
            return;
        }
        MarFxSet(self, 9);
        self->zPriority = PLAYER.zPriority + 4;
        for (prim = &g_PrimBuf[self->primIndex]; prim; prim = prim->next) {
            // outer edge green, middle pale
            prim->r0 = prim->r2 = 0x60;
            prim->g0 = prim->g2 = 0xF8;
            prim->b0 = prim->b2 = 0x60;
            prim->r1 = prim->r3 = 0xD0;
            prim->g1 = prim->g3 = 0xF8;
            prim->b1 = prim->b3 = 0xD0;
            prim->priority = PLAYER.zPriority - 10;
            prim->y0 = prim->y1 = 0;
            prim->drawMode = DRAW_HIDE;
        }
        self->flags = FLAG_HAS_PRIMS | FLAG_UNK_20000;
        RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_BEAST, 2), 0);
        self->step++;
    case 1:
        if (PLAYER.anim != g_MariaAnim_060C31E8) {
            return;
        }
        g_Status.hp += g_Status.hpMax >> 2;
        if (g_Status.hp > g_Status.hpMax) {
            g_Status.hp = g_Status.hpMax;
        }
        for (prim = &g_PrimBuf[self->primIndex]; prim; prim = prim->next) {
            prim->drawMode = DRAW_DEFAULT;
        }
        MarSfx(0x72F);
        self->step++;
        return;
    case 2:
        if (++EXT16(self, 8) > 15) {
            self->step++;
        }
        break;
    default:
        if (PLAYER.pose == 0x1A) {
            if (EXT16(self, 8) == 0) {
                for (prim = &g_PrimBuf[self->primIndex]; prim; prim = prim->next) {
                    prim->drawMode = DRAW_HIDE;
                }
            } else {
                EXT16(self, 8)--;
            }
        }
        break;
    }
    self->posX.i.hi = PLAYER.posX.i.hi;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->facingLeft = PLAYER.facingLeft;
    self->animCurFrame = g_MarCmd1Frames[PLAYER.pose];
    w = SATX(EXT16(self, 8));
    cx = self->posX.i.hi + (PLAYER.facingLeft ? -3 : 2);
    left = cx - w;
    for (prim = &g_PrimBuf[self->primIndex]; prim; prim = prim->next) {
        prim->x0 = prim->x2 = left;
        prim->x1 = prim->x3 = cx;
        prim->y2 = prim->y3 = PLAYER.posY.i.hi + 0x18;
        left = cx + w;
    }
}

// --- command move 2 (060BB4AC): time stops and fire rains forward
// (fireballs: entity 78)
static u8 g_MarCmd2Frames[44] = {
    8, 7, 6, 5, 4, 3, 2, 1, 8, 7, 6, 5, 4, 3, 2, 1, 8, 7, 6, 5, 4, 3,
    2, 1, 8, 7, 6, 5, 4, 3, 2, 1, 9, 9, 9, 9, 9, 9, 0, 0, 0, 0, 0, 0,
};

void MarEntity75(Entity* self) {
    if (PLAYER.anim != g_MariaAnim_060C33D8 &&
        PLAYER.anim != g_MariaAnim_060C3438 &&
        PLAYER.anim != g_MariaAnim_060C34D4) {
        MarTimeStop(0);
        MAR_COLOR_OFFSET = 0;
        DestroyEntity(self);
        return;
    }
    if (++EXT16(self, 0x1C) == 0x68) {
        MarSfx(0x5CF);
    }
    switch (self->step) {
    case 0:
        MarFxSet(self, 10);
        self->zPriority = PLAYER.zPriority + 4;
        self->flags = FLAG_UNK_20000 | FLAG_UNK_10000;
        MarTimeStop(1);
        if ((rand() & 0x1F) == 0) {
            EXT16(self, 8) = 1; // the occasional double-damage volley
        }
        self->step++;
    case 1:
        // the screen darkens while she gathers it
        if (EXT16(self, 0x0C) > -0x1E) {
            EXT16(self, 0x0C)--;
        }
        MAR_COLOR_OFFSET = EXT16(self, 0x0C);
        if (PLAYER.anim == g_MariaAnim_060C3438) {
            MarSfx(0x72A);
            self->step++;
        } else if (PLAYER.pose == 15 && PLAYER.poseTimer == 1) {
            RicCreateEntFactoryFromEntity(
                self, FACTORY(BP_MAR_BEAST, 1), 0);
        }
        return;
    case 2:
        if (PLAYER.anim == g_MariaAnim_060C34D4) {
            MarTimeStop(0);
            MarSfx(0x72B);
            self->step++;
        }
        break;
    }
    self->posX.i.hi = PLAYER.posX.i.hi;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->facingLeft = PLAYER.facingLeft;
    if (PLAYER.anim == g_MariaAnim_060C3438) {
        self->animCurFrame = g_MarCmd2Frames[PLAYER.pose];
        return;
    }
    self->animCurFrame = 0;
    self->posX.i.hi += PLAYER.facingLeft ? -SATX(0x16) : SATX(0x16);
    self->posY.i.hi += 3;
    if (PLAYER.anim == g_MariaAnim_060C34D4) {
        RicCreateEntFactoryFromEntity(
            self, FACTORY(BP_MAR_FIREBALL, EXT16(self, 8)), 0);
    }
}

// 060BB334: one of command move 2's fireballs. The Saturn draws it from a
// shared sprite bank (0.BIN's bank 11, pixels in every stage's CHR), which
// build-mariasat.sh adds as package 13: palette 0 is the fire, 1 the white
// of a double-damage volley.
#define MAR_PKG_FIRE 13
static AnimationFrame g_MarFireballAnim[] = {
    POSE(2, 1, 0), POSE(2, 2, 0), POSE(2, 3, 0), POSE(2, 4, 0),
    POSE(2, 5, 0), POSE(2, 6, 0), POSE(2, 7, 0), POSE_END,
};
static MarAttackDef g_MarFireballDef = {20, 16, 16, 0, 0, 0x9000, 2, 16, 4, 1, 8};

void MarEntity78(Entity* self) {
    s32 a;

    if (self->step == 0) {
        MarFxSet(self, MAR_PKG_FIRE);
        FX(self)->bank = (self->params >> 8) ? 1 : 0;
        self->zPriority = PLAYER.zPriority + 2;
        self->anim = g_MarFireballAnim;
        self->pose = self->poseTimer = 0;
        self->flags = FLAG_POS_CAMERA_LOCKED | FLAG_UNK_100000 | FLAG_UNK_10000;
        a = (self->facingLeft ? 0x700 : 0xF00) + (rand() & 0x1FF);
        self->velocityX = SATX(rcos(a) * 0x280 >> 2);
        self->velocityY = rsin(a) * -0x80;
        self->posX.val += self->velocityX * 2;
        self->posY.val += self->velocityY * 2;
        MarApplyAttackDef(self, &g_MarFireballDef);
        if (self->params >> 8) {
            self->attack *= 2;
        }
        self->step++;
        return;
    }
    self->posX.val += self->velocityX;
    self->posY.val += self->velocityY;
    if (self->poseTimer < 0) {
        DestroyEntity(self);
    }
}

// --- command move 3 (060BBEF8): time stops and she summons the dragon
// (entity 79), which you steer
void MarEntity76(Entity* self) {
    if (PLAYER.anim != g_MariaAnim_060C32BC &&
        PLAYER.anim != g_MariaAnim_060C32F8 &&
        PLAYER.anim != g_MariaAnim_060C3390 &&
        PLAYER.anim != g_MariaAnim_060C33B0) {
        DestroyEntity(self);
        return;
    }
    if (++EXT16(self, 0x1C) == 0x50) {
        MarSfx(0x5CF);
    }
    if (self->step == 0) {
        self->zPriority = PLAYER.zPriority + 4;
        self->flags = FLAG_UNK_10000;
        RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_BEAST, 3), 0);
        MarTimeStop(1);
        self->step++;
    }
    if (self->step == 1) {
        if (PLAYER.anim == g_MariaAnim_060C3390) {
            RicCreateEntFactoryFromEntity(self, BP_MAR_DRAGON, 0);
            MarSfx(0x72C);
            self->step++;
        }
        return;
    }
    MarTimeStop(0);
    DestroyEntity(self);
}

// 060BB868: the dragon. Segment 0 is the head, steered with the d-pad; each
// of the 15 body segments follows 5 frames behind the one before. Even ones
// hurt. Drawn from package 12 (head frame 10, body frame 11).
#define MAR_DRAGON_HEAD_FRAME 19 // tools/mariasat-convert.py EXTRA_FRAMES
static MarAttackDef g_MarDragonDef = {25, 16, 16, 0, 0, 0x1000, 2, 16, 4, 1, 24};
#define DRAGON_TIME 597

static s32 MarPadAngle(void) {
    u32 p = g_Player.padPressed;

    if (p & PAD_UP) {
        if (p & PAD_RIGHT) {
            return 0xE00;
        }
        return (p & PAD_LEFT) ? 0xA00 : 0xC00;
    }
    if (p & PAD_DOWN) {
        if (p & PAD_RIGHT) {
            return 0x200;
        }
        return (p & PAD_LEFT) ? 0x600 : 0x400;
    }
    if (p & PAD_RIGHT) {
        return 0;
    }
    return (p & PAD_LEFT) ? 0x800 : -1;
}

void MarEntity79(Entity* self) {
    s32 n = self->params >> 8;
    s32 a, limit, i;
    Entity* prev;

    switch (self->step) {
    case 0:
        MarFxSet(self, 12);
        self->flags = FLAG_KEEP_ALIVE_OFFCAMERA | FLAG_UNK_10000;
        self->zPriority = PLAYER.zPriority + 20 - n;
        self->posX.val = PLAYER.posX.val;
        self->posY.val = PLAYER.posY.val;
        EXT16(self, 8) = 0;
        if (!(n & 1)) {
            MarApplyAttackDef(self, &g_MarDragonDef);
        }
        if (n == 0) {
            self->posX.i.hi += PLAYER.facingLeft ? -SATX(0x1C) : SATX(0x1C);
            self->posY.i.hi -= 0xC;
            self->velocityX = self->facingLeft ? -0x40000 : 0x40000;
        }
        for (i = 0; i < 5; i++) {
            EXT16(self, 0x14 + i * 6) = self->posX.i.hi;
            EXT16(self, 0x16 + i * 6) = self->posY.i.hi;
            EXT16(self, 0x18 + i * 6) = 0;
        }
        self->step++;
        break;
    case 1:
        EXT16(self, 8) += 0x10;
        limit = n < 0xC ? 0x100 : (n - 0xB) * -0x30 + 0x100;
        if (EXT16(self, 8) >= limit) {
            self->step++;
        }
        if (EXT16(self, 10) == 3 && n < 0xF) {
            RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_DRAGON, n + 1), 0);
        }
    case 2:
        if (EXT16(self, 10) > DRAGON_TIME) {
            self->step = 3;
            EXT16(self, 10) = 0;
        }
        break;
    case 3:
        if (EXT16(self, 8) > 0xF) {
            EXT16(self, 8) -= 0x10;
        }
        if (EXT16(self, 8) == 0) {
            self->attack = 0;
            FX(self)->pkg = 0;
        }
        if (EXT16(self, 10) > 0xF && EXT16(self, 8) == 0) {
            self->step++;
            if (n == 0) {
                RicCreateEntFactoryFromEntity(
                    self, FACTORY(BP_MAR_STARBURST, 0x26), 0);
            }
        }
        break;
    case 10:
        DestroyEntity(self);
        return;
    default:
        self->step++;
        break;
    }
    EXT16(self, 10)++;

    // remember where this segment was, for the next one
    i = EXT8(self, 0x3A);
    EXT16(self, 0x14 + i * 6) = self->posX.i.hi;
    EXT16(self, 0x16 + i * 6) = self->posY.i.hi;
    EXT16(self, 0x18 + i * 6) = self->rotate - 0x400;
    EXT8(self, 0x3A) = (i + 1) % 5;

    if (n == 0) {
        a = MarPadAngle();
        if (a >= 0) {
            self->velocityX += rcos(a) * 8;
            self->velocityY += rsin(a) * 8;
            self->rotate = ratan2(self->velocityY, self->velocityX);
            self->velocityX = rcos(self->rotate) * 0x40;
            self->velocityY = rsin(self->rotate) * 0x40;
        } else {
            self->rotate = ratan2(self->velocityY, self->velocityX);
        }
        self->posX.val += SATX(self->velocityX);
        self->posY.val += self->velocityY;
    } else {
        prev = self->ext.factory.parent;
        i = (EXT8(prev, 0x3A)) % 5; // its oldest entry
        self->posX.i.hi = EXT16(prev, 0x14 + i * 6);
        self->posY.i.hi = EXT16(prev, 0x16 + i * 6);
        self->rotate = EXT16(prev, 0x18 + i * 6);
    }
    // The Saturn draws each segment as a quad textured with one image, its
    // top edge at the front: the head (image 3) at its own size, a body
    // segment's image (48x40) squeezed to 40 across and 48 along. Drawn here
    // as the frame holding that image, turned so its top leads (rotate is
    // the heading, kept as that in the trail).
    self->facingLeft = 0;
    self->drawFlags = ENTITY_ROTATE | ENTITY_SCALEX | ENTITY_SCALEY;
    self->rotate += 0x400;
    if (n == 0) {
        self->scaleX = self->scaleY = EXT16(self, 8);
        self->animCurFrame = MAR_DRAGON_HEAD_FRAME;
    } else {
        self->scaleX = EXT16(self, 8) * 40 / 48;
        self->scaleY = EXT16(self, 8) * 48 / 40;
        self->animCurFrame = MAR_DRAGON_HEAD_FRAME + 1;
    }
    FX(self)->bank = -1;
}

// 060BB724: a star burst (package 12): grows, holds for params frames,
// shrinks
static u8 g_MarStarFrames[6] = {13, 14, 15, 16, 17, 18};

void MarEntity80(Entity* self) {
    switch (self->step) {
    case 0:
        MarFxSet(self, 12);
        self->zPriority = 0x1BC;
        self->flags =
            FLAG_POS_CAMERA_LOCKED | FLAG_KEEP_ALIVE_OFFCAMERA | FLAG_UNK_10000;
        self->drawFlags = ENTITY_SCALEX | ENTITY_SCALEY;
        self->scaleX = self->scaleY = 0x80;
        self->step++;
        break;
    case 1:
        self->scaleX += 0x10;
        self->scaleY += 0x10;
        if (self->scaleX > 0x17F) {
            self->step++;
            EXT16(self, 4) = self->params >> 8;
        }
        break;
    case 2:
        if (--EXT16(self, 4) < 0) {
            self->step++;
        }
        break;
    default:
        self->scaleX -= 0x10;
        self->scaleY -= 0x10;
        if (self->scaleX <= 0) {
            DestroyEntity(self);
            return;
        }
        break;
    }
    self->animCurFrame = g_MarStarFrames[EXT16(self, 6)];
    if (++EXT16(self, 6) > 5) {
        EXT16(self, 6) = 0;
    }
}

// --- command move 4: the familiar (060BC320), a bird that follows her
// around and flies at enemies for 2000 frames; its trail is entity 81
static MarAttackDef g_MarFamiliarDef = {30, 0, 0, 0, 0, 0x1000, 2, 16, 4, 1, 0x18};
static AnimationFrame g_MarFamAnim0[] = {
    POSE(8, 4, 0), POSE(8, 5, 0), POSE(8, 6, 0), POSE(8, 7, 0), POSE_LOOP(0),
};
static AnimationFrame g_MarFamAnim1[] = {
    POSE(8, 8, 0), POSE(8, 9, 0), POSE(8, 10, 0), POSE(8, 11, 0), POSE_LOOP(0),
};
static AnimationFrame g_MarFamAnim2[] = {POSE(5, 4, 0), POSE_LOOP(0)};
static AnimationFrame g_MarFamAnim3[] = {POSE(5, 8, 0), POSE_LOOP(0)};
static AnimationFrame g_MarFamAnim4[] = {POSE(5, 9, 0), POSE_LOOP(0)};
static AnimationFrame g_MarFamAnim5[] = {
    POSE(4, 4, 0), POSE(4, 5, 0),  POSE(4, 6, 0), POSE(4, 7, 0), POSE(4, 4, 0),
    POSE(4, 5, 0), POSE(4, 6, 0),  POSE(4, 7, 0), POSE(5, 4, 0), POSE(5, 5, 0),
    POSE(5, 6, 0), POSE(5, 7, 0),  POSE(6, 4, 0), POSE(6, 5, 0), POSE(7, 6, 0),
    POSE(8, 7, 0), POSE(10, 1, 0), POSE(8, 2, 0), POSE(8, 3, 0), POSE(8, 2, 0),
    POSE_LOOP(16),
};
static u16 g_MarFamiliarCount;

static void MarSetAnim(Entity* e, AnimationFrame* anim) {
    if (e->anim != anim) {
        e->anim = anim;
        e->pose = e->poseTimer = 0;
    }
}

// 060BC0F4: steer towards her (ext 0x0C/0x0E: where, relative to her);
// 0: arrived, 2: settled, 1: still flying
static s32 MarFamiliarFollow(Entity* self) {
    s32 dx = PLAYER.posX.i.hi + EXT16(self, 0x0C) - self->posX.i.hi;
    s32 dy = PLAYER.posY.i.hi + EXT16(self, 0x0E) - self->posY.i.hi;
    s32 d2 = dx * dx + dy * dy;
    s32 res = 1, speed, c, s;

    self->rotate = ratan2(-dy, dx);
    if (d2 < 0x65) {
        res = 0;
        if (dx > 2) {
            self->velocityX = 0x4000;
        } else if (dx < -2) {
            self->velocityX = -0x4000;
        } else {
            res = 1;
        }
        if (dy > 2) {
            self->velocityY = 0x4000;
        } else if (dy < -2) {
            self->velocityY = -0x4000;
        } else {
            res++;
        }
        return res == 2 ? 2 : 0;
    }
    speed = d2 < 0x191 ? 10 : d2 < 0xE11 ? 0x10 : 0x28;
    c = speed * rcos(self->rotate);
    s = -speed * rsin(self->rotate);
    if (self->velocityX > c) {
        self->velocityX -= 0x1000;
    } else if (self->velocityX < c) {
        self->velocityX += 0x1000;
    }
    if (self->velocityY > s) {
        self->velocityY -= 0x1000;
    } else if (self->velocityY < s) {
        self->velocityY += 0x1000;
    }
    return 1;
}

// 060BC024: an enemy on screen ahead of it to go for
static Entity* MarFamiliarTarget(Entity* self) {
    Entity* e;
    Entity* last = NULL;
    s32 dir = self->facingLeft ? 1 : -1;

    for (e = &g_Entities[STAGE_ENTITY_START]; e < &g_Entities[TOTAL_ENTITY_COUNT]; e++) {
        if (!e->entityId || !e->hitboxState) {
            continue;
        }
        if (e->posX.i.hi <= -0x11 || e->posX.i.hi > 0x110 ||
            e->posY.i.hi >= 0xF1 || e->posY.i.hi < 0 || e->hitPoints >= 0x7000) {
            continue;
        }
        if (!(e->flags & FLAG_UNK_80000)) {
            if ((e->posX.i.hi - self->posX.i.hi) * dir > 0) {
                e->flags |= FLAG_UNK_80000;
                return e;
            }
        } else {
            last = e;
        }
    }
    return last;
}

// 060BC23C: which way it faces
static void MarFamiliarFacing(Entity* self) {
    s32 dx, dy;

    if (self->step >= 5) {
        if (self->velocityX) {
            self->facingLeft = self->velocityX > 0;
        }
        return;
    }
    if (++EXT16(self, 6) < 0x33) {
        return;
    }
    EXT16(self, 6) = 0;
    dx = PLAYER.posX.i.hi + EXT16(self, 0x0C) - self->posX.i.hi;
    dy = PLAYER.posY.i.hi + EXT16(self, 0x0E) - self->posY.i.hi;
    if (abs(dx) < 0x1E && abs(dy) < 0x1E) {
        if (self->facingLeft == PLAYER.facingLeft) {
            self->facingLeft = !self->facingLeft;
        }
    } else if (self->velocityX) {
        self->facingLeft = self->velocityX > 0;
    }
}

void MarEntity77(Entity* self) {
    Entity* t;
    s32 trail = 0, follow = 0, r, dx, dy, side;

    if (++EXT16(self, 0x1C) == 0x58) {
        MarSfx(0x5CF);
    }
    switch (self->step) {
    case 0:
        MarFxSet(self, 11);
        EXT16(self, 8) = g_MarFamiliarCount;
        self->zPriority =
            PLAYER.zPriority + ((g_MarFamiliarCount++ & 1) ? 4 : -4);
        self->scaleX = self->scaleY = 0x100;
        self->flags = FLAG_POS_CAMERA_LOCKED | FLAG_KEEP_ALIVE_OFFCAMERA |
                      FLAG_UNK_20000;
        MarSetAnim(self, g_MarFamAnim0);
        self->posX.i.hi = PLAYER.posX.i.hi + (PLAYER.facingLeft ? -SATX(9) : SATX(9));
        self->posY.i.hi = PLAYER.posY.i.hi - 0x32;
        self->facingLeft = PLAYER.facingLeft == 0;
        RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_BEAST, 4), 0);
        self->step++;
        break;
    case 1:
        if (PLAYER.animCurFrame == 0x100) {
            RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_STARBURST, 4), 0);
            MarSfx(0x72D);
            self->step++;
            self->step_s = 0;
        }
        break;
    case 2:
        if (++self->step_s > 0xF) {
            MarApplyAttackDef(self, &g_MarFamiliarDef);
            self->velocityY = -0x20000;
            self->flags |= FLAG_UNK_100000;
            self->step++;
        }
        break;
    case 3:
        self->velocityY += 0x2000;
        if (self->velocityY >= 0) {
            EXT16(self, 0x0C) = self->posX.i.hi - PLAYER.posX.i.hi;
            EXT16(self, 0x0E) = self->posY.i.hi - PLAYER.posY.i.hi;
            self->step++;
            self->step_s = 0;
        }
        break;
    case 4:
        // with her; now and then it picks a target
        follow = 1;
        r = MarFamiliarFollow(self);
        self->hitFlags = 0;
        if (self->step_s == 0) {
            if (++EXT16(self, 0x10) > 0x64 && !(rand() & 0x1F)) {
                EXT16(self, 0x10) = 0;
                t = MarFamiliarTarget(self);
                EXT32(self, 0x14) = (s32)t;
                if (t) {
                    EXT16(self, 0x18) = t->entityId;
                    dx = t->posX.i.hi + (t->facingLeft ? -t->hitboxOffX : t->hitboxOffX) -
                         self->posX.i.hi;
                    dy = self->posY.i.hi - (t->posY.i.hi + t->hitboxOffY);
                    self->rotate = ratan2(dy, dx);
                    self->velocityX = rcos(self->rotate) * 0x30;
                    self->velocityY = rsin(self->rotate) * -0x30;
                    MarSetAnim(self, g_MarFamAnim0);
                    self->step++;
                    self->step_s = 0;
                    break;
                }
            }
        }
        // step_s: 0, 1 while she's just stopped; 2 perched on her shoulder
        // (the side it's on: in front of her or behind) until she moves,
        // when it picks somewhere above her to hover and starts over
        if (self->step_s >= 2) {
            if (PLAYER.step != PL_S_STAND || PLAYER.step_s != 1) {
                EXT16(self, 0x0C) = rand() & 0x1F;
                EXT16(self, 0x0E) = -0x1E - (rand() & 0xF);
                MarSetAnim(self, g_MarFamAnim0);
                self->poseTimer = 4;
                self->pose = rand() & 3;
                self->step_s = 0;
                break;
            }
            side = PLAYER.zPriority < self->zPriority ? 6 : -6;
            EXT16(self, 0x0C) = PLAYER.facingLeft ? side : -side;
            EXT16(self, 0x0E) = -15;
            if (r == 2) {
                if (self->anim != g_MarFamAnim5) {
                    MarSetAnim(self, g_MarFamAnim5);
                }
                EXT16(self, 4)--;
            }
        } else if (PLAYER.step == PL_S_STAND && PLAYER.step_s == 1) {
            self->step_s++;
        }
        break;
    case 5:
        // flying at its target
        trail = 1;
        self->hitboxWidth = SATX(8);
        self->hitboxHeight = 8;
        t = (Entity*)EXT32(self, 0x14);
        if (!self->hitFlags && t && EXT16(self, 0x18) == t->entityId) {
            dx = t->posX.i.hi + (t->facingLeft ? -t->hitboxOffX : t->hitboxOffX) -
                 self->posX.i.hi;
            dy = t->posY.i.hi + t->hitboxOffY - self->posY.i.hi;
            if (abs(dx) > 0x14 || abs(dy) > 0x14) {
                r = ratan2(-dy, dx);
                self->velocityX += rcos(r) * 0x10;
                self->velocityY -= rsin(r) * 0x10;
                self->rotate = ratan2(-self->velocityY, self->velocityX);
                self->velocityX = rcos(self->rotate) * 0x30;
                self->velocityY = rsin(self->rotate) * -0x30;
            }
            break;
        }
        self->step++;
        break;
    case 6:
        // out past the target, then back
        trail = 1;
        if ((u16)(self->posX.i.hi + 0x20) < 0x140 &&
            (u16)(self->posY.i.hi + 0x20) < 0x131) {
            break;
        }
        EXT16(self, 0x0C) = rand() & 0x1F;
        EXT16(self, 0x0E) = -0x1E - (rand() & 0xF);
        MarSetAnim(self, g_MarFamAnim1);
        self->hitboxWidth = self->hitboxHeight = 0;
        self->step++;
        break;
    case 7:
        follow = 1;
        if (MarFamiliarFollow(self) == 0) {
            MarSetAnim(self, g_MarFamAnim0);
            self->step = 4;
            self->step_s = 0;
        }
        break;
    case 100:
        // time's up: slows to a stop, then shrinks away
        if (abs(self->velocityX) > 0xC000) {
            self->velocityX += self->velocityX > 0 ? -0x8000 : 0x8000;
        }
        if (abs(self->velocityY) > 0xC000) {
            self->velocityY += self->velocityY > 0 ? -0x8000 : 0x8000;
        }
        if (abs(self->velocityX) <= 0xC000 && abs(self->velocityY) <= 0xC000) {
            RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_STARBURST, 4), 0);
            self->drawFlags = ENTITY_SCALEX | ENTITY_SCALEY;
            self->step++;
        }
        break;
    case 101:
        self->scaleX -= 9;
        self->scaleY -= 9;
        if (self->scaleX < 0) {
            DestroyEntity(self);
            return;
        }
        break;
    }
    if (trail && !(g_GameTimer & 3)) {
        RicCreateEntFactoryFromEntity(
            self, FACTORY(BP_MAR_FAMILIAR_TRAIL, self->animCurFrame & 0xFF), 0);
    }
    self->posX.val += SATX(self->velocityX);
    self->posY.val += self->velocityY;
    if (follow) {
        // drag
        if (self->velocityX > 0x800) {
            self->velocityX -= 0x800;
        } else if (self->velocityX < -0x800) {
            self->velocityX += 0x800;
        } else {
            self->velocityX = 0;
        }
        if (self->velocityY > 0x800) {
            self->velocityY -= 0x800;
        } else if (self->velocityY < -0x800) {
            self->velocityY += 0x800;
        } else {
            self->velocityY = 0;
        }
    }
    MarFamiliarFacing(self);
    if (++EXT16(self, 4) > 2000 && self->step < 100) {
        self->step = 100;
    }
    if ((self->anim == g_MarFamAnim0 || self->anim == g_MarFamAnim1 ||
         self->anim == g_MarFamAnim5) &&
        (self->animCurFrame == 5 || self->animCurFrame == 9) &&
        self->poseTimer == 1 && (u16)(self->posX.i.hi + 0xF) < 0x11F &&
        (u16)(self->posY.i.hi + 0xF) < 0x11F) {
        MarSfx(0x72E); // wings
    }
}

// 060BCBD8: the familiar's fading trail (params: its frame)
void MarEntity81(Entity* self) {
    if (self->step == 0) {
        MarFxSet(self, 11);
        FX(self)->blend = 1;
        self->zPriority = PLAYER.zPriority - 5;
        self->flags = FLAG_POS_CAMERA_LOCKED;
        self->animCurFrame = self->params >> 8;
        EXT16(self, 4) = 10;
        self->step++;
    } else if ((g_GameTimer & 1) && --EXT16(self, 4) == 0) {
        DestroyEntity(self);
        return;
    }
    FX(self)->bright = EXT16(self, 4) * 0x80 / 16;
}

// --- full charge (060BD150): while fully charged her hand flashes; touching
// an enemy with it sparks (entity 89)
static MarAttackDef g_MarChargedHandDef = {255, 8, 16, 22, -4, 0, 4, 6, 0, 1, 1};

void MarEntity88(Entity* self) {
    switch (self->step) {
    case 0:
        self->flags = FLAG_POS_PLAYER_LOCKED | FLAG_UNK_20000;
        MarApplyAttackDef(self, &g_MarChargedHandDef);
        EXT16(self, 4) = 0x12C;
        RicCreateEntFactoryFromEntity(self, BP_MAR_SPARK, 0);
        self->step++;
        break;
    case 1:
        if (MarChargeLevel() != 3) {
            self->step++;
        }
        break;
    case 2:
        if (--EXT16(self, 4) < 0) {
            RicCreateEntFactoryFromEntity(self, FACTORY(BP_MAR_SPARK, 0x100), 0);
            self->step++;
        }
        break;
    default:
        DestroyEntity(self);
        return;
    }
    if (self->hitFlags) {
        RicCreateEntFactoryFromEntity(self, BP_MAR_SPARK, 0);
        self->hitFlags = 0;
    }
    self->posX.i.hi = PLAYER.posX.i.hi;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->facingLeft = PLAYER.facingLeft;
}

// 060BD244: a diamond-shaped flash at her hand (params 0x100: shrinking)
void MarEntity89(Entity* self) {
    Primitive* prim;
    s32 cx, cy, w, h;

    if (self->step == 0) {
        self->primIndex = g_api.AllocPrimitives(PRIM_G4, 1);
        if (self->primIndex == -1) {
            DestroyEntity(self);
            return;
        }
        prim = &g_PrimBuf[self->primIndex];
        prim->r0 = prim->r1 = prim->r2 = prim->r3 = 0xA0;
        prim->g0 = prim->g1 = prim->g2 = prim->g3 = 0xA0;
        prim->b0 = prim->b1 = prim->b2 = prim->b3 = 0xF8;
        prim->priority = PLAYER.zPriority + 4;
        prim->drawMode = DRAW_TPAGE | DRAW_TPAGE2 | DRAW_TRANSP;
        self->flags = FLAG_HAS_PRIMS | FLAG_POS_PLAYER_LOCKED | FLAG_UNK_20000;
        if ((self->params & 0xFF00) == 0) {
            EXT16(self, 8) = 1;
            self->step = 3;
        } else if ((self->params & 0xFF00) == 0x100) {
            EXT16(self, 8) = 0xB;
            EXT16(self, 10) = 0x16;
            self->step = 2;
        } else {
            EXT16(self, 8) = 1;
            self->step = 1;
        }
    } else if (self->step == 2) {
        if (g_GameTimer & 1) {
            if (EXT16(self, 8)) {
                EXT16(self, 8)--;
            }
            if (EXT16(self, 10)) {
                EXT16(self, 10)--;
            }
            if (EXT16(self, 10) == 0) {
                DestroyEntity(self);
                return;
            }
        }
    } else if (self->step == 1 || (g_GameTimer & 1)) {
        if (EXT16(self, 8) < 0xB) {
            EXT16(self, 8)++;
        }
        if (EXT16(self, 10) < 0x16) {
            EXT16(self, 10) += 2;
        }
        if (EXT16(self, 8) >= 0xB && EXT16(self, 10) >= 0x16) {
            DestroyEntity(self);
            return;
        }
    }
    cx = PLAYER.posX.i.hi + (PLAYER.facingLeft ? -SATX(0x16) : SATX(0x16));
    cy = PLAYER.posY.i.hi - 4;
    w = SATX(EXT16(self, 8));
    h = EXT16(self, 10);
    prim = &g_PrimBuf[self->primIndex];
    prim->x0 = cx;
    prim->y0 = cy - h;
    prim->x1 = cx + w;
    prim->y1 = cy;
    prim->x2 = cx - w;
    prim->y2 = cy;
    prim->x3 = cx;
    prim->y3 = cy + h;
}

// --- command move 5 (060BCFF4): invincible for 2000 frames, and her body
// hurts what it touches
static MarAttackDef g_MarAuraDef = {20, 8, 8, 0, 0, 0x1000, 2, 20, 20, 1, 0};

void MarEntity91(Entity* self) {
    if (self->step == 0) {
        MarApplyAttackDef(self, &g_MarAuraDef);
        self->flags = FLAG_KEEP_ALIVE_OFFCAMERA | FLAG_UNK_20000;
        self->step++;
    } else if (self->step == 1) {
        if (PLAYER.poseTimer < 0) {
            g_Player.timers[PL_T_INVINCIBLE_SCENE] = 0x7D0;
            self->step++;
        }
    } else if (g_Player.timers[PL_T_INVINCIBLE_SCENE] == 0) {
        DestroyEntity(self);
        return;
    }
    self->posX.i.hi = PLAYER.posX.i.hi;
    self->posY.i.hi = PLAYER.posY.i.hi;
    self->facingLeft = PLAYER.facingLeft;
    self->hitboxOffX = PLAYER.hitboxOffX;
    self->hitboxOffY = PLAYER.hitboxOffY;
    self->hitboxWidth = PLAYER.hitboxWidth;
    self->hitboxHeight = PLAYER.hitboxHeight;
}

// 060AE3C0 (replaces Richter's entity 22): a guardian beast (package 6)
// zooms in from the edge of the screen, holds, and fades away
static struct {
    s16 x, y;
    s32 vx, vy;
    s16 hold, image;
} g_MarBeasts[4] = {
    {-64, 0, 0x28000, 0, 96, 0},
    {64, 0, -0x28000, 0, 72, 1},
    {0, -64, 0, 0x28000, 48, 2},
    {0, 64, 0, -0x28000, 24, 3},
};

void MarEntity22(Entity* self) {
    s32 kind = self->params;
    s32 size;

    if (kind & 0xFF00) {
        kind = (kind >> 8) - 1;
    }
    kind &= 3;
    switch (self->step) {
    case 0:
        MarFxSet(self, 6);
        FX(self)->blend = 2;
        MarSfx(0x729);
        EXT16(self, 0) = 0x100;
        self->zPriority = PLAYER.zPriority + 8;
        self->velocityX = g_MarBeasts[kind].vx; // as on the PS1 (RicEntityApplyMariaPowerAnim)
        self->velocityY = g_MarBeasts[kind].vy;
        self->posX.i.hi += g_MarBeasts[kind].x;
        self->posY.i.hi += g_MarBeasts[kind].y;
        self->flags = FLAG_KEEP_ALIVE_OFFCAMERA | FLAG_UNK_10000;
        self->animCurFrame = g_MarBeasts[kind].image + 1;
        self->drawFlags = ENTITY_SCALEX | ENTITY_SCALEY;
        if (!(self->params & 0xFF00) && kind == 1) {
            MarSfx(0x5CE);
        }
        self->step++;
        break;
    case 1:
        EXT16(self, 0) -= 8;
        self->posX.val += self->velocityX;
        self->posY.val += self->velocityY;
        if (EXT16(self, 0) <= 0x18) {
            EXT16(self, 2) = g_MarBeasts[kind].hold;
            self->step++;
        }
        break;
    case 2:
        if ((self->params & 0xFF00) || --EXT16(self, 2) == 0) {
            self->step++;
        }
        break;
    default:
        EXT16(self, 0) -= 2;
        if (EXT16(self, 0) < 0) {
            DestroyEntity(self);
            return;
        }
        break;
    }
    size = EXT16(self, 0);
    self->scaleX = self->scaleY = size * 4;
}

static MarAttackDef g_MarBoltDef = {30, 4, 8, 0, 0, 0x8000, 2, 16, 4, 0x81, 0x20};

// 060B99A8: lightning along the level-3 beam: each link draws a line on from
// the one before (its factory parent) and spawns the next until off screen
void MarEntity85(Entity* self) {
    Entity* parent = self->ext.factory.parent;
    Primitive* prim;
    s32 x, y, c;

    if (self->step == 0) {
        self->primIndex = g_api.AllocPrimitives(PRIM_LINE_G2, 1);
        if (self->primIndex == -1 || !parent || !parent->entityId) {
            DestroyEntity(self);
            return;
        }
        self->flags = FLAG_HAS_PRIMS | FLAG_POS_CAMERA_LOCKED;
        if (self->params & 0x100) {
            MarApplyAttackDef(self, &g_MarBoltDef);
            self->hitboxWidth = SATX(4);
            self->hitboxHeight = 4;
        }
        prim = &g_PrimBuf[self->primIndex];
        prim->r0 = prim->g0 = prim->b0 = prim->r1 = prim->g1 = prim->b1 = 0xF8;
        prim->priority = PLAYER.zPriority + 3;
        prim->drawMode = DRAW_TPAGE | DRAW_TPAGE2 | DRAW_TRANSP;
        // from where the link before ended (the beam: her hand), to 8 on
        // and the beam's height plus a sine of params (so each chain waves
        // up to 16 above or below it); the end is kept in unk80/unk82
        if (parent->entityId == self->entityId) {
            prim->x0 = parent->ext.factory.unk80;
            prim->y0 = parent->ext.factory.unk82;
        } else {
            prim->x0 = parent->posX.i.hi;
            prim->y0 = parent->posY.i.hi;
        }
        self->posY.i.hi = parent->posY.i.hi;
        x = prim->x0 + (self->facingLeft ? -SATX(8) : SATX(8));
        y = self->posY.i.hi + (rsin(self->params) >> 8);
        self->posX.i.hi = prim->x1 = x;
        prim->y1 = y;
        self->ext.factory.unk80 = x;
        self->ext.factory.unk82 = y;
        if (x > 0 && x < 255 && y > 0 && y < 240) {
            RicCreateEntFactoryFromEntity(
                self,
                FACTORY(BP_MAR_BEAM_SPARK, ((self->params >> 8) + 1) & 0xFF),
                0);
        }
        MarSfx(0x724);
        self->step++;
        return;
    }
    if (++self->step_s >= 8) {
        DestroyEntity(self);
        return;
    }
    prim = &g_PrimBuf[self->primIndex];
    c = 0xF8 - self->step_s * 0x1C;
    prim->r0 = prim->g0 = prim->b0 = prim->r1 = prim->g1 = prim->b1 = c;
}

// not ported: the vibhuti's puff from the Saturn's shared sprites (87;
// Richter's vibhuti runs instead), the super mode's countdown (90) and her
// charge aura (92)
MAR_TODO(86)
MAR_TODO(87)
MAR_TODO(90)
MAR_TODO(92)
