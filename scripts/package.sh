#!/usr/bin/env bash
# Usage: scripts/package.sh <version> — builds dist/BepInEx/plugins/Helmsman (plugin + SDL3 for Windows) and Helmsman-<version>.zip.
set -euo pipefail

version="$1"
sdl_version=3.4.18
sdl_sha256=75c2c0fc74e7d1206aaedc22893a35887332f4f85f5c8b74fddc10d177985f2c

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet build src/Helmsman -c Release -p:Version="$version"

sdl_zip="$work/SDL3-$sdl_version-win32-x64.zip"
curl -fsSL -o "$sdl_zip" "https://github.com/libsdl-org/SDL/releases/download/release-$sdl_version/SDL3-$sdl_version-win32-x64.zip"
echo "$sdl_sha256  $sdl_zip" | sha256sum -c -
unzip -q "$sdl_zip" SDL3.dll LICENSE.txt -d "$work/sdl"

rm -rf dist "Helmsman-$version.zip"
dest=dist/BepInEx/plugins/Helmsman
mkdir -p "$dest"
cp src/Helmsman/bin/Release/netstandard2.1/Helmsman.dll README.md LICENSE "$dest/"
cp "$work/sdl/SDL3.dll" "$dest/"
cp "$work/sdl/LICENSE.txt" "$dest/SDL3-LICENSE.txt"
(cd dist && python3 -m zipfile -c "../Helmsman-$version.zip" BepInEx)
echo "Packaged Helmsman-$version.zip"
