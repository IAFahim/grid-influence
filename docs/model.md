# Gi

Sparse tiled integer influence fields for .NET. One library, no dependencies.

## Model

- A **world** (`World.New`, up to 64) owns **grids** and **layers**. A grid
  (`Grid.New(world, power, x, y, size)`, up to 32 per world) is a power-of-two cell grid,
  `2^power` cells per side (power 5–14), laid over a world-space rect `x,y,size`. A layer
  (`Layer.New(world)`, up to 32 per world) is an independent field channel present on every grid.
- **Stamps** (`Stamp.New(sbyte* data, w, h)` / `Stamp.Box(w, h, value)`, up to 255) are baked
  cell-space content: `w×h` `sbyte` samples, centered on the placement position (origin offset
  `−w/2` cells in Q8). Uniform rasters classify as `ConstantRectangle` and take the
  difference-array path; the rest are `Raster` and deposit with sub-cell bilinear weights.
  Raster storage keeps a one-sample zero border (pitch `w+2`, `(w+2)×(h+2)`) so the deposit loop
  reads `x−1`/`y−pitch` unconditionally. Every raster stamp also bakes a mip chain: each level
  halves its predecessor with zero-padded 2×2 box averages (round-half-away-from-zero, divided by
  four — missing samples count as zero, so the field tapers to zero at the stamp's true extent),
  bordered identically and packed contiguously.
- **Sources** are persistent placements: `World.Place(world, layer, x, y, stamp, gain)` returns an
  int id; `Move`/`SetGain`/`Remove`/`Clear` mutate it. `gain` is an integer −16–16 (stored as one
  `sbyte`-ranged byte; deltas deposit exactly, so a negative gain subtracts). The id packs a
  7-bit generation above a 24-bit slot index; `Remove` recycles the slot through a per-world free
  list and the next fill of that slot bumps its generation, so a stale id (dead slot, recycled
  slot, or out-of-range) is an inert no-op for `Move`/`SetGain`/`Remove`. Generations wrap after
  128 fills of one slot, and `Place` returns `-1` once 2^24 slots are live. `Clear` empties every
  slot and resets the free list but keeps the generation bytes, so pre-clear ids stay stale.
  Column growth zeroes the new generation bytes, so returned ids are deterministic across runs.
  Positions are
  float world units, converted to cell space per grid as `floor((x−ox)·scale·256)` in Q8 — the
  integer part is the cell, the low byte is the sub-cell phase. Stamp extents are world-anchored:
  a `w×h` stamp covers `w×h` world cells at scale 1, and each grid scales the extent by its
  `ScaleQ8` (truncated integer Q8), so the same source covers the same world rect on every grid.
  Fractional leading/trailing edges become Q8 band weights (boxes) or bilinear phases (rasters).
  Grids with `ScaleQ8 == 256` take the native deposit loop, bit-identical to the 0.2 series;
  finer grids upsample bilinearly at mip level 0, coarser grids select
  `floor(log2(1/scale))` mip levels and step sample coordinates in Q16.16, so minified deposits
  are box-filtered rather than aliased.
- **Deposits are incremental**: each live tile owns a 6,528 B block — a 33×33 `int32` difference
  array padded to 4,384 B, a 32×32 `int16` page, an `int64` page sum, and an `int16` page max in
  the padded 64 B sum slot. Raster stamps also need the tile's 32×32 `int32` dense buffer: blocks
  touched by a raster deposit allocate it once (block grows to 10,624 B, dense pinned at the block
  tail) and box-only tiles never carry it — resolve reads a shared zero page instead. `Place`
  deposits the stamp's contribution into every touched tile immediately;
  `Move`/`Remove` deposit the exact negation at the stored position and `SetGain` the gain delta
  (integer adds invert perfectly — no rebuild, no source scan).
- **Process** drains the per-(grid,layer) dirty lists: each dirty tile resolves its difference
  array through a 2D prefix sum (horizontal inclusive prefix + previous-row carry), adds dense,
  saturates to `short` **after** summation so cancellation is preserved, and writes the page's
  cell total into its `int64` sum slot and the page's maximum cell into its `int16` max slot
  (both bit-identical across resolve paths). A tile that resolves to all-zero frees its block
  and leaves the map — a missing page reads as 0. A world holding at least 32 dirty tiles across
  all its (grid, layer) pairs fans the whole drain over a fixed worker pool — one flattened
  queue, chunks of eight tiles crossing layer boundaries freely; smaller totals and contended
  pools resolve sequentially. After the drain (and, for pooled phases, strictly after the join),
  every dirty tile pushes its new page max — 0 for a dead tile — into its layer's **max
  pyramid**: level 0 groups 8×8 tile maxes per node, each higher level groups 8×8 child-node
  maxes, stopping at a single root. A tile write updates the cached node max in its parent slot
  and propagates only when it must: a value above the cached max replaces it without a scan, an
  unchanged slot stops the walk, and only a falling former maximum rescans its 64-slot node
  (vectorized for full nodes) before continuing upward.
- **Query**: `World.Query(world, grid, layer, x, y)` is one hash lookup + page read;
  `(x, y, w, h)` sums a rect tile-wise — full-grid reads sum one `int64` per live page, partial
  regions sum the same `int64` slot for every tile the rect fully covers and scan only the
  clipped edge strips (rows widened to `int32` vectors, one horizontal add per tile — 15 µs for
  a 1022² rect on a 1024² grid that page-cell scanning cost ~390 µs);
  `QueryAt` takes world-space floats and maps them with the deposits' own truncated
  `ScaleQ8` product, so the cell it reads is the cell the deposit wrote;
  `QueryMax(world, grid, layer, out x, out y)` walks the max pyramid root-down — each node's
  cached max is matched against its valid child rectangle (edge nodes cover fewer than 8×8
  children; slots beyond the grid never enter a max or a match), then the winning tile scans its
  page for the first cell equal to the recorded maximum. The returned value is the layer's exact
  maximum over all cells — absent tiles count as 0, so the maximum is never negative unless every
  cell of the grid is covered by live negative pages — and the returned position is one cell
  holding that maximum, fixed deterministically by the descent (block children in one row band
  interleave in scan order, so the descent's choice is deterministic but is not guaranteed to be
  the row-major-first maximum; verify with `Query` at the returned cell). An empty layer reports
  0 at (0,0). 0.1 µs for a 1024² layer where one million `Query` scans cost ~5,600 µs;
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
  conversion (`multiply + floor`, correctly rounded IEEE ops) and the grid-scale truncation to
  integer Q8 at grid creation. Mip baking, band weights, and the Q16.16 sampler are pure integer
  ops. Deposits are commutative integer
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
short m = World.QueryMax(world, grid, layer, out var mx, out var my); // best cell
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
- `page-sum-matches-scan` — the per-page `int64` sums behind full-region and partial-region
  reads equal a naive per-cell rescan across random place/move/remove churn worlds.
- `region-sum-matches-cell-scans` — nine rect shapes (tile-aligned, one-off, clipped,
  negative-offset, 1×1) sum identically to per-cell scans on a 512² mixed box/raster field.
- `query-at-matches-deposits` — on a 2^14-over-10000 grid (truncated `ScaleQ8` 419 vs float
  1.6384), `QueryAt` at 200 source positions reads exactly the cell the deposit mapping wrote;
  the float mapping diverges on a measurable subset of them.
- `query-max-matches-full-scan` — across three churn rounds on a 1024² field (two layers, one
  left empty), then a fully covered negative layer, then the same layer pushed to saturation,
  `QueryMax` equals a full `QueryRegion` rescan of the layer, `Query` at the returned cell
  returns the maximum, and repeated calls return the identical value and position.
- `source-slots-reuse-and-stale-handles-inert` — 2000 place/remove pairs keep slots bounded,
  the recycled id differs from the stale one, and stale `Move`/`SetGain`/`Remove` leave the
  field bit-identical.
- `signed-gain-exact` — ± gains add and subtract exactly, `SetGain` crosses zero and clamps at
  ±16, and removing a signed pair restores the zero baseline.
- `multi-layer-pooled-matches-scans` — 16 layers × 30 sources through three churn rounds
  (world-total fan-out) match full, partial, and random-rect scans per layer.
- `saturated-sum-clamps` — saturation sticks at ±32767 after summation.
- `cross-grid-sums-conserve-world-integral` — the same box sources summed over four grids at
  scales 2/1/0.5/0.25 scale exactly by cell area (`full == 4·fine == 16·half == 64·quarter`).
- `naive-grid-matches-gi` (+ `naive-grid-matches-gi-after-churn`, under `benchmarks --compare`)
  — a managed dense-grid reference rebuild (clear + redraw every source, clamp on read) produces
  bit-identical output to Gi before and after churn; the same command then times both.

`--timing` adds min-over-20-rep lines for unchanged, incremental, move-200 churn, place-200
churn, full-grid sum, a 1022² partial-region sum, the best-cell query against a one-million-call
naive scan (0.1 µs vs ~5,600 µs on a 1024² layer), a 16-layer × 25-dirty move-400 process, and
256² region reads (4000 sources, 1024² grid). Timing receipts live in
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
  The same trap bites callees too large to inline into an optimized caller: the partial-region
  sum's edge scanner measured 700 µs stuck in tier-0 and 12 µs once it carried
  `AggressiveOptimization` itself — audit any new standalone hot helper against
  `DOTNET_TieredCompilation=0` before trusting a number.
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
  `InDirty`/`Dirty` per layer, `Prev` scratch, `SourceColumns` buffers — positions, stamp,
  layer, gain, liveness, the free-slot chain, and the generation bytes — and, per (grid, layer),
  the max pyramid: a flat `int16` slot array plus per-level offset and side tables, allocated
  zeroed by the layer's first resolved tile and freed only by `World.Clear`) or by a `PageMap`
  (each tile block — 6,528 B, or 10,624 B once a raster deposit attached the dense buffer at its
  tail — is owned by its slot and
  freed exactly when the tile resolves to zero, the world is cleared, or the map is disposed;
  attaching or growing dense happens only on the serial deposit thread, which republishes the
  block pointer through the map before any later resolve can observe it). The shared zero dense
  page is allocated once with the arenas and never written afterwards.
  `Stamp` variants — base samples, mip chain, and box constants — are catalog-owned for process
  lifetime. The resolve pool (background worker
  threads, one `Prev`-width scratch slice per worker, the shared dead-index buffer, and the
  1024-entry task buffer sized to 32 grids × 32 layers) is created
  lazily by the first ≥32-dirty-tile `Process` and lives for the process; its native scratch is
  never freed and its threads never terminate. `QueryRegion` writes only the caller's
  destination. No `World.Free` exists: worlds are process-lifetime
  singletons, so no pointer escapes an owner.
- **Aliasing**: each block is written by deposits and resolved in place; during a pooled phase
  every tile is claimed by exactly one participant, so difference/dense/page/sum writes are
  disjoint and `previousRow` is one slice per participant (the caller's `Prev` for the main
  thread, a pool slice per worker), cleared per tile before use; sources are read-only during
  deposits of other sources. `PageMap` mutation happens only through its owning `LayerData`
  pointer on the thread that called `Process`. The partial-region sum reads page rows only
  inside `[0,32)×[0,32)` of a live block and writes nothing. Pyramid updates alias nothing
  outside their owning `LayerData`: each dirty tile touches exactly its own level-0 slot, and
  the propagation walk reads one 64-slot node plus one parent slot per level; `QueryMax` reads
  pyramid slots, page maps, and live pages only. Node scans honor the node's valid child
  rectangle, so no slot past the grid's tile count is read as data; the SSE2 row compare in the
  descent may load past the valid prefix of a row but masks those lanes out of the match, and
  the loaded bytes stay inside the 128 B node.
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
  128-bit) stores with scalar tails, so any caller destination alignment is correct. Partial
  region sums widen page rows to `int32` vectors with the same unaligned-safe loads and scalar
  tails; a strip is at most 32 shorts per row, so the `int32` row accumulators and per-tile
  horizontal add cannot overflow (32 · 32768 < 2^20), and the result joins the `int64` region
  total. The sum
  slot sits at a 64 B block offset and is written as one naturally aligned `int64`; the page max
  sits at `SumOffset + 8` as one naturally aligned `int16` inside the same padded slot. Max
  pyramid nodes are 64 `int16` values at 128 B offsets inside a 64 B aligned buffer, so the
  full-node AVX2 max loads stay inside the allocation; partial nodes scan scalar. The
  dense
  tail of a raster block starts at `BlockBytes` — a multiple of 64 — so its rows are 64 B
  aligned; tiles without dense read the shared zero page, itself a 64 B aligned allocation. On
  netstandard2.1 (Unity) `NativeHeap` backs onto `Marshal.AllocHGlobal`
  with platform-natural alignment instead of 64-byte `AlignedAlloc`, and no SIMD paths compile:
  every access in that build is a naturally aligned scalar load or store, so the weaker
  alignment guarantee cannot be observed.
