#!/bin/bash
# Double-click me to play. Keep this window open while you play (it's the game's log).
cd "$(dirname "$0")"
bash ./files/play-mac.sh "$@" || { echo; read -n 1 -s -r -p "Press any key to close this window."; }
