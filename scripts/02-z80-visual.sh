#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
exec dotnet run --project src/Hc2000.App/Hc2000.App.csproj -c Release -- --demo z80
