# Gi

Sparse tiled integer influence fields for .NET. One library, no dependencies.

## Model

- A **world** (`World.New`, up to 32) owns **grids** and **layers**. A grid
  (`Grid.New(world, power, x, y, size)`, up to 32 per world) is a power-of-two cell grid,
  `2^power` cells per side (power 5–14), laid over a world-space rect `x,y,size`. A layer
  (`Layer.New(world)`, up to 32 per world) is an independent field channel present on every grid.
- **Stamps** (`Stamp.New(sbyte* data, w, h)` / `Stamp.Box(w, h, value)`, up to 255) are baked
  cell-space content: `w×h` `sbyte` samples, centered on the placement position (origin offset
  `−w/2` cells in Q8). Uniform rasters classify as `ConstantRectangle` and take the
  difference-array path; the rest are `Raster` and deposit with sub-cell bilinear weights.
  Raster storage keeps a one-sample zero border (pitch `w+2`, `(w+2)×(h+2)`) so the deposit loop
  reads `x−1`/`y−pitch` unconditionally.
- **Sources** are persistent placements: `World.Place(world, layer, x, y, stamp, gain)` returns an
  int id; `Move`/`SetGain`/`Remove`/`Clear` mutate it. `gain` is an integer 0–16. Positions are
  float world units, converted to cell space per grid as `floor((x−ox)·scale·256)` in Q8 — the
  integer part is the cell, the low byte is the sub-cell phase.
- **Deposits are incremental**: each live tile owns a 6,528 B block — a 33×33 `int32` difference
  array padded to 4,384 B, a 32×32 `int16` page, and an `int64` page sum in a padded 64 B slot.
  Raster stamps also need the tile's 32×32 `int32` dense buffer: blocks touched by a raster
  deposit allocate it once (block grows to 10,624 B, dense pinned at the block tail) and box-only
  tiles never carry it — resolve reads a shared zero page instead. `Place` deposits the stamp's
  contribution into every touched tile immediately;
  `Move`/`Remove` deposit the exact negation at the stored position and `SetGain` the gain delta
  (integer adds invert perfectly — no rebuild, no source scan).
- **Process** drains the per-(grid,layer) dirty list: each dirty tile resolves its difference
  array through a 2D prefix sum (horizontal inclusive prefix + previous-row carry), adds dense,
  saturates to `short` **after** summation so cancellation is preserved, and writes the page's
  cell total into its `int64` sum slot (the widened saturated cells — exact integer adds, so the
  slot is bit-identical across resolve paths). A tile that resolves to all-zero frees its block
  and leaves the map — a missing page reads as 0. Layers with at least 32 dirty tiles fan the
  resolve out over a fixed worker pool; smaller lists and contended pools resolve sequentially.
- **Query**: `World.Query(world, grid, layer, x, y)` is one hash lookup + page read;
  `(x, y, w, h)` sums a rect tile-wise — full-grid reads sum one `int64` per live page, partial
  regions scan page rows; `QueryAt` takes world-space floats;
  `QueryRegion(world, grid, layer, x, y, w, h, short* dst)` fills `w×h` cells row-major with
  exactly the per-cell `Query` values, copying row segments per tile with vector stores where
  intrinsics allow (page rows are 64 B aligned by construction; the destination is written with
  unaligned-safe vector stores and scalar tails). Invalid handles,
  out-of-range cells, and missing pages all read 0 — no exceptions on the warm path.
  Region endpoints widen to `long` before clipping, so oversized rectangles cannot overflow.
  Invalid worlds make mutation and process calls return immediately; `Place` returns `-1`.
  Grid/layer creation rejects invalid worlds before accessing the arena.
Empty region queries return before iterating tiles; full-grid sums walk allocated map slots and
add one `int64` page sum per live page, so large sparse grids do not visit every possible tile.
- **Determinism**: field contents are integer-only; the only float math is the world→cell
  conversion (`multiply + floor`, correctly rounded IEEE ops). Deposits are commutative integer
  adds, so pages are bit-identical across runs, machines, and resolve scheduling (sequential or
  pooled — tiles are independent and the pooled dead set is determined by page content alone).
- **Idle process**: an unchanged world drains an empty dirty list and returns immediately —
  0 B warm.
  Moving a source to its stored position leaves the dirty lists empty. Page-map tombstones
  trigger compaction at the current capacity; only live-slot pressure requires growth.
  Tile resolution uses eight-cell AVX2 prefixes where available, with the existing four-cell
  SSE2/AdvSimd and scalar paths preserving the same integer sum and saturation semantics.

## Usage

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);   // 256×256 cells
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);   // or Stamp.Box(8, 8, 100)

int source = World.Place(world, layer, x: 128.5f, y: 64f, stamp, gain: 8);
World.Move(world, source, 129f, 64f);      // relocates — negates old, deposits new
World.SetGain(world, source, 4);           // adjusts weight in place
World.Process(world);                      // resolves dirty tiles once

