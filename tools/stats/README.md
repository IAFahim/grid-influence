# Gi stats

A standalone .NET 10 CLI over Gi's existing engine. Compact JSON is the default. No packages,
reflection, public diagnostic API, or instrumentation on place/process/query paths are added.
This runs its own deterministic workload; it does not attach to another process or a Unity
Editor. The installed `unity` CLI supplied the command/format convention.

```sh
dotnet build Gi.slnx -c Release -m:1
dotnet run --project tools/stats -c Release --no-build -- stats
dotnet run --project tools/stats -c Release --no-build -- memory --stamp raster
dotnet run --project tools/stats -c Release --no-build -- profile
dotnet run --project tools/stats -c Release --no-build -- verify
DOTNET_EnableHWIntrinsic=0 dotnet run --project tools/stats -c Release --no-build -- verify
```

Use `--format human` for text or `--help` for options. Workload settings include `--power`,
`--layers`, `--sources`, `--stamp box|raster`, and `--stamp-size`. Profiling also takes
`--iterations`, `--queries`, and `--moves`; moves are capped to the source count. Exit codes
are 0 for success, 1 for a failed receipt/allocation check, and 2 for invalid arguments.
Argument errors also produce JSON by default. Each successful invocation writes one JSON
object plus a newline, with stable property order and invariant numeric formatting.

`stats` and `memory` include internal counters, exact requested native memory categories,
and GC/process observations. Source slots include removed placements; source capacity includes
unused retained slots. Live tiles include unresolved blocks, whereas the CLI captures after
processing, when dirty count is zero. Page maps and dirty queues report allocated capacity.
Native totals cover the selected world and shared arenas/stamps, including capacities retained
after clearing. They exclude allocator overhead and other worlds. Managed heap, working set,
and private process memory include the runtime and tool; they are not Gi native memory.

`profile` runs memory/output/allocation receipts before timing, warms each operation, consumes
field and query checksums, and fails if any measured phase allocates managed bytes. It reports
idle process ns/call, cell query ns/query, and move-plus-process ns/frame. The last phase
includes one cell read to consume output. Checksums are checked against the stable warmed
states. Timing excludes inspection, memory observations, and formatting. Receipts use their
own world, which is cleared before the measured scene is created. Gi retains world metadata
and stamp storage for process lifetime, so shared catalog counts include receipt stamps.

The tool disables tiered compilation by default so measured methods compile fully optimized
from startup. CPU scheduling still affects results. For a repeatable before/after comparison,
use the same workload and CPU affinity and set `DOTNET_TieredCompilation=0` for both runs,
including hosts with different runtime defaults.
Timing and runtime memory values vary even though the workload and JSON layout are deterministic.

Measured on the i9-14900K with .NET 10.0.12, CPU 0, tiered compilation disabled, median of
three runs: 4,000 box sources, three layers, 256 cells per side, 200 moves per frame,
12,000 frames, and 10,000 queries per frame. Comparing the previous library with the AVX2 and
bounds/churn fixes, move-plus-process fell from 76,944 to 55,565 ns/frame (27.8%). Cell query
measured 3.59 versus 3.40 ns/query. All phase checksums matched and every phase allocated 0 B.
These figures describe this machine and workload; the CLI can repeat the measurement on others.

JSON is written directly to stdout using `Utf8JsonWriter` and static UTF-8 property names.
There is no DTO traversal, serializer metadata, reflection, or UTF-16 JSON string construction.
Capture allocates 0 B; formatting and runtime observations happen outside measured operations.
The capture's lifetime, aliasing, alignment, and concurrency proof is in
[`docs/model.md`](../../docs/model.md#tool-inspection).

Linux CPU profiling:

```sh
bash tools/stats/perf.sh stat --iterations 12000 --queries 10000
bash tools/stats/perf.sh record --iterations 12000 --queries 10000
perf report -i tools/stats/out/perf.jit.data
```

The script builds and verifies before collecting counters or samples. `stat` writes CPU
counters to stderr and the CLI JSON to stdout. `record` enables .NET perf maps/JIT dumps,
uses the monotonic clock required for JIT injection, and produces raw and injected recordings
under the ignored `out/` directory. Counters/samples include startup, receipt verification,
scene construction, warmup, and reporting; the JSON timings cover only measured loops. Use
longer runs to make warm operations dominate sampling. Perf maps and recording must use the
same PID namespace to resolve managed symbols.

The Gi package remains at `0.2.0-alpha.1`: internal friend access and compatible bounds/SIMD
fixes add no public types or methods and no dependency. Review the release version before
publishing; no package or existing tag is published or moved by this tool.
