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
- **Deposits are incremental**: each live tile owns a 12,480 B block — a 33×48 `int32` difference
  array, a 32×32 `int32` dense buffer, and a 32×32 `int16` page. `Place` deposits the stamp's
  contribution into every touched tile immediately; `Move`/`Remove` deposit the exact negation at
  the stored position and `SetGain` the gain delta (integer adds invert perfectly — no rebuild,
  no source scan).
- **Process** drains the per-(grid,layer) dirty list: each dirty tile resolves its difference
  array through a 2D prefix sum (horizontal inclusive prefix + previous-row carry), adds dense,
  saturates to `short` **after** summation so cancellation is preserved. A tile that resolves to
  all-zero frees its block and leaves the map — a missing page reads as 0.
- **Query**: `World.Query(world, grid, layer, x, y)` is one hash lookup + page read;
  `(x, y, w, h)` sums a rect tile-wise; `QueryAt` takes world-space floats. Invalid handles,
  out-of-range cells, and missing pages all read 0 — no exceptions on the warm path.
  Region endpoints widen to `long` before clipping, so oversized rectangles cannot overflow.
  Invalid worlds make mutation and process calls return immediately; `Place` returns `-1`.
  Grid/layer creation rejects invalid worlds before accessing the arena.
  Empty region queries return before iterating tiles; full-grid sums scan allocated map slots
  and live pages, so large sparse grids do not visit every possible tile.
- **Determinism**: field contents are integer-only; the only float math is the world→cell
  conversion (`multiply + floor`, correctly rounded IEEE ops). Deposits are commutative integer
  adds, so pages are bit-identical across runs and machines.
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
- `saturated-sum-clamps` — saturation sticks at ±32767 after summation.

Timing receipts live in the README (deposit vs re-emitted marks, i9-14900K, .NET 10, min over
reps); the perf pass behind them was `perf`-profile guided, receipts first.

## Unsafe proof

- **Lifetime**: `Worlds` is a static arena of 32 `WorldCtx` allocated once; world contents are
  `NativeHeap` blocks owned by the context (grid array, `LayerData` array,
  `InDirty`/`Dirty` per layer, `Prev` scratch, `SourceColumns` buffers) or by a `PageMap` (each
  12,480 B block — difference array + dense buffer + page — is owned by its slot and freed exactly
  when the tile resolves to zero, the world is cleared, or the map is disposed). `Stamp` variants
  are catalog-owned for process lifetime. No `World.Free` exists: worlds are process-lifetime
  singletons, so no pointer escapes an owner.
- **Aliasing**: each block is written by deposits and resolved in place; `Prev` is per-world
  scratch used by exactly one tile resolve at a time; sources are read-only during deposits of
  other sources. `PageMap` mutation happens only through its owning `LayerData` pointer.
- **Alignment**: tile blocks, native buffers, page-map arrays, and prefix scratch use 64-byte
  aligned allocations. World/grid/layer/catalog metadata uses `AllocZeroed` with natural
  alignment; raster samples require only byte alignment. SIMD accesses use unaligned load/store
  semantics. AVX2 resolves eight `int32` cells per group: each 128-bit lane computes its own
  prefix, the low lane's final sum carries into the high lane, and the eighth sum carries to
  the next group. Saturating pack results permute 64-bit chunks into cell order before storing
  eight `int16` values. Groups cover exactly cells 0–31 and never read padded columns or beyond
  the 32-cell scratch/page rows. Raster taps widen unaligned 4-byte sample reads within the
  padded allocation. On netstandard2.1 (Unity) `NativeHeap` backs onto `Marshal.AllocHGlobal`
  with platform-natural alignment instead of 64-byte `AlignedAlloc`, and no SIMD paths compile:
  every access in that build is a naturally aligned scalar load or store, so the weaker
  alignment guarantee cannot be observed.
- **Concurrency**: `Place`/`Move`/`SetGain`/`Remove`/`Process` are serial; there is no shared
  mutable state between worlds, so different worlds may run on different threads. Within one
  world all access is single-threaded; no locks or interlocked ops exist.
  World creation and stamp catalog mutation are also serialized across worlds because their
  arenas and counts are process-wide.
- **Bounds**: stamps clip to grid rects before marking; tile-local box corners land in
  `[0,32]×[0,32]` of the difference array (rows 0–32 exist for the exclusive far edge; column 32
  is written but never read, by design of the half-open prefix form). Raster fragments clip to
  the tile and read only inside the padded stamp allocation.

## Tool inspection

`tools/stats` reads internal state through friend access, without reflection, counters on the
data path, or additional public types. `Inspection.Read` validates the world handle, scans
source liveness and grid/layer metadata, and returns a pointer-free value snapshot. Its cost
is O(source slots + grids × layers + catalog stamps); it does not scan cells or page-map slots.
Memory includes allocated capacity, retained source/dirty buffers after `Clear`, every live
12,480 B tile block, and shared world/stamp arenas plus padded rasters. The selected world and
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
