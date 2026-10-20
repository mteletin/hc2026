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
export PATH="$tmp/mock:$PATH" MOCK_ARGS="$tmp/publish-args"
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
bash install.sh --dest "$itmp/done"
check test "$("$itmp/done/hc2000")" = mock-hc2000-app
check test "$(cat "$itmp/done/source-commit")" = "$(git -C "$root" rev-parse HEAD)"
reject bash install.sh --dest "$itmp/done"
check test "$(cat "$itmp/done/VERSION")" = 0.1.26
printf 'Packaging protocol checks: %s passed (mock publish/runtime; no registered artifact or real App installation).\n' "$checks"
