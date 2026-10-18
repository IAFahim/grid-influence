#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
plugins=$root/unity/com.gi.entities/Plugins/Gi
variants="netstandard2.1:netstandard2.1"
projects="Gi Gi.Validation"
check=false

for arg in "$@"; do
    case $arg in
        --check) check=true ;;
        *) printf 'usage: build-plugins.sh [--check]\n' >&2; exit 2 ;;
    esac
done

command -v dotnet >/dev/null 2>&1 || { printf 'dotnet not found\n' >&2; exit 2; }

sdk=$(dotnet --version)
staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

manifest=$staging/manifest.txt
printf 'sdk %s\n' "$sdk" >"$manifest"

status=0

for entry in $variants; do
    variant=${entry%%:*}
    tfm=${entry#*:}
    mkdir -p "$plugins/$variant"
    for project in $projects; do
        dotnet build "$root/src/$project/$project.csproj" -c Release -f "$tfm" -t:Rebuild -m:1 -nodeReuse:false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -p:EnableSourceLink=false -p:EmbedAllSources=false
        dll=$root/src/$project/bin/Release/$tfm/$project.dll
        [ -f "$dll" ] || { printf 'missing %s\n' "$dll" >&2; exit 2; }
        sha=$(sha256sum "$dll" | cut -d' ' -f1)
        printf '%s %s %s\n' "$variant" "$project.dll" "$sha" >>"$manifest"
        if $check; then
            cmp -s "$dll" "$plugins/$variant/$project.dll" || { printf 'changed %s/%s\n' "$variant" "$project.dll" >&2; status=1; }
        else
            cp -- "$dll" "$plugins/$variant/$project.dll"
        fi
    done
done

if $check; then
    cmp -s "$manifest" "$plugins/manifest.txt" || { printf 'manifest differs\n' >&2; status=1; }
else
    cp -- "$manifest" "$plugins/manifest.txt"
fi

if [ "$status" -ne 0 ]; then
    printf 'build-plugins.sh --check failed\n' >&2
    exit 1
fi
