#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
host=$root/unity/project
version=$(sed -n 's/^m_EditorVersion: //p' "$host/ProjectSettings/ProjectVersion.txt")
editor=${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/$version/Editor/Unity}
[ -x "$editor" ] || { printf 'editor not found: %s; set UNITY_EDITOR\n' "$editor" >&2; exit 2; }

work=$host/Logs/quickstart
rm -rf -- "$work"
mkdir -p "$work/Packages" "$work/ProjectSettings" "$work/Assets/Editor"
cp -- "$host/ProjectSettings/ProjectVersion.txt" "$work/ProjectSettings/"
cp -- "$root/unity/quickstart/QuickStartCheck.cs" "$work/Assets/Editor/"
cat >"$work/Assets/Editor/QuickStartCheck.asmdef" <<'JSON'
{
    "name": "QuickStartCheck",
    "references": ["Gi.Entities", "Gi.Entities.Authoring", "Unity.Entities", "Unity.Entities.Hybrid", "Unity.Collections", "Unity.Mathematics"],
    "includePlatforms": ["Editor"],
    "overrideReferences": true,
    "precompiledReferences": ["Gi.dll"],
    "autoReferenced": false
}
JSON
pin=$(python3 -I -c "import json,sys; print(json.load(open(sys.argv[1]))['dependencies']['com.bovinelabs.core'])" "$host/Packages/manifest.json")
cat >"$work/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.bovinelabs.core": "$pin",
    "com.gi.entities": "file:$root/unity/com.gi.entities"
  }
}
JSON

log=$work/quickstart.log
shim=$host/Logs/lib-shim
export LD_LIBRARY_PATH=$shim${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
set +e
timeout 1200 "$editor" -batchmode -nographics -projectPath "$work" -executeMethod QuickStartCheck.Run -logFile "$log" >/dev/null 2>&1
rc=$?
set -e
grep -E 'QUICKSTART|error CS[0-9]+' "$log" | sort -u | head -20 || true
if grep -q 'QUICKSTART OK' "$log"; then
    printf 'quickstart: ok\n'
    exit 0
fi

printf 'quickstart: failed (editor exit %s); log: %s\n' "$rc" "$log" >&2
exit 1
