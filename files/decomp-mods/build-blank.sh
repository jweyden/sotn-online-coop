#!/bin/sh
# Builds the three characters' player programs with no data from any disc: every header
# sotn-decomp's asset step (or our converters) would generate from the discs is replaced
# by its "shape" (decomp-mods/shapes: the same arrays, every value 1). The layout is the
# real build's exactly, so the player's Setup only has to put the disc data back where
# the recipe (decomp-mods/recipes) says. Runs on Linux with mipsel-linux-gnu-gcc and make
# (GitHub Actions: .github/workflows/characters.yml).
#
#   decomp-mods/build-blank.sh SOTN_DECOMP_CHECKOUT OUT_DIR
#
# OUT_DIR gets MARIA, MARIASAT and RICHTER2 .BIN / .elf / .map.
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
D="$(cd "$1" && pwd)"; mkdir -p "$2"; OUT="$(cd "$2" && pwd)"
CC_MIPS="mipsel-linux-gnu-gcc -no-pie"

# the shapes where the generated headers would be
mkdir -p "$D/src/maria/gen"
cp "$HERE/shapes/maria/"*.h "$D/src/maria/gen/"
for d in mariasat-forpsx richter2-forpsx; do
    rm -rf "$D/mods/$d"; mkdir -p "$D/mods/$d"
    (cd "$HERE/$d" && tar cf - --exclude build --exclude src/gen .) | (cd "$D/mods/$d" && tar xf -)
    mkdir -p "$D/mods/$d/src/gen"
    cp -R "$HERE/shapes/$d/." "$D/mods/$d/src/gen/"
done

# PSP Maria: sotn-decomp's own build, minus its asset step (the shapes stand in)
(cd "$D/mods/mariapsp-forpsx" && mkdir -p build/us && touch build/us/.assets_done && make build/us/MARIA.BIN CC="$CC_MIPS")
cp "$D/mods/mariapsp-forpsx/build/us/MARIA.BIN" "$D/mods/mariapsp-forpsx/build/us/maria.elf" "$D/mods/mariapsp-forpsx/build/us/maria.map" "$OUT/"
for d in mariasat richter2; do
    (cd "$D/mods/$d-forpsx" && make build CC="$CC_MIPS")
    up="$(echo "$d" | tr a-z A-Z)"
    cp "$D/mods/$d-forpsx/build/us/$up.BIN" "$D/mods/$d-forpsx/build/us/$d.elf" "$D/mods/$d-forpsx/build/us/$d.map" "$OUT/"
done
ls -la "$OUT"
