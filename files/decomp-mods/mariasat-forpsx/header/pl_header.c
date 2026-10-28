// SPDX-License-Identifier: AGPL-3.0-or-later
#include "ric.h"

extern u8* richter_sprites[];
void RicMain(void);
void RicInit(u16 params);
void RicUpdatePlayerEntities(void);
void RicGetPlayerSensor(Collider* col);

__attribute__((section(".header.0"))) PlayerOvl RIC_player = {
    RicMain,
    RicInit,
    RicUpdatePlayerEntities,
    RicGetPlayerSensor,
    richter_sprites,
    richter_sprites,
    richter_sprites,
    richter_sprites,
};

#include "gen/mariasat_gfx.h"
