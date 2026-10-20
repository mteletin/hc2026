#!/usr/bin/env bash
# Shared compiler-free native assembly for the generic archive and installer.
# Decision53: reuse the original SDL2 2.32.10 bundle, never build or use host SDL.
set -euo pipefail
[[ $# == 1 && $(uname -m) == x86_64 && $(uname -s) == Linux ]] || { echo 'usage: package-linux-sdl2.sh APP_DIRECTORY (Linux x64)' >&2; exit 2; }
app=$(realpath "$1")
script_dir=$(cd "$(dirname "$0")" && pwd)
bundle=${SDL2_PREBUILT_DIR:-"$script_dir/sdl2/linux-x64"}
# Original five-file manifest from the digest-verified cf02fd43... factory tar;
# pins native 7444ff99... and its unchanged license/config/build provenance.
# An override may relocate these exact bytes, not replace the trust anchor.
manifest_sha=eaa8c4d123d42eebb818963eab6e4bfec19a5346e7c627da8cdac858ffa6f074
files=(libSDL2-2.0.so.0 SDL2-LICENSE.txt SDL2-config.h SDL2-configure.log SDL2-provenance.txt SDL2-MANIFEST.sha256)
for file in "${files[@]}"; do
  [[ -f "$bundle/$file" ]] || { echo "pinned SDL2 bundle unavailable: $bundle/$file; restore the repository/LFS bundle (no native rebuild fallback)" >&2; exit 1; }
done
verify() {
  (cd "$1" && printf '%s  SDL2-MANIFEST.sha256\n' "$manifest_sha" | sha256sum --strict -c - && sha256sum --strict -c SDL2-MANIFEST.sha256)
}
verify "$bundle"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
for file in "${files[@]}"; do cp -- "$bundle/$file" "$work/$file"; done
# Check staged bytes too, before changing the destination, even for overrides.
verify "$work"
native="$app/runtimes/linux-x64/native"
mkdir -p "$native"
for file in "${files[@]}"; do install -m 0644 "$work/$file" "$native/$file"; done
chmod 0755 "$native/libSDL2-2.0.so.0"
