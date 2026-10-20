#!/usr/bin/env bash
# install_entry: run from an extracted, digest-verified factory artifact.
# Disposable own-account /tmp install of the HC2000 emulator/App.
# No system directories, no service, no D-Bus registration, no apt/dnf.
# Removal = rm -rf <dest>.
set -euo pipefail
umask 077
dest=''
while (($#)); do
  [[ $1 == --dest && $# -ge 2 ]] || { echo 'usage: install.sh [--dest /tmp/ABSOLUTE_DIRECTORY]' >&2; exit 2; }
  dest=$2
  shift 2
done
[[ -z $dest || ($dest == /tmp/* && $dest != *..*) ]] || { echo 'dest must be a directory under /tmp' >&2; exit 2; }
[[ -f VERSION && -f source-commit && -x bin/Hc2000.App && -f bin/Hc2000.App.dll \
  && -f bin/Hc2000.App.runtimeconfig.json && -f bin/Hc2000.Machine.dll ]] || { echo 'incomplete HC2000 emu artifact' >&2; exit 1; }
native=bin/runtimes/linux-x64/native
[[ -f $native/libSDL2-2.0.so.0 && -f $native/SDL2-MANIFEST.sha256 ]] || { echo 'missing pinned SDL2 payload' >&2; exit 1; }
(cd "$native" && sha256sum --strict -c SDL2-MANIFEST.sha256) || { echo 'corrupt SDL2 payload' >&2; exit 1; }
version=$(cat VERSION)
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'invalid artifact version' >&2; exit 1; }
[[ $(cat source-commit) =~ ^[0-9a-f]{40}$ ]] || { echo 'invalid source commit' >&2; exit 1; }
runtimes=$(timeout 10 dotnet --list-runtimes)
grep -q '^Microsoft.NETCore.App 10\.' <<< "$runtimes" || { echo '.NET 10 runtime is required' >&2; exit 1; }
[[ -z $dest || ! -e $dest ]] || { echo "dest already exists: $dest" >&2; exit 1; }
if [[ -z $dest ]]; then
  dest=$(mktemp -d /tmp/hc2000-emu-install.XXXXXX)
else
  mkdir -- "$dest"
fi
trap 'rm -rf -- "$dest"' EXIT
cp -a bin "$dest/app"
chmod +x -- "$dest/app/Hc2000.App"
install -m 0644 VERSION source-commit "$dest/"
cat > "$dest/hc2000" <<EOF
#!/usr/bin/env bash
exec "$dest/app/Hc2000.App" "\$@"
EOF
chmod 0755 -- "$dest/hc2000"
trap - EXIT
printf 'installed=hc2000-emu\nversion=%s\nsource_commit=%s\ndest=%s\ncommand=%s/hc2000\n' "$version" "$(cat source-commit)" "$dest" "$dest"
