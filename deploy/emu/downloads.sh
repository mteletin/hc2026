#!/usr/bin/env bash
# HC2000 Emu installer publisher (Windows + Ubuntu)
#
# Contract: the builder runs this script from the repository root:
#   deploy/emu/downloads.sh --component hc2000-emu --version V --output DIR
#
# The script must:
#   1. Build the Windows NSIS installer -> DIR/hc2000-emu-V-setup.exe
#   2. Build the Ubuntu .run installer  -> DIR/hc2000-emu-V-linux-x64.run
#   3. Write DIR/downloads.txt with one line per installer:
#        windows-x64 hc2000-emu-V-setup.exe
#        linux-x64   hc2000-emu-V-linux-x64.run
#   4. Write DIR/payload-roms.sha256: one line per leg verifying the v2 boot
#      ROM ships at its exact payload path (hc2000-release-ships-m27c512):
#        <leg>\t<relpath>\t<sha256>   (fails the build if a leg omits it)
#
# If the script fails or the manifest is wrong, nothing is published.

set -euo pipefail

# --- Parse arguments ---
COMPONENT=""
VERSION=""
OUTPUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --component) COMPONENT="$2"; shift 2 ;;
    --version)   VERSION="$2";   shift 2 ;;
    --output)    OUTPUT="$2";    shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$COMPONENT" || -z "$VERSION" || -z "$OUTPUT" ]]; then
  echo "Usage: $0 --component hc2000-emu --version V --output DIR" >&2
  exit 1
fi

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$REPO_ROOT"

mkdir -p "$OUTPUT"
STAGE="$(mktemp -d /tmp/hc2000-emu-inst.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT

# v2 boot ROM guard (hc2000-release-ships-m27c512): every release leg ships
# roms/hc2000/eprom-dump-hc2k-2/M27C512.bin at exactly that payload path next
# to the app. record_payload_roms appends leg<TAB>relpath<TAB>sha256 to the
# build's payload-roms.sha256 and fails the build if a leg omits the file.
ROMS_GUARD_REL="roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"
ROMS_GUARD_MANIFEST="$OUTPUT/payload-roms.sha256"
: > "$ROMS_GUARD_MANIFEST"
record_payload_roms() {
  local leg="$1" payload="$2" f
  f="$payload/$ROMS_GUARD_REL"
  if [[ ! -f "$f" ]]; then
    echo "ERROR: $leg payload is missing $ROMS_GUARD_REL" >&2
    exit 1
  fi
  printf '%s\t%s\t%s\n' "$leg" "$ROMS_GUARD_REL" \
    "$(sha256sum "$f" | cut -d' ' -f1)" >> "$ROMS_GUARD_MANIFEST"
  echo "payload rom verified ($leg): $ROMS_GUARD_REL"
}

echo "=== HC2000 Emu installer build: $COMPONENT v$VERSION ==="

# ------------------------------------------------------------------
# 1. dotnet publish (self-contained)
# ------------------------------------------------------------------
echo "--- dotnet publish win-x64 ---"
dotnet publish src/Hc2000.App -c Release -r win-x64 --self-contained \
  -p:Version="$VERSION" -p:InformationalVersion="${VERSION}-$(git rev-parse --short HEAD 2>/dev/null || echo dev)" \
  -o "$STAGE/win-app"

echo "--- dotnet publish linux-x64 ---"
dotnet publish src/Hc2000.App -c Release -r linux-x64 --self-contained \
  -p:Version="$VERSION" -p:InformationalVersion="${VERSION}-$(git rev-parse --short HEAD 2>/dev/null || echo dev)" \
  -o "$STAGE/linux-app"

# ------------------------------------------------------------------
# 2. SDL2 native library
# ------------------------------------------------------------------
# SDL2 2.32.10 pinned (per KB hc2000 doc note-app-audio).
# Windows: SDL2.dll (x64)  -> win-app/runtimes/win-x64/native/
# Linux:   libSDL2-2.0.so.0 -> linux-app/runtimes/linux-x64/native/

SDL2_VERSION="2.32.10"
SDL2_SHA256="5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165"
SDL2_URL="https://www.libsdl.org/release/SDL2-${SDL2_VERSION}.tar.gz"

