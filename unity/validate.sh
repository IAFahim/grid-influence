#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
project=$root/unity/project
usage='usage: validate.sh <editmode|playmode|il2cpp|perf>'

[ $# -eq 1 ] || { printf '%s\n' "$usage" >&2; exit 2; }
suite=$1
case $suite in
    editmode | playmode | il2cpp | perf) ;;
    *) printf '%s\n' "$usage" >&2; exit 2 ;;
esac

"$root/unity/build-plugins.sh" --check

version=$(sed -n 's/^m_EditorVersion: //p' "$project/ProjectSettings/ProjectVersion.txt")
[ -n "$version" ] || { printf 'missing ProjectVersion.txt\n' >&2; exit 2; }

unity=${UNITY_BIN:-}
if [ -z "$unity" ]; then
    for candidate in "$HOME/Unity/Hub/Editor/$version/Editor/Unity" "$HOME/Unity/Hub/Editor/$version/Unity" /opt/Unity/Editor/Unity; do
        if [ -x "$candidate" ]; then
            unity=$candidate
            break
        fi
    done
fi
if [ -z "$unity" ]; then
    command -v Unity >/dev/null 2>&1 && unity=$(command -v Unity)
fi
[ -n "$unity" ] || { printf 'Unity editor %s not found; set UNITY_BIN\n' "$version" >&2; exit 2; }

mkdir -p "$project/Logs"
results=$project/Logs/validate-$suite.xml
log=$project/Logs/validate-$suite.log

platform=EditMode
backend=mono

case $suite in
    editmode) platform=EditMode ;;
    playmode) platform=PlayMode ;;
    il2cpp)
        platform=PlayMode
        backend=il2cpp
        ;;
    perf)
        platform=PlayMode
        ;;
esac

args=(-batchmode -projectPath "$project" -runTests -testPlatform "$platform" -testResults "$results" -logFile "$log")
case $suite in
    editmode | il2cpp) args+=(-nographics) ;;
esac
case $suite in
    il2cpp) args+=(-buildTarget Linux64) ;;
    perf) args+=(-testFilter 'Gi.Entities.Performance') ;;
esac

set +e
"$unity" "${args[@]}"
rc=$?
set -e

[ -f "$results" ] || { printf 'no results at %s; editor log: %s\n' "$results" "$log" >&2; exit 2; }

if [ "$rc" -ne 0 ] && [ "$rc" -ne 2 ]; then
    printf 'unity exited %s; editor log: %s\n' "$rc" "$log" >&2
    exit 2
fi

counts=$(python3 - "$results" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
attributes = root.attrib
print(attributes.get('passed', '0'), attributes.get('failed', '0'), attributes.get('skipped', '0'), attributes.get('duration', '0'))
PY
) || { printf 'cannot parse %s\n' "$results" >&2; exit 2; }

set -- $counts
passed=$1
failed=$2
skipped=$3
duration=$4

head=$(git -C "$root" rev-parse HEAD)

printf 'suite:     %s\n' "$suite"
printf 'passed:    %s\n' "$passed"
printf 'failed:    %s\n' "$failed"
printf 'skipped:   %s\n' "$skipped"
printf 'duration:  %.3f\n' "$duration"
printf 'unity:     %s\n' "$version"
printf 'backend:   %s\n' "$backend"
printf 'head:      %s\n' "$head"

if [ "$rc" -ne 0 ] || [ "$failed" -ne 0 ]; then
    exit 1
fi
