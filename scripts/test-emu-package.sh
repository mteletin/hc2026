#!/usr/bin/env bash
# Hermetic packaging/installer protocol checks for the hc2000-emu package:
# fake publish/runtime, disposable own-account /tmp install.
set -euo pipefail
cd "$(dirname "$0")/.."
root=$PWD
tmp=$(mktemp -d)
itmp=$(mktemp -d /tmp/hc2000-emu-protocol.XXXXXX)
trap 'rm -rf -- "$tmp" "$itmp"' EXIT
mkdir "$tmp/mock"
cat > "$tmp/mock/dotnet" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
if [[ $1 == --list-runtimes ]]; then
  echo "Microsoft.NETCore.App ${MOCK_RUNTIME:-10.0.0} [/mock]"
  exit
fi
printf '%s\n' "$@" > "$MOCK_ARGS"
out=''
while (($#)); do
  if [[ $1 == -o ]]; then out=$2; shift; fi
  shift
done
mkdir -p "$out"
printf '#!/usr/bin/env bash\necho mock-hc2000-app\n' > "$out/Hc2000.App"
chmod +x "$out/Hc2000.App"
touch "$out/Hc2000.App.dll" "$out/Hc2000.App.runtimeconfig.json" "$out/Hc2000.Machine.dll" "$out/Hc2000.Disasm.dll"
MOCK
chmod +x "$tmp/mock/dotnet"
# A compiler, sysroot probe or network fetch is a regression, even if a negative
# test would otherwise swallow its failure. Managed publish alone is mocked.
for tool in gcc cc clang make pkg-config ld as readelf objdump curl wget x86_64-w64-mingw32-gcc-win32; do
  cat > "$tmp/mock/$tool" <<'FORBIDDEN'
#!/usr/bin/env bash
printf '%s\n' "$0 $*" >> "$NATIVE_TOOL_CALLS"
exit 99
FORBIDDEN
  chmod +x "$tmp/mock/$tool"
done
export PATH="$tmp/mock:$PATH" MOCK_ARGS="$tmp/publish-args" NATIVE_TOOL_CALLS="$tmp/native-tool-calls"
checks=0
check() { "$@" || { echo "FAILED: $*" >&2; exit 1; }; checks=$((checks+1)); }
reject() { if "$@" > "$tmp/rejection" 2>&1; then echo "unexpected success: $*" >&2; exit 1; fi; checks=$((checks+1)); }
version_digest=$(sha256sum VERSION.md)
bash scripts/package-actor.sh --project src/Hc2000.App/Hc2000.App.csproj --version-file VERSION.md --version 0.1.26 --deploy deploy/emu --output "$tmp/emu.tar.gz"
check grep -Fx -- '-p:Version=0.1.26' "$MOCK_ARGS"
check grep -Fx -- "-p:InformationalVersion=0.1.26+$(git rev-parse HEAD)" "$MOCK_ARGS"
check test "$(sha256sum VERSION.md)" = "$version_digest"
mkdir "$tmp/artifact"
tar -C "$tmp/artifact" -xzf "$tmp/emu.tar.gz"
check test -f "$tmp/artifact/bin/runtimes/linux-x64/native/libSDL2-2.0.so.0"
check timeout 20 python3 scripts/check-linux-sdl2.py "$tmp/artifact/bin"
sha256sum "$tmp/emu.tar.gz" "$tmp/artifact/bin/runtimes/linux-x64/native/libSDL2-2.0.so.0"
if [[ -n ${SDL2_TEST_RECEIPTS:-} ]]; then
  mkdir -p "$SDL2_TEST_RECEIPTS"
  cp "$tmp/emu.tar.gz" "$SDL2_TEST_RECEIPTS/protocol-emu.tar.gz"
fi
bundle="$root/deploy/emu/sdl2/linux-x64"
reject env SDL2_PREBUILT_DIR="$tmp/unavailable" bash deploy/emu/package-linux-sdl2.sh "$tmp/artifact/bin"
check grep -F 'pinned SDL2 bundle unavailable' "$tmp/rejection"
cp -a "$bundle" "$tmp/relocated"
check env SDL2_PREBUILT_DIR="$tmp/relocated" bash deploy/emu/package-linux-sdl2.sh "$tmp/artifact/bin"
for file in libSDL2-2.0.so.0 SDL2-LICENSE.txt SDL2-config.h SDL2-configure.log SDL2-provenance.txt SDL2-MANIFEST.sha256; do
  mv "$tmp/relocated/$file" "$tmp/saved"
  reject env SDL2_PREBUILT_DIR="$tmp/relocated" bash deploy/emu/package-linux-sdl2.sh "$tmp/artifact/bin"
  mv "$tmp/saved" "$tmp/relocated/$file"
  cp "$tmp/relocated/$file" "$tmp/saved"
  printf 'corrupt' >> "$tmp/relocated/$file"
  reject env SDL2_PREBUILT_DIR="$tmp/relocated" bash deploy/emu/package-linux-sdl2.sh "$tmp/artifact/bin"
  mv "$tmp/saved" "$tmp/relocated/$file"
done
# Recalculating a manifest over arbitrary bytes must not manufacture trust.
printf 'corrupt' >> "$tmp/relocated/libSDL2-2.0.so.0"
(cd "$tmp/relocated" && sha256sum libSDL2-2.0.so.0 SDL2-LICENSE.txt SDL2-config.h SDL2-configure.log SDL2-provenance.txt > SDL2-MANIFEST.sha256)
reject env SDL2_PREBUILT_DIR="$tmp/relocated" bash deploy/emu/package-linux-sdl2.sh "$tmp/artifact/bin"
check timeout 20 python3 scripts/check-linux-sdl2.py "$tmp/artifact/bin"
check test -x "$tmp/artifact/install.sh"
check test "$(cat "$tmp/artifact/source-commit")" = "$(git rev-parse HEAD)"
check test "$(cat "$tmp/artifact/VERSION")" = 0.1.26
check test "$(find "$tmp/artifact" -mindepth 1 -maxdepth 1 -printf '%f\n' | sort | tr '\n' ' ')" = 'VERSION bin deploy install.sh source-commit '
reject bash scripts/package-actor.sh --version
reject bash scripts/package-actor.sh --project src/Hc2000.App/Hc2000.App.csproj --version-file VERSION.md --version ../bad --deploy deploy/emu --output "$tmp/bad.tar.gz"
reject bash scripts/package-actor.sh --project src/Hc2000.App/Hc2000.App.csproj --version-file VERSION.md --version 0.1.26 --deploy deploy/tools --output "$tmp/mismatch.tar.gz"
cd "$tmp/artifact"
reject bash install.sh --dest /etc/hc2000-emu
reject bash install.sh --dest relative
MOCK_RUNTIME=9.0.0 reject bash install.sh --dest "$itmp/no-runtime"
check test ! -e "$itmp/no-runtime"
printf '../bad\n' > VERSION
reject bash install.sh --dest "$itmp/bad-version"
check test ! -e "$itmp/bad-version"
printf '0.1.26\n' > VERSION
printf 'not-a-commit\n' > source-commit
reject bash install.sh --dest "$itmp/bad-commit"
check test ! -e "$itmp/bad-commit"
git -C "$root" rev-parse HEAD > source-commit
mv bin/Hc2000.Machine.dll bin/Hc2000.Machine.dll.saved
reject bash install.sh --dest "$itmp/incomplete"
check test ! -e "$itmp/incomplete"
mv bin/Hc2000.Machine.dll.saved bin/Hc2000.Machine.dll
native=bin/runtimes/linux-x64/native
mv "$native/libSDL2-2.0.so.0" "$tmp/sdl.so"
reject bash install.sh --dest "$itmp/missing-sdl"
check test ! -e "$itmp/missing-sdl"
mv "$tmp/sdl.so" "$native/libSDL2-2.0.so.0"
cp "$native/libSDL2-2.0.so.0" "$tmp/sdl.so"
printf 'corrupt' >> "$native/libSDL2-2.0.so.0"
reject bash install.sh --dest "$itmp/corrupt-sdl"
check test ! -e "$itmp/corrupt-sdl"
mv "$tmp/sdl.so" "$native/libSDL2-2.0.so.0"
bash install.sh --dest "$itmp/done"
check timeout 20 python3 "$root/scripts/check-linux-sdl2.py" "$itmp/done/app"
check cmp "$native/libSDL2-2.0.so.0" "$itmp/done/app/runtimes/linux-x64/native/libSDL2-2.0.so.0"
check test "$("$itmp/done/hc2000")" = mock-hc2000-app
check test "$(cat "$itmp/done/source-commit")" = "$(git -C "$root" rev-parse HEAD)"
reject bash install.sh --dest "$itmp/done"
check test "$(cat "$itmp/done/VERSION")" = 0.1.26

# Exercise the other shared-helper caller too, including the real Linux .run
# assembly/extraction/installer. NSIS is mocked: this is not a Windows test.
cd "$root"
cat > "$tmp/mock/makensis" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
for arg in "$@"; do
  case "$arg" in -DOUTFILE=*) printf 'mock NSIS output\n' > "${arg#-DOUTFILE=}";; esac
done
MOCK
chmod +x "$tmp/mock/makensis"
mkdir "$tmp/downloads" "$tmp/runhome"
check timeout 60 bash deploy/emu/downloads.sh --component hc2000-emu --version 0.1.26 --output "$tmp/downloads"
check grep -Fx 'linux-x64 hc2000-emu-0.1.26-linux-x64.run' "$tmp/downloads/downloads.txt"
check timeout 30 env HOME="$tmp/runhome" bash "$tmp/downloads/hc2000-emu-0.1.26-linux-x64.run" --prefix "$tmp/runprefix"
check timeout 20 python3 scripts/check-linux-sdl2.py "$tmp/runprefix"
for file in libSDL2-2.0.so.0 SDL2-LICENSE.txt SDL2-config.h SDL2-configure.log SDL2-provenance.txt SDL2-MANIFEST.sha256; do
  check cmp "$tmp/artifact/bin/runtimes/linux-x64/native/$file" "$tmp/runprefix/runtimes/linux-x64/native/$file"
done
# The same Ubuntu installer must reject native damage before removing an old
# owned file, and must not create a fresh prefix. Restore the file per case.
mkdir "$tmp/bad-run"
cp deploy/emu/install-ubuntu.sh "$tmp/bad-run/"
printf '0.1.27\n' > "$tmp/bad-run/VERSION"
cp -a "$tmp/runprefix" "$tmp/bad-run/app"
printf 'do not remove\n' > "$tmp/runprefix/guard"
printf 'guard\n' >> "$tmp/runprefix/.hc2000-owned.txt"
for file in libSDL2-2.0.so.0 SDL2-LICENSE.txt SDL2-config.h SDL2-configure.log SDL2-provenance.txt SDL2-MANIFEST.sha256; do
  damaged="$tmp/bad-run/app/runtimes/linux-x64/native/$file"
  mv "$damaged" "$tmp/saved"
  reject env HOME="$tmp/runhome" bash "$tmp/bad-run/install-ubuntu.sh" --prefix "$tmp/runprefix"
  check test -f "$tmp/runprefix/guard"
  reject env HOME="$tmp/runhome" bash "$tmp/bad-run/install-ubuntu.sh" --prefix "$tmp/fresh-prefix"
  check test ! -e "$tmp/fresh-prefix"
  mv "$tmp/saved" "$damaged"
  cp "$damaged" "$tmp/saved"
  printf 'corrupt' >> "$damaged"
  reject env HOME="$tmp/runhome" bash "$tmp/bad-run/install-ubuntu.sh" --prefix "$tmp/runprefix"
  check test -f "$tmp/runprefix/guard"
  mv "$tmp/saved" "$damaged"
done
# Reject arbitrary Windows input too, rather than resurrecting native builds.
printf 'arbitrary DLL' > "$tmp/bad.dll"
reject env SDL2_DLL="$tmp/bad.dll" timeout 60 bash deploy/emu/downloads.sh --component hc2000-emu --version 0.1.27 --output "$tmp/downloads"
check test ! -e "$tmp/downloads/hc2000-emu-0.1.27-linux-x64.run"
check test ! -e "$NATIVE_TOOL_CALLS"
printf 'Packaging protocol checks: %s passed (mock managed publish/runtime and NSIS, real pinned native load and Linux installer; no compiler/network, registered artifact or App/audio launch).\n' "$checks"