# Check for cached SDL2 source
SDL2_SRC="$STAGE/SDL2-${SDL2_VERSION}"
if [[ -d "$SDL2_SRC" ]]; then
  echo "SDL2 source already cached"
else
  echo "--- Fetching SDL2 ${SDL2_VERSION} ---"
  # Try to find a pre-cached copy first (e.g. from a previous build or /tmp)
  if [[ -d "/tmp/sdl2/SDL2-${SDL2_VERSION}" ]]; then
    cp -r "/tmp/sdl2/SDL2-${SDL2_VERSION}" "$SDL2_SRC"
    echo "Using cached SDL2 source from /tmp/sdl2/"
  else
    curl -fsSL -o "$STAGE/sdl2.tar.gz" "$SDL2_URL"
    echo "${SDL2_SHA256}  $STAGE/sdl2.tar.gz" | sha256sum -c -
    tar xzf "$STAGE/sdl2.tar.gz" -C "$STAGE"
  fi
fi

# --- Windows SDL2.dll ---
# Precedence: repo-vendored DLL (sha256-pinned in deploy/emu/sdl2/MANIFEST.sha256,
# so the build is deterministic on ANY host - no mingw, no network) -> a local
# /tmp cache -> an explicit SDL2_DLL env override -> cross-compile with mingw
# (last resort; needs gcc-mingw-w64-x86-64-win32 and the SDL2 source fetched
# above, which is why it also downloads the tarball). The vendored DLL is the
# official SDL 2.32.10 win32-x64 release binary, unmodified (source URL and zip
# sha256 in MANIFEST.sha256). The earlier vendored DLL was this script's own
# mingw cross-compile and overflowed its stack on Windows 11 (0.1.97,
# 2026-10-14); the cross-compile below is a last resort only, never a release.
WIN_SDL2_DIR="$STAGE/win-app/runtimes/win-x64/native"
mkdir -p "$WIN_SDL2_DIR"

REPO_SDL2_DLL="$REPO_ROOT/deploy/emu/sdl2/SDL2.dll"
if [[ -f "$REPO_SDL2_DLL" ]]; then
  # Verify against the pinned manifest before trusting it.
  MANIFEST_LINE="$(grep -F "deploy/emu/sdl2/SDL2.dll" "$REPO_ROOT/deploy/emu/sdl2/MANIFEST.sha256" | head -1)"
  if [[ -z "$MANIFEST_LINE" ]]; then
    echo "ERROR: $REPO_ROOT/deploy/emu/sdl2/MANIFEST.sha256 does not pin deploy/emu/sdl2/SDL2.dll" >&2
    exit 1
  fi
  EXPECTED_SHA="${MANIFEST_LINE%% *}"
  ACTUAL_SHA="$(sha256sum "$REPO_SDL2_DLL" | cut -d' ' -f1)"
  if [[ "$EXPECTED_SHA" != "$ACTUAL_SHA" ]]; then
    echo "ERROR: vendored SDL2.dll sha256 $ACTUAL_SHA != pinned $EXPECTED_SHA" >&2
    exit 1
  fi
  echo "Using repo-vendored SDL2.dll (sha256 verified: $ACTUAL_SHA)"
  cp "$REPO_SDL2_DLL" "$WIN_SDL2_DIR/SDL2.dll"
elif [[ -f "/tmp/sdl2/winbuild/SDL2.dll" ]]; then
  echo "Using pre-built SDL2.dll from /tmp/sdl2/winbuild/"
  cp "/tmp/sdl2/winbuild/SDL2.dll" "$WIN_SDL2_DIR/SDL2.dll"
elif [[ -n "${SDL2_DLL:-}" && -f "$SDL2_DLL" ]]; then
  echo "Using SDL2_DLL=$SDL2_DLL"
  cp "$SDL2_DLL" "$WIN_SDL2_DIR/SDL2.dll"
