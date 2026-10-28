#!/usr/bin/env bash
# Starts the game (started by "Play (Mac).command"). Host and join in the game:
# the title screen's main menu, Triangle (Netplay).
# Optional, for direct connections:  ./play-mac.sh host   |   ./play-mac.sh join 100.x.y.z
HERE="$(cd "$(dirname "$0")" && pwd)"
[ -d "$HERE/SymphonyRecomp/bin" ] || { echo "The game isn't set up yet: double-click \"Setup (Mac)\" first."; exit 1; }
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
BREW="$(brew --prefix 2>/dev/null || { [ -d /opt/homebrew ] && echo /opt/homebrew || echo /usr/local; })"
export DYLD_FALLBACK_LIBRARY_PATH="$BREW/opt/openal-soft/lib:$BREW/lib"
case "${1:-}" in
    host) export COOP_ONLINE="host:${2:-7777}" ;;
    join) [ -n "${2:-}" ] || { echo "usage: $0 join <friend's address> [port]"; exit 1; }
          export COOP_ONLINE="join:$2:${3:-7777}" ;;
esac
# (a copied or moved folder: point the settings at this folder's disc)
CUE="$HERE/SymphonyRecomp/disc/Castlevania - Symphony of the Night (USA).cue"
[ -f "$HERE/SymphonyRecomp/settings.json" ] && [ -f "$CUE" ] && CUE="$CUE" perl -0pi -e 's/"CdPath":\s*"[^"]*"/"CdPath": "$ENV{CUE}"/' "$HERE/SymphonyRecomp/settings.json"
cd "$HERE/SymphonyRecomp" && exec dotnet run --no-build