- **Concurrency**: `Place`/`Move`/`SetGain`/`Remove`/`Process` are serial per world; different
  worlds may run on different threads. `Process` fans the whole world drain out when its grids
  and layers hold at least 32 dirty tiles in total:
  a fixed pool of `min(cores−1, 4)` background workers, created once and parked
  between calls. Pool ownership is a compare-and-swap flag; a second world whose `Process`
  overlaps a pooled phase runs its own layers sequentially, so pooled and sequential resolves
  never interleave. A phase builds a task list — one entry per (grid, layer) with dirty tiles,
  each owning a contiguous base-offset range of a global index space whose size is the summed
  dirty counts — and publishes it, the dead-index buffer, the cursor, and the task count
  before signalling the worker events; wait-handle set/wait are full fences, so workers observe
  the published state. Work is claimed in 8-index chunks through one atomic cursor; because
  claims interleave, a participant's chunks are descending but not contiguous, so each claim
  locates its starting task by binary search over task bases and advances only within the
  claim's contiguous index run — every global index resolves exactly one tile, and every block
  write during the phase is tile-local.
  `InDirty` clears are disjoint single bytes. Workers only read the page map (lookups); tiles
  that resolve to zero append their global index to a pre-sized shared buffer through an atomic
  index, and
  the owning thread maps each dead index back to its task (the same binary search), performs
  every `Remove`, `AlignedFree`, and dirty-list resize after joining
  the phase (a spin-join: the phase is µs-scale, so no worker ever sleeps mid-phase; parked
  workers block on their events between phases), and only then walks the dirty lists again, on
  the owning thread alone, to fold each tile's page max into its max pyramid — pyramid slots are
  never touched by pool workers, so no pyramid write races a resolve. Determinism is unaffected: tiles are
  independent, arithmetic is integer-exact, the dead set is a pure function of page content, and
  the order of removals has no observable effect on map contents. Warm pooled `Process`
  allocates 0 B: the pool, its scratch, the task buffer, and the dead buffer are sized at
  creation and by grow-to-max before steady state. World creation and stamp catalog mutation are also serialized
  across worlds because their arenas and counts are process-wide. Pool initialization rides the
  same rule: the first creation call allocates both arenas before any handle exists, and handles
  only originate from creation calls, so no query can observe an uninitialized arena.
  `Query`/`QueryRegion`/`QueryMax` read resolved pages, page sums, and pyramid slots only and
  may run concurrently with each other, never with mutation or process.
