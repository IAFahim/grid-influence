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
  int id; `Move`/`SetGain`/`Remove`/`Clear` mutate it. `gain` is an integer 0–16. Positions are
  float world units, converted to cell space per grid as `floor((x−ox)·scale·256)` in Q8 — the
  integer part is the cell, the low byte is the sub-cell phase.
- **Deposits are incremental**: each live tile owns a 12,480 B block — a 33×48 `int32` difference
  array, a 32×32 `int32` dense buffer, and a 32×32 `int16` page. `Place` deposits the stamp's
  contribution into every touched tile immediately; `Move`/`SetGain`/`Remove` deposit the exact
  negation at the stored position (integer adds invert perfectly — no rebuild, no source scan).
- **Process** drains the per-(grid,layer) dirty list: each dirty tile resolves its difference
  array through a 2D prefix sum (horizontal inclusive prefix + previous-row carry), adds dense,
  saturates to `short` **after** summation so cancellation is preserved. A tile that resolves to
  all-zero frees its block and leaves the map — a missing page reads as 0.
- **Query**: `World.Query(world, grid, layer, x, y)` is one hash lookup + page read;
  `(x, y, w, h)` sums a rect tile-wise; `QueryAt` takes world-space floats. Invalid handles,
  out-of-range cells, and missing pages all read 0 — no exceptions on the warm path.
- **Determinism**: field contents are integer-only; the only float math is the world→cell
  conversion (`multiply + floor`, correctly rounded IEEE ops). Deposits are commutative integer
  adds, so pages are bit-identical across runs and machines.
- **Idle process**: an unchanged world drains an empty dirty list and returns immediately —
  0 B warm.

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

Timing numbers are deliberately absent until PMU receipts exist for this engine.

## Unsafe proof

- **Lifetime**: `Worlds` is a static arena of 32 `WorldCtx` allocated once; world contents are
  `NativeMemory`/`AlignedAlloc` blocks owned by the context (grid array, `LayerData` array,
  `InDirty`/`Dirty` per layer, `Prev` scratch, `SourceColumns` buffers) or by a `PageMap` (each
  12,480 B block — difference array + dense buffer + page — is owned by its slot and freed exactly
  when the tile resolves to zero, the world is cleared, or the map is disposed). `Stamp` variants
  are catalog-owned for process lifetime. No `World.Free` exists: worlds are process-lifetime
  singletons, so no pointer escapes an owner.
- **Aliasing**: each block is written by deposits and resolved in place; `Prev` is per-world
  scratch used by exactly one tile resolve at a time; sources are read-only during deposits of
  other sources. `PageMap` mutation happens only through its owning `LayerData` pointer.
- **Alignment**: every unmanaged block is 64-byte aligned; `Vector128<int>` loads/stores in
  `Resolve`/`PackDense` use unaligned semantics (`LoadVector128`/`PackSignedSaturate` results
  stored through scalar 8-byte writes on 8-byte-aligned short offsets).
- **Concurrency**: `Place`/`Move`/`SetGain`/`Remove`/`Process` are serial; there is no shared
  mutable state between worlds, so different worlds may run on different threads. Within one
  world all access is single-threaded; no locks or interlocked ops exist.
- **Bounds**: stamps clip to grid rects before marking; tile-local box corners land in
  `[0,32]×[0,32]` of the difference array (rows 0–32 exist for the exclusive far edge; column 32
  is written but never read, by design of the half-open prefix form). Raster fragments clip to
  the tile and read only inside the padded stamp allocation.
