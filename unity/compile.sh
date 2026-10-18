#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
project=$root/unity/project

version=$(sed -n 's/^m_EditorVersion: //p' "$project/ProjectSettings/ProjectVersion.txt")
[ -n "$version" ] || { printf 'missing ProjectVersion.txt\n' >&2; exit 2; }
editor=${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/$version/Editor/Unity}
[ -x "$editor" ] || { printf 'editor not found: %s; set UNITY_EDITOR\n' "$editor" >&2; exit 2; }

mkdir -p "$project/Logs"
log=$project/Logs/compile.log
rm -f "$log"

shim=$project/Logs/lib-shim
export LD_LIBRARY_PATH=$shim${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}

set +e
timeout 900 "$editor" -batchmode -nographics -quit -projectPath "$project" -logFile "$log" >/dev/null 2>&1
rc=$?
set -e

[ -f "$log" ] || { printf 'no log at %s\n' "$log" >&2; exit 2; }

errors=$(grep -cE 'error CS[0-9]+' "$log" || true)
built=$(grep -cE 'Tundra build success' "$log" || true)

grep -E 'error CS[0-9]+' "$log" | sort -u | head -40 || true
printf 'errors:    %s\n' "$errors"
printf 'unity:     %s\n' "$version"
printf 'head:      %s\n' "$(git -C "$root" rev-parse HEAD)"

if [ "$errors" -ne 0 ]; then
    exit 1
fi
if [ "$rc" -ne 0 ] || [ "$built" -eq 0 ]; then
    printf 'editor exited %s without a successful build; log: %s\n' "$rc" "$log" >&2
    exit 2
fi
