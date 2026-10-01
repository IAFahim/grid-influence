# Gi

Chunked, sparse, **integer** influence fields for .NET — a faithful port of the core of
[BovineLabs Timeline Grid Influence](https://github.com/vex-studio/com.bovinelabs.timeline.grid.influence)
(MIT, © 2026 BovineLabs) with the Unity/DOTS plumbing replaced by plain .NET. Derived code is used
under the MIT license; the engine carries no Unity dependencies.

## Model

- The world is an unbounded cell grid. Stamps (solid rect, rect shell, disc, annulus, capsule,
  ellipse, rounded rect, thick line, sector — all exact integer geometry) carry an `sbyte` weight
  (−128…127; the type enforces the bound) and are rasterized into horizontal `WeightedRect` spans.
- Cells are signed 16-bit integers. All accumulation saturates — cells stick at ±32767 instead of
  wrapping. Exact while a cell's per-tick stamp accumulation stays inside int16 range (~258
  coincident max-weight stamps); beyond that the bound-stick applies.
- Each tick: prepare slots (budgets, retention eviction, compaction every 60 ticks, stencil
  frontier — edge scans run in parallel, activation inserts stay serial and ordered) → rasterize
  stamps → clear active chunk difference arrays → scatter ±weight at span corners → resolve
  (inclusive prefix sum per chunk — horizontal pass is an exact int32 SIMD lane scan with a
  saturating scalar fallback per row, vertical pass is saturating vector adds — then decay/spread
  stencil against the previous buffer). The
  difference-array trick makes a tick cost O(spans + touched chunks), independent of stamped area.
- An idle tick — no stamps, nothing live, no live stencil source — skips the pipeline entirely:
  frame bookkeeping plus retention/compaction only (~12–45 ns).
- Decay/spread are per tick: `kept = v·(1000−decay)/1000`, each 4-neighbour receives
  `kept/spread`. Cross-chunk flow uses edge halos. All-zero source rows are detected and skipped
  (`kept(0)=outflow(0)=0`, so the row is an identity and the halo band is already zero from the
  halo clear); all-zero neighbour edges skip the halo fill the same way. The chunk's nonzero flag
  is OR-folded out of the decay/inflow stores instead of a separate scan pass, and a chunk that is
  neither live nor adjacent to a live chunk skips the stencil entirely. Chunks deactivate only at
  exact zero.
- Stamps sorted before budget accounting, so budget drops are insertion-order independent; pure
  integer math makes results bit-identical across machines.
- Stamps are per-tick emissions: a field rescheduled with no stamps shows no cells unless decay
  keeps the frontier alive. `WriteRegion`/`ReadRegion` inject/snapshot bulk cells as unmanaged
  spans.

## Usage

```csharp
var spec = GridSpec.FromPowerOfTwo(chunkPower: 5, retentionFrames: 256);
using var front = new Field(spec);
using var back = new Field(spec, parallelism: 8);   // optional persistent worker pool

Stamp[] stamps = [new Stamp(InfluenceShape.Disc(Int2.Zero, 8, 100), new Int2(x, y))];
back.Tick(stamps, Stencil.Create(front, decayPerMille: 300, spreadDenominator: 4));
(front, back) = (back, front);   // front now holds the new frame

var reader = front.AsReader();   // ref-struct borrow, zero allocation
int value = reader.ReadCell(new Int2(12, -6));
Int2 gradient = reader.Gradient(cell);          // un-normalized central difference
float smooth = reader.SampleBilinear(4.5f, 7.5f);
```

Or drive everything from a JSON scene with PPM weight layers via
[`Gi.Io`](../src/Gi.Io/pack-readme.md) and see
[`samples/scene`](../samples/scene/README.md).

## Receipts

Measured on the validation machine (i9-14900K, .NET 10, `benchmarks`), 256 stamps,
4 ticks per invocation, median:

| World   | Naive per-cell scatter + full-grid decay | Gi serial | Gi par=32 | Speedup |
|---------|------------------------------------------|----------------------|----------------------|---------|
| 256²    | 1 755 µs                                 | ~230 µs              | ~230 µs              | ~7.6×   |
| 1024²   | 26 781 µs                                | ~1 500 µs            | ~390 µs              | ~69×    |
| 2048²   | 113 464 µs                               | ~1 470 µs            | ~240–380 µs          | ~300–470× |

Queries: ReadCell ~3 ns, 32×32 capture ~0.1 µs (chunk-run SIMD). Idle tick 12.5 ns (nodecay) /
45 ns (decay source) — 32 idle layers fit in ~0.4–1.5 µs. Warm ticks allocate 0 B serial and
parallel (`--verify` receipts). Parallel resolve is bit-identical to serial and to the naive
oracle (`pipeline-parallel-matches-naive-*`).

The baseline is the cost model the difference-array design exists to replace: paint every covered
cell of every stamp every tick plus a full-grid decay pass. Burst/Unity numbers are not compared —
same-machine, same-fixture, one-variable comparisons only.

