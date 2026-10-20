#!/usr/bin/env bash
# The factory builder's package protocol, for the HC CLI (hc2000-tools) and
# the HC2000 emulator/App (hc2000-emu: machine + Avalonia App in one package).
set -euo pipefail
usage() {
  echo 'usage: package-actor.sh --project src/Hc2000.Tools/Hc2000.Tools.csproj --version-file VERSION.md --version major.minor.build --deploy deploy/tools --output FILE' >&2
  echo '       package-actor.sh --project src/Hc2000.App/Hc2000.App.csproj --version-file VERSION.md --version major.minor.build --deploy deploy/emu --output FILE' >&2
  exit 2
}
recipe_ok() {
  [[ $version_file == VERSION.md && -n $output ]] || return 1
  if [[ $project == src/Hc2000.Tools/Hc2000.Tools.csproj && $deploy == deploy/tools ]]; then
    return 0
  fi
  if [[ $project == src/Hc2000.App/Hc2000.App.csproj && $deploy == deploy/emu ]]; then
    return 0
  fi
  return 1
}
project='' version_file='' version='' deploy='' output=''
while (($#)); do
  (($# >= 2)) || usage
  case "$1" in
    --project) project=$2 ;;
    --version-file) version_file=$2 ;;
    --version) version=$2 ;;
    --deploy) deploy=$2 ;;
    --output) output=$2 ;;
    *) usage ;;
  esac
  shift 2
done
recipe_ok || usage
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || usage
[[ -f $version_file && -f $project && -f $deploy/artifact-install.sh ]] || usage
commit=$(git rev-parse HEAD)
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT
# Stamp build output, never rewrite the merge owner's VERSION.md. No ROMs/disks/fixtures.
dotnet publish "$project" -c Release -r linux-x64 --self-contained false -o "$stage/bin" \
  -p:Version="$version" -p:InformationalVersion="$version+$commit" -p:SourceRevisionId="$commit"
if [[ $deploy == deploy/emu ]]; then
  bash deploy/emu/package-linux-sdl2.sh "$stage/bin"
fi
cp -a "$deploy" "$stage/deploy"
install -m 0755 "$deploy/artifact-install.sh" "$stage/install.sh"
printf '%s\n' "$version" > "$stage/VERSION"
printf '%s\n' "$commit" > "$stage/source-commit"
tar -C "$stage" -czf "$output" .
printf 'artifact=%s\nversion=%s\nsource_commit=%s\n' "$output" "$version" "$commit"
