#!/usr/bin/env bash
# Acceptance for hc2000-release-ships-m27c512: every release leg ships
# roms/hc2000/eprom-dump-hc2k-2/M27C512.bin at exactly that payload path
# next to the app. Runs the real builder (deploy/emu/downloads.sh) and
# verifies per leg:
#   linux-x64   the .run's embedded payload (extracted, exact path + sha256)
#               AND the ROM after a fresh-HOME install at
#               ~/.config/hc2000/roms/hc2000/eprom-dump-hc2k-2/M27C512.bin
#   windows-x64 the installer is a PE32 exe and the builder's per-leg
#               payload-roms.sha256 manifest line matches; when a 7z binary
#               is available the exe is unpacked and the ROM entry is
#               verified at its exact path + sha256 (otherwise that check is
#               reported as skipped: makensis packs the staging tree with
#               File /r, which is the manifest line's source).
# Run: bash scripts/test-release-ship-m27c512.sh
set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"

REL="roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"
EXPECTED_SHA="83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b"
V="0.1.0-ship"
OUT="$(mktemp -d /tmp/hc2000-ship-test.XXXXXX)"
trap 'rm -rf "$OUT"' EXIT

checks=0
fail() { echo "FAIL: $*" >&2; exit 1; }
check() { if eval "$2"; then echo "ok: $1"; checks=$((checks+1)); else fail "$1"; fi; }
sha() { sha256sum "$1" | cut -d' ' -f1; }

check "repo rom pinned to the item sha" \
  "[ \"\$(sha $REPO_ROOT/$REL)\" = \"$EXPECTED_SHA\" ]"

echo "== build (real builder, version $V)"
bash deploy/emu/downloads.sh --component hc2000-emu --version "$V" --output "$OUT"
MAN="$OUT/payload-roms.sha256"
RUN="$OUT/hc2000-emu-$V-linux-x64.run"
EXE="$OUT/hc2000-emu-$V-setup.exe"

check "downloads.txt windows line" \
  "grep -qx 'windows-x64 hc2000-emu-$V-setup.exe' $OUT/downloads.txt"
check "downloads.txt linux line" \
  "grep -qx 'linux-x64 hc2000-emu-$V-linux-x64.run' $OUT/downloads.txt"
check "builder wrote payload-roms.sha256" "[ -s $MAN ]"
wsha="$(awk -F'\t' '$1=="windows-x64" && $2=="'"$REL"'" {print $3}' "$MAN" | head -1)"
lsha="$(awk -F'\t' '$1=="linux-x64" && $2=="'"$REL"'" {print $3}' "$MAN" | head -1)"
check "windows manifest line sha" "[ \"$wsha\" = \"$EXPECTED_SHA\" ]"
check "linux manifest line sha" "[ \"$lsha\" = \"$EXPECTED_SHA\" ]"

echo "== linux-x64: .run embedded payload"
X="$OUT/run-x"
mkdir -p "$X"
if grep -q '^TAREOF$' "$RUN"; then
  # plain sh+tar stub: the payload tarball is base64-embedded between the
  # 'base64 -d > "$TMP/payload.tar.gz" <<'"'"'TAREOF'"'"'' line and TAREOF
  awk '/base64 -d > "\$TMP\/payload.tar.gz"/{f=1;next} f&&/^TAREOF$/{exit} f' \
    "$RUN" | base64 -d > "$X/payload.tar.gz"
  tar xzf "$X/payload.tar.gz" -C "$X"
else
  # makeself variant
  sh "$RUN" --extract "$X" >/dev/null
fi
check ".run payload rom at exact path" "[ -f $X/$REL ]"
check ".run payload rom sha" "[ \"\$(sha $X/$REL)\" = \"$EXPECTED_SHA\" ]"
check ".run payload app next to roms" "[ -f $X/app/Hc2000.App ]"
echo "ok: linux-x64 .run payload ships $REL (sha256 $(sha "$X/$REL"))"