else
  echo "--- Cross-compiling SDL2.dll (mingw-w64 x86-64) ---"
  MINGW_CC="${MINGW_CC:-x86_64-w64-mingw32-gcc-win32}"
  if ! command -v "$MINGW_CC" &>/dev/null; then
    # Try common mingw locations
    for p in "$HOME/tools/mingw/x64root/usr/bin" /usr/bin; do
      if [[ -x "$p/$MINGW_CC" ]]; then
        export PATH="$p:$PATH"
        break
      fi
    done
  fi
  if ! command -v "$MINGW_CC" &>/dev/null; then
    echo "ERROR: mingw-w64 cross compiler not found. Set MINGW_CC or install gcc-mingw-w64-x86-64-win32." >&2
    exit 1
  fi

  # Patch SDL_config_windows.h to disable the generic cond suffix
  # (required for manual compilation without cmake)
  sed -i 's/^#define SDL_THREAD_GENERIC_COND_SUFFIX 1$/#define SDL_THREAD_GENERIC_COND_SUFFIX 0/' \
    "$SDL2_SRC/include/SDL_config_windows.h"

  # Build source list (all .c files needed for a Windows SDL2 build)
  SDL2_SRCS_FILE="$STAGE/sdl2-winsrcs.list"
  (
    cd "$SDL2_SRC"
    for dir in \
      src/dynapi src/thread src/atomic src/audio src/audio/disk src/audio/dummy \
      src/audio/wasapi src/audio/dsound src/audio/winmm \
      src/video src/video/windows src/video/dummy \
      src/render src/joystick src/joystick/windows src/joystick/hidapi src/joystick/virtual \
      src/core src/core/windows src/events src/keyboard src/timer \
      src/locale src/haptic src/haptic/windows \
      src/sensor src/sensor/windows \
      src/power src/power/windows \
      src/file src/filesystem src/filesystem/windows \
      src/loadso src/log src/messagebox src/messagebox/windows \
      src/system src/system/windows src/stdlib \
      src/timer src/samples src/stdio src/strings src/threads \
      src/cpuinfo src/cpuinfo/x86 src/simd \
      src/main src/main/windows \
      src/dynapi
    do
      find "$dir" -name '*.c' 2>/dev/null
    done
    # Add specific files that find might miss
    echo "src/video/windows/SDL_windowsopengles.c"
    echo "src/video/windows/SDL_windowsopengl.c"
  ) | sort -u > "$SDL2_SRCS_FILE"

  # Create stubs for D3D render functions
  cat > "$STAGE/sdl2-stubs.c" <<'STUBS'
#include "SDL.h"
#include "SDL_render.h"
#include "SDL_error.h"
int SDL_RenderGetD3D9Device_REAL(SDL_Renderer *r, void **d) { (void)r;(void)d; return SDL_SetError("D3D not available"); }
int SDL_RenderGetD3D11Device_REAL(SDL_Renderer *r, void **d) { (void)r;(void)d; return SDL_SetError("D3D11 not available"); }
int SDL_RenderGetD3D12Device_REAL(SDL_Renderer *r, void **d) { (void)r;(void)d; return SDL_SetError("D3D12 not available"); }
STUBS

  "$MINGW_CC" -shared -O2 -DNDEBUG \
    -I"$SDL2_SRC" -I"$SDL2_SRC/include" \
    -DSDL_AUDIO_DRIVER_WASAPI=1 -DSDL_AUDIO_DRIVER_WINMM=1 -DSDL_AUDIO_DRIVER_DSOUND=1 \
    -DSDL_VIDEO_OPENGL_WGL=0 -DSDL_VIDEO_OPENGL_EGL=0 -DSDL_VIDEO_RENDER_OGL=0 \
    -DSDL_VIDEO_RENDER_D3D=0 -DSDL_VIDEO_RENDER_D3D11=0 -DSDL_VIDEO_RENDER_D3D12=0 \
    -DSDL_VIDEO_RENDER_OGL_ES2=0 -DSDL_VIDEO_RENDER_OGL_ES=0 \
    -DSDL_USE_BUILTIN_OPENGL_DEFINITIONS=1 \
    -Wno-error \
    $(tr '\n' ' ' < "$SDL2_SRCS_FILE") "$STAGE/sdl2-stubs.c" \
    -o "$WIN_SDL2_DIR/SDL2.dll" \
    -lwinmm -lole32 -loleaut32 -luser32 -lgdi32 -lsetupapi -lhid -limm32 \
    -ldxgi -ldxguid -ldinput -lversion -ladvapi32 -lkernel32 -mthreads
  echo "SDL2.dll built: $(file "$WIN_SDL2_DIR/SDL2.dll")"
