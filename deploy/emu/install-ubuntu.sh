#!/usr/bin/env bash
# hc2000-emu Ubuntu/Linux (x64) installer, run from the .run payload in a temp
# dir. No sudo: everything goes under $HOME. Program files -> $PREFIX,
# launcher -> ~/.local/bin/hc2000, desktop entry -> ~/.local/share/applications.
# User data (settings, ROMs, disk/tape images) lives in ~/.config/hc2000 and is
# NEVER deleted: ROMs are copied only if absent, upgrade removes only the
# program files the previous install recorded, and --uninstall keeps the whole
# config directory.
set -euo pipefail

# Run from the payload root the .run extracted (the stub runs us with the
# caller's cwd, so $PWD is not where VERSION/app/roms live).
cd "$(cd "$(dirname "$0")" && pwd)"

SELF_VERSION="$(cat "$PWD/VERSION")"
CONFIG_DIR="${HOME:?HOME must be set}/.config/hc2000"
INSTALL_JSON="$CONFIG_DIR/install.json"
DEFAULT_PREFIX="$HOME/.local/opt/hc2000"
LAUNCHER="$HOME/.local/bin/hc2000"
DESKTOP="$HOME/.local/share/applications/hc2000.desktop"
OWNED_NAME=".hc2000-owned.txt"
PREFIX=""
MODE="install"

usage() {
  echo "hc2000-emu $SELF_VERSION linux-x64 installer"
  echo "Usage: hc2000-emu-V-linux-x64.run [--prefix <dir>] [--uninstall] [--help]"
  echo "  (no option)       install, or upgrade in place at the recorded prefix"
  echo "  --prefix <dir>    install/upgrade into <dir> (default $DEFAULT_PREFIX,"
  echo "                    or the previously recorded prefix when it exists)"
  echo "  --uninstall       remove program files, launcher and desktop entry;"
  echo "                    keeps ~/.config/hc2000 (settings, ROMs, images)"
  echo "  --help            this text"
}

recorded_prefix() {
  [ -f "$INSTALL_JSON" ] || return 0
  sed -n 's/.*"prefix"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$INSTALL_JSON" | head -n 1
}

recorded_version() {
  [ -f "$INSTALL_JSON" ] || return 0
  sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$INSTALL_JSON" | head -n 1
}

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) [ $# -ge 2 ] || { echo "error: --prefix needs a value" >&2; exit 2; }; PREFIX="$2"; shift 2 ;;
    --prefix=*) PREFIX="${1#--prefix=}"; shift ;;
    --uninstall) MODE="uninstall"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "error: unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# Never delete a whole prefix; remove exactly the files the previous install
# recorded as its own, then clean up the directories it leaves empty.
remove_recorded_program_files() {
  local prefix="$1" owned="$1/$OWNED_NAME" f
  [ -f "$owned" ] || return 0
  while IFS= read -r f; do
    rm -f -- "$prefix/$f"
  done < "$owned"
  find "$prefix" -mindepth 1 -depth -type d -empty -delete 2>/dev/null || true
}

copy_roms_if_absent() {
  local src="$1" rel dest copied=0
  [ -d "$src" ] || { echo "error: payload has no roms/ directory" >&2; exit 1; }
  while IFS= read -r -d '' rel; do
    # rel is relative to the roms/ tree, e.g. hc2000/v1/basic.rom; the app
    # expects <config>/roms/<rel>.
    dest="$CONFIG_DIR/roms/$rel"
    if [ -e "$dest" ]; then
      continue
    fi
    mkdir -p "$(dirname "$dest")"
    cp -a -- "$src/$rel" "$dest"
    copied=$((copied + 1))
  done < <(cd "$src" && find . -type f -print0 | sed -z 's|^\./||')
  echo "ROMs: copied $copied into $CONFIG_DIR/roms (existing ROM files were left untouched)"
}

# G1 disk image: ships in the payload flat as disks/CPM22-HC.DSK and installs
# to the one location the --disk resolver checks (<config>/disks/),
# copy-if-absent like the ROMs - an existing user copy is never touched.
copy_disk_if_absent() {
  local src="$1" name="${1##*/}" dest="$CONFIG_DIR/disks/${1##*/}"
  [ -f "$src" ] || { echo "error: payload has no disks/$name" >&2; exit 1; }
  if [ -e "$dest" ]; then
    echo "disk: $name already at $dest (left untouched)"
    return 0
  fi
  mkdir -p -- "$CONFIG_DIR/disks"
  cp -a -- "$src" "$dest"
  echo "disk: copied $name into $CONFIG_DIR/disks"
}