### Row-scan receipts (Ryzen 5 8500G, .NET 10.0.12, before/after, bit-identical outputs)

The horizontal prefix pass is lowered to an exact int32 lane scan (AVX2/SSE2, zero blocks skipped,
two interleaved rows per chunk) instead of the scalar saturating chain:

| Workload (median)              | Before    | After     |
|--------------------------------|-----------|-----------|
| 1024² tick, no decay           | 909 µs    | 506 µs    |
| 2048² tick, no decay           | 1 073 µs  | 577 µs    |
| 1024² tick, decay + spread     | 3 646 µs  | 3 041 µs  |
| 2048² tick, decay + spread     | 5 208 µs  | 4 293 µs  |
| PNM gray-16 encode 1024²       | 3 413 µs  | 436 µs    |

The i9-14900K table above predates the vectorized row scan and PNM encoders.

### Stencil-pass receipts (Ryzen 5 8500G, .NET 10.0.12, before/after, bit-identical outputs)

The decay/spread pass drops work instead of re-mathematicizing it: per-tick hoisted magic
divisors, all-zero row/edge skips, the nonzero flag folded into the decay/inflow stores, and a
full skip for chunks with no live chunk in or around them:

| Workload (median)                                  | Before    | After     |
|----------------------------------------------------|-----------|-----------|
| 2048² sparse decay harness, interleaved A/B        | 1 094 µs  | 889 µs    |
| TickBenchmarks 1024² decay + spread                | 3 158 µs  | 2 599 µs  |
| TickBenchmarks 2048² decay + spread                | 4 331 µs  | 3 600 µs  |

Both TickBenchmarks rows are a same-session before/after (working tree vs HEAD worktree); the
harness A/B ran three interleaved rounds per variant. No-decay rows are unchanged within noise.
Sparse fields win most (more all-zero rows); dense fields win the folded nonzero scan only.
Receipts matched at equal tick parity — outputs bit-identical.

Two measured dead ends, kept on record so they are not retried: fusing decay and inflow into one
row-pipelined pass was 44% slower than the two separate passes (store-to-load interleaving beats
pass locality on Zen 4), and replacing the 64-bit-lane magic division with an exhaustively
validated double-precision divide (int→double convert, reciprocal multiply, biased round, sign
fixup) was 26% slower — x86 SIMD float/int conversion ports serialize the sequence even though it
is exact and uses fewer arithmetic ops.

## Unsafe proof

- **Lifetime**: `Field` is a value-type handle to a `FieldContext` block allocated once via
  `NativeMemory.AllocZeroed`; all field state lives in that block or in `NativeBuffer<T>` blocks it
  owns. `Dispose` joins the worker pool, frees every buffer, frees the context block, and nulls the
  handle — copies of the handle share the context and see `_disposed`. `FieldReader`/`ChunkView`/
  `FlowReader` are `ref struct`s; they cannot escape the owning field's scope. The worker pool is the
  only managed state: created once at construction, referenced by the context through a `GCHandle`,
  freed at `Dispose`. No borrowed span, pointer, or reader is retained beyond its call.
- **Aliasing**: a field's data pointer is only captured inside one pipeline phase at a time; the
  stencil reads the previous buffer while writing the current one (disjoint objects enforced by
  the pipeline). Chunk acquisition zeroes fresh and reused chunks, so no stale data leaks through
  `WriteRegion`.
- **Alignment**: every unmanaged block is 64-byte aligned; `ElementsPerChunk` strides are aligned
  to at least 8 cells, which satisfies `Vector128`/`Vector256` `Vector<short>` loads in the resolve
  pass. Vector adds use saturating `Vector.AddSaturate` semantics identical to the scalar clamp.
  The horizontal row scan loads `Vector256`/`Vector128` blocks fully inside each row's
  `[0, dimension)` cells (no padding or seam reads), computes exact int32 inclusive scans, and
  stores a block only when every prefix so far lies in `[-32767, 32767]` — a superset of the
  saturating range — otherwise the row is redone with the scalar saturating chain from the
  offending block, so stored bits equal the scalar definition in all cases, including under
  saturation. Vector zero tests use `CompareEqual` + `MoveMask`; `Avx.TestZ` is avoided because it
  was observed returning true for nonzero operands on .NET 10.0.12 (x86-64-v4). The PNM encoders
  clamp in int32, then narrow with a bias-32768 pre-shift (gray-16, byte-swapped and corrected by
  a masked xor) or `PackUnsignedSaturate` (gray-8) — exact for clamped inputs.
- **Stencil skips and the folded nonzero flag**: an all-zero decay source row is an identity —
  `kept(0)=0` and `outflow(0)=0` leave the target row unchanged and would only write zeros into a
  halo band that the per-chunk halo clear already zeroed — so the row is skipped after an
  OR-test of its source block, and skipping the halo write is bit-identical to writing zeros. The
  same argument skips halo edge fills whose gathered neighbour block is all zero. The nonzero flag
  is OR-folded out of the vector stores of the decay and inflow passes instead of a separate
  post-pass scan: the decay pass fully overwrites every `[0, chunkSize)` cell of every row, the
  inflow pass writes every cell again, and a saturating add keeps any nonzero operand nonzero, so
  the OR of the stored values equals the OR of a full re-scan. A chunk that is neither live in the
  stencil source nor adjacent to a live chunk reads the prefix-pass flag unchanged: no decay writes
  (not live) and inflow would add an all-zero halo (no live neighbours can put anything in it).
