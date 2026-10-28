#!/usr/bin/env bash
# Sets up SymphonyRecomp with the online co-op mod and every character (macOS, Apple
# Silicon or Intel). Started by "Setup (Mac).command". Run it again any time: it skips
# what's done.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"          # files/
KIT="$(dirname "$HERE")"                       # the folder the player unzipped
GAME="$HERE/SymphonyRecomp"
PS1_DIR="$KIT/1 - PlayStation game"
PSP_DIR="$KIT/2 - PSP game"
SAT_DIR="$KIT/3 - Saturn game"
CUE_NAME="Castlevania - Symphony of the Night (USA).cue"   # the name the engine's config expects
SR_COMMIT="$(awk '$1=="SymphonyRecomp"{print $2}' "$HERE/engine/base-commit.txt")"
R1_COMMIT="$(awk '$1=="RecompOne"{print $2}' "$HERE/engine/base-commit.txt")"
# the exact releases the characters are built from (SHA-1 of Track 1 / the .iso)
PS1_SHA=f967119e006695a59a6442237f9fc7c7811cf7bf   # US, SLUS-00067
PSP_SHA=25bb86c2c86f29e8a9d09ee0ae2a45fc6252befb   # The Dracula X Chronicles, EU, ULES-00841
SAT_SHA=ebf3209692dcc4df8d4448bbcada9ab9266549d0   # Akumajou Dracula X, JP, T-9527G

say() { printf '\n\033[1;36m==>\033[0m %s\n' "$1"; }
die() { printf '\n\033[1;31mStopped:\033[0m %s\n' "$1" >&2; exit 1; }

# ---- your three games first (so nothing downloads before they're there)

# A .cue and its .bin files in a folder: sets BINS (the .bin names the .cue asks for,
# as they're really named) and CUE.
read_cue() {   # folder, what it is
    CUE="$(find "$1" -maxdepth 2 -iname '*.cue' 2>/dev/null | head -1 || true)"
    [ -n "$CUE" ] || die "Your $2 files aren't there yet.
Drag your own $2 .cue and .bin files into the folder
    $(basename "$1")
then double-click Setup again."
    local dir; dir="$(dirname "$CUE")"
    local want=() have=() f i
    while IFS= read -r f; do [ -n "$f" ] && want+=("$f"); done < <(grep -i '^ *FILE' "$CUE" | sed -E 's/^ *FILE +"?([^"]+)"?( +[A-Za-z]+)? *$/\1/' | tr -d '\r')
    while IFS= read -r f; do have+=("$(basename "$f")"); done < <(find "$dir" -maxdepth 1 -iname '*.bin' | sort)
    BINS=()
    for i in "${!want[@]}"; do
        if [ -f "$dir/${want[$i]}" ]; then BINS+=("${want[$i]}")
        elif [ "${#have[@]}" = "${#want[@]}" ]; then BINS+=("${have[$i]}")   # renamed dump: match by track order
        else BINS=(); break; fi
    done
    if [ "${#BINS[@]}" != "${#want[@]}" ] || [ "${#want[@]}" = 0 ]; then
        echo "  Your .cue file asks for:"; for f in "${want[@]}"; do echo "    $f"; done
        echo "  but the folder has:"; for f in "${have[@]}"; do echo "    $f"; done
        die "Put all the .bin files that belong to your $2 .cue in '$(basename "$1")' (just one game), then double-click Setup again."
    fi
}

