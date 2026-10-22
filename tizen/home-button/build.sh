#!/usr/bin/env bash
# Builds HomeQuery.dll and HomeControl.dll into out/ next to this script.
#
# Uses the Roslyn compiler (csc.dll) that ships with any .NET SDK and the .NET Standard 2.1 reference
# assemblies (NETStandard.Library.Ref 2.1.0, part of the SDK's packs). The result is a plain managed .dll,
# no apphost, which the TV's own `dotnet` runs. Tested with SDK 7.0.203.
#
#   DOTNET_ROOT=~/.dotnet ./build.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="${DOTNET_ROOT:-$HOME/.dotnet}"
dotnet="$root/dotnet"
[ -x "$dotnet" ] || dotnet="$(command -v dotnet || true)"
[ -n "$dotnet" ] || { echo "dotnet not found; set DOTNET_ROOT" >&2; exit 1; }
csc="$(ls -d "$root"/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | sort -V | tail -1)"
refs="$root/packs/NETStandard.Library.Ref/2.1.0/ref/netstandard2.1"
[ -f "$csc" ] || { echo "csc.dll not found under $root/sdk/*/Roslyn/bincore" >&2; exit 1; }
[ -f "$refs/netstandard.dll" ] || { echo "NETStandard.Library.Ref 2.1.0 not found under $root/packs" >&2; exit 1; }
mkdir -p "$here/out"
args=(-nologo -noconfig -nostdlib -optimize+ -deterministic -target:exe -platform:anycpu)
for ref in "$refs"/*.dll; do args+=("-reference:$ref"); done
"$dotnet" "$csc" "${args[@]}" -out:"$here/out/HomeQuery.dll" "$here/Query.cs"
"$dotnet" "$csc" "${args[@]}" -out:"$here/out/HomeControl.dll" "$here/Control.cs"
echo "Built out/HomeQuery.dll and out/HomeControl.dll"
