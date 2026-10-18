#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
out=$(mktemp -d)
trap 'rm -rf -- "$out"; rm -f -- "$root"/tests/Gi.Tests/Recorded_*.cs' EXIT

GI_EXPORT_DIR=$out dotnet test "$root/unity/tests/Gi.Entities.Columns.Tests" -c Release --filter 'FullyQualifiedName~ARangeIsWrittenToTheDirectoryNamedByTheEnvironment' >/dev/null
shopt -s nullglob
files=("$out"/Recorded_*.cs)
[ ${#files[@]} -gt 0 ] || { printf 'no exported stream\n' >&2; exit 2; }

for file in "${files[@]}"; do
    cp -- "$file" "$root/tests/Gi.Tests/"
    printf 'exported: %s (%s lines)\n' "$(basename -- "$file")" "$(wc -l <"$file" | tr -d ' ')"
done

dotnet test "$root/tests/Gi.Tests" -c Release --filter 'FullyQualifiedName~Recorded_' | tail -4
