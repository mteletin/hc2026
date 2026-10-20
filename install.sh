#!/usr/bin/env bash
# One-shot HC2000 emulator install for Ubuntu (x64): downloads a release's Linux installer from GitHub, checks its
# sha256 against the digest GitHub publishes for the asset, and runs it. No sudo: the installer puts everything under
# $HOME (see deploy/emu/install-ubuntu.sh).
#   curl -fsSL https://raw.githubusercontent.com/mteletin/hc2026/main/install.sh | bash
#   ... | bash -s -- --prefix ~/apps/hc2000     options go to the installer (--prefix, --uninstall, --help)
#   ... | HC2000_VERSION=0.1.118 bash            a given version instead of the latest
set -euo pipefail

main() {
  local repo="${HC2000_REPO:-mteletin/hc2026}" version="${HC2000_VERSION:-}" api json name="" digest="" url=""
  [ "$(uname -s)" = Linux ] || die "this script is for Linux; on Windows use install.ps1"
  case "$(uname -m)" in x86_64|amd64) ;; *) die "only x64 is built, this machine is $(uname -m)" ;; esac
  command -v curl >/dev/null || die "curl is needed: sudo apt install curl"
  command -v sha256sum >/dev/null || die "sha256sum is needed (package coreutils)"

  if [ -n "$version" ]; then api="https://api.github.com/repos/$repo/releases/tags/v${version#v}"
  else api="https://api.github.com/repos/$repo/releases/latest"; fi
  json="$(curl -fsSL -H 'Accept: application/vnd.github+json' "$api")" || die "cannot read $api"

  # One JSON field per line, whether GitHub sends it pretty-printed or compact. In an asset the name comes before its
  # digest and its download URL.
  read -r name digest url < <(printf '%s' "$json" | tr ',{}' '\n\n\n' | awk '
    /"name": *"hc2000-emu-[^"]*-linux-x64\.run"/ { n = $0; sub(/.*"name": *"/, "", n); sub(/".*/, "", n); next }
    n != "" && /"digest":/ { d = $0; sub(/.*"digest": *"?(sha256:)?/, "", d); sub(/".*/, "", d); next }
    n != "" && /"browser_download_url":/ { u = $0; sub(/.*"browser_download_url": *"/, "", u); sub(/".*/, "", u)
      print n, d, u; exit }') || true
  [ -n "$name" ] && [ -n "$url" ] || die "no Linux installer in $api"
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || die "GitHub published no sha256 for $name; nothing was installed"

  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  echo "Downloading $name"
  curl -fL --progress-bar -o "$tmp/$name" "$url" || die "download of $url failed"
  echo "$digest  $tmp/$name" | sha256sum -c --status - || die "$name does not match its sha256; nothing was installed"
  bash "$tmp/$name" "$@" < /dev/null

  case " $* " in *" --uninstall "*|*" --help "*|*" -h "*) return ;; esac
  # A system library the app needs (SkiaSharp, under Avalonia); Ubuntu Desktop has it, a minimal install may not.
  # grep reads all of it (no -q), so pipefail sees no SIGPIPE; ldconfig is in /sbin when PATH lacks it.
  { ldconfig -p 2>/dev/null || /sbin/ldconfig -p 2>/dev/null; } | grep 'libfontconfig\.so\.1 ' >/dev/null ||
    echo "The app needs libfontconfig1, which this system lacks: sudo apt install libfontconfig1"
  command -v hc2000 >/dev/null ||
    echo "~/.local/bin is not on your PATH in this shell yet: run ~/.local/bin/hc2000, or log out and back in."
}

die() { echo "hc2000 install: $*" >&2; exit 1; }

tmp="" # global: the EXIT trap runs after main has returned

# Called on the last line, so that bash has read the whole script before it runs any of it when it comes from a pipe.
main "$@"