- **Measurement hazards on this runtime** (.NET 10.0.12, x86-64-v4): `Avx.TestZ` was observed
  returning true for nonzero operands (documented above), and `dotnet-trace` sampled-thread-time
  leaf attribution is not trustworthy either — the same workload profiled before/after removing one
  small memset reported 92.8% exclusive in `Buffer.ZeroMemoryInternal` and then 50% in
  `Array.Sort` while a direct measurement of the sort showed 4.2 µs per tick (0.4%). Every number
  in these receipts comes from interleaved A/B runs or per-phase timestamps, never from profiler
  leaf attribution.
- **Concurrency**: the serial warm path is single-threaded and deterministic. CoordMap and buffers
  are single-writer. A defensive-copy bug on a readonly struct field (map table filled while its count
  stayed 0) was found by stress and fixed; buffer growth on a readonly struct field is forbidden by
  the same rule — all growable buffers live in non-readonly fields.
  With `parallelism > 1` the field owns a fixed worker set created in the constructor and joined in
  `Dispose`; no thread or event state is created on the tick path. Work items are claimed through one
  packed `(generation << 32 | index)` counter via `Interlocked.Add`, so an item is never claimed twice
  and a stale worker can only observe the current generation's published buffers — generation,
  phase, count, stencil, and buffer pointers are written before the counter release and read after a
  full fence on the claim. Each item writes disjoint storage: resolve/clear items touch only their
  own chunk, frontier-scan items are read-only (activation inserts stay serial and ordered),
  rasterize items touch only their own span slice, and scatter items update int16 corners through a
  CAS on the containing int32 pair — saturating adds commute while inside range, so output is
  bit-identical regardless of claim order.
  Reads of the stencil source field are safe because the source is quiescent during the tick; a
  self-referencing stencil forces the serial path. Warm parallel ticks allocate 0 B; the receipt is
  `warm-parallel-tick-allocates-0-bytes`, and bit-exactness is `pipeline-parallel-matches-naive-*`.
- **Total reads**: every reader returns 0 for missing or stale chunks; no input can cause an
  out-of-bounds access. Budget-oversized stamps drop whole, deterministically.

## World engine (`World`, `Stamps`, `Fade`)

A second, virtual-field engine alongside `Field`: grids never rasterize to a cell buffer — a mark
stays a record and influence is evaluated at query time.

- **Shape**: `World` holds up to 32 `GridCtx` (power-of-two `Size`, world-space rect
  `Origin/WorldSize`) and 32 layers. `Queue` stores caller-owned pointers (`Float2*`, `float*`,
  `byte*`) — caller must keep the memory alive and stable between `Queue` and `ClearQueue`;
  engine never copies or mutates caller arrays except `Mul`/`GridHint` (engine-owned scratch
  parallel to each queue entry).
- **Apply**: per queued item — decay `Mul` (fade id 0 skips), weight = `strength×mul/1000`,
  route by mark-rect ∩ grid-rect (float world space; a mark straddling a seam emits into every
  grid it overlaps → no clipping, no edge jitter), convert to cell ints, append a 16-byte
  `MarkRec` to that grid-layer's contiguous mark table. `sbyte` strengths, int weights; cells
  saturate to `short` at read.
- **Queries**: `Cell` lazily builds a CSR tile index per (grid, layer) on first read after an
  apply (`BuiltGen` vs `ApplyGen`), then scans only that tile's marks. `Total` sums `Cell`.
- **Determinism**: marks append in queue order; bucket order is stable within a tile.
- **Concurrency**: `World.Apply` is serial. For external parallelism the caller runs
  `World.BeginApply(w)` once (grid/layer fade decay, per-layer cursor reset — must be
  single-threaded), then `World.ApplySlice(w, entry, start, count)` on disjoint item ranges from
  any number of threads: mark slots are claimed by an `Interlocked` cursor so concurrent appends
  never collide; each slice owns its items' `Mul`/`GridHint` bytes; item-level `Fade.Stamp` decay
  happens inside `ApplySlice` after the weight is read, so a slice decays exactly its own items
  once. Mark order within a layer is nondeterministic under slicing but cell sums are commutative,
  so `Cell`/`Total` results are bit-identical to serial. Queries must run after all slices join.
  Worlds are independent — different worlds on different threads never share state.
- **Ownership**: all engine state is `NativeMemory`/`AlignedAlloc`; `ClearQueue` frees `Mul`/
  `GridHint`. No managed state on any warm path; `Queue`/`Apply`/`Cell`/`Total` allocate 0 B.
