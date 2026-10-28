#!/bin/sh
# Runs inside the character builder (Dockerfile next to this), started by Setup.
# Builds every extra character from your own discs, mounted read-only at
#   /discs/ps1.bin     PlayStation, US (Track 1)
#   /discs/psp.iso     PSP, The Dracula X Chronicles, EU
#   /discs/saturn.bin  Saturn, JP (Track 1)
# with the kit's files at /work (sotn-decomp/, decomp-mods/, tools/). Everything it
# makes goes to /work/characters, for Setup to put into the game:
#   maria/     PSP Maria: MARIA.BIN (+ .elf, .map), her voice bank SD_RIH.VH/VB
#   psp/       the PSP recast's Alucard voice (sd_alk.spk)
#   mariasat/  Saturn Maria: MARIASAT.BIN (+ .elf, .map), .SPR, .FX, .SND
#   richter2/  the Saturn's long-coat Richter: RICHTER2.BIN (+ .elf, .map), .SPR
#   charsel/   the Saturn character select screen: CHARSEL.BIN
# Run again any time: what's done is skipped (delete /work/characters to redo it).
set -eu
W=/work; D=$W/sotn-decomp; T=$W/tools; O=$W/characters
CC_MIPS="mipsel-linux-gnu-gcc -no-pie"   # (Debian's GCC links PIE by default)
say() { printf '\n==> %s\n' "$1"; }
mkdir -p "$O"

# ---- the discs' files
if [ ! -d "$D/disks/pspeu/PSP_GAME" ]; then say "Reading your PSP disc"; python3 -I "$T/extract-iso.py" /discs/psp.iso extract "$D/disks/pspeu"; fi
if [ ! -f "$D/disks/saturn/MARIA.PRG" ]; then say "Reading your Saturn disc"; python3 -I "$T/extract-iso.py" /discs/saturn.bin extract "$D/disks/saturn"; fi
if [ ! -f "$D/disks/us/BIN/RIC.BIN" ]; then say "Reading your PlayStation disc"; python3 -I "$T/extract-iso.py" /discs/ps1.bin extract "$D/disks/us"; fi
SE="$D/disks/pspeu/PSP_GAME/USRDIR/res/ps/snd_e/SE"

# ---- PSP Maria (sotn-decomp's mods/mariapsp-forpsx) and the PSP voices
if [ ! -f "$O/maria/MARIA.BIN" ]; then
    say "Building PSP Maria"
    M="$D/mods/mariapsp-forpsx"
    (cd "$M" && make build/us/.assets_done >/dev/null && touch build/us/.assets_done && make build/us/MARIA.BIN CC="$CC_MIPS" >/dev/null)
    cargo run --quiet --manifest-path "$D/tools/maria-sfx/Cargo.toml" -- build "$SE/sd_mar2.spk" "$M/build/us/sfx"
    mkdir -p "$O/maria"
    cp "$M/build/us/MARIA.BIN" "$M/build/us/maria.elf" "$M/build/us/maria.map" \
       "$M/build/us/sfx/SD_RIH.VH" "$M/build/us/sfx/SD_RIH.VB" "$O/maria/"
fi
mkdir -p "$O/psp" && cp "$SE/sd_alk.spk" "$O/psp/"

# ---- the Saturn's characters: our builds on sotn-decomp (decomp-mods/)
if [ ! -f "$O/mariasat/MARIASAT.BIN" ] || [ ! -f "$O/richter2/RICHTER2.BIN" ] || [ ! -f "$O/charsel/CHARSEL.BIN" ]; then
    if [ ! -f "$D/src/saturn/maria/gen/frames.h" ] || [ ! -f "$D/src/ric/gen/us/blueprints.h" ]; then
        say "Reading the Saturn and PlayStation characters' art (takes a while)"
        (cd "$D" && ./sotn.sh extract-assets config/assets.saturn.yaml && ./sotn.sh build-assets config/assets.saturn.yaml &&
                    ./sotn.sh extract-assets config/assets.us.yaml && ./sotn.sh build-assets config/assets.us.yaml) >/dev/null
    fi
    for d in mariasat-forpsx richter2-forpsx; do
        rsync -a --exclude build --exclude src/gen "$W/decomp-mods/$d/" "$D/mods/$d/"
        mkdir -p "$D/mods/$d/src/gen/us"
        cp "$D"/src/ric/gen/spritebank[123].h "$D/src/ric/gen/subweapons.h" "$D/mods/$d/src/gen/"
        cp "$D/src/ric/gen/us/blueprints.h" "$D/mods/$d/src/gen/us/"
    done

    say "Building Saturn Maria"
    MS="$D/mods/mariasat-forpsx"; mkdir -p "$O/mariasat"
    python3 -I "$T/mariasat-convert.py" "$D" "$O/mariasat/MARIASAT.SPR" "$MS/src/gen/mariasat_gfx.h" "$O/mariasat/MARIASAT.FX"
    python3 -I "$T/mariasat-sfx.py" "$D" "$O/mariasat/MARIASAT.SND"
    (cd "$MS" && make build CC="$CC_MIPS" >/dev/null)
    cp "$MS/build/us/MARIASAT.BIN" "$MS/build/us/mariasat.elf" "$MS/build/us/mariasat.map" "$O/mariasat/"

    say "Building the long-coat Richter"
    R2="$D/mods/richter2-forpsx"; mkdir -p "$O/richter2"
    python3 -I "$T/richter2-convert.py" "$D" "$O/richter2/RICHTER2.SPR" "$R2/src/gen/richter2_gfx.h"
    (cd "$R2" && make build CC="$CC_MIPS" >/dev/null)
    cp "$R2/build/us/RICHTER2.BIN" "$R2/build/us/richter2.elf" "$R2/build/us/richter2.map" "$O/richter2/"

    say "Building the Saturn character select"
    mkdir -p "$O/charsel"
    python3 -I "$T/charselect-convert.py" "$D" "$O/charsel/CHARSEL.BIN"
fi
say "All characters built"