fi

# --- Linux libSDL2 ---
LINUX_SDL2_DIR="$STAGE/linux-app/runtimes/linux-x64/native"
mkdir -p "$LINUX_SDL2_DIR"

if [[ -n "${SDL2_SO:-}" && -f "$SDL2_SO" ]]; then
  echo "Using SDL2_SO=$SDL2_SO"
  cp "$SDL2_SO" "$LINUX_SDL2_DIR/libSDL2-2.0.so.0"
elif [[ -f /usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0 ]]; then
  cp /usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0 "$LINUX_SDL2_DIR/libSDL2-2.0.so.0"
else
  # Build from source (Linux native)
  echo "--- Building libSDL2 (Linux native) ---"
  cd "$SDL2_SRC"
  mkdir -p "$STAGE/sdl2-linux-build"
  cd "$STAGE/sdl2-linux-build"
  "$SDL2_SRC"/configure --prefix="$STAGE/sdl2-linux-install" \
    --disable-video-x11 --disable-video-wayland \
    --disable-audio-alsa --disable-audio-pulse \
    --disable-joystick --disable-haptic --disable-sensor \
    CFLAGS="-O2 -DNDEBUG" LDFLAGS="-static-libstdc++ -static-libgcc" \
    > /dev/null 2>&1
  make -j"$(nproc)" > /dev/null 2>&1
  make install > /dev/null 2>&1
  cp "$STAGE/sdl2-linux-install/lib/libSDL2-2.0.so.0" "$LINUX_SDL2_DIR/libSDL2-2.0.so.0"
  echo "libSDL2-2.0.so.0 built"
fi

# ------------------------------------------------------------------
# 3. ROMs
# ------------------------------------------------------------------
# The roms/ tree is copied wholesale (all components): the app and both
# installers place ROMs at <config>/roms/<rel> with rel a path inside roms/,
# e.g. hc2000/v1/basic.rom - trimming to roms/hc2000 would shift every ROM
# one directory up and the app would not find them.
ROMS_SRC="$REPO_ROOT/roms"
if [[ -d "$ROMS_SRC" ]]; then
  cp -r "$ROMS_SRC" "$STAGE/roms"
  echo "ROMs copied: $(ls "$ROMS_SRC" | tr '\n' ' ')"
else
  echo "WARNING: ROMs not found at $ROMS_SRC" >&2
fi

# ------------------------------------------------------------------
# 3b. G1 disk image (hc2000-fresh-install-roms-dsk)
# ------------------------------------------------------------------
# CPM22-HC.DSK ships in both payloads: the acceptance bar is "no setup" - a
# fresh install must run `hc2000 --disk A:CPM22-HC.DSK --start` out of the
# box. Both installers place it at <config>/disks/CPM22-HC.DSK (the one
# installed disk location the --disk resolver checks, spec v4.2), so the
# payload carries it flat under disks/, not the repo's disks/cpm/ layout.
# Missing on disk = the G1 round cannot ship: hard error, like the ROMS
# guard, not a warning.
DISK_SRC="$REPO_ROOT/disks/cpm/CPM22-HC.DSK"
if [[ -f "$DISK_SRC" ]]; then
  mkdir -p "$STAGE/disks"
  cp -a -- "$DISK_SRC" "$STAGE/disks/CPM22-HC.DSK"
  echo "Disk image staged: CPM22-HC.DSK sha256 $(sha256sum "$STAGE/disks/CPM22-HC.DSK" | cut -d' ' -f1)"
else
  echo "ERROR: G1 disk image not found at $DISK_SRC (LFS checkout missing?)" >&2
  exit 1
fi

# ------------------------------------------------------------------
# 4. Windows NSIS installer
# ------------------------------------------------------------------
WIN_EXE="$OUTPUT/hc2000-emu-${VERSION}-setup.exe"

