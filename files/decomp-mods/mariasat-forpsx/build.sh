#!/bin/sh
# Build MARIASAT.BIN in Docker (the sotn-maria-build image from build-maria.sh).
cd "$(dirname "$0")/../.." && docker run --rm -v "$PWD":/src -w /src/mods/mariasat-forpsx sotn-maria-build \
    make build CC="mipsel-linux-gnu-gcc -no-pie" "$@"
