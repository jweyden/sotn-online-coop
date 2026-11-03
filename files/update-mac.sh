#!/usr/bin/env bash
# Checks for a newer version of the kit and, if you say yes, updates it (run by
# play-mac.sh before the game starts). Never touches your games, the built game, your
# saves or your settings. The kit you had is kept in "files.prev" (one update back).
#   update.txt (on GitHub): version=, kind= (quick | rebuild), summary=, zip=, sha256=
# Offline, or GitHub not answering: nothing happens and the game starts.
HERE="$(cd "$(dirname "$0")" && pwd)"          # files/
KIT="$(dirname "$HERE")"
URL="${COOP_UPDATE_URL:-https://raw.githubusercontent.com/jweyden/sotn-online-coop/main/update.txt}"
NOTE="$HERE/update-available.txt"              # (the game's Netplay screen shows it)

[ -f "$HERE/version.txt" ] || exit 0
[ "${COOP_NO_UPDATE:-}" = 1 ] && exit 0
HAVE="$(tr -d ' \r\n' < "$HERE/version.txt")"
INFO="$(curl -fsSL --max-time 5 "$URL" 2>/dev/null)" || exit 0
field() { printf '%s\n' "$INFO" | sed -n "s/^$1=//p" | head -1 | tr -d '\r'; }
NEW="$(field version)"; KIND="$(field kind)"; SUMMARY="$(field summary)"; ZIP="$(field zip)"; SHA="$(field sha256)"

# a yes/no from whoever's at the window (nobody there: the default)
ask() { if (exec < /dev/tty) 2>/dev/null; then read -r -p "$1" ok < /dev/tty || ok=""; else ok=""; fi; }

# is NEW newer than HAVE? (numbers separated by dots)
newer() {
    local IFS=.; local -a a=($1) b=($2); local i
    for i in 0 1 2 3; do
        local x="${a[$i]:-0}" y="${b[$i]:-0}"
        [[ "$x" =~ ^[0-9]+$ && "$y" =~ ^[0-9]+$ ]] || return 1
        (( 10#$x > 10#$y )) && return 0
        (( 10#$x < 10#$y )) && return 1
    done
    return 1
}
if [ -z "$NEW" ] || [ -z "$ZIP" ] || [ -z "$SHA" ] || ! newer "$NEW" "$HAVE"; then rm -f "$NOTE"; exit 0; fi

echo
echo "  A new version is out: v$NEW (you have v$HAVE)"
[ -n "$SUMMARY" ] && echo "  $SUMMARY"
if [ "$KIND" = rebuild ]; then
    echo "  This one rebuilds the game from your discs: 5-15 minutes."
    ask "  Update now? [y/N] "
    [[ "$ok" =~ ^[Yy] ]] || { printf '%s\n%s\n' "$NEW" "$SUMMARY" > "$NOTE"; echo "  Later, then. (Everyone in a game needs the same version.)"; exit 0; }
else
    echo "  It takes about a minute."
    ask "  Update now? [Y/n] "
    [[ -z "$ok" || "$ok" =~ ^[Yy] ]] || { printf '%s\n%s\n' "$NEW" "$SUMMARY" > "$NOTE"; echo "  Later, then. (Everyone in a game needs the same version.)"; exit 0; }
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "  The update didn't work: $1"; echo "  Starting the game as it is. (Try again next time.)"; printf '%s\n%s\n' "$NEW" "$SUMMARY" > "$NOTE"; exit 0; }
echo "  Downloading v$NEW..."
curl -fL --progress-bar --max-time 600 "$ZIP" -o "$TMP/kit.zip" || fail "couldn't download it"
[ "$(shasum -a 256 "$TMP/kit.zip" | cut -d' ' -f1)" = "$SHA" ] || fail "the download is damaged"
unzip -q "$TMP/kit.zip" -d "$TMP/new" || fail "couldn't unpack it"
NEWKIT="$(find "$TMP/new" -maxdepth 2 -type d -name files -print -quit)"
[ -n "$NEWKIT" ] && [ -f "$NEWKIT/version.txt" ] || fail "it isn't a kit"
NEWROOT="$(dirname "$NEWKIT")"

# the kit's own files: the old ones go to files.prev (moved, so scripts that are
# running, this one included, keep their copy), the new ones in
PREV="$KIT/files.prev"
rm -rf "$PREV"; mkdir -p "$PREV/files"
for f in "$NEWKIT"/* "$NEWKIT"/.[!.]*; do
    [ -e "$f" ] || continue
    n="$(basename "$f")"
    [ -e "$HERE/$n" ] && mv "$HERE/$n" "$PREV/files/$n"
    mv "$f" "$HERE/$n"
done
# the launchers and the READ ME (files, not the game folders)
for f in "$NEWROOT"/*; do
    [ -f "$f" ] || continue
    n="$(basename "$f")"
    if [ -e "$KIT/$n" ]; then cmp -s "$f" "$KIT/$n" && continue; mv "$KIT/$n" "$PREV/$n"; fi
    mv "$f" "$KIT/$n"
done
chmod +x "$HERE"/*.sh "$KIT"/*.command 2>/dev/null
rm -f "$NOTE"
echo "  Updated to v$NEW. Setting it up..."
[ "${COOP_UPDATE_NO_SETUP:-}" = 1 ] && exit 0   # (testing the updater)
if ! bash "$HERE/setup-mac.sh"; then
    echo
    echo "  Setup stopped (see above). Do what it says, then double-click Setup."
    echo "  (Your previous version is in files.prev.)"
    exit 1
fi
echo
