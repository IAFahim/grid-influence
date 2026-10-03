#!/usr/bin/env bash
set -euo pipefail

stats_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
stats_mode="${1:-stat}"
if (($# > 0)); then shift; fi

case "$stats_mode" in
    stat|record) ;;
    *) printf 'Usage: bash tools/stats/perf.sh stat|record [profile options]\n' >&2; exit 2 ;;
esac

command -v perf >/dev/null
dotnet build "$stats_dir/Gi.Stats.csproj" -c Release -m:1 >&2
stats_dll="$stats_dir/bin/Release/net10.0/Gi.Stats.dll"
dotnet "$stats_dll" verify >&2

if [[ "$stats_mode" == stat ]]; then
    exec perf stat -e task-clock,cycles,instructions,branches,branch-misses,cache-misses -- \
        dotnet "$stats_dll" profile "$@"
fi

mkdir -p "$stats_dir/out"
export DOTNET_PerfMapEnabled=1
perf record -k 1 -e cycles:u -g --call-graph dwarf -o "$stats_dir/out/perf.data" -- \
    dotnet "$stats_dll" profile "$@"
perf inject --jit -i "$stats_dir/out/perf.data" -o "$stats_dir/out/perf.jit.data"
