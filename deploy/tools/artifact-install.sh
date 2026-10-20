#!/usr/bin/env bash
# install_entry: run from an extracted, digest-verified factory artifact.
# No service, user data, /usr/local or system configuration is touched.
set -euo pipefail
prefix=/opt/hc2000-tools
while (($#)); do
  [[ $1 == --prefix && $# -ge 2 ]] || { echo 'usage: install.sh [--prefix ABSOLUTE_DIRECTORY]' >&2; exit 2; }
  prefix=$2
  shift 2
done
[[ $prefix == /* && $prefix != / ]] || { echo 'prefix must be an absolute non-root directory' >&2; exit 2; }
prefix=$(realpath -m -- "$prefix")
[[ $prefix != / ]] || { echo 'prefix must not resolve to /' >&2; exit 2; }
[[ -f VERSION && -f source-commit && -x bin/hc && -f bin/hc.dll && -f bin/hc.runtimeconfig.json ]] || { echo 'incomplete HC2000 Tools artifact' >&2; exit 1; }
version=$(cat VERSION)
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'invalid artifact version' >&2; exit 1; }
[[ $(cat source-commit) =~ ^[0-9a-f]{40}$ ]] || { echo 'invalid source commit' >&2; exit 1; }
runtimes=$(timeout 10 dotnet --list-runtimes)
grep -q '^Microsoft.NETCore.App 10\.' <<< "$runtimes" || { echo '.NET 10 runtime is required' >&2; exit 1; }
target="$prefix/lib/hc2000-tools/$version"
[[ ! -e $target && ! -L $target ]] || { echo "version already installed: $target" >&2; exit 1; }
[[ ! -e $prefix/bin/hc && ! -L $prefix/bin/hc ]] || { echo 'hc already exists in this prefix; use a clean prefix' >&2; exit 1; }
mkdir -p -- "$prefix/lib/hc2000-tools" "$prefix/bin"
stage=$(mktemp -d "$prefix/lib/hc2000-tools/.incoming.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT
cp -a bin/. "$stage/"
install -m 0644 VERSION source-commit "$stage/"
mv -T -- "$stage" "$target"
ln -s -- "../lib/hc2000-tools/$version/hc" "$prefix/bin/hc"
printf 'installed=hc2000-tools\nversion=%s\nsource_commit=%s\ncommand=%s/bin/hc\n' "$version" "$(cat source-commit)" "$prefix"