# Find makensis
MAKENSIS="${MAKENSIS:-makensis}"
NSISDIR_VAL="${NSISDIR:-}"

if ! command -v "$MAKENSIS" &>/dev/null; then
  # Try common locations
  for p in "$HOME/tools/nsis" /usr/bin; do
    if [[ -x "$p/makensis" ]]; then
      MAKENSIS="$p/makensis"
      break
    fi
  done
fi
if ! command -v "$MAKENSIS" &>/dev/null && [[ ! -x "$MAKENSIS" ]]; then
  echo "ERROR: makensis not found. Install nsis or set MAKENSIS." >&2
  exit 1
fi

if [[ -n "$NSISDIR_VAL" ]]; then
  export NSISDIR="$NSISDIR_VAL"
fi

# NSIS expects the payload in a specific layout:
#   PAYLOAD_DIR/app/   -> published app
#   PAYLOAD_DIR/roms/  -> ROMs
NSIS_PAYLOAD="$STAGE/nsis-payload"
mkdir -p "$NSIS_PAYLOAD"
cp -r "$STAGE/win-app" "$NSIS_PAYLOAD/app"
if [[ -d "$STAGE/roms" ]]; then
  cp -r "$STAGE/roms" "$NSIS_PAYLOAD/roms"
fi
# Staged unconditionally above (3b) - a G1 round without the disk is a
# build error, verified here so a bad stage cannot sneak into the .exe.
if [[ ! -f "$STAGE/disks/CPM22-HC.DSK" ]]; then
  echo "ERROR: G1 disk image missing from stage before NSIS build" >&2
  exit 1
fi
cp -r "$STAGE/disks" "$NSIS_PAYLOAD/disks"
record_payload_roms "windows-x64" "$NSIS_PAYLOAD"

# Build the installer
# NSIS on Linux uses forward slashes; pass the output path with / separators
NSIS_SCRIPT="$REPO_ROOT/deploy/emu/setup.nsi"
NSIS_OUT_DIR="$(dirname "$NSIS_SCRIPT")"
# Convert to forward slashes for NSIS on Linux
NSIS_PAYLOAD_FWD="$(echo "$NSIS_PAYLOAD" | sed 's|\\|/|g')"
WIN_EXE_FWD="$(echo "$WIN_EXE" | sed 's|\\|/|g')"

