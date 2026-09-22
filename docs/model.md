# GridInfluence

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
  int id; `World.Remove(world, id)`/`World.Clear(world)` retract. `gain` is an integer 0–16.
  Positions are float world units, converted to cell space per grid as `floor((x−ox)·scale·256)`
  in Q8 — the integer part is the cell, the low byte is the sub-cell phase.
- **Process** rebuilds tiles, not cells-in-place: per (grid, layer), the union of live source
  footprints (stamp `w+1×h+1` cells including sub-cell spill, clipped to the grid) marks dirty
  tiles; pages under tiles no longer touched are retired; each dirty tile is rebuilt from every
  overlapping source in placement order into a 32×32 `int16` page. Tiles with no live footprint
  keep nothing — a missing page reads as 0.
- **Tile bake**: constant rectangles split into ≤3×3 axis bands weighted by the Q8 sub-cell phase
  and emit `±value` corners into a 33×48 `int32` difference array; raster fragments deposit into a
  32×32 `int32` dense buffer as `RoundQ16(s·W00 + s[x−1]·W10 + s[y−1]·W01 + s[x−1,y−1]·W11)·gain`.
  Resolve runs the horizontal inclusive prefix sum plus previous-row carry — a 2D prefix sum that
  turns the difference array back into box coverage — adds dense, and saturates to `short`
  **after** summation, so cancellation is preserved (int32 accumulators bound the worst case:
  4·128·16 corner adds per cell per source × 100k sources stays inside int32).
- **Query**: `World.Query(world, grid, layer, x, y)` is one hash lookup + page read;
  `(x, y, w, h)` sums a rect tile-wise; `QueryAt` takes world-space floats. Invalid handles,
  out-of-range cells, and missing pages all read 0 — no exceptions on the warm path.
- **Determinism**: field contents are integer-only; the only float math is the world→cell
  conversion (`multiply + floor`, correctly rounded IEEE ops). Deposits are commutative integer
  adds applied in placement order, so pages are bit-identical across runs and machines.
- **Idle process**: `SourceGen`/`BuiltGen` per grid skips untouched grids; an unchanged world
  returns immediately — 0 B warm.

## Usage

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);   // 256×256 cells
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);   // or Stamp.Box(8, 8, 100)

int source = World.Place(world, layer, x: 128.5f, y: 64f, stamp, gain: 8);
World.Process(world);

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

Timing numbers are deliberately absent until PMU receipts exist for this engine.

## Unsafe proof

- **Lifetime**: `Worlds` is a static arena of 32 `WorldCtx` allocated once; world contents are
  `NativeMemory`/`AlignedAlloc` blocks owned by the context (grid array, `LayerData` array,
  `Mark`/`Dirty` per layer, `Dense`/`Diff`/`Prev` scratch, `SourceColumns` buffers) or by a
  `PageMap` (each `short*` page is owned by its slot and freed exactly when the page is removed,
  retired, or the map is disposed). `Stamp` variants are catalog-owned for process lifetime.
  No `World.Free` exists: worlds are process-lifetime singletons, so no pointer escapes an owner.
- **Aliasing**: `Dense`/`Diff`/`Prev` are per-world scratch used by exactly one `BuildTile` at a
  time; a page is written by exactly one tile build; sources are read-only during `Process`.
  `stackalloc` temp in `BuildTile` never escapes. `PageMap` mutation happens only through its
  owning `LayerData` pointer.
- **Alignment**: every unmanaged block is 64-byte aligned; `Vector128<int>` loads/stores in
  `Resolve`/`PackDense` use unaligned semantics (`LoadVector128`/`PackSignedSaturate` results
  stored through scalar 8-byte writes on 8-byte-aligned short offsets). `stackalloc` temp is
  16-byte aligned by the runtime.
- **Concurrency**: `Process` and `Place` are serial; there is no shared mutable state between
  worlds, so different worlds may be processed on different threads. Within one world all access
  is single-threaded; no locks or interlocked ops exist.
- **Bounds**: stamps clip to grid rects before marking; tile-local box corners land in
  `[0,32]×[0,32]` of the difference array (rows 0–32 exist for the exclusive far edge; column 32
  is written but never read, by design of the half-open prefix form). Raster fragments clip to
  the tile and read only inside the padded stamp allocation.
