#!/usr/bin/env bash
# Run inside the private checkout; never upload ROMs or derived sources elsewhere.
set -euo pipefail
cd "$(dirname "$0")/.."
timeout 180s dotnet build src/Hc2000.Tools --nologo -v q
for rom in roms/hc2000/*/*.rom; do
    version=$(basename "$(dirname "$rom")")
    name=$(basename "$rom" .rom)
    origin=0000
    if [[ "$version/$name" == v2/cpm ]]; then origin=E000; fi
    timeout 60s dotnet src/Hc2000.Tools/bin/Debug/net10.0/hc.dll disasm \
        "$rom" --org "$origin" --ctl "bios/src/hc2000/$version/$name.ctl" \
        -o "bios/src/hc2000/$version/$name.asm"
done
