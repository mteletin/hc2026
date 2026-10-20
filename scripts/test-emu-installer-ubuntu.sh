#!/usr/bin/env bash
# Acceptance for the Ubuntu .run installer (hc2000-emu-inst-ubuntu), run from
# the repo root: clean-box install (fresh $HOME + --prefix ~/apps/hc2000),
# app start on a real X display (Xvfb), ROM check + BASIC reached against the
# installed ROM copy (headless MainWindow probe), in-place upgrade with
# user data byte-identical, and uninstall that keeps ~/.config/hc2000.
# Run: bash scripts/test-emu-installer-ubuntu.sh
set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
V_OLD="${V_OLD:-0.1.63}"
V_NEW="${V_NEW:-0.1.64}"
OUT="$(mktemp -d /tmp/hc2000-emu-dltest.XXXXXX)"
trap 'rm -rf "$OUT"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
check() { if eval "$2"; then echo "ok: $1"; else fail "$1"; fi; }

echo "== build $V_OLD"
bash deploy/emu/downloads.sh --component hc2000-emu --version "$V_OLD" --output "$OUT"
RUN_OLD="$OUT/hc2000-emu-$V_OLD-linux-x64.run"
check "run built" "[ -s $RUN_OLD ]"
check "downloads.txt linux line" "grep -qx 'linux-x64 hc2000-emu-$V_OLD-linux-x64.run' $OUT/downloads.txt"

echo "== clean-box install (fresh HOME, --prefix ~/apps/hc2000)"
HOME2="$(mktemp -d /tmp/hc2000-emu-home.XXXXXX)"
env HOME="$HOME2" bash "$RUN_OLD" --prefix "$HOME2/apps/hc2000" | tee "$OUT/install.log"
check "install.json version" "grep -q '\"version\":\"$V_OLD\"' $HOME2/.config/hc2000/install.json"
check "install.json prefix" "grep -q '\"prefix\":\"$HOME2/apps/hc2000\"' $HOME2/.config/hc2000/install.json"
check "launcher" "[ -x $HOME2/.local/bin/hc2000 ]"
check "desktop entry" "grep -q \"Exec=$HOME2/apps/hc2000/Hc2000.App\" $HOME2/.local/share/applications/hc2000.desktop"
check "app binary" "[ -x $HOME2/apps/hc2000/Hc2000.App ]"
check "SDL2 pinned in place" "[ -f $HOME2/apps/hc2000/runtimes/linux-x64/native/libSDL2-2.0.so.0 ]"
check "icon" "[ -f $HOME2/apps/hc2000/icon.png ]"
for rom in basic.rom cpm.rom if1.rom; do
  a=$(sha256sum "$REPO_ROOT/roms/hc2000/v1/$rom" | cut -d' ' -f1)
  b=$(sha256sum "$HOME2/.config/hc2000/roms/hc2000/v1/$rom" | cut -d' ' -f1)
  [ "$a" = "$b" ] || fail "ROM $rom differs from the repo copy"
  echo "ok: ROM v1/$rom sha256 matches repo ($a)"
done

echo "== app start on a real X display (Xvfb) via the installed launcher"
xvfb-run -a env HOME="$HOME2" timeout 60 bash -c '
  "$HOME/.local/bin/hc2000" > /dev/null 2>&1 & app=$!
  w=""
  for i in $(seq 1 30); do
    w=$(xdotool search --name "^HC2000$" 2>/dev/null | head -n1 || true)
    [ -n "$w" ] && break
    sleep 1
  done
  kill "$app" 2>/dev/null || true
  wait "$app" 2>/dev/null || true
  [ -n "$w" ] && echo "window $w"
' > "$OUT/x11.log" || true
check "window mapped" "grep -q 'window [0-9]' $OUT/x11.log"

echo "== ROM check + BASIC reached against the installed ROM copy (headless MainWindow)"
env HOME="$HOME2" HC2000_VERIFY_ASSET_ROOT="$HOME2/.config/hc2000" \
  dotnet test scripts/installer-verify -c Release --nologo 2>&1 | tee "$OUT/verify.log" \
  | grep -E "INSTALLER-VERIFY|Passed!|Failed!|error" | head -20
check "ROM check + BASIC" "grep -q 'Passed!  - Failed:     0' $OUT/verify.log"

echo "== user data before upgrade"
mkdir -p "$HOME2/.config/hc2000/images"
printf 'FAKE-DISK-IMAGE' > "$HOME2/.config/hc2000/images/disk.dsk"
printf '{"theme":"dark"}\n' > "$HOME2/.config/hc2000/settings.json"
printf '\nhand-edit-marker\n' >> "$HOME2/.config/hc2000/roms/hc2000/v1/cpm.rom"
sha256sum \
  "$HOME2/.config/hc2000/images/disk.dsk" \
  "$HOME2/.config/hc2000/settings.json" \
  "$HOME2/.config/hc2000/roms/hc2000/v1/cpm.rom" > "$OUT/userdata.sha"

echo "== in-place upgrade to $V_NEW (recorded prefix, no --prefix)"
bash deploy/emu/downloads.sh --component hc2000-emu --version "$V_NEW" --output "$OUT"
RUN_NEW="$OUT/hc2000-emu-$V_NEW-linux-x64.run"
env HOME="$HOME2" bash "$RUN_NEW" | tee "$OUT/upgrade.log"
check "old -> new printed" "grep -q \"$V_OLD -> $V_NEW\" $OUT/upgrade.log"
check "install.json bumped" "grep -q '\"version\":\"$V_NEW\"' $HOME2/.config/hc2000/install.json"
check "same prefix kept" "grep -q '\"prefix\":\"$HOME2/apps/hc2000\"' $HOME2/.config/hc2000/install.json"
check "user data byte-identical" "sha256sum -c $OUT/userdata.sha --quiet"
check "launcher still points at prefix" "grep -q 'exec \"$HOME2/apps/hc2000/Hc2000.App\"' $HOME2/.local/bin/hc2000"

echo "== --uninstall keeps ~/.config/hc2000"
env HOME="$HOME2" bash "$RUN_NEW" --uninstall | tee "$OUT/uninstall.log"
check "program files gone" "[ ! -e $HOME2/apps/hc2000/Hc2000.App ]"
check "prefix dir removed when empty" "[ ! -d $HOME2/apps/hc2000 ]"
check "launcher gone" "[ ! -e $HOME2/.local/bin/hc2000 ]"
check "desktop entry gone" "[ ! -e $HOME2/.local/share/applications/hc2000.desktop ]"
check "install.json gone" "[ ! -e $HOME2/.config/hc2000/install.json ]"
check "settings kept" "[ -f $HOME2/.config/hc2000/settings.json ]"
check "image kept" "[ -f $HOME2/.config/hc2000/images/disk.dsk ]"
check "hand-edited ROM kept" "sha256sum -c $OUT/userdata.sha --quiet"

echo "== --help and default prefix (no --prefix, fresh HOME)"
bash "$RUN_OLD" --help | grep -q -- "--prefix <dir>" && echo "ok: --help lists --prefix"
HOME3="$(mktemp -d /tmp/hc2000-emu-home3.XXXXXX)"
env HOME="$HOME3" bash "$RUN_OLD" | tee "$OUT/default.log"
check "default prefix used" "grep -q '\"prefix\":\"$HOME3/.local/opt/hc2000\"' $HOME3/.config/hc2000/install.json"
rm -rf "$HOME3"

echo "ALL PASS ($V_OLD -> $V_NEW)"