echo "== linux-x64: fresh-HOME install (--prefix, no display needed)"
HOME2="$(mktemp -d /tmp/hc2000-ship-home.XXXXXX)"
trap 'rm -rf "$OUT" "$HOME2"' EXIT
env HOME="$HOME2" bash "$RUN" --prefix "$HOME2/apps/hc2000" > "$OUT/install.log" 2>&1 \
  || { tail -40 "$OUT/install.log" >&2; fail "install into fresh HOME failed"; }
INST="$HOME2/.config/hc2000/roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"
check "installed rom at exact path" "[ -f $INST ]"
check "installed rom sha" "[ \"\$(sha $INST)\" = \"$EXPECTED_SHA\" ]"
check "installed app next to config roms" "[ -x $HOME2/apps/hc2000/Hc2000.App ]"
echo "ok: linux-x64 install ships ~/.config/hc2000/roms/hc2000/eprom-dump-hc2k-2/M27C512.bin (sha256 $(sha "$INST"))"

echo "== windows-x64: setup.exe"
check "setup.exe present" "[ -s $EXE ]"
check "setup.exe is PE32" \
  "[ \"\$(head -c2 $EXE)\" = 'MZ' ] && dd if=$EXE bs=1 skip=\$(od -An -j 60 -N 4 -t u4 $EXE | tr -d ' ') count=4 2>/dev/null | cmp -s - <(printf 'PE\0\0')"
echo "ok: windows-x64 staging manifest ships $REL (sha256 $wsha)"
ZBIN=""
for c in 7z 7za 7zr; do command -v "$c" >/dev/null && ZBIN="$c" && break; done
[ -z "$ZBIN" ] && [ -x "$HOME/tools/7zz" ] && ZBIN="$HOME/tools/7zz"
if [ -n "$ZBIN" ]; then
  XW="$OUT/exe-x"
  mkdir -p "$XW"
  "$ZBIN" x -y -o"$XW" "$EXE" >/dev/null 2>&1 || fail "7z could not unpack $EXE"
  WROM="$(find "$XW" -type f -ipath '*M27C512.bin' | head -1)"
  # 7z unpacks the NSIS $APPDATA variable as a literal directory name, so
  # $WROM holds a literal '$APPDATA' component. In the eval strings below,
  # pass it as a quoted variable (\$WROM), never inline its text.
  check "exe payload rom entry present" "[ -n \"\$WROM\" ]"
  if [ -n "$WROM" ]; then
    WROM_REL="$(realpath -m --relative-to="$XW" "$WROM")"
    # setup.nsi installs the staging roms/ tree to $APPDATA\hc2000\roms
    # (SetOutPath), so the unpacked rel path is that literal '$APPDATA'
    # dir + hc2000/ + the staging rel path.
    WROM_EXPECTED='$APPDATA/hc2000/'"$REL"
    check "exe payload rom exact rel path" \
      "[ \"\$WROM_REL\" = \"\$WROM_EXPECTED\" ]"
    check "exe payload rom sha" \
      "[ \"\$(sha \"\$WROM\")\" = \"$EXPECTED_SHA\" ]"
    echo "ok: windows-x64 exe unpacked ships $REL at $WROM_REL (sha256 $(sha "$WROM"))"
  fi
else
  echo "skip: no 7z/7za/7zr/~/tools/7zz on this box - the NSIS exe is not unpacked;"
  echo "      the staging-tree manifest line above verifies makensis's File /r input"
fi

echo ""
echo "=== per-leg M27C512 listing (path + sha256) ==="
awk -F'\t' '{printf "%s\t%s\t%s\n", $1, $2, $3}' "$MAN"
printf 'linux-x64 .run payload\t%s\t%s\n' "$REL" "$(sha "$X/$REL")"
printf 'linux-x64 installed\t%s\t%s\n' "$HOME2/.config/hc2000/roms/hc2000/eprom-dump-hc2k-2/M27C512.bin" "$(sha "$INST")"
[ -n "${ZBIN:-}" ] && [ -n "${WROM:-}" ] && { printf 'windows-x64 exe unpacked\t%s\t%s\n' "$REL" "$(sha "$WROM")"; } || true
printf 'hc2000-release-ships-m27c512 payload checks: %s passed\n' "$checks"