- **Bounds**: stamps clip to grid rects before marking (extents clamp to the grid size in Q8;
  grids whose `ScaleQ8` truncates to 0 are skipped); tile-local box corners land in
  `[0,32]×[0,32]` of the difference array (rows 0–32 exist for the exclusive far edge; column 32
  is written but never read, by design of the half-open prefix form). Raster fragments clip to
  the tile and read only inside the padded stamp allocation: sample coordinates advance by
  `256·65536/ScaleQ8` per cell in Q16.16 from a phase-derived origin, so at any mip level `L` the
  integer sample index stays within `[(−1), ceil(w/2^L)]` and every `+1` tap lands on the level's
  zero border. Level selection stops at the stamp's mip count, so tiny stamps sample level 0 with
  a wider step rather than reading past the chain.

## Tool inspection

`tools/stats` reads internal state through friend access, without reflection, counters on the
data path, or additional public types. `Inspection.Read` validates the world handle, scans
source liveness and grid/layer metadata, walks live page-map slots to count raster-tile dense
buffers, and returns a pointer-free value snapshot. Its cost
is O(source slots + grids × layers + map slots + catalog stamps); it does not scan cells.
Memory includes allocated capacity, retained source/dirty buffers after `Clear`, every live tile
block plus its dense buffer where one exists, the per-(grid, layer) max pyramids, and shared
world/stamp arenas plus padded rasters. The selected world and
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