# Compute 4-part version for VIProductVersion
if [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  VERSION_4="${VERSION}.0"
else
  VERSION_4="$VERSION"
fi

"$MAKENSIS" -V2 \
  "-DHC2000_VERSION=${VERSION}" \
  "-DHC2000_VERSION_4=${VERSION_4}" \
  "-DPAYLOAD_DIR=${NSIS_PAYLOAD_FWD}" \
  "-DOUTFILE=${WIN_EXE_FWD}" \
  "$NSIS_SCRIPT"

# Verify output
if [[ ! -f "$WIN_EXE" ]]; then
  echo "ERROR: $WIN_EXE not found after NSIS build" >&2
  exit 1
fi

echo "Windows installer: $WIN_EXE ($(du -h "$WIN_EXE" | cut -f1))"

# ------------------------------------------------------------------
# 5. Ubuntu .run installer (makeself)
# ------------------------------------------------------------------
LINUX_RUN="$OUTPUT/hc2000-emu-${VERSION}-linux-x64.run"

if [[ -f "$REPO_ROOT/deploy/emu/install-ubuntu.sh" ]]; then
  echo "--- Building Ubuntu .run installer ---"
  # Create the payload directory for the .run
  RUN_PAYLOAD="$STAGE/run-payload"
  mkdir -p "$RUN_PAYLOAD"
  cp -r "$STAGE/linux-app" "$RUN_PAYLOAD/app"
  if [[ -d "$STAGE/roms" ]]; then
    cp -r "$STAGE/roms" "$RUN_PAYLOAD/roms"
  fi
  if [[ ! -f "$STAGE/disks/CPM22-HC.DSK" ]]; then
    echo "ERROR: G1 disk image missing from stage before .run build" >&2
    exit 1
  fi
  cp -r "$STAGE/disks" "$RUN_PAYLOAD/disks"
  record_payload_roms "linux-x64" "$RUN_PAYLOAD"
  cp "$REPO_ROOT/deploy/emu/install-ubuntu.sh" "$RUN_PAYLOAD/install-ubuntu.sh"
  # install-ubuntu.sh reads its version from $PWD/VERSION and copies
  # $PWD/icon.png into the prefix: both are part of the payload contract.
  printf '%s\n' "$VERSION" > "$RUN_PAYLOAD/VERSION"
  if [[ -f "$REPO_ROOT/deploy/emu/hc2000-icon.png" ]]; then
    cp "$REPO_ROOT/deploy/emu/hc2000-icon.png" "$RUN_PAYLOAD/icon.png"
  fi

  if command -v makeself &>/dev/null; then
    makeself --compress=gzip \
      --copy --noverify \
      --exe "install-ubuntu.sh" \
      "$RUN_PAYLOAD" "$LINUX_RUN" \
      "HC2000 Emu ${VERSION} Linux installer" \
      > /dev/null 2>&1
  else
    # Fallback: plain sh + tar stub
    echo "makeself not found, creating plain sh+tar stub"
    (
      cd "$RUN_PAYLOAD"
      tar czf "$STAGE/run-payload.tar.gz" .
    )
    {
      echo '#!/bin/sh'
      echo "# HC2000 Emu ${VERSION} Linux installer (self-extracting)"
      echo "TMP=\"\$(mktemp -d)\""
      echo "trap 'rm -rf \"\$TMP\"' EXIT"
      # Embed the tarball as base64
      echo "echo \"Extracting...\""
      echo "base64 -d > \"\$TMP/payload.tar.gz\" <<'TAREOF'"
      base64 "$STAGE/run-payload.tar.gz"
      echo "TAREOF"
      # Extract to a temp dir (NOT $PREFIX): install-ubuntu.sh cds to its own
      # directory and copies $PWD/app/. and $PWD/icon.png into $PREFIX - with
      # the payload root == $PREFIX that is a copy of a file onto itself.
      # The prefix must contain only the program files.
      echo "tar xzf \"\$TMP/payload.tar.gz\" -C \"\$TMP\""
      # Pass the caller's args straight through: the installer resolves
      # --prefix itself and, when none is given, upgrades in place at the
      # prefix the previous install recorded (install.json).
      echo "exec bash \"\$TMP/install-ubuntu.sh\" \"\$@\""
    } > "$LINUX_RUN"
    chmod +x "$LINUX_RUN"
  fi

  if [[ -f "$LINUX_RUN" ]]; then
    echo "Linux installer: $LINUX_RUN ($(du -h "$LINUX_RUN" | cut -f1))"
  else
    echo "ERROR: Linux installer not created" >&2
    exit 1
  fi
else
  echo "WARNING: install-ubuntu.sh not found; skipping Linux installer" >&2
fi

# ------------------------------------------------------------------
# 6. Write downloads.txt manifest (append-only, per the builder contract:
# lines accumulate in DIR/downloads.txt and are never truncated; if the
# script fails or the manifest is wrong, nothing is published)
# ------------------------------------------------------------------
MANIFEST="$OUTPUT/downloads.txt"

if [[ -f "$WIN_EXE" ]]; then
  echo "windows-x64 hc2000-emu-${VERSION}-setup.exe" >> "$MANIFEST"
fi

if [[ -f "$LINUX_RUN" ]]; then
  echo "linux-x64 hc2000-emu-${VERSION}-linux-x64.run" >> "$MANIFEST"
fi

# Validate manifest
LINES=$(wc -l < "$MANIFEST")
if [[ $LINES -lt 1 || $LINES -gt 8 ]]; then
  echo "ERROR: downloads.txt has $LINES lines (expected 1-8)" >&2
  exit 1
fi

echo ""
echo "=== downloads.txt ==="
cat "$MANIFEST"
echo "=== payload-roms.sha256 ==="
cat "$ROMS_GUARD_MANIFEST"
echo "=== Done ==="
