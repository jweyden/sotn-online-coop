// SPDX-License-Identifier: AGPL-3.0-or-later
// The Saturn's long-coat Richter: Richter's PS1 code, his Saturn art
// (tools/richter2-convert.py; sprites in high RAM at 0x80600000).
#include "ric.h"

extern u8* richter_sprites[];
void RicMain(void);
void RicInit(u16 params);
void RicUpdatePlayerEntities(void);
void RicGetPlayerSensor(Collider* col);
static void Ric2Main(void);
static void Ric2Init(u16 params);

__attribute__((section(".header.0"))) PlayerOvl RIC_player = {
    Ric2Main,
    Ric2Init,
    RicUpdatePlayerEntities,
    RicGetPlayerSensor,
    richter_sprites,
    richter_sprites,
    richter_sprites,
    richter_sprites,
};

#include "gen/richter2_gfx.h"

// his palette goes where Richter's is; dra may load Richter's over it
static void Ric2LoadPalette(void) {
    u16* dst = &g_Clut[0][PAL_RICHTER * COLORS_PER_PAL];
    RECT rect = {0, 0xF0 + ((PAL_RICHTER & 0xFF) >> 4), COLORS_PER_PAL, 1};
    s32 i;

    for (i = 1; i < COLORS_PER_PAL; i++) {
        if (dst[i] != g_Ric2Palette[i]) {
            break;
        }
    }
    if (i == COLORS_PER_PAL) {
        return;
    }
    for (i = 0; i < COLORS_PER_PAL; i++) {
        dst[i] = g_Ric2Palette[i];
    }
    LoadImage(&rect, (u_long*)dst);
}

static void Ric2Init(u16 params) {
    RicInit(params);
    Ric2LoadPalette();
}

static void Ric2Main(void) {
    Ric2LoadPalette();
    RicMain();
}
