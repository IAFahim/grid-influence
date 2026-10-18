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
corpus_project=$root/unity/tests/Gi.Corpus/Gi.Corpus.csproj
corpus_stage=$root/unity/com.gi.entities/Tests/Corpus
check=false

for arg in "$@"; do
    case $arg in
        --check) check=true ;;
        --corpus) ;;
        *) printf 'usage: build-plugins.sh [--check] [--corpus]\n' >&2; exit 2 ;;
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

dotnet build "$corpus_project" -c Release -f netstandard2.1 -t:Rebuild -m:1 -nodeReuse:false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -p:EnableSourceLink=false -p:EmbedAllSources=false
corpus_dll=$root/unity/tests/Gi.Corpus/bin/Release/netstandard2.1/Gi.Corpus.dll
[ -f "$corpus_dll" ] || { printf 'missing %s\n' "$corpus_dll" >&2; exit 2; }
printf 'corpus Gi.Corpus.dll %s\n' "$(sha256sum "$corpus_dll" | cut -d' ' -f1)" >>"$manifest"
if $check; then
    cmp -s "$corpus_dll" "$corpus_stage/Gi.Corpus.dll" || { printf 'changed Tests/Corpus/Gi.Corpus.dll\n' >&2; status=1; }
else
    mkdir -p "$corpus_stage"
    cp -- "$corpus_dll" "$corpus_stage/Gi.Corpus.dll"
fi

frames_project=$root/unity/tests/Gi.Frames/Gi.Frames.csproj
frames_stage=$root/unity/com.gi.entities/Tests/Performance
dotnet build "$frames_project" -c Release -f netstandard2.1 -t:Rebuild -m:1 -nodeReuse:false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -p:EnableSourceLink=false -p:EmbedAllSources=false
frames_dll=$root/unity/tests/Gi.Frames/bin/Release/netstandard2.1/GiBenchmarks.dll
[ -f "$frames_dll" ] || { printf 'missing %s\n' "$frames_dll" >&2; exit 2; }
printf 'frames GiBenchmarks.dll %s\n' "$(sha256sum "$frames_dll" | cut -d' ' -f1)" >>"$manifest"
if $check; then
    cmp -s "$frames_dll" "$frames_stage/GiBenchmarks.dll" || { printf 'changed Tests/Performance/GiBenchmarks.dll\n' >&2; status=1; }
else
    cp -- "$frames_dll" "$frames_stage/GiBenchmarks.dll"
fi

if $check; then
    cmp -s "$manifest" "$plugins/manifest.txt" || { printf 'manifest differs\n' >&2; status=1; }
else
    cp -- "$manifest" "$plugins/manifest.txt"
fi

if [ "$status" -ne 0 ]; then
    printf 'build-plugins.sh --check failed\n' >&2
    exit 1
fi
