#!/usr/bin/env bash
# Builder-hook contract tests (hc2000-emu-inst-builder-contract).
# Encodes the epic spec section "Contract with the builder" verbatim.
# Dry-run only: no setup.nsi, no makensis, no real publish, no builder changes.
#
# The merged Ubuntu payload is deploy/emu/install-ubuntu.sh (byte-copied into
# the fixture as the linux installer). The windows-x64 installer is a stub
# file named hc2000-emu-<V>-setup.exe — there is no NSIS build in this suite.
#
# Rules (epic spec, verbatim):
#   R1  builder runs deploy/emu/downloads.sh --component hc2000-emu --version <V> --output <DIR>
#   R2  installers in <DIR> are named hc2000-emu-<V>-... (prefix followed by '-' or '.')
#   R3  <DIR>/downloads.txt has one line per installer, form '<target> <file>'
#   R4  targets windows-x64 and linux-x64; windows-arm64 and linux-arm64 also accepted
#   R5  at most 8 lines
#   R6  script failure or a wrong manifest -> nothing is published
#
# Run from anywhere: bash scripts/test-builder-downloads-contract.sh
set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
UBUNTU_PAYLOAD="$REPO_ROOT/deploy/emu/install-ubuntu.sh"
DOWNLOADS_SH="$REPO_ROOT/deploy/emu/downloads.sh"
V="0.1.69"
PREFIX="hc2000-emu-${V}"
STUB_WIN="${PREFIX}-setup.exe"
LINUX_NAME="${PREFIX}-linux-x64.run"

passed=0
failed=0
WORKDIR="$(mktemp -d /tmp/hc2000-builder-contract.XXXXXX)"
trap 'rm -rf "$WORKDIR"' EXIT

ok() { echo "ok: $1"; passed=$((passed + 1)); }
fail() { echo "FAIL: $1" >&2; failed=$((failed + 1)); }

# prefix followed by '-' or '.' (spec: hc2000-emu-<V>-... ).
name_ok() {
  local name="$1"
  [[ "$name" == "$PREFIX"-* || "$name" == "$PREFIX".* ]]
}

target_ok() {
  case "$1" in
    windows-x64|linux-x64|windows-arm64|linux-arm64) return 0 ;;
    *) return 1 ;;
  esac
}

