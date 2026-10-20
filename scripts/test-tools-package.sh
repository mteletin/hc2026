#!/usr/bin/env bash
# Hermetic packaging/installer protocol checks: fake publish/runtime, disposable prefix.
set -euo pipefail
cd "$(dirname "$0")/.."
root=$PWD
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT
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
printf '#!/usr/bin/env bash\necho mock-hc\n' > "$out/hc"
chmod +x "$out/hc"
touch "$out/hc.dll" "$out/hc.runtimeconfig.json"
MOCK
chmod +x "$tmp/mock/dotnet"
export PATH="$tmp/mock:$PATH" MOCK_ARGS="$tmp/publish-args"
checks=0
check() { "$@" || { echo "FAILED: $*" >&2; exit 1; }; checks=$((checks+1)); }
reject() { if "$@" > "$tmp/rejection" 2>&1; then echo "unexpected success: $*" >&2; exit 1; fi; checks=$((checks+1)); }
version_digest=$(sha256sum VERSION.md)
bash scripts/package-actor.sh --project src/Hc2000.Tools/Hc2000.Tools.csproj --version-file VERSION.md --version 0.1.3 --deploy deploy/tools --output "$tmp/hc.tar.gz"
check grep -Fx -- '-p:Version=0.1.3' "$MOCK_ARGS"
check grep -Fx -- "-p:InformationalVersion=0.1.3+$(git rev-parse HEAD)" "$MOCK_ARGS"
check test "$(sha256sum VERSION.md)" = "$version_digest"
mkdir "$tmp/artifact"
tar -C "$tmp/artifact" -xzf "$tmp/hc.tar.gz"
check test -x "$tmp/artifact/install.sh"
check test "$(cat "$tmp/artifact/source-commit")" = "$(git rev-parse HEAD)"
check test "$(cat "$tmp/artifact/VERSION")" = 0.1.3
check test "$(find "$tmp/artifact" -mindepth 1 -maxdepth 1 -printf '%f\n' | sort | tr '\n' ' ')" = 'VERSION bin deploy install.sh source-commit '
reject bash scripts/package-actor.sh --version
reject bash scripts/package-actor.sh --project src/Hc2000.Tools/Hc2000.Tools.csproj --version-file VERSION.md --version ../bad --deploy deploy/tools --output "$tmp/bad.tar.gz"
cd "$tmp/artifact"
reject bash install.sh --prefix /
reject bash install.sh --prefix /tmp/..
reject bash install.sh --prefix relative
MOCK_RUNTIME=9.0.0 reject bash install.sh --prefix "$tmp/no-runtime"
check test ! -e "$tmp/no-runtime"
printf '../bad\n' > VERSION
reject bash install.sh --prefix "$tmp/bad-version"
check test ! -e "$tmp/bad-version"
printf '0.1.3\n' > VERSION
printf 'not-a-commit\n' > source-commit
reject bash install.sh --prefix "$tmp/bad-commit"
check test ! -e "$tmp/bad-commit"
git -C "$root" rev-parse HEAD > source-commit
mv bin/hc.dll bin/hc.dll.saved
reject bash install.sh --prefix "$tmp/incomplete"
check test ! -e "$tmp/incomplete"
mv bin/hc.dll.saved bin/hc.dll
bash install.sh --prefix "$tmp/clean prefix"
check test "$("$tmp/clean prefix/bin/hc")" = mock-hc
check test "$(cat "$tmp/clean prefix/lib/hc2000-tools/0.1.3/source-commit")" = "$(git -C "$root" rev-parse HEAD)"
reject bash install.sh --prefix "$tmp/clean prefix"
check test "$("$tmp/clean prefix/bin/hc")" = mock-hc
printf 'Packaging protocol checks: %s passed (mock publish/runtime; no registered artifact or real CLI installation).\n' "$checks"
