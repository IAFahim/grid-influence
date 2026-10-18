#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
project=$root/unity/project
usage='usage: validate.sh <editmode|playmode|il2cpp|coreclr|perf>'

[ $# -eq 1 ] || { printf '%s\n' "$usage" >&2; exit 2; }
suite=$1
case $suite in
    editmode | playmode | il2cpp | coreclr | perf) ;;
    *) printf '%s\n' "$usage" >&2; exit 2 ;;
esac

"$root/unity/build-plugins.sh" --check

version=$(sed -n 's/^m_EditorVersion: //p' "$project/ProjectSettings/ProjectVersion.txt")
[ -n "$version" ] || { printf 'missing ProjectVersion.txt\n' >&2; exit 2; }

mkdir -p "$project/Logs"
results=$project/Logs/validate-$suite.xml
log=$project/Logs/validate-$suite.log
rm -f "$results"

case $suite in
    editmode | playmode | perf)
        unity=${UNITY_BIN:-unity}
        command -v "$unity" >/dev/null 2>&1 || { printf 'unity CLI not found; set UNITY_BIN\n' >&2; exit 2; }
        mode=EditMode
        backend=editor
        [ "$suite" = editmode ] || mode=PlayMode
        args=(test "$project" --editor-version "$version" --mode "$mode" --report-format junit --output "$results" --allow-install --non-interactive --no-banner --timeout 900)
        if [ "$suite" = perf ]; then
            args+=(--filter Gi.Entities.Performance)
        fi
        set +e
        "$unity" "${args[@]}" >"$log" 2>&1
        rc=$?
        set -e
        case $rc in
            0 | 8) ;;
            *) printf 'unity test exited %s; log: %s\n' "$rc" "$log" >&2; exit 2 ;;
        esac
        ;;
    il2cpp | coreclr)
        editor=${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/$version/Editor/Unity}
        [ -x "$editor" ] || { printf 'editor not found: %s; set UNITY_EDITOR\n' "$editor" >&2; exit 2; }
        backend=$suite
        shim=$project/Logs/lib-shim
        mkdir -p "$shim"
        libraries=$(ldconfig -p 2>/dev/null || true)
        for pair in 'libncurses.so.6:^libncursesw[.]so[.]6$' 'libxml2.so.2:^libxml2[.]so[.][0-9]+$'; do
            wanted=${pair%%:*}
            provider=${pair#*:}
            present=$(awk -v w="$wanted" '$1 == w { n++ } END { print n + 0 }' <<<"$libraries")
            if [ "$present" -eq 0 ]; then
                target=$(awk -v p="$provider" '$1 ~ p { t = $NF } END { print t }' <<<"$libraries")
                [ -z "$target" ] || ln -sf "$target" "$shim/$wanted"
            fi
        done
        export LD_LIBRARY_PATH=$shim${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
        settings=$project/ProjectSettings/ProjectSettings.asset
        saved=$(mktemp)
        cp -- "$settings" "$saved"
        trap 'cp -- "$saved" "$settings"; rm -f "$saved"' EXIT
        set +e
        GI_SCRIPTING_BACKEND=$suite timeout 1800 "$editor" -batchmode -nographics -projectPath "$project" -runTests -testPlatform StandaloneLinux64 -testResults "$results" -logFile "$log"
        rc=$?
        set -e
        case $rc in
            0 | 2) ;;
            *) printf 'editor exited %s; log: %s\n' "$rc" "$log" >&2; exit 2 ;;
        esac
        ;;
esac

[ -f "$results" ] || { printf 'no results at %s; log: %s\n' "$results" "$log" >&2; exit 2; }

counts=$(python3 -I - "$results" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
if root.tag == "test-run":
    total = int(root.get("total", 0))
    failed = int(root.get("failed", 0))
    skipped = int(root.get("skipped", 0)) + int(root.get("inconclusive", 0))
    passed = int(root.get("passed", 0))
    time = float(root.get("duration", 0))
else:
    suites = [root] if root.tag == "testsuite" else list(root.iter("testsuite"))
    total = sum(int(s.get("tests", 0)) for s in suites)
    failed = sum(int(s.get("failures", 0)) + int(s.get("errors", 0)) for s in suites)
    skipped = sum(int(s.get("skipped", 0)) for s in suites)
    passed = total - failed - skipped
    time = sum(float(s.get("time", 0)) for s in suites)
print(passed, failed, skipped, time)
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

if [ $((passed + failed)) -eq 0 ]; then
    printf 'no tests ran; log: %s\n' "$log" >&2
    exit 2
fi

if [ "$failed" -ne 0 ]; then
    exit 1
fi
