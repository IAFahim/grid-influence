#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
plugins=$root/unity/com.gi.entities/Plugins/Gi
projects="Gi Gi.Validation"
corpus_project=$root/unity/tests/Gi.Corpus/Gi.Corpus.csproj
corpus_stage=$root/unity/com.gi.entities/Tests/Corpus
retarget_project=$root/unity/tests/Gi.Retarget/Gi.Retarget.csproj
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

dotnet build "$retarget_project" -c Release -m:1 -nodeReuse:false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -p:EnableSourceLink=false -p:EmbedAllSources=false
retarget=$root/unity/tests/Gi.Retarget/bin/Release/net10.0/Gi.Retarget.dll

netstandard_ref() {
    if [ -n "${NETSTANDARD_REF:-}" ]; then
        [ -f "$NETSTANDARD_REF/netstandard.dll" ] && { printf '%s/netstandard.dll' "$NETSTANDARD_REF"; return 0; }
        [ -f "$NETSTANDARD_REF" ] && { printf '%s' "$NETSTANDARD_REF"; return 0; }
    fi
    for dir in "$HOME"/Unity/Hub/Editor/*/Editor/Data/NetStandard/ref/2.1.0; do
        [ -f "$dir/netstandard.dll" ] && { printf '%s/netstandard.dll' "$dir"; return 0; }
    done
    cache=$(dotnet nuget locals global-packages -l | sed 's/^global-packages: //')
    dll=$cache/netstandard.library.ref/2.1.0/ref/netstandard2.1/netstandard.dll
    [ -f "$dll" ] && { printf '%s' "$dll"; return 0; }
    return 1
}

shims_dir() {
    if [ -n "${NETSTANDARD_REF:-}" ]; then printf '%s' "${NETSTANDARD_REF%/ref/2.1.0}/compat/2.1.0/shims/netstandard"; return 0; fi
    for dir in "$HOME"/Unity/Hub/Editor/*/Editor/Data/NetStandard/compat/2.1.0/shims/netstandard; do
        [ -d "$dir" ] && { printf '%s' "$dir"; return 0; }
    done
    return 1
}

ref=$(netstandard_ref) || { printf 'netstandard.dll not found; set NETSTANDARD_REF\n' >&2; exit 2; }

for project in $projects; do
    dotnet build "$root/src/$project/$project.csproj" -c Release -f netstandard2.1 -t:Rebuild -m:1 -nodeReuse:false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -p:EnableSourceLink=false -p:EmbedAllSources=false
done

shims=$(shims_dir) || shims=""
if [ -n "$shims" ]; then
    dotnet "$retarget" --check-shims "$shims" || exit 1
fi

for project in $projects; do
    dll=$root/src/$project/bin/Release/netstandard2.1/$project.dll
    [ -f "$dll" ] || { printf 'missing %s\n' "$dll" >&2; exit 2; }
    sha=$(sha256sum "$dll" | cut -d' ' -f1)
    printf 'portable %s %s\n' "$project.dll" "$sha" >>"$manifest"
    mkdir -p "$plugins/portable"
    if $check; then
        cmp -s "$dll" "$plugins/portable/$project.dll" || { printf 'changed portable/%s\n' "$project.dll" >&2; status=1; }
    else
        cp -- "$dll" "$plugins/portable/$project.dll"
    fi
done

for project in $projects; do
    dotnet build "$root/src/$project/$project.csproj" -c Release -f net10.0 -t:Rebuild -m:1 -nodeReuse:false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -p:EnableSourceLink=false -p:EmbedAllSources=false
    input=$root/src/$project/bin/Release/net10.0/$project.dll
    [ -f "$input" ] || { printf 'missing %s\n' "$input" >&2; exit 2; }
    dotnet "$retarget" "$ref" "$input" "$staging/$project.dll" || exit 1
    sha=$(sha256sum "$staging/$project.dll" | cut -d' ' -f1)
    printf 'coreclr %s %s\n' "$project.dll" "$sha" >>"$manifest"
    mkdir -p "$plugins/coreclr"
    if $check; then
        cmp -s "$staging/$project.dll" "$plugins/coreclr/$project.dll" || { printf 'changed coreclr/%s\n' "$project.dll" >&2; status=1; }
    else
        cp -- "$staging/$project.dll" "$plugins/coreclr/$project.dll"
    fi
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
    mkdir -p "$frames_stage"
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
