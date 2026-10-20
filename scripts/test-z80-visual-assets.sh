#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
dotnet run --project src/Hc2000.Tools/Hc2000.Tools.csproj -c Release -- asm software/demos/z80-visual.asm -o "$tmp/paint.bin"
dotnet run --project src/Hc2000.Tools/Hc2000.Tools.csproj -c Release -- asm software/demos/z80-visual-negative.asm -o "$tmp/negative.bin"
cmp software/demos/z80-visual.bin "$tmp/paint.bin"
cmp software/demos/z80-visual-negative.bin "$tmp/negative.bin"
sha256sum software/demos/z80-visual.asm software/demos/z80-visual.bin software/demos/z80-visual-negative.asm software/demos/z80-visual-negative.bin
