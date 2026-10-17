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

unity=${UNITY_BIN:-unity}
command -v "$unity" >/dev/null 2>&1 || { printf 'unity CLI not found; set UNITY_BIN\n' >&2; exit 2; }

mkdir -p "$project/Logs"
results=$project/Logs/validate-$suite.junit.xml
log=$project/Logs/validate-$suite.log

mode=EditMode
backend=mono
timeout=600
forward=()

case $suite in
    editmode) mode=EditMode ;;
    playmode) mode=PlayMode ;;
    il2cpp)
        mode=PlayMode
        backend=il2cpp
        timeout=1800
        forward=(-buildTarget Linux64)
        ;;
    perf) mode=PlayMode ;;
esac

args=(test "$project" --editor-version "$version" --mode "$mode" --report-format junit --output "$results" --allow-install --non-interactive --no-banner --timeout "$timeout")
if [ "$suite" = perf ]; then
    args+=(--filter Gi.Entities.Performance)
fi
if [ ${#forward[@]} -gt 0 ]; then
    args+=(-- "${forward[@]}")
fi

set +e
"$unity" "${args[@]}" >"$log" 2>&1
rc=$?
set -e

case $rc in
    0 | 8) ;;
    *) printf 'unity test exited %s; log: %s\n' "$rc" "$log" >&2; exit 2 ;;
esac

[ -f "$results" ] || { printf 'no results at %s; log: %s\n' "$results" "$log" >&2; exit 2; }

counts=$(python3 - "$results" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
suites = [root] if root.tag == "testsuite" else list(root.iter("testsuite"))
tests = sum(int(s.get("tests", 0)) for s in suites)
failures = sum(int(s.get("failures", 0)) for s in suites)
errors = sum(int(s.get("errors", 0)) for s in suites)
skipped = sum(int(s.get("skipped", 0)) for s in suites)
time = sum(float(s.get("time", 0)) for s in suites)
print(tests - failures - errors - skipped, failures + errors, skipped, time)
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