install() {
  local disk
  local disks=(CPM22-HC.DSK HC2000-UTIL.DSK HC2000-UTIL-8M.DSK CPM22-HC-ram-paged-upgrade.DSK utilities-manifest.json m-v1-source-fit.txt)
  for disk in "${disks[@]}"; do
    [ -s "$PWD/disks/$disk" ] || { echo "error: payload lacks disks/$disk" >&2; return 1; }
  done
  # Reject an incomplete/corrupt native payload before touching an existing
  # installation or user data. No host SDL, compiler or audio-device probe.
  local native="$PWD/app/runtimes/linux-x64/native"
  [ -f "$native/libSDL2-2.0.so.0" ] && [ -f "$native/SDL2-MANIFEST.sha256" ] || {
    echo "error: payload lacks pinned SDL2 library/manifest" >&2; return 1;
  }
  (cd "$native" && sha256sum --strict -c SDL2-MANIFEST.sha256) || return 1
  if [ -z "$PREFIX" ]; then
    local rec
    rec="$(recorded_prefix)"
    if [ -n "$rec" ] && [ -d "$rec" ]; then
      PREFIX="$rec"
    else
      PREFIX="$DEFAULT_PREFIX"
    fi
  fi
  local old
  old="$(recorded_version)"
  mkdir -p -- "$PREFIX"
  remove_recorded_program_files "$PREFIX"
  cp -a -- "$PWD/app/." "$PREFIX/"
  cp -a -- "$PWD/icon.png" "$PREFIX/icon.png"
  # Record exactly the files this install placed (not the whole directory, so
  # anything a user added later is never touched by the next upgrade).
  {
    (cd "$PWD/app" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)
    printf '%s\n' "icon.png"
    printf '%s\n' "$OWNED_NAME"
  } > "$PREFIX/$OWNED_NAME"

  copy_roms_if_absent "$PWD/roms"
  for disk in "${disks[@]}"; do
    copy_disk_if_absent "$PWD/disks/$disk"
  done

  mkdir -p -- "$CONFIG_DIR"
  printf '{"version":"%s","prefix":"%s","installed_at":"%s"}\n' \
    "$SELF_VERSION" "$PREFIX" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$INSTALL_JSON"

  mkdir -p -- "$(dirname "$LAUNCHER")"
  printf '#!/bin/sh\n# hc2000-emu launcher (generated by the installer)\nexec "%s/Hc2000.App" "$@"\n' \
    "$PREFIX" > "$LAUNCHER"
  chmod +x -- "$LAUNCHER"

  mkdir -p -- "$(dirname "$DESKTOP")"
  printf '[Desktop Entry]\nType=Application\nName=HC2000\nComment=HC2000 emulator\nExec=%s/Hc2000.App\nIcon=%s/icon.png\nPath=%s\nTerminal=false\nCategories=Utility;\n' \
    "$PREFIX" "$PREFIX" "$PREFIX" > "$DESKTOP"

  if [ -n "$old" ]; then
    echo "hc2000-emu $old -> $SELF_VERSION (prefix: $PREFIX)"
  else
    echo "hc2000-emu $SELF_VERSION installed (prefix: $PREFIX)"
  fi
  echo "launcher: $LAUNCHER"
  echo "desktop entry: $DESKTOP"
  echo "user data (settings, ROMs, images) kept at: $CONFIG_DIR"
}

uninstall() {
  if [ -z "$PREFIX" ]; then
    local rec
    rec="$(recorded_prefix)"
    if [ -n "$rec" ]; then PREFIX="$rec"; else PREFIX="$DEFAULT_PREFIX"; fi
  fi
  remove_recorded_program_files "$PREFIX"
  rmdir -- "$PREFIX" 2>/dev/null || true
  rm -f -- "$LAUNCHER" "$DESKTOP" "$INSTALL_JSON"
  echo "hc2000-emu $SELF_VERSION uninstall: removed program files from $PREFIX, $LAUNCHER and $DESKTOP"
  echo "user data kept at: $CONFIG_DIR (settings, ROMs, disk/tape images)"
}

if [ "$MODE" = "uninstall" ]; then
  uninstall
else
  install
fi