# Is this the exact release? (checked once: remembered by file size and date)
check_sha() {   # file, expected SHA-1, what it is
    local stamp="$HERE/.checked-$(basename "$1" | tr -c 'A-Za-z0-9.' '_')"
    local id; id="$(stat -f '%z %m' "$1")"
    [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$id" ] && return 0
    say "Checking your $3"
    [ "$(shasum -a 1 "$1" | cut -d' ' -f1)" = "$2" ] || die "$(basename "$1") isn't the $3 this needs.
It must be an unmodified dump of exactly: $4"
    echo "$id" > "$stamp"
}

read_cue "$PS1_DIR" "PlayStation game"
PS1_CUE="$CUE"; PS1_BINS=("${BINS[@]}"); PS1_T1="$(dirname "$PS1_CUE")/${PS1_BINS[0]}"
PSP_ISO="$(find "$PSP_DIR" -maxdepth 2 -iname '*.iso' 2>/dev/null | head -1 || true)"
[ -n "$PSP_ISO" ] || die "Your PSP game isn't there yet.
Drag your own Castlevania: The Dracula X Chronicles .iso into the folder
    2 - PSP game
then double-click Setup again."
read_cue "$SAT_DIR" "Saturn game"
SAT_T1="$(dirname "$CUE")/${BINS[0]}"

check_sha "$PS1_T1" "$PS1_SHA" "PlayStation game" "Castlevania: Symphony of the Night, USA (SLUS-00067), Track 1"
check_sha "$PSP_ISO" "$PSP_SHA" "PSP game" "Castlevania: The Dracula X Chronicles, Europe (ULES-00841)"
check_sha "$SAT_T1" "$SAT_SHA" "Saturn game" "Akumajou Dracula X: Gekka no Yasoukyoku, Japan (T-9527G), Track 1"

# ---- tools
if ! xcode-select -p >/dev/null 2>&1; then
    xcode-select --install >/dev/null 2>&1 || true
    die "Your Mac needs Apple's free command line tools first.
A window should have popped up: click Install and wait for it to finish (a few minutes).
Then double-click Setup again."
fi

export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    say "Installing .NET 10 (Microsoft's free runtime for the game, into your user folder)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet" --no-path
fi

for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do [ -x "$b" ] && eval "$("$b" shellenv)"; done
if ! command -v brew >/dev/null; then
    say "Installing Homebrew (needed for the game's sound library). It asks for your Mac login password: type it (nothing shows while you type) and press Enter, then press Enter again when asked."
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do [ -x "$b" ] && eval "$("$b" shellenv)"; done
    command -v brew >/dev/null || die "Homebrew didn't install. Double-click Setup again to retry."
fi
brew list openal-soft >/dev/null 2>&1 || { say "Installing OpenAL (sound)"; brew install openal-soft; }

# ---- the game engine, at the exact version the mod was made for
if [ ! -d "$GAME/.git" ]; then
    say "Downloading SymphonyRecomp"
    git clone https://github.com/BlackLabelHQ/SymphonyRecomp.git "$GAME"
fi
cd "$GAME"
# (RecompOne is a submodule: until it's fetched, `git -C RecompOne` would talk to the outer repo)
if [ ! -e RecompOne/.git ] || ! git -C RecompOne apply --reverse --check "$HERE/engine/recompone.patch" 2>/dev/null \
                           || ! git apply --reverse --check "$HERE/engine/outer.patch" 2>/dev/null; then
    say "Checking out the engine version the mod was made for"
    git fetch -q origin
    # (an older version of the co-op patch may be in: drop it first; untracked files,
    # like your disc and the built game, aren't touched)
    git reset -q --hard
    [ -e RecompOne/.git ] && git -C RecompOne reset -q --hard
    git checkout -q "$SR_COMMIT"
    git submodule update --init --recursive
    git -C RecompOne checkout -q "$R1_COMMIT"
    say "Applying the co-op engine patch"
    git apply "$HERE/engine/outer.patch"
    git -C RecompOne apply "$HERE/engine/recompone.patch"
else
    say "Engine patch already applied"
fi

# ---- your PlayStation game into the engine's disc folder (the .cue under the name its config expects)
say "Copying your PlayStation game"
mkdir -p disc
PS1_SRC="$(dirname "$PS1_CUE")"
for f in "${PS1_BINS[@]}"; do
    [ -f "disc/$f" ] && [ "$(stat -f %z "disc/$f")" = "$(stat -f %z "$PS1_SRC/$f")" ] || cp "$PS1_SRC/$f" "disc/$f"
done
# (with the .bin names it really has)
i=0
while IFS= read -r line; do
    line="${line%$'\r'}"
    if printf '%s' "$line" | grep -qi '^ *FILE'; then
        kind="$(printf '%s' "$line" | sed -E 's/.*[" ]([A-Za-z]+) *$/\1/')"
        printf 'FILE "%s" %s\n' "${PS1_BINS[$i]}" "$kind"; i=$((i + 1))
    else printf '%s\n' "$line"; fi
done < "$PS1_CUE" > "disc/$CUE_NAME"
CUE_ABS="$GAME/disc/$CUE_NAME"
if [ -f settings.json ]; then
    CUE_ABS="$CUE_ABS" perl -0pi -e 's/"CdPath":\s*"[^"]*"/"CdPath": "$ENV{CUE_ABS}"/' settings.json
else
    printf '{\n  "CdPath": "%s"\n}\n' "$CUE_ABS" > settings.json
fi

# ---- the characters, from your PSP and Saturn games: the kit's character builder
# (files/extractor) puts your discs' data into their programs (files/characters/blank,
# built with none), and makes their sprites, effects, sounds and the Saturn select screen
say "Building the characters from your PSP and Saturn games"
dotnet build -v q -c Release -o "$HERE/extractor/bin/out" "$HERE/extractor/extractor.csproj" >/dev/null
dotnet "$HERE/extractor/bin/out/sotn-extract.dll" characters "$HERE/decomp-mods/recipes" "$HERE/characters/blank" disc \
    --ps1 "$PS1_T1" --psp "$PSP_ISO" --saturn "$SAT_T1" || die "The characters didn't build. Double-click Setup again to retry; if it keeps failing, report it with this window's text."
cp "$HERE/characters/funcmaps/"*.json config/funcmaps/

# ---- the mod
say "Installing the Online Co-op mod"
# (mods/maria was a mod of its own before 2026-10-26: it's part of this one now)
rm -rf mods/coop-online mods/maria
cp -R "$HERE/mods/coop-online" mods/coop-online
INI=interface.ini
if [ ! -f "$INI" ]; then printf '[RecompOne]\n' > "$INI"; fi
grep -v '^mods\.coop-online\.enabled=\|^mods\.coop\.enabled=\|^mods\.maria\.enabled=' "$INI" > "$INI.tmp" || true
awk 'NR==1{print; print "mods.coop-online.enabled=True"; print "mods.coop.enabled=False"; next} {print}' "$INI.tmp" > "$INI"
rm -f "$INI.tmp"

# ---- build (the game's code from your discs: again whenever the characters changed)
STAMP="$HERE/.recompiled"
WANT_STAMP="$(cat config/funcmaps/maria.json config/funcmaps/mariasat.json config/funcmaps/richter2.json "$HERE/engine/base-commit.txt" | shasum -a 1 | cut -d' ' -f1)"
if [ ! -d generated ] || [ ! -f "$STAMP" ] || [ "$(cat "$STAMP")" != "$WANT_STAMP" ]; then
    say "Building the game from your discs (takes 5-15 minutes; let it run)"
    dotnet run --project RecompOne/RecompOne.Recompiler config/sotn.json
    echo "$WANT_STAMP" > "$STAMP"
fi
say "Building"
dotnet build RecompOne/RecompOne.sln -v q
dotnet build -v q

printf '\n\033[1;32mAll set!\033[0m Double-click "Play (Mac)" to start.\n'
