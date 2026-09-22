# GridInfluence

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
  (inclusive prefix sum per chunk, then decay/spread stencil against the previous buffer). The
  difference-array trick makes a tick cost O(spans + touched chunks), independent of stamped area.
- An idle tick — no stamps, nothing live, no live stencil source — skips the pipeline entirely:
  frame bookkeeping plus retention/compaction only (~12–45 ns).
- Decay/spread are per tick: `kept = v·(1000−decay)/1000`, each 4-neighbour receives
  `kept/spread`. Cross-chunk flow uses edge halos. Chunks deactivate only at exact zero.
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
[`GridInfluence.Io`](../src/GridInfluence.Io/pack-readme.md) and see
[`samples/scene`](../samples/scene/README.md).

## Receipts

Measured on the validation machine (i9-14900K, .NET 10, `benchmarks`), 256 stamps,
4 ticks per invocation, median:

| World   | Naive per-cell scatter + full-grid decay | GridInfluence serial | GridInfluence par=32 | Speedup |
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