short v = World.Query(world, grid, layer, 64, 32);      // cell read
long  t = World.Query(world, grid, layer, 0, 0, 32, 32); // region sum
short p = World.QueryAt(world, grid, layer, 128f, 64f);  // world-space point
World.Remove(world, source);
```

A world can mix resolutions freely — `Grid 256x / 1024x / 32x` — each grid covering its own
world rect at its own cell density; a source deposits into every grid it overlaps.

## Receipts

`dotnet run --project benchmarks -c Release -- --verify` asserts:

- `process-matches-oracle` — 300 randomly placed boxes vs a per-cell band oracle.
- `process-deterministic` — identical worlds produce identical pages.
- `remove-restores-baseline` — remove rebuilds tiles without the source.
- `warm-process-allocates-0-bytes` — unchanged and place/remove churn, 0 B.
- `warm-query-allocates-0-bytes` — 200k cell reads, 0 B.
- `query-region-matches-cells` — bulk fill equals per-cell reads across tile boundaries,
  clipped and negative-offset regions, 0 B warm.
- `page-sum-matches-scan` — the per-page `int64` sums behind full-region reads equal a naive
  per-cell rescan across random place/move/remove churn worlds.
- `saturated-sum-clamps` — saturation sticks at ±32767 after summation.

`--timing` adds min-over-20-rep lines for unchanged, incremental, move-200 churn, place-200
churn, full-grid sum, and 256² region reads (4000 sources, 1024² grid). Timing receipts live in
the README (deposit vs re-emitted marks, i9-14900K; perf-pass deltas, Ryzen 5 8500G); the perf
pass behind them was `perf`-profile guided, receipts first.

### Measurement hazards

- dotnet-trace leaf attribution is unreliable on this runtime (two profiles contradicted ground
  truth). Attribute with Linux `perf` (`tools/stats/perf.sh record`, `DOTNET_PerfMapEnabled=1`)
  and take receipts from interleaved A/B runs or per-phase timers only.
- Tiered compilation can trap hot loops in tier-0 during a timing window: `Resolve256` measured
  558–1010 µs for a move-200 frame in one build and 155 µs with `DOTNET_TieredCompilation=0`,
  same binary. The engine's hot methods carry
  `MethodImplOptions.AggressiveOptimization` (under `NET`) so they never run tier-0; when timing
  a change that predates that, set `DOTNET_TieredCompilation=0` or the baseline may be garbage.
- Resolve is memory-bound at scale: 2,400 dirty tiles stream ~25 MB per frame, past L3. Worker
  scaling tops out at the memory controller, not at core count — expect ~1.5× frame time on
  such scenes, not core-count multiples, and measure before promising.
- Interleaved A/B on a hybrid-core part swings 5–8% between identical builds. Lazy dense measured
  timing-neutral on box scenes and ~3% slower on an all-raster 2048² churn (dense moved from
  mid-block to the block tail); that delta sat inside the A/B noise band, so it was accepted for
  the −38.6% box-tile block size. Judge such trades on interleaved minima, never on single runs.

## Unsafe proof

- **Lifetime**: the world arena (32 `WorldCtx`) and stamp catalog (256 `StampVariant`) are
  process-lifetime pools allocated by the first creation call (`World.New`, `Stamp.New`,
  `Stamp.Box`) — never by static construction, so no static constructor on the assembly performs
  calls and Burst can compile `Query`/`QueryRegion` call graphs; world contents are
  `NativeHeap` blocks owned by the context (grid array, `LayerData` array,
  `InDirty`/`Dirty` per layer, `Prev` scratch, `SourceColumns` buffers) or by a `PageMap` (each
  tile block — 6,528 B, or 10,624 B once a raster deposit attached the dense buffer at its tail —
  is owned by its slot and
  freed exactly when the tile resolves to zero, the world is cleared, or the map is disposed;
  attaching or growing dense happens only on the serial deposit thread, which republishes the
  block pointer through the map before any later resolve can observe it). The shared zero dense
  page is allocated once with the arenas and never written afterwards.
  `Stamp` variants are catalog-owned for process lifetime. The resolve pool (background worker
  threads, one `Prev`-width scratch slice per worker, the shared dead-tile buffer) is created
  lazily by the first ≥32-dirty-tile `Process` and lives for the process; its native scratch is
  never freed and its threads never terminate. `QueryRegion` writes only the caller's
  destination. No `World.Free` exists: worlds are process-lifetime
  singletons, so no pointer escapes an owner.
- **Aliasing**: each block is written by deposits and resolved in place; during a pooled phase
  every tile is claimed by exactly one participant, so difference/dense/page/sum writes are
  disjoint and `previousRow` is one slice per participant (the caller's `Prev` for the main
  thread, a pool slice per worker), cleared per tile before use; sources are read-only during
  deposits of other sources. `PageMap` mutation happens only through its owning `LayerData`
  pointer on the thread that called `Process`.
- **Alignment**: tile blocks, native buffers, page-map arrays, pool scratch, and prefix scratch
  use 64-byte aligned allocations. World/grid/layer/catalog metadata uses `AllocZeroed` with
  natural alignment; raster samples require only byte alignment. SIMD accesses use unaligned
  load/store semantics. AVX2 resolves eight `int32` cells per group: each 128-bit lane computes
  its own prefix, the low lane's final sum carries into the high lane, and the eighth sum carries
  to the next group. Saturating pack results permute 64-bit chunks into cell order before storing
  eight `int16` values; the same permuted cells widen into the page-sum accumulators, one
  horizontal add per tile. Groups cover exactly cells 0–31 and never read padded columns or
  beyond the 32-cell scratch/page rows. Raster taps widen unaligned 4-byte sample reads within
  the padded allocation. Page rows (`PageOffset` is a multiple of 32) are
  32 B aligned, and `QueryRegion`'s vectorized copy/zero fills use unaligned-safe 256-bit (or
  128-bit) stores with scalar tails, so any caller destination alignment is correct. The sum
  slot sits at a 64 B block offset and is written as one naturally aligned `int64`. The dense
  tail of a raster block starts at `BlockBytes` — a multiple of 64 — so its rows are 64 B
  aligned; tiles without dense read the shared zero page, itself a 64 B aligned allocation. On
  netstandard2.1 (Unity) `NativeHeap` backs onto `Marshal.AllocHGlobal`
  with platform-natural alignment instead of 64-byte `AlignedAlloc`, and no SIMD paths compile:
  every access in that build is a naturally aligned scalar load or store, so the weaker
  alignment guarantee cannot be observed.
- **Concurrency**: `Place`/`Move`/`SetGain`/`Remove`/`Process` are serial per world; different
  worlds may run on different threads. `Process` fans a layer's dirty list out when it holds at
  least 32 tiles: a fixed pool of `min(cores−1, 4)` background workers, created once and parked
  between calls. Pool ownership is a compare-and-swap flag; a second world whose `Process`
  overlaps a pooled phase runs its own layers sequentially, so pooled and sequential resolves
  never interleave. A phase publishes its descriptor (dirty pointer/count, `InDirty`, page map)
  before signalling the worker events; wait-handle set/wait are full fences, so workers observe
  the published state. Work is claimed in 8-tile chunks through one atomic cursor — each tile is
  resolved by exactly one participant, and every block write during the phase is tile-local.
  `InDirty` clears are disjoint single bytes. Workers only read the page map (lookups); tiles
  that resolve to zero are appended to a pre-sized shared buffer through an atomic index, and
  the owning thread performs every `Remove`, `AlignedFree`, and dirty-list resize after joining
  the phase (a spin-join: the phase is µs-scale, so no worker ever sleeps mid-phase; parked
  workers block on their events between phases). Determinism is unaffected: tiles are
  independent, arithmetic is integer-exact, the dead set is a pure function of page content, and
  the order of removals has no observable effect on map contents. Warm pooled `Process`
  allocates 0 B: the pool, its scratch, and the dead buffer are sized at creation and by
  grow-to-max before steady state. World creation and stamp catalog mutation are also serialized
  across worlds because their arenas and counts are process-wide. Pool initialization rides the
  same rule: the first creation call allocates both arenas before any handle exists, and handles
  only originate from creation calls, so no query can observe an uninitialized arena.
  `Query`/`QueryRegion` read resolved pages and page sums only and may run concurrently with
  each other, never with mutation or process.
- **Bounds**: stamps clip to grid rects before marking; tile-local box corners land in
  `[0,32]×[0,32]` of the difference array (rows 0–32 exist for the exclusive far edge; column 32
  is written but never read, by design of the half-open prefix form). Raster fragments clip to
  the tile and read only inside the padded stamp allocation.

## Tool inspection

`tools/stats` reads internal state through friend access, without reflection, counters on the
data path, or additional public types. `Inspection.Read` validates the world handle, scans
source liveness and grid/layer metadata, walks live page-map slots to count raster-tile dense
buffers, and returns a pointer-free value snapshot. Its cost
is O(source slots + grids × layers + map slots + catalog stamps); it does not scan cells.
Memory includes allocated capacity, retained source/dirty buffers after `Clear`, every live tile
block plus its dense buffer where one exists, and shared world/stamp arenas plus padded rasters. The selected world and
process-wide shared allocations are reported separately; allocator metadata, alignment slack,
other worlds, and the runtime are excluded from native totals. GC heap and process memory are
separate runtime observations.

Inspection shares the owner's lifetime and thread rules: capture it on the world-owning thread
between mutations/process calls, and serialize stamp creation with the capture. It neither
mutates nor frees storage, creates aliases that escape the capture, nor allocates managed or
native memory. All pointer reads use the naturally aligned owning structs and bounded allocated
capacities. The CLI is single-threaded and formats only after capture and timing. JSON uses
`Utf8JsonWriter` directly with static UTF-8 property names and a fixed object/array layout;
serializer reflection is disabled in the tool project.