# Print a reason on stdout and return 1 when the manifest is wrong.
manifest_wrong() {
  local dir="$1" line target file n=0 listed
  if [[ ! -f "$dir/downloads.txt" ]]; then
    echo "missing downloads.txt"
    return 0
  fi
  # Spec: at most 8 lines. Count every line, including a trailing blank.
  n=$(wc -l < "$dir/downloads.txt")
  n=$((n + 0))
  if [[ "$n" -gt 8 ]]; then
    echo "downloads.txt has $n lines (at most 8)"
    return 0
  fi
  if [[ "$n" -lt 1 ]]; then
    echo "downloads.txt has no installer line"
    return 0
  fi
  listed="$(mktemp)"
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" != *" "* || "$line" == *" "*' '* || "$line" == " "* || "$line" == *" " ]]; then
      echo "line is not '<target> <file>': $line"
      rm -f "$listed"
      return 0
    fi
    target="${line%% *}"
    file="${line#* }"
    if ! target_ok "$target"; then
      echo "target not accepted: $target"
      rm -f "$listed"
      return 0
    fi
    if [[ "$file" == */* || "$file" == *\\* ]]; then
      echo "file is not a bare name: $file"
      rm -f "$listed"
      return 0
    fi
    if ! name_ok "$file"; then
      echo "installer name is not ${PREFIX} followed by '-' or '.': $file"
      rm -f "$listed"
      return 0
    fi
    if [[ ! -f "$dir/$file" ]]; then
      echo "installer missing: $file"
      rm -f "$listed"
      return 0
    fi
    printf '%s\n' "$file" >> "$listed"
  done < "$dir/downloads.txt"
  # One line per installer: every matching file in DIR is listed once.
  local extra
  extra="$(find "$dir" -maxdepth 1 -type f -printf '%f\n' | while read -r f; do
    name_ok "$f" || continue
    count=$(grep -cFx "$f" "$listed" || true)
    if [[ "$count" -ne 1 ]]; then
      printf '%s\n' "$f"
    fi
  done)"
  rm -f "$listed"
  if [[ -n "$extra" ]]; then
    echo "not one line per installer: $extra"
    return 0
  fi
  return 1
}

# Builder gate. script_rc non-zero, or a wrong manifest, publishes nothing.
publish() {
  local src="$1" dest="$2" script_rc="$3"
  mkdir -p "$dest"
  if [[ "$script_rc" -ne 0 ]]; then
    return 0
  fi
  if manifest_wrong "$src" >/dev/null; then
    return 0
  fi
  local line file
  while IFS= read -r line; do
    file="${line#* }"
    cp -a "$src/$file" "$dest/$file"
  done < "$src/downloads.txt"
}

new_case() {
  local id="$1"
  local dir="$WORKDIR/$id/out" pub="$WORKDIR/$id/pub"
  rm -rf "$WORKDIR/$id"
  mkdir -p "$dir" "$pub"
  printf '%s\n' "$dir" "$pub"
}

write_pair() {
  local dir="$1"
  printf 'stub windows-x64 installer (not an NSIS build)\n' > "$dir/$STUB_WIN"
  cp -a "$UBUNTU_PAYLOAD" "$dir/$LINUX_NAME"
  printf 'windows-x64 %s\nlinux-x64 %s\n' "$STUB_WIN" "$LINUX_NAME" > "$dir/downloads.txt"
}

assert_empty() {
  local pub="$1" label="$2"
  if find "$pub" -mindepth 1 -print -quit | grep -q .; then
    fail "$label (publish dir not empty)"
  else
    ok "$label"
  fi
}

echo "== contract readback (epic spec, Contract with the builder)"
echo "R1 invocation: deploy/emu/downloads.sh --component hc2000-emu --version <V> --output <DIR>"
echo "R2 names: hc2000-emu-<V> followed by '-' or '.'  (this run V=$V)"
echo "R3 downloads.txt: one line per installer, '<target> <file>'"
echo "R4 targets: windows-x64, linux-x64; also windows-arm64, linux-arm64"
echo "R5 at most 8 lines"
echo "R6 script failure or wrong manifest -> nothing published"
echo "stub windows-x64 installer file: $STUB_WIN"
echo "ubuntu payload: deploy/emu/install-ubuntu.sh -> $LINUX_NAME"

# --- R1 / R6: the real script, failure before any installer is written ---
mapfile -t paths < <(new_case r1-missing)
out="${paths[0]}" pub="${paths[1]}"
if bash "$DOWNLOADS_SH" --output "$out" >/dev/null 2>&1; then
  fail "R1 missing args should exit non-zero"
else
  ok "R1 missing args: downloads.sh exits non-zero"
fi
publish "$out" "$pub" 1
assert_empty "$pub" "R6 missing args publishes nothing"

mapfile -t paths < <(new_case r1-unknown)
out="${paths[0]}" pub="${paths[1]}"
printf 'canary\n' > "$out/KEEP"
if bash "$DOWNLOADS_SH" --component hc2000-emu --version "$V" --output "$out" --nope >/dev/null 2>&1; then
  fail "R1 unknown arg should exit non-zero"
else
  ok "R1 unknown arg: downloads.sh exits non-zero"
fi
[[ -f "$out/KEEP" && ! -f "$out/downloads.txt" && ! -e "$out/$STUB_WIN" ]] \
  && ok "R1 unknown arg writes no installer and no downloads.txt" \
  || fail "R1 unknown arg mutated the output dir"
publish "$out" "$pub" 1
assert_empty "$pub" "R6 unknown arg publishes nothing"

# --- happy path: merged ubuntu payload + stub windows installer ---
mapfile -t paths < <(new_case happy)
out="${paths[0]}" pub="${paths[1]}"
write_pair "$out"
if manifest_wrong "$out" >/dev/null; then
  fail "R2-R5 happy manifest rejected: $(manifest_wrong "$out")"
else
  ok "R2-R5 happy manifest accepted (dash prefix, two targets, 2 lines)"
fi
publish "$out" "$pub" 0
cmp -s "$UBUNTU_PAYLOAD" "$pub/$LINUX_NAME" \
  && ok "R2 linux installer published is the merged install-ubuntu.sh" \
  || fail "R2 published linux installer is not install-ubuntu.sh"
cmp -s "$out/$STUB_WIN" "$pub/$STUB_WIN" \
  && ok "R3 stub $STUB_WIN published from the windows-x64 line" \
  || fail "R3 stub windows installer was not published"

# --- R2: prefix followed by '.' is accepted; a missing separator is not ---
mapfile -t paths < <(new_case r2-dot)
out="${paths[0]}" pub="${paths[1]}"
dot_name="${PREFIX}.setup.exe"
printf 'dot-form\n' > "$out/$dot_name"
printf 'windows-x64 %s\n' "$dot_name" > "$out/downloads.txt"
if manifest_wrong "$out" >/dev/null; then
  fail "R2 dot-form name rejected"
else
  publish "$out" "$pub" 0
  [[ -f "$pub/$dot_name" ]] && ok "R2 prefix followed by '.' is accepted" \
    || fail "R2 dot-form was not published"
fi

mapfile -t paths < <(new_case r2-bad)
out="${paths[0]}" pub="${paths[1]}"
bad_name="${PREFIX}setup.exe"
printf 'bad\n' > "$out/$bad_name"
printf 'windows-x64 %s\n' "$bad_name" > "$out/downloads.txt"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R2 name without '-' or '.' rejected ($reason)" \
  || fail "R2 bad name was accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 bad name publishes nothing"

# --- R3: line form ---
mapfile -t paths < <(new_case r3-comma)
out="${paths[0]}" pub="${paths[1]}"
write_pair "$out"
printf 'windows-x64,%s\n' "$STUB_WIN" > "$out/downloads.txt"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R3 comma line rejected ($reason)" || fail "R3 comma line accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 bad line form publishes nothing"

mapfile -t paths < <(new_case r3-three)
out="${paths[0]}" pub="${paths[1]}"
write_pair "$out"
printf 'windows-x64 %s extra\n' "$STUB_WIN" > "$out/downloads.txt"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R3 three-field line rejected ($reason)" || fail "R3 three-field line accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 three-field line publishes nothing"

# --- R4: targets ---
mapfile -t paths < <(new_case r4-bad)
out="${paths[0]}" pub="${paths[1]}"
write_pair "$out"
printf 'macos-x64 %s\n' "$STUB_WIN" > "$out/downloads.txt"
# one line, but the linux file would then be an unlisted installer — drop it
rm -f "$out/$LINUX_NAME"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R4 macos-x64 rejected ($reason)" || fail "R4 macos-x64 accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 unknown target publishes nothing"

for target in windows-arm64 linux-arm64; do
  mapfile -t paths < <(new_case "r4-$target")
  out="${paths[0]}" pub="${paths[1]}"
  fname="${PREFIX}-${target}.bin"
  printf '%s\n' "$target" > "$out/$fname"
  printf '%s %s\n' "$target" "$fname" > "$out/downloads.txt"
  if manifest_wrong "$out" >/dev/null; then
    fail "R4 $target rejected: $(manifest_wrong "$out")"
  else
    publish "$out" "$pub" 0
    [[ -f "$pub/$fname" ]] && ok "R4 $target accepted and published" \
      || fail "R4 $target not published"
  fi
done

# --- R5: 8 accepted, 9 rejected ---
mapfile -t paths < <(new_case r5-eight)
out="${paths[0]}" pub="${paths[1]}"
: > "$out/downloads.txt"
for i in 1 2 3 4 5 6 7 8; do
  fname="${PREFIX}-n${i}"
  printf 'n\n' > "$out/$fname"
  printf 'linux-x64 %s\n' "$fname" >> "$out/downloads.txt"
done
if manifest_wrong "$out" >/dev/null; then
  fail "R5 8 lines rejected: $(manifest_wrong "$out")"
else
  publish "$out" "$pub" 0
  count=$(find "$pub" -maxdepth 1 -type f | wc -l)
  [[ "$count" -eq 8 ]] && ok "R5 8 lines accepted and published" \
    || fail "R5 expected 8 published files, got $count"
fi

mapfile -t paths < <(new_case r5-nine)
out="${paths[0]}" pub="${paths[1]}"
: > "$out/downloads.txt"
for i in 1 2 3 4 5 6 7 8 9; do
  fname="${PREFIX}-n${i}"
  printf 'n\n' > "$out/$fname"
  printf 'linux-x64 %s\n' "$fname" >> "$out/downloads.txt"
done
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R5 9 lines rejected ($reason)" || fail "R5 9 lines accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 9 lines publishes nothing"

# --- R3/R6: missing file, missing manifest, extra installer ---
mapfile -t paths < <(new_case r3-missing)
out="${paths[0]}" pub="${paths[1]}"
printf 'windows-x64 %s\n' "$STUB_WIN" > "$out/downloads.txt"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R3 missing installer file rejected ($reason)" || fail "R3 missing file accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 missing file publishes nothing"

mapfile -t paths < <(new_case r3-nomanifest)
out="${paths[0]}" pub="${paths[1]}"
printf 'stub\n' > "$out/$STUB_WIN"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R3 absent downloads.txt rejected ($reason)" || fail "R3 absent manifest accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 absent manifest publishes nothing"

mapfile -t paths < <(new_case r3-extra)
out="${paths[0]}" pub="${paths[1]}"
write_pair "$out"
printf 'extra\n' > "$out/${PREFIX}-extra"
reason="$(manifest_wrong "$out" || true)"
[[ -n "$reason" ]] && ok "R3 extra installer without a line rejected ($reason)" \
  || fail "R3 extra installer accepted"
publish "$out" "$pub" 0
assert_empty "$pub" "R6 extra installer publishes nothing"

# --- R6: a failing script does not publish even if DIR already looks valid ---
mapfile -t paths < <(new_case r6-script)
out="${paths[0]}" pub="${paths[1]}"
write_pair "$out"
fail_sh="$WORKDIR/fail.sh"
printf '#!/bin/sh\nexit 1\n' > "$fail_sh"
set +e
bash "$fail_sh" --component hc2000-emu --version "$V" --output "$out"
rc=$?
set -e
[[ "$rc" -ne 0 ]] && ok "R6 stand-in script exits non-zero" || fail "R6 stand-in script exited 0"
publish "$out" "$pub" "$rc"
assert_empty "$pub" "R6 script failure publishes nothing even with a valid manifest"

echo
echo "Passed $passed, Failed $failed, Total $((passed + failed))"
[[ "$failed" -eq 0 ]]
