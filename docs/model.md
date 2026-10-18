# Gi

Sparse tiled integer influence fields for .NET. One library, no dependencies.

## Axioms

The rest of this file derives from seven statements.

1. **Field semantics.** Box, raster, dense and round stamps are rounded per source into the `int32`
   plane `I`. Tent and bell stamps accumulate exactly in Q40 into `int64` planes and are rounded once
   per cell, half away from zero. A cell's total forms in `int64` and saturates once to `int16`.
   Overflow is exact: a plane folds into wide planes before a sum could pass its width.
2. **Precision.** A layer snaps source positions to `PhaseBits` (0–8) bits of sub-cell phase and
   headings to `TurnBits` (0–16) bits of angle. Every kernel depends only on `cell − anchor`, is linear
   in gain, and clipping to the grid drops only cells outside it, so the engine at precision `p` equals
   the exact engine run with the same masks, bit for bit. A source costs work only when its snapped key
   changes.
3. **Residency.** A persistent layer keeps one deposited record per (source, grid); `Process`
   sweeps the changed sources and emits −old and +new kernel taps into the tile planes. A transient
   layer forgets the frame: `Splat` accumulates, `Process` publishes, and the next frame starts empty
   (at `PhaseBits` 0 through a per-tile histogram that is convolved with the stamp).
4. **Time.** `Process` advances one tick. Fades step only when the integer gain changes, expiries free
   slots in ascending slot order, and a sweep over the dense list of timed slots costs O(timed).
5. **Recipes.** A derived layer (`Sum`, `Min`, `Max`, `Mask`) is a per-cell function of two other layers,
   recombined over the tiles its inputs changed; an exclusion recurses through the recipes.
6. **Change.** A tile has changed when its published `int16` bytes differ from those of the previous
   `Process`, whichever path produced them, and its epoch is written only then.
7. **Determinism.** Published pages, summaries, epochs and read results are identical across runs,
   machines and instruction sets (AVX-512, AVX2, SSE, scalar, ARM64); the golden corpus pins page and
   read hashes for 1,200 generated streams, and every stream also agrees with the interpreter.

## Model

- A **world** (`World.New`, up to 255 live at once) owns **grids** and **layers**. `World.Free(world)`
  releases the world's memory and its handle: `World.New` reuses the lowest free handle, and a freed
  handle reads as absent to every call. `World.CopyWorldIds(ids)` copies the allocated ids,
  ascending, into a span and returns the live count; `World.ProcessAll()` processes every
  allocated world in id order on the calling thread. `World.Teardown()` returns the process-wide
  pools to the OS once no world is live: it returns `false` and changes nothing while any world
  is live, and `true` once it has freed everything (the pools paragraph states the full contract).
  `World.Memory(world, out storeBytes,
  out liveTiles, out pages, out kernelEntries, out liveSources)` is the tooling read-out: it
  sums, over every (grid, layer) of the world, the retained capacity of the four plane slabs
  (`PlaneStore.Bytes`, free lists included), the engine's live-tile counter (`LivePages`, one per
  directory entry), and the live plane units those slabs hold — every live tile's page, `I`,
  `T`/`B` and summary unit, folded tiles' wide temporaries included — then counts the kernel
  cache's occupied entries out of 32 and the sources whose alive bit is set. It reads only
  existing counters, allocates nothing, throws nothing, and returns all zeros for a dead or
  never-allocated handle; `Clear` empties the live counts while the slab bytes stay (regions are
  retained, free lists grow), and `Free` returns everything to zero. `World.Revision(world)` returns an `int`
  that changes whenever queued work could be stale — `World.New` assigns it a fresh value,
  and `Clear`, `Rewind`, `Free`, and slot reuse bump it; mutations, `Process`, and `ProcessAll`
  leave it alone. `World.Capture(world)` seals the id and that revision into a `World.Token`
  (`IsValid` is false for an unallocated or freed slot, whose stored revision stays nonzero —
nonzero never implies existence). `World.ApplyBatch(token, …)`, `World.Sync(token, …)`,
`World.SyncTransforms(token, …)`, and `World.Process(token)` validate the token inside Gi
before reading command payloads, writing
  results, or touching source state, timers, journals, pending work, or ticks: an allocated
  world with a different revision returns `BatchStatus.StaleRevision`, a dead or never-allocated
  slot returns `BatchStatus.InvalidWorld`, and rejection leaves the world byte-identical. The
  revision is an `int` in both the token and the runtime store — never a widened copy — and
  comparisons are equality, so the only wrap hazard is a stale token falsely matching after
  exactly 2³² lifecycle transitions on one slot. A `World.Teardown` does not reopen that hazard:
  before the revision array is freed, its maximum is folded into a process-lifetime floor with
  which the next `Ensure` seeds the reallocated array, so a slot's revisions stay increasing
  across teardowns and a token captured before a teardown is refused with `StaleRevision` after
  the restart, exactly as a token from before a `World.Free` is. Capture is legal whenever reading the world is
  (between lifecycle controls, under the host's producer/reader fences); consumption runs in the
  existing exclusive mutation phase — a token check is not a lock, and concurrent same-world
  mutation or a `Free`/`Clear`/`Rewind` racing a consumer remains forbidden. A grid
  (`Grid.New(world, power, x, y, size)`, up to 32 per world) is a power-of-two cell grid,
  `2^power` cells per side (power 5–14), laid over a world-space rect `x,y,size`. The origin
  and size must be finite, the size positive, and the truncated integer Q8 scale
  `ScaleQ8 = trunc(2^power / size · 256)` must lie in `[1, 2^20]`; `Grid.New` throws
  `ArgumentOutOfRangeException` naming `power`, `x`, `y`, or `size` otherwise and consumes no
  grid id, and `GiValidation.TryGrid(power, x, y, size, out error)` applies the same rules from
  the same source (`ValidationRules`). Grids are
  never freed singly, so their ids stay the dense range `0..Grid.Count(world)-1`;
  `Grid.CopyIds(world, ids)` copies them into a span and returns the count, and
  `Grid.TryGetSpec(world, grid, out power, out x, out y, out size)` returns the exact
  constructor arguments — the world extent is stored, not recovered from the truncated
  scale, so the spec round-trips bit-exact. `Layer.Count(world)` and
  `Layer.CopyIds(world, ids)` enumerate layers the same way, deposited and derived sharing
  one dense id space, and `Layer.TryGetRecipe(world, layer, out recipe)` inspects any live
  layer's `Layer.Recipe` (`Layer.Derivation` operator plus input layers, weights, shift, and
  mask bounds). A layer
  (`Layer.New(world)` or `Layer.New(world, precision)`) is an independent field channel present
  on every grid. Each base layer carries a `Layer.Precision` (`PhaseBits` 0–8, `TurnBits` 0–16;
  presets `Exact` = (8, 16), `Half` = (1, 16), `Quarter` = (2, 16), `Cell` = (0, 16);
  `Layer.Precision.TryCreate(phaseBits, turnBits, out precision)` validates ranges; plain
  `Layer.New(world)` uses `Exact`; `Layer.TryGetPrecision(world, layer, out precision)` reads a
  base layer's precision and returns `false` on derived or invalid layers). `PhaseBits` masks
  each Q8 anchor coordinate (`floor((p − origin)·ScaleQ8) & ~((1 << (8 − PhaseBits)) − 1)`)
  before adding the stamp origin, and `TurnBits` masks the 16-bit binary angle
  (`angle & (0xFFFF & ~((1 << (16 − TurnBits)) − 1))`) on every placement and exclusion path
  (`Deposit`, `BuildDeposits`, `ScatterDeposit`, `ScatterSource`, `ShapeOf`). Write paths
  (`Move`, `Turn`, `Sync`, `SyncColumns`, `SyncTransforms`, `ApplyBatch`) compare the snapped
  key (per-grid anchor X/Y and masked angle) of the old and new state: when no grid's key
  changed and stamp, gain, scale, layer, and liveness are unchanged, the new floats/angle are
  stored in the source columns without enqueuing a deposit mutation. Precision lives in the
  world's `LayerRecipe` table and survives `Clear`. A layer either
  receives deposits (`Layer.New`) or is **derived** (`Layer.Sum`/`Min`/`Max`/
  `Mask`): its cells are a pure function of other layers' cells, maintained by `Process` (below).
  A deposited layer is **persistent** (its sources stay until removed) or **transient**
  (`Layer.New(world, Layer.Residency.Transient, precision)`): a transient layer has no sources,
  only **splats** — `World.Splat(world, layer, xs, ys, stamp, gain)`, the per-element overload
  taking a stamp and a gain per row, or a `World.SplatWriter` from `World.BeginSplat` — each a
  stamp at a position with unit scale and no angle, snapped by the layer's precision and
  emitted into the tile planes exactly like a persistent source's deposit, on every grid. Splats
  accumulate between `Process` calls and the layer's published pages change only at `Process`,
  so reads keep seeing the previous frame until then. `Process` publishes the frame's splats and
  the next frame starts empty: after each publish a transient tile's `I`, `T` and `B` planes are
  zeroed and the tile joins the layer's stale list, and the next `Process` republishes every
  stale tile in full, so a tile that received no splat reads zero and `Changed` reports it
  exactly when its bytes differ from the previous frame. The persistent mutators — `Place` and
  the batch `Place` — reject a transient layer as an invalid layer, and `Splat` on a
  persistent or derived layer applies nothing. Derived layers read a transient input like any
  other. At `PhaseBits` 0 a splat inside the grid does not emit: its clamped gain is added to a
  per-stamp-class, per-tile `int32` **histogram** page (4,096 B, allocated on the class's first
  splat in that tile), and `Process` turns each class's histogram into planes before publishing.
  A class whose gain-1 taps are one nonzero value across a rectangle no wider than a tile (an
  integer box at its phase-0 anchor, a frame-C 4×4 box) is box-filtered: for each output tile
  the histogram window — the tile plus the kernel's reach, gathered from up to nine pages — is
  summed along rows and then columns and `value × sum` is added to the tile's `I` plane. Every
  other class replays its nonzero histogram cells through the exact emitters with the cell's
  summed gain, in steps of at most 127. Emission is linear in gain and the taps of a phase-0,
  unscaled, unturned placement do not depend on its position, so both forms add exactly what the
  per-splat emission would; splats outside the grid, and every splat at a precision above
  `Cell`, still emit directly. The first nonzero-gain in-grid splat of a frame allocates the
  layer's epoch array, as the first covered deposit does for a persistent layer.
  Deposited and derived layers share one table: a world holds at most `World.MaxLayers` (32)
  layers in total, so a derived combo takes one of the same slots as a raw channel and the plan
  is `raw + derived ≤ 32`.
- **Stamps** (`Stamp.New(sbyte* data, w, h)` / `Stamp.Box(w, h, value)` / `Stamp.Tent(w, h, value)` /
  `Stamp.Bell(w, h, value)` / `Stamp.Disk`/`Cone`/`Dome(radius, value, arc)`, up to 255 live at
  once) are baked
  cell-space content: `w×h` `sbyte` samples, centered on the placement position (origin offset
  `−w/2` cells in Q8). Uniform rasters classify as `ConstantRectangle` and take the
  difference-array path; the rest are `Raster` and deposit with sub-cell bilinear weights.
  `Tent` and `Bell` are smooth kernels sampled at cell centres: each axis measures the distance
  `d` from a cell's centre to the kernel's true centre (`lead + extent/2` in Q8, so sub-cell
  placement is continuous) and weighs it as `max(0, H − |d|)` (tent, piecewise-linear) or
  `max(0, H² − d²)` (bell, paraboloid), where `H = max(256, extent/2)` in Q8. The one-cell
  minimum half-width makes kernels narrower than a cell interpolate linearly between the two
  nearest cells — a 1-wide tent on a cell boundary reads 50/50, never vanishes, and never
  steps as it moves. To keep every product exact in `int64`, distances run in a per-axis unit
  of `2^u` Q8 (`u ≤ 7`, chosen so the tent's `H < 2^15` units and the bell's `H < 2^8` units),
  `d(c) = ((c·256 + 128) >> u) − (centre >> u)` — linear in `c`, so the weights stay an exact
  piecewise polynomial. One joint normalizer `C = round(2^40 / (peakX·peakY))` (`peak = H` for
  tents, `H²` for bells) scales the product to Q40, so a centred kernel's peak cell reads
  `value·gain` (the `Box` unit) within 0.2%; `C ≥ 2^8`, a source's largest product is below
  `2^51`. A tile's `int64` buffer is folded into exact wide planes before a sum could pass 2^62
  (see Plane limits), so there is no stacking limit.
  Each tile the kernel touches keeps the exact unrounded sum of every kernel of that kind in a
  lazily attached 32×32 `int64` buffer (8,192 B; one for tents, one for bells, each behind its
  own pointer slot in the block pad). A deposit adds its product `value·gain·C·Wx·Wy` to every
  cell of its support inside the tile — per axis the cells `First..Last` whose weight is
  positive, the per-axis weights computed once per column and once per row — and publish rounds
  each cell once, `RoundQ40(Σ value·gain·C·Wx·Wy)`, over all sources of that kind in the tile.
  A tent or bell's cell bounds are its support — per axis `First..Last` — rather than its box
  footprint, so tiles and exclusion follow a kernel narrower than a cell to the neighbour its
  one-cell minimum half-width reaches. Every weight is zero outside that support, so tiles
  beyond it receive nothing; moves
  and removes subtract the identical products, so retraction returns the buffer to literal
  zero, and sensing reads a cell's unrounded sum with one load.
  A bell whose half-extent on a grid reaches 128 cells (`extent/2 ≥ 2^15` Q8) cannot stay exact
  in `int64` (an exact cell-lattice paraboloid needs about `H⁴` of range), so on that grid it
  deposits through the raster path instead: `Stamp.Bell` bakes its paraboloid as a `w×h`
  raster with mips at creation (`round(value·(w²−ex²)(h²−ey²)/(w²h²))`, `e = 2s+1−w`), and
  the raster sampler upsamples it bilinearly — smooth, full strength, `O(area)` per deposit.
  The choice is per grid and deterministic (`TileBake.Effective`), and sensing uses the same
  rule. Tents fit at every scale.
  Raster storage keeps a one-sample zero border (pitch `w+2`, `(w+2)×(h+2)`) so the deposit loop
  reads `x−1`/`y−pitch` unconditionally. Every raster stamp also bakes a mip chain: each level
  halves its predecessor with zero-padded 2×2 box averages (round-half-away-from-zero, divided by
  four — missing samples count as zero, so the field tapers to zero at the stamp's true extent),
  bordered identically and packed contiguously.
  The scaled bilinear sampler (`EmitRaster` away from 256) and the turned sampler (`SampleAt`) read the
  four taps `(ix, iy)`, `(ix+1, iy)`, `(ix, iy+1)`, `(ix+1, iy+1)` of a level of size `w×h`. Taps with
  `ix < −1`, `ix ≥ w`, `iy < −1` or `iy ≥ h` have both samples of an axis outside the border and are zero by
  definition, so both samplers skip them; every tap that is read lies in `[−1, w] × [−1, h]` of the
  level's `(w+2)×(h+2)` block. Lifetime: the block is owned by the stamp and freed by `Stamp.Free`, which
  is refused while a world uses the stamp. Aliasing: the block is written once at bake and read-only
  afterwards. Alignment: byte reads. Concurrency: none on the warm path. The apron test copies baked
  stamps into buffers whose surroundings are poisoned and requires identical emission.
  **Round stamps** `Stamp.Disk`/`Cone`/`Dome(radius, value, arc)` (radius 1–128 world cells,
  arc 1–360 degrees) are radial kernels evaluated per cell around the source position: with `r`
  the distance from the cell centre to the kernel centre and `R` the radius on that grid, a disk
  weighs `clamp(R − r + ½, 0, 1)` (a one-cell anti-aliased edge, capped at `2R` for sub-cell
  radii), a cone `max(0, 1 − r/R)`, and a dome `max(0, 1 − r²/R²)`, with `R` at least one cell
  for cones and domes so narrow kernels interpolate instead of vanishing — all three read
  `value·gain` at a centred cell, the box unit. Distances run in half-Q8 (1/512 cell, so
  `centre = 2·lead + extent` is exact), `r = ⌊√(dx² + dy²)⌋` is an exact integer root, and the
  weight is a Q16 fraction with no per-cell division: a cone weighs `((R − r)·⌈2^48/R⌉) >> 32`,
  a dome `(((R² − d²) >> q)·⌈2^62/(R² >> q)⌉) >> 46` (`q` keeps the reduced square below 2^46),
  so a centred cell weighs exactly 1, and a disk takes its root only inside its one-cell edge
  band (`(R − 255)² ≤ d² < (R + 256)²` in half-Q8 — outside it the weight is exactly full or
  zero). An arc below 360° keeps the sector of half-angle `arc/2` around the
  source's facing (below), with a one-cell anti-aliased edge: the coverage is
  `clamp(½ − s, 0, 1)` for `s` the signed cell distance to the sector's edge lines. A cell
  deposits `RoundQ24(value · weight_Q16 · coverage_Q8) · gain` into the `I` plane.
  `Stamp.Free(stamp)` returns a stamp's id to the catalog — only while no source in any world
  uses it, no pending snapshot or journal entry names it — frees its samples, and the next creation
  reuses the lowest free id; a freed id places nothing (`Place` returns `-1`) until it is
  reissued. `Stamp.TryGetSpec(stamp, out spec)` returns a live stamp's `Stamp.Spec`
  (`Stamp.Shape`, cell width and height, peak `sbyte` value, and exact `ArcDegrees` 1–360) and
  `false` for `0` or a freed handle.
- **Sources** are persistent placements: `World.Place(world, layer, x, y, stamp, gain)` returns a
  `World.SourceId(uint Slot, uint Generation)` (`default` when the world, layer, or stamp is
  invalid, the layer is derived, or `x`/`y` is non-finite); `Move`/`SetGain`/`Remove`/`Clear`
  mutate it (`Move` ignores non-finite `x`/`y`
  coordinates, and so do `Sync` and `SyncTransforms` row by row; `World.TryGetSourceSpec(world,
  id, out spec)` reads a live source's current position, quantized heading in radians, quantized scale, gain, layer, and stamp). `gain` is an integer −16–16 (stored as one
  `sbyte`-ranged byte; deltas deposit exactly, so a negative gain subtracts). The id carries a
  32-bit generation beside a slot index below 2^24. A freed slot — by `Remove`, by an `Expire` that comes
  due, or by a batch `Remove` — joins the tail of a per-world **free ring**, and `Place` (scalar or
  batch) takes the head, so the slot freed longest ago is the next to be filled and a slot just freed
  is the last: with `F` slots free, one slot is refilled at most once per `F` places. Filling a
  slot bumps its generation, so a stale id (dead slot, recycled slot, or out-of-range) is an inert
  no-op for `Move`/`SetGain`/`Remove`. A slot whose generation reaches `uint.MaxValue` is
  **retired**: it is never pushed back onto the ring (by `Remove`, expiry, batch remove, or the
  `Rewind` rebuild), so no id is ever reissued and a stale id can never address a live source —
  the 1.x 7-bit ABA hazard is gone. `Place` returns `default` once 2^24 slots are live or every
  slot is retired. `Clear` empties every slot and the
  free ring but keeps the generations counting, so pre-clear ids stay stale.
  New chunks are zero-filled, so returned ids are deterministic across runs.
  Positions are
  float world units, converted to cell space per grid as `floor((x−ox)·scale·256)` in Q8 — the
  integer part is the cell, the low byte is the sub-cell phase. The float Q8 position is
  range-checked before the integer conversion — deposits and reads clamp it to ±2^30, and reads
  map NaN to −2^30 — so no result depends on how a platform converts an out-of-range float; a
  position that far out lies outside every grid, and so does a non-finite one. Stamp extents are
  world-anchored:
  a `w×h` stamp covers `w×h` world cells at scale 1, and each grid scales the extent by its
  `ScaleQ8` (truncated integer Q8), so the same source covers the same world rect on every grid.
  Fractional leading/trailing edges become Q8 band weights (boxes) or bilinear phases (rasters).
  Grids with `ScaleQ8 == 256` take the native deposit loop, bit-identical to the 0.2 series;
  finer grids upsample bilinearly at mip level 0, coarser grids select
  `floor(log2(1/scale))` mip levels and step sample coordinates in Q16.16, so minified deposits
  are box-filtered rather than aliased.
  `World.Turn(world, id, radians)` and `World.Scale(world, id, scale)` set a source's facing and
  size; both queue and merge like `Move`, so a turn, a scale, a move, and a gain change in one
  window cost one retract-and-apply pair. Angles store as binary angles (65,536 per turn,
  `⌊frac(θ/2π)·65536⌋`, so whole turns read 0) with sine and cosine from a fixed Q14 polynomial
  that is exact at the four quarter turns; scales store in Q8 (`⌊scale·256⌋`, 1/256 to 255.99).
  Scale multiplies the stamp's sampling rate on every grid: extents and origin offsets use
  `sampling = ScaleQ8·scale_Q8 >> 8` in place of `ScaleQ8`, so a box of width `w` at scale 2 is
  bit-identical to a box of width `2w`. A source with angle 0 deposits through the exact
  axis-aligned paths above; a turned source — or any round stamp — evaluates every cell of its
  rotated bounds: boxes weigh the product of their two one-cell anti-aliased edge coverages
  along the turned axes, tents and bells add `value·gain·C·Wu·Wv` with the same per-axis units
  and normalizer as unturned kernels into the same sum stores, and rasters sample bilinearly at
  the turned cell centre — its Q16.16 sample coordinates are `(dx·Ux + dy·Uy + U0) >> 32` with
  `Ux = ⌊cos·2^33/S⌋`, `Uy = ⌊sin·2^33/S⌋`, `U0 = w·2^47 − ⌊2^55/S⌋` (and `−sin`, `cos`, `h` for
  the other axis), folding the turn and the sampling rate `S` into fixed-point coefficients;
  sample indices are bounded to the padded border and the mip level is chosen as for unturned
  rasters. Every turned or round weight is a pure function of the cell, the
  placement, and the stamp, computed in integers, so retraction cancels it exactly.
  Footprints never clamp a stamp to the grid size: an unturned box clips its edges to the grid
  window (one cell of margin) without moving its interior, a stamp wider than its grid covers
  the grid, tents cap their extent at `2^23` Q8 (where the exact unit scheme ends — wider than
  any grid), and other kinds at `2^30` Q8.
- **Writes mark, `Process` sweeps**: each live tile owns one set of plane units in the layer's
  `PlaneStore`: a 2,048 B `int16` page (the published field), a 4,096 B `int32` **`I` plane**
  that sums every box, raster and round contribution of the tile, and — attached on the tile's
  first tent or bell emission — one 8,192 B `int64` Q40 plane each for tents (`T`) and bells
  (`B`), plus a 256 B summary block (cell sum, maximum with its first argmax, epoch, and the 32
  row sums and row maxima). A derived-layer tile takes a page and a summary only. Mutations do
  not touch tiles: `Place`/`Move`/`Turn`/`Scale`/`SetGain`/`Remove`, every fade step and expiry,
  `Sync`, `SyncTransforms`, `ApplyBatch` and `Rewind` write the source columns and mark the slot
  on the world's **changed list** once per window — a per-slot `Mark` column compared with the
  world's mark epoch deduplicates — and the first mark of a window also snapshots the slot's
  columns into `Pending` (24 B), which `Rewind`, the journal and `Stamp.Free` read. Each
  (slot, grid) owns a 16 B **deposited record** of what is in the field for that source: layer,
  stamp, gain, liveness, the masked binary angle, the Q8 scale, and the anchor — the position's
  Q8 cell coordinates on that grid, reduced by the layer's anchor mask. A zero record deposits
  nothing. `Process` sweeps the changed list in mark order. For each changed slot and each grid
  it forms the **target** record from the current columns and compares it, as two 8-byte words,
  with the deposited record. Equal means nothing moves — below `Exact` precision a source whose
  masked anchor did not change costs no deposit at all. Different means it emits the deposited
  record's stamp at gain `−g`, emits the target's stamp at `+g`, both straight into `I`/`T`/`B`,
  grows each touched tile's dirty rectangle, and overwrites the record. Emission is per-cell
  linear in gain (`RoundQ16(base)·gain`; smooth kernels add `value·gain·C·Wx·Wy` in `int64`),
  so every contribution is a plain integer add, the order of adds is immaterial, and any
  mutation sequence on a slot collapses exactly to one withdraw-and-deposit pair, bit-identical
  to stepping the same sequence through a `Process` per mutation. A slot placed and removed in
  one window deposits nothing: its record is zero before and after. Slot reuse can merge a
  removal on one layer with a placement on another before processing; the record's own layer
  takes the withdrawal and the target's layer the deposit.
  Every emission first resolves a **placement** per grid — footprint, effective kind, target
  plane, turned frame, and smooth or raster parameters — and skips tiles its turned or round
  bounds cannot touch before allocating anything. An unturned `ConstantRectangle` emits its Q8
  band weights straight into `I`: per tile at most three column bands by three row bands, each
  band adding `RoundQ16(Constant·wx·wy)·gain` to its cells (two bands can cover one cell, and
  the adds accumulate). When a tile's live columns span at most eight cells and 256-bit vectors
  are supported, each row band adds one eight-lane vector instead — lanes outside the live
  columns hold zero — and wider boxes add per band. Every other placement first looks up gain-1
  taps in the per-world kernel cache and adds `tap·gain`, and falls back to the exact per-kind
  emitters when the cache refuses the kernel. Before emitting slot `i` the sweep prefetches the
  `I` rows slot `i + 8` will touch (x86 `Sse` only; it reads no cell and writes nothing). There
  is no crowd heuristic and no rebuild: a frame costs in proportion to the slots whose target
  differs from their record, never to the field.
- **Caps.** A source's stamp footprint after scaling, `ceil(extent · ScaleQ8 · scale / 2^16)` cells on
  every grid, may not exceed 512 cells per axis: `Place` returns `InvalidStamp` (batch placements report
  `InvalidStamp`), `Scale` returns `CapacityReached`, and batch and transform scale writes leave the
  scale unchanged. A disk read's reach may not exceed 4,096 cells on the grid it would use; a grid whose
  scale puts the reach past that is skipped, so a read nothing else covers returns `false` with zeroed
  outputs. Both caps are in `ValidationRules`, mirrored by `GiValidation.TryFootprint` and `TryReach`,
  and by the interpreter. `ScaleQ8` is still held to `[1, 2^20]` at `AddGrid`.
- **Statuses.** `Place` (with `out WriteStatus`), `Move`, `Turn`, `Scale`, `SetGain`, `Remove`, `Fade`
  and `Expire` return `World.WriteStatus`; an unknown world counts as `InvalidLayer` for `Place` and as
  `StaleSource` for the id mutators, and non-finite values are rejected before the id is looked up.
  Column reads classify their result as `World.ReadStatus` after the read: `InvalidLayer` for an unknown
  world or layer, `LengthMismatch` for a null required column while `count > 0`, `Complete` for a
  complete answer (and for `count ≤ 0`), otherwise `Partial` or `Outside` according to whether any bit
  of the `whole` mask is set (`Partial` when no mask was passed, and always for `SenseNearestColumn`,
  whose mask means "found").
- **Columns** batch the per-agent API. `World.SenseColumn(world, layer, xs, ys, count, values,
  whole)` walks the pointer arrays the way a kernel walks a column: it senses each agent's
  position (the same grid-picking, exclusion-free path as `TrySense`) and writes the `short`
  value, optionally a `ulong` word per 64 agents flagging whole-tile answers; it returns
  `false` only when some agent stands off every grid. The overload
  `World.SenseColumn(world, layer, xs, ys, excludes, count, values, whole)` adds an optional
  per-row `excludes` column (`null` or `< 0` senses without exclusion; `>= 0` excludes that
  source bit-exactly via `TrySense`), and `SenseGradientColumn`, `SenseAreaColumn`,
  `SenseMaxColumn`, and `SenseNearestColumn` provide the matching columnar forms for
  `TrySenseGradient`, `TrySenseArea`, `TrySenseMax`, and `TrySenseNearest` (writing `found`
  bits where `value != short.MinValue`); an overload of `SenseGradientColumn` that also takes a
  `short* values` column returns each agent's value and gradient in one pass, reading all four
  neighbours from the agent's own page whenever its local cell is at least one cell from the
  page edge, and equal to a `SenseColumn` plus a `SenseGradientColumn` call on every row, with
  the `whole` mask the conjunction of the two. Every column read overwrites its mask words, so
  bits never accumulate across calls. `World.ChangedMask(world, layer, xs, ys,
  reach, since, count, words)` writes one `ulong` per 64 agents — bit set when that agent's
  disk changed since the tick — zeros for quiet words, and returns the changed count.
  `World.Sync(world, sources, xs, ys, count)` is the fused column form of `Move`: it
  validates generations, skips no-ops, ignores rows with a non-finite position, and marks
  the changed-list entries `Move` would, batch after batch, with zero managed allocation, in one serial loop.
  `World.SyncTransforms(token, ranges)` borrows compact native pose columns through the same
  boundary: each `World.TransformRange` names ID/X/Y columns plus optional heading/scale
  columns (null preserves the current value), a row count, and an optional invalid mask.
  Rows apply in range order then row order — a duplicate id simply applies again, so the last
  row wins and the slot is marked once — with `UpdateTransform`'s field semantics but `Sync`'s bit-exact position compare:
  `-0.0` and `+0.0` are different poses, where ordered `Move`/`UpdateTransform` compare with
  `==`. Gain, fades, and expiry are never touched. A row whose x or y is not finite is rejected
  whole — its heading and scale are not applied either — and, like a stale or dead row, sets its
  mask bit (bit `row & 63` of word `row >> 6`; cleared words leave
  tail bits zero and untouched words keep their contents) instead of producing a per-row
  result, and the call still returns `Applied`. Validation runs in a fixed order before any
  write — token (`StaleRevision`/`InvalidWorld`), descriptor structure (`InvalidRange` for
  negative counts, missing required pointers, or a mask shorter than `(Count + 63) / 64`
  words), total rows over 16,777,216 (`TooManyCommands`), then mask aliasing
  (`OverlappingBuffers` when a mask overlaps any column or another mask; read-only columns
  may share storage) — so a rejected call leaves masks and world byte-identical. The columns
  are borrowed only for the synchronous call: one inline pass on the caller applies each row
  through the same write helpers `Move`/`Turn`/`Scale` use, with zero scratch. No caller
  pointer survives the return.
- **Source batches** use `World.ApplyBatch` over a sealed `ReadOnlySpan<World.SourceCommand>`
  and caller-owned result span. Input order defines command order; pending creation targets
  reference an earlier `Place` index. Structural rejection writes no output and changes no source;
  stale handles and failed creations are explicit per-command results and do not abort unrelated
  commands. `Update` owns position, heading, scale, and gain, including fade cancellation. Partial
  methods change only their named fields. Ingestion does not tick or touch tiles; a later `Process`
  deposits the result. After structural validation, commands apply in order through the same
  write helpers as the scalar mutators — `Place` pops the real free ring, a `Remove` pushes it, and
  a pending target resolves to the id its creating command returned — so a batch is exactly the
  ordered scalar calls, with no planning pass, grouping or scratch.
- **Process** runs due timers, sweeps the changed list as above, advances the mark epoch,
  clears the window's `Pending` snapshots, and
  publishes: each dirty tile's rectangle is recomputed by `TileBake.Publish` from `I`, `T` and
  `B` (v2 tile planes and publish, below). A rectangle covering at least half the tile publishes
  the whole tile; each new cell is compared with the published `int16` before it is stored; the
  tile's summary stays exact; and a tile is changed exactly when a published byte differs, which
  also writes its epoch. A tile whose page publishes to all zeros frees its set and leaves the
  directory — a missing page reads as 0 — and stays changed only when its old page held a
  non-zero cell, which the same comparison decides. Deposits that cancel inside one window — a
  source moved out and back before `Process`, or removed while an identical source takes its
  place — leave the published bytes equal, so nothing is reported. After the publish,
  every dirty tile pushes its new page max — 0 for a dead tile — into its layer's **max
  pyramid**: level 0 groups 8×8 tile maxes per node, each higher level groups 8×8 child-node
  maxes, stopping at a single root. A tile write updates the cached node max in its parent slot
  and propagates only when it must: a value above the cached max replaces it without a scan, an
  unchanged slot stops the walk, and only a falling former maximum rescans its 64-slot node
  (vectorized for full nodes) before continuing upward. Tiles whose published bytes did not
  differ leave the dirty list there and then — inside the publish — so the
  changed feed costs nothing for quiet tiles. Each `Process` then rotates every
  (grid, layer) dirty list into its **changed list** — a zero-copy buffer swap, so the tiles the
  call changed stay readable until the next `Process` overwrites them.
- **Derived layers**: `Layer.Sum(world, a, weightA, b, weightB, shift)` reads
  `clamp(round(weightA·A + weightB·B) / 2^shift)` — rounding half away from zero, weights within
  ±32,767 and `shift` 0–15, so the `int32` total `|weightA·A + weightB·B| < 2^31` is exact —
  and `Layer.Sum(world, a, weight, shift)` is its one-input form; `Layer.Min`/`Layer.Max(world,
  a, b)` read the cellwise minimum/maximum; `Layer.Mask(world, a, b, min, max)` reads `A` where
  `min ≤ B ≤ max` and 0 elsewhere. `A` and `B` are the inputs' published, saturated cells, so a
  derived cell is exactly what a caller computes from `Query` on its inputs. Every operator maps
  two zero inputs to zero, so a tile where no input holds a page holds no derived page either:
  derived layers are as sparse as their inputs. Inputs must already exist (lower ids), so
  creation order is a topological order and chains nest to the 32-layer limit; each layer
  records the bitmask of source layers it reads. The 32-layer budget is shared between
  deposited and derived layers, and the cap is load-bearing rather than an allocation knob: a
  recipe's read set is one `uint` over layer indices and layer ids stay bytes, so a wider
  budget widens that mask on the place and process paths. `Process`, after the publish, visits
  derived layers in id order: each marks the union of its inputs' dirty lists —
  this window's byte-changed tiles, derived inputs included, which is every tile whose
  recombination can move a byte, because a derived page always equals the recipe applied to its
  inputs' current bytes — and recombines exactly those tiles in
  one pass over the 1,024 cells of each input page (an absent page reads the shared zero page),
  comparing each published cell against the combined one before storing it, and writing the
  derived page, its sum, and its max (16 or 8 cells per vector step, bit-identical
  to the scalar loop). All-zero results free the tile — a fresh derived block starts zeroed, so
  it compares against the zeros an absent page reads as; pyramids and changed lists update as
  for any layer, so every query works on derived layers. `Place` on a derived layer returns `-1`.
  Recipes belong to the world's layer table and survive `Clear`; pages follow their inputs
  through `Rewind` and `Clear`. A derived layer created while its inputs already hold pages
  marks every live input tile, so the next `Process` fills it completely; publish skips derived
  layers, whose dirty lists only recombination drains.
- **Time**: `World.Tick(world)` counts the world's `Process` calls (it advances at the start of
  each one and wraps at 2^32; every tick comparison is wrap-safe within 2^31 ticks).
  `World.Fade(world, id, gain, ticks)` ramps a source's gain linearly from its current gain to
  `gain` over the next `ticks` `Process` calls: the call at elapsed tick `k` applies
  `from + sign(Δ)·⌊(|Δ|·k + ⌊ticks/2⌋) / ticks⌋`, so a fade changes the field at exactly the
  `|Δ| ≤ 32` ticks where that integer gain steps — `⌈((j+1)·ticks − ⌊ticks/2⌋) / |Δ|⌉` for step
  `j + 1` — and costs nothing in between; `ticks ≤ 0` or an unchanged gain is an immediate
  `SetGain`. `World.Expire(world, id, ticks)` removes the source in the `ticks`-th `Process` from
  now (`ticks ≤ 0` cancels a pending expiry); a source both fading and expiring on one tick is
  removed. `SetGain` cancels a fade, `Remove` cancels both, `Move`/`Turn`/`Scale` keep them, and
  a fresh `Place` on a recycled slot starts unscheduled. Schedules live in per-slot source
  columns; a per-world dense list of timed slots, each entry carrying the next tick at which its
  fade steps or its expiry falls due, holds every scheduled slot once (a per-slot index column
  gives the entry back for swap-removal, and entries for slots that died or lost their schedule
  are dropped when they come due). At the start of `Process`, after the tick advances, one scan
  of the list collects the due slots, sorts them ascending, and applies each as an ordinary
  gain change or removal — marked, snapshotted, and merged exactly like the API calls — so
  scheduled and manual mutations are indistinguishable downstream, and determinism follows from
  the ascending-slot order (expired slots return to the free ring in that order). Ticks count `Process` calls, not time: Gi has no clock.
- **Schedule read-back**: `World.TryGetSchedule(world, source, out gain, out fadeFrom, out fadeTo,
  out fadeTicks, out fadeTicksLeft, out expireTicksLeft)` reads one live source's schedule. It
  returns false — every out zeroed — for a dead world, a stale or dead id, or a source with
  nothing left scheduled, and true otherwise. `gain` is the current effective gain, the column
  the sweep steps and deposits read. `fadeTicks > 0` marks a fade that still has a gain step to
  apply — a fade whose rounded gain has already reached `fadeTo` no longer counts, even before
  its nominal end — and carries `fadeFrom`/`fadeTo` (the ramp ends), `fadeTicks` (the requested
  duration) and `fadeTicksLeft` (ticks to the nominal end, at least 1 while stepping);
  `fadeTicks − fadeTicksLeft` is the elapsed tick, so the whole staircase `from +
  sign(Δ)·⌊(|Δ|·k + ⌊ticks/2⌋) / ticks⌋` is recomputable and an interrupted run can be re-driven
  tick for tick (a single fresh `Fade(world, source, fadeTo, fadeTicksLeft)` restarts rounding
  from the current gain and is not bit-exact in general). `expireTicksLeft` is the ticks before
  the pending removal, so `Expire(world, source, expireTicksLeft)` reproduces the removal tick;
  a source both fading and expiring reports both. *Lifetime*: the call borrows the world's
  source columns for its duration only and no pointer escapes it. *Aliasing*: the out
  parameters are caller-owned slots, never aliasing Gi memory. *Alignment*: outs sit at their
  natural alignment (sbyte 1 B, int 4 B). *Concurrency*: a per-world read under the same
  serial-per-world rule as `TryGetSourceSpec` — safe on any thread that already respects that
  rule, torn rather than protected against a concurrent writer or `Process` on the same world;
  it writes nothing, allocates nothing, and throws nothing.
- **Rewind**: `Record` drops any prior journal, bumps a per-world record epoch in O(1), and
  moves the pending snapshots (mutations made since the last `Process`) into the journal. From
  then on the first marked write to each slot journals that slot's 24 B column state (position,
  stamp, layer, gain, angle, scale, liveness, and the 32-bit generation) as it was before the
  write, and a per-slot epoch stamp keeps it to one entry per slot per recording, so the journal
  is bounded by the slot count however many windows run; schedule state is journaled the same
  way on the first schedule change. A mutation made between a `Process` and the `Record` that
  follows it is undone too. `Rewind` first restores every unprocessed `Pending` snapshot, newest
  first, then consumes the journal: each entry restores its slot's columns to the recorded state
  (position, stamp, layer, gain, angle, scale, liveness, and — only when the source was alive at
  the checkpoint — the generation, so a removed source revives with its exact checkpoint id while
  a rolled-back place's id stays stale and, because a dead slot keeps the highest generation
  issued, is never reissued), and marks the slot once on the changed list with a fresh
  `Pending` snapshot. The next `Process` sweeps each restored slot against its
  deposited record and emits the net withdraw-and-deposit pair for the whole rewound span,
  exactly like ordinary mutations. The free-slot ring cannot be replayed positionally (journal
  order is first-mutation order, not free-event order), so `Rewind` rebuilds it canonically —
  every dead slot pushed in ascending index order, smallest slot at the head — a deterministic
  pure function of the restored columns. A rewind costs the same as the window it undoes —
  O(changes), never O(field). `StopRecording` discards the journal without restoring; `Record`
  again re-anchors the checkpoint; `Clear` resets both. With no journal there is no checkpoint
  and `Rewind` only cancels pending mutations, which is why `Pending` snapshots are taken
  whether or not the world is recording (`PendingSnapshotTests` pins both cases).
  Schedules rewind too: while recording, every API change to a slot's schedule (`Fade`,
  `Expire`, a fade-cancelling `SetGain`, `Remove`, `Place`) appends the slot's prior schedule to
  a schedule journal, and scheduled events are ordinary marked mutations already in the journal. `Rewind`
  restores the schedule journal in reverse, then shifts every live schedule by the ticks elapsed
  since `Record` — the tick itself never runs backwards, so change epochs stay monotonic — and
  rebuilds the timed list: pending fades and expiries resume with exactly the remaining time they had
  at the checkpoint, and a rewound world re-simulates tick for tick like a twin that never left
  it.
- **Field view**: `World.View(world, grid, layer)` returns a `World.FieldView`, a `ref struct`
  over one (grid, layer) directory and its published pages, in cell coordinates. `view[x, y]` is
  one bounds test, one tile-index compare against a cached tile and one `int16` load (0 outside
  the grid and in absent tiles, exactly `Query`); `view.Gradient(x, y, out gx, out gy)` is the
  `Query` central difference, read from the cached page when the cell is at least one cell from
  the page edge; `view.At(x, y)` and `view.TryCell(x, y, out cx, out cy)` map world floats the
  way `QueryAt` and the column reads do. An invalid handle yields a view of size 0 that reads 0.
  A view is valid until the next `Process`, `Clear`, `Rewind` or `World.Free` — those can
  allocate or free pages and directories — and is for the owning thread's read phase only;
  reads never write, so any number of views may read concurrently with each other and never with
  mutation or `Process`. It copies nothing and allocates nothing (a coherent read costs about
  0.8 ns; `benchmarks --view` pins it to `Query` on every cell).
- **Query**: `World.Query(world, grid, layer, x, y)` is one hash lookup + page read;
  `(x, y, w, h)` sums a rect tile-wise — full-grid reads sum one `int64` per live page, partial
  regions sum the same `int64` slot for every tile the rect fully covers and scan only the
  clipped edge strips (rows widened to `int32` vectors, one horizontal add per tile — 15 µs for
  a 1022² rect on a 1024² grid that page-cell scanning cost ~390 µs);
  `QueryAt` takes world-space floats and maps them with the deposits' own truncated
  `ScaleQ8` product, so the cell it reads is the cell the deposit wrote; a non-finite or
  unreachably distant position reads 0;
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
  `QueryMax(world, grid, layer, x, y, w, h, out bx, out by)` scopes the same descent to a
  cell-space rectangle: at every level only children whose tile range overlaps the rect are
  visited, any node whose cached max is no better than the running best is skipped outright,
  and only tiles that can still improve get their overlap cells scanned — ~0.5 µs for a
  128×128 rect on a 1024² layer where the naive rescan reads 16k cells. Missing pages count
  as 0, the returned position is the first cell in descent order holding the maximum inside
  the rect, and an empty or out-of-range rect reports 0 at the clipped origin;
  `QueryGradient(world, grid, layer, x, y, out gx, out gy)` maps a world-space point with the
  same truncated `ScaleQ8` product as `QueryAt` and returns the central difference over resolved
  page cells — `Q(cx±1, cy)` and `Q(cx, cy±1)` as two ints; out-of-grid neighbors read 0, so
  gradients at the border measure the drop toward empty space, and a non-finite position
  returns `(0, 0)`. ~5 ns per point;
  `ChangedTiles(world, grid, layer, int* dst)` returns how many tiles the most recent
  `Process` changed on that (grid, layer) — changed meaning the tile's published `int16`
  bytes differ from what they were before that `Process`, on every deposit path — and copies
  the tile ids to `dst` (ascending tile index, whichever deposit path ran) when it is not
  null — repainting, incremental network sync, and dirty-rect presentation read one call
  instead of diffing pages. The list includes a tile that resolved to zero and left the map
  exactly when its old page held a non-zero cell; a deposit that cancels inside one window
  reports nothing;
  it is empty before the first `Process`, after a `Process` with nothing dirty on that pair,
  and after `Clear`. The caller's buffer must hold the grid's tile count; passing null fetches
  only the count. Buffers swap, so the feed allocates nothing warm.
  `ChangedTiles(world, grid, layer, since, int* dst)` serves consumers that read at their own
  cadence: every (grid, layer) keeps an `int32` **epoch** per tile — the tick of the last
  `Process` that changed its bytes, written for every byte-changed tile (base and derived,
  including tiles that resolved to zero with published content) after the drain — and the
  overload returns, ascending, every tile whose
  epoch is after `since`. `Clear` stamps every tile it frees with the next tick, so a consumer
  polling with the current tick sees the cleared tiles;
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
- **Sensing (grid-free)**: `TrySense*` answer world-space questions without a grid handle, and
  every one returns `bool complete` — `true` only when the chosen grid holds the whole query
  footprint. The grid is chosen by one rule, a pure function of the point, footprint, and grid
  configuration: among grids whose cell space contains the point (the deposits' own truncated
  `ScaleQ8` mapping), prefer a grid that contains the whole footprint, then the larger
  `ScaleQ8` (finer), then the lower grid id. No grid contains the point → `false` with every
  out value zeroed — `TrySenseNearest` keeps its `short.MinValue` not-found sentinel so
  `value ≥ threshold` and the column `found` bits stay truthful, and the reported positions of
  `TrySenseMax` and `TrySenseNearest` are zeroed on rejection rather than echoing the query point
  (`Covers(world, x, y)` tells "not covered" from "covered but clipped"), so an
  uncovered point never reads as an empty field; a non-finite position or reach is contained by no grid,
  so `Covers` is `false` and every `TrySense*`, column read, `Changed`, and `ChangedMask` row
  treats it as uncovered. A footprint no grid holds whole is answered on
  the finest grid containing the point, clipped, and reports `false`; answers are never
  stitched across grids, so a query crossing a fine grid's edge moves to the coarser grid that
  holds it instead of reading the fine grid's empty outside.
  `TrySense(world, layer, x, y, out v)` is the chosen grid's cell under the point.
  `TrySenseArea(world, layer, x, y, reach, out total)` sums the disk of cells whose centres lie
  within `reach` world units (Q8 integer test `dx²+dy² ≤ ⌊reach·ScaleQ8⌋²` against cell centres
  `c·256+128`), always including the cell under the point, and normalizes the cell sum to world
  area — `round(sum·65536 / ScaleQ8²)`, half away from zero — so the same field reads the same
  total on grids of different resolution (exact for box stamps whose bands align on both grids;
  receipt below). Fully covered tiles add their `int64` page sum; edge tiles sum masked row
  spans. `TrySenseMax(world, layer, x, y, reach, out v, out bx, out by)` returns the largest cell
  in the same disk and the world-space centre of the first such cell in row-major order;
  absent tiles count as 0; tiles are visited highest page-max first and pruned by page max.
  `TrySenseGradient(world, layer, x, y, out gx, out gy)` needs the point and its four
  neighbours in one grid and returns the central difference per world unit,
  `(Q(c+1) − Q(c−1))·ScaleQ8/512`, so steering speed does not jump at a resolution seam.
  **Self-exclusion**: `TrySense(..., exclude, out v)`, `TrySenseArea(..., exclude, out t)`, and
  `TrySenseGradient(..., exclude, out gx, out gy)` (four excluded neighbour reads)
  answer exactly what the same query would return after `Remove(exclude); Process()` — bit for
  bit, including saturation and the tent/bell once-per-cell rounding — on derived layers too,
  where the excluded source's own layer takes its excluded value (the rule below) and every
  derived layer on the path to the queried one recombines it per cell; an area sums that delta
  over the source's footprint inside the disk. The excluded source's
  *applied* state is used: a pending move, gain change, or removal is ignored until `Process`
  applies it; a source placed this frame excludes nothing; stale, foreign-layer, and invalid ids
  exclude nothing. Method: a page cell strictly inside `(−32768, 32767)` is the exact
  unsaturated total `T`; box and raster sources subtract their own integer contribution; tent
  and bell sources replace the rounded sum of their kernel with the
  rounded sum minus their exact product `value·gain·C·Wx·Wy`, which needs the cell's unrounded
  kernel sum — one load from the tile's tent or bell store. Saturated page cells rebuild every
  component (the `I` total, tent, bell) before subtracting, and the rebuilt fold forms the
  `I`-plus-tent-plus-bell total in `int64` and saturates once, exactly as publish does, so exclusion
  matches the page on tiles whose planes each fit `int32` but whose sum does not. On a transient layer
  `TrySense`, `TrySenseGradient` and `TrySenseArea` also take `(stamp, gain)` in place of a source id and
  exclude a splat of that stamp and gain made at the query point this frame, by the same rule, from
  the planes the last publish kept; any other layer, gain 0 or an unknown stamp excludes nothing. Areas apply the same rule per
  cell of the source's footprint inside the disk, reading the planes directly. The source's own
  contribution at a cell is its cached kernel tap times its gain on a `TryFindTaps` hit — the
  read never builds a kernel or spends insert budget, so a miss (a kernel the write paths have
  not cached, a clipped placement, or a shape past the canvas limit) recomputes the
  contribution from the stamp through `BoxAt`, `EmitRaster` or the smooth weights, with
  identical results; only the sweep and publish write paths insert kernels, so a read mutates
  nothing outside its own stack and stays bit-identical whatever the cache holds.
  `World.Changed(world, layer, x, y, reach, since)` answers whether any tile holding a cell of
  the same disk (on the grid `TrySenseArea` would pick) has an epoch after `since` — an agent
  can skip re-deciding while its neighbourhood is unchanged. `TrySenseNearest(world, layer, x,
  y, reach, threshold, out value, out nx, out ny)` returns the disk cell with `value ≥
  threshold` nearest to the point (squared Q8 distance between the point and cell centres, ties
  to the row-major-first cell) and its world centre, or `short.MinValue` and the point itself
  when no cell qualifies — so `value ≥ threshold` tells found from not found. It visits tiles in
  rings of growing Chebyshev distance from the point's tile, skips tiles whose page max (0 for
  an absent tile) is below the threshold, scans only rows that can still beat the best
  distance, and stops at the first ring that cannot.
  Sensing reads only resolved state, so it inherits `Query`'s one-frame latency: a source placed
  or moved this frame is seen after the next `Process`. Values saturate at ±32767 and signed
  sources cancel; a layer meant to detect presence should hold same-signed sources.
- **Determinism**: field contents are integer-only; the only float math is the world→cell
  conversion (`multiply + range test + floor`, correctly rounded IEEE ops) and the grid-scale truncation to
  integer Q8 at grid creation. Mip baking, band weights, and the Q16.16 sampler are pure integer
  ops. Deposits are commutative integer
  adds, so pages are bit-identical across runs and machines.
- **Idle process**: an unchanged world sweeps an empty changed list, publishes nothing, and
  returns immediately — 0 B warm. Moving a source to its stored position, or by less than its
  layer's anchor step, leaves the dirty lists empty. Publish uses eight-cell AVX2 groups where
  available, with the Vector128 and scalar paths preserving the same integer sum and
  saturation semantics.

## Plane limits

The 1.x engine documented finite plane widths: the `I` plane wrapped at `2^31 / 2048 =
1,048,576` maximum-amplitude sources on one cell, and the tent and bell planes at 4,096
maximum-strength kernels at one peak. 2.0 removes both limits by promotion (ROADMAP D9).

Each tile set keeps, per plane, a **bound**: the sum of the largest absolute change any one
cell can receive from each emission since the plane was last folded. An emission charges the
bound before it writes: a box adds `|constant·gain|` to `I`, a cached kernel adds
`|gain|·MaxTap` to `I` or `|gain|·MaxTapQ40` to `T` or `B`, an uncached kernel adds the
worst case for its kind (`128·|gain|`, scaled by `2^40` for the smooth planes), and a
histogram tile adds `max|box sum|·|value|`. When the charge would carry the bound past `2^31 − 1`
for `I` or `2^62` for `T` and `B`, the plane is **folded** first: its cells move exactly into
wide `int64` planes drawn from the existing 8,192 B slab and the plane restarts from zero with
a bound of zero. `I` folds into one integer carry plane. `T` and `B` fold into a pair each: the
whole-unit count `high` and the fraction `low ∈ [0, 2^40)` of the exact Q40 sum
(`high·2^40 + low`), which loses nothing and cannot overflow for `2^23` folds of a
saturating tile. The triangle inequality makes the live `int32` and `int64` planes exact at
every point: they only ever hold a sum of at most `2^31 − 1` or `2^62` in magnitude.

A tile with wide planes publishes through `PublishWide`, a scalar loop that forms
`I + carry + RoundWide(high, low + T) + RoundWide(high, low + B)` in `int64` and saturates
once. `RoundWide(high, low)` rounds `high·2^40 + low` half away from zero exactly as
`RoundQ40` rounds a single `int64` (it reduces to it for `high = 0`; a test pins the identity over
two million values and splits). Exclusion reads the same wide planes. A tile drops only when
every plane's exact total is zero, never when its published page is zero alone (a sub-unit tent
residue would otherwise vanish with the tile and a later removal would start from a lost
partial sum). The histogram path charges every increment against a per-layer budget of
`2^31 − 1`; a splat that would pass it is emitted directly instead, so no histogram cell or
window sum overflows `int32`, and a tile's box-filter product that would pass `2^31 − 1` is
added to the carry plane directly.

The folding limits are per layer and default to those two constants; a test hook lowers them
(down to 1) and the differential suite replays gate, transient and heavy streams against the
interpreter with a fold on nearly every emission.

## v2 tile planes and publish

The v2 plane set (ROADMAP D6) holds every tile of the field: `Process` emits into it and
publishes the pages from it.
A `PlaneStore` holds four fixed-size slabs: 2,048 B **pages** (32×32 `int16`), 4,096 B **`I`
planes** (32×32 `int32`, always allocated with the tile), 8,192 B **`T`/`B` planes** (32×32
`int64` Q40, attached on the tile's first tent or bell use), and 256 B **summary blocks**. A
summary block holds the `int64` cell sum, the `int16` maximum with the first argmax of its row
(32×32 tiles, so both coordinates are `int16`), the `int32` epoch, 32 `int32` row sums, and 32
`int16` row maxima. A fresh unit is zero, which is a consistent summary of an all-zero page
(maximum 0 at (0,0)).

`TileBake.Publish(tile, rect)` recomputes every cell of the tile-local rectangle as
`Sat16(I + RoundQ40(T) + RoundQ40(B))` — the total formed once in `int64`, so publish equals an
independent per-cell model of that sum, cell for cell and summary for summary (sum and maximum). Each new cell is compared
with the published page before it is stored; a tile is changed exactly when a published byte
differs (D4), and the summary epoch is written with the caller's tick only on that condition,
matching the epochs `Process` writes today. A rectangle covering at least half the tile's cells
is published as the whole tile. Summaries update exactly: every touched row's sum and maximum
are recomputed from the published page, the tile sum is the `int64` sum of all 32 row sums, and
the tile maximum keeps its first argmax — the whole tile is rescanned only when the old maximum
cell decreased; otherwise a larger rect cell raises the maximum, and an equal rect cell before
the old argmax moves the argmax earlier (an equal or smaller cell after it changes nothing).

The scalar publish is that exact definition. The Vector128 and AVX2 publishes round the `T` and
`B` rows once per row through the resolve's own `SmoothRow` and form `I + tent + bell` in
`int32` behind the resolve's exactness guard over `I` (`|I| < 2^29`; the `RoundQ40` terms lie in
`[−2^23, 2^23]` for any `int64` sums, so the `int32` total cannot wrap). Publish has no
cross-cell carry — each cell reads only its own plane cells — so the guard runs per row and a
tripped row falls back to the exact scalar row instead of redoing the tile. Every path
publishes identical bytes, summaries, changed bits and epochs. A tile that has folded takes the
wide path above instead.

## Usage

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);   // 256×256 cells
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);   // or Stamp.Box(8, 8, 100) / Stamp.Tent(12, 12, 90)

World.SourceId source = World.Place(world, layer, x: 128.5f, y: 64f, stamp, gain: 8);
World.Move(world, source, 129f, 64f);      // relocates — queued, applied at Process
World.SetGain(world, source, 4);           // adjusts weight in place
World.Process(world);                      // sweeps changed sources, publishes dirty tiles

short v = World.Query(world, grid, layer, 64, 32);      // cell read
long  t = World.Query(world, grid, layer, 0, 0, 32, 32); // region sum
short p = World.QueryAt(world, grid, layer, 128f, 64f);  // world-space point
short m = World.QueryMax(world, grid, layer, out var mx, out var my); // best cell
World.QueryGradient(world, grid, layer, 128f, 64f, out var gx, out var gy); // ±1-cell slope
World.Remove(world, source);
World.Free(world);                         // releases the world's memory and its handle
World.Teardown();                          // returns the process pools to the OS once no world is live
```

A world can mix resolutions freely — `Grid 256x / 1024x / 32x` — each grid covering its own
world rect at its own cell density; a source deposits into every grid it overlaps.

## Receipts

`dotnet run --project benchmarks -c Release -- --verify` asserts:

- `process-matches-oracle` — 300 randomly placed boxes vs a per-cell band oracle.
- `process-deterministic` — identical worlds produce identical pages.
- `remove-restores-baseline` — removing a source withdraws its deposit and restores the baseline pages.
- `warm-process-allocates-0-bytes` — unchanged and place/remove churn, 0 B.
- `warm-query-allocates-0-bytes` — 200k cell reads, 0 B.
- `query-region-matches-cells` — bulk fill equals per-cell reads across tile boundaries,
  clipped and negative-offset regions, 0 B warm.
- `page-sum-matches-scan` — the per-page `int64` sums behind full-region and partial-region
  reads equal a naive per-cell rescan across random place/move/remove churn worlds.
- `region-sum-matches-cell-scans` — nine rect shapes (tile-aligned, one-off, clipped,
  negative-offset, 1×1) sum identically to per-cell scans on a 512² mixed box/raster field.
- `kernels-move-smoothly` — box, tent, and bell stamps of 7 widths glide 2 cells in 1/16-cell
  steps: no cell ever changes by more than the kernel's continuous slope allows (whole-cell
  stepping kernels fail it).
- `kernels-hold-strength-at-every-scale` — tents and bells of widths 1–255 on grids of scale
  1/16 to 32 read `value·gain` within 1.5% at a centred cell (the bell raster fallback
  included; exact bells used to vanish on fine grids).
- `kernels-share-box-units-and-centre` — box, tent, and bell stamps of 12 widths (1–255),
  values 90 (gain ±16) and 1, placed on a cell centre: the centre reads `value·gain` within 0.5%,
  profiles are symmetric along both axes, never exceed the centre, and odd widths cover exactly
  `w` cells. It shares no kernel formula with the engine and fails on each historical bug —
  tents/bells at 256× box strength (Q24 rounding of a Q32 product), the vanished last bell
  cell, left-edge sampling, and the truncated bell curve.
- `sense-picks-finest-covering-grid-and-reports-gaps` — 4,000 probes (many on grid edges) over
  four overlapping grids, including two equal-scale grids offset by a fraction of a cell:
  `TrySense` equals `Query` on the grid the documented rule picks, its `bool` equals
  completeness, and uncovered points return `false` with 0 and `Covers == false`.
- `sense-area-matches-disk-scan` — 1,500 disks vs per-cell centre-in-disk scans normalized to
  world area, including the seam case that must fall back to the coarse grid.
- `sense-area-conserves-across-grids` — the same aligned box field sensed on a 512² and a 128²
  world grid gives the identical world-area total, equal to the raw fine-grid sum.
- `sense-max-matches-disk-scan` — value and row-major-first position of the disk maximum.
- `sense-gradient-per-world-unit` — central differences per world unit on the chosen grid.
- `sense-exclude-matches-removal` — 120 trials over box, raster, tent, and bell sources on three
  grid scales, including saturated positive and negative piles: excluded point, area, and gradient reads
  equal the reads after `Remove` + `Process`, and `Rewind` restores the originals.
- `sense-exclude-reads-applied-state` — pending moves, same-frame places, stale and bogus ids.
- `warm-sense-allocates-0-bytes` — 20k points × all six sensing calls, 0 B.
- `query-at-matches-deposits` — on a 2^14-over-10000 grid (truncated `ScaleQ8` 419 vs float
  1.6384), `QueryAt` at 200 source positions reads exactly the cell the deposit mapping wrote;
  the float mapping diverges on a measurable subset of them.
- `query-max-matches-full-scan` — across three churn rounds on a 1024² field (two layers, one
  left empty), then a fully covered negative layer, then the same layer pushed to saturation,
  `QueryMax` equals a full `QueryRegion` rescan of the layer, `Query` at the returned cell
  returns the maximum, and repeated calls return the identical value and position.
- `query-max-region-matches-scan` — through three churn rounds on a 256² field, 40 random
  rectangles per round plus full-grid, tile-straddling, single-cell, empty, and out-of-range
  rects: the region `QueryMax` equals the per-cell rescan of the rect, the returned position
  lies inside the rect, `Query` there returns the maximum, and repeated calls are identical.
- `gradient-matches-central-differences` — through three churn rounds on a two-grid world
  (native and 2× scales), `QueryGradient` at random points equals the ±1-cell `Query`
  differences on both grids' own cell mappings.
- `changed-tiles-match-drain` — on a 64² grid, the changed-tile set after each `Process`
  equals the tile footprints of that window's place/move/remove operations exactly (count,
  membership, no duplicates), stays layer-isolated, empties on idle `Process` and `Clear`, and
  includes tiles that published to zero and left the map with published content. The
  byte rule itself — cancelled deposits report nothing, emptied tiles report exactly when
  their old page held content, and the sweep and derived paths agree — is held by
  `ChangedBytesTests`.
- `deferred-window-matches-stepped-processing` — three worlds of 90 mixed mutations applied
  with one `Process` at the end produce bit-identical pages to the same sequences stepped
  through a `Process` per mutation; and a place-200 + remove-200 window collapses to an empty
  field with zero tiles, zero pages, and an empty changed list.
- `tent-matches-impulse-oracle` — 90 tent sources of eight support widths through three churn
  rounds (place/move/remove, signed gains, sub-cell phases) match a per-cell
  second-order-impulse oracle — each source's per-axis slope-break impulses reconstructed to
  `Wx·Wy`, scaled, and rounded once — bit-exactly on two grids at scales 1 and 0.5, including
  exact zero beyond the support inside shared tiles.
- `bell-matches-paraboloid-oracle` — 90 bell sources of seven support widths through three churn
  rounds (place/move/remove, signed gains, sub-cell phases) match a direct separable-product
  oracle — each source's per-axis paraboloid weights `curve·(h²−e²)` multiplied per cell,
  scaled, and rounded once — bit-exactly on two grids at scales 1 and 0.5, including exact
  zero beyond the support inside shared tiles.
- `source-slots-reuse-and-stale-handles-inert` — 2000 place/remove pairs keep slots bounded,
  the recycled id differs from the stale one, and stale `Move`/`SetGain`/`Remove` leave the
  field bit-identical.
- `fifo-free-ring-delays-slot-reuse` — with 64 free slots and 16 live slots, 127×64 remove/place
  churn cycles pop slots in strict FIFO order and never alias a stale id.
- `rewind-restores-recorded-state` — after a two-window journal mixing a remove, a slot
  reoccupation, moves, gain changes, and a new place across box and tent stamps, `Rewind` +
  `Process` restores the recorded field's whole-layer sum bit-exactly; rolled-back ids stay
  inert (the field is unchanged by mutating them) while the revived source's original id
  mutates the field again, and a second `Rewind` with no checkpoint is inert.
- `signed-gain-exact` — ± gains add and subtract exactly, `SetGain` crosses zero and clamps at
  ±16, and removing a signed pair restores the zero baseline.
- `multi-layer-churn-matches-scans` — 16 layers × 30 sources through three churn rounds
  (world-total fan-out) match full, partial, and random-rect scans per layer.
- `saturated-sum-clamps` — saturation sticks at ±32767 after summation.
- `cross-grid-sums-conserve-world-integral` — the same box sources summed over four grids at
  scales 2/1/0.5/0.25 scale exactly by cell area (`full == 4·fine == 16·half == 64·quarter`).
- `sub-cell-kernels-reach-their-support` — tents and bells of widths 1–3 on grids at scales
  1/2, 1/4, and 1/8, swept across a tile boundary in 17/256-cell phase steps: every cell of the
  neighbourhood equals the independent kernel oracle and every excluded read there equals the
  read after removal — a kernel narrower than a cell reaches the cell beside its box footprint
  through its one-cell minimum half-width, which 0.6 dropped across tile edges and left out of
  exclusion.
- `derived-layers-match-cell-formulas` — eleven derived recipes (weighted sums with and without
  shifts and saturating weights, the one-input form, `Min`, `Max`, two `Mask` bands, and chains
  of derived inputs) over four grids through five churn rounds of mixed box, raster, tent, and
  bell sources: every cell equals the formula applied to the inputs' cells, the full and random
  rect sums equal scans, `QueryMax` (full and rect) equals the scan maximum at a cell holding
  it, and every layer's changed tiles — base and derived — equal the tiles whose oracle-computed
  bytes differ from the previous round's.
- `derived-exclude-matches-removal` — 60 trials over five derived layers (including a chain and
  saturating piles): excluded point, area, and gradient reads equal the reads after
  `Remove` + `Process`, and `Rewind` restores the originals.
- `derived-layers-follow-sources-rewind-and-clear` — `Place` on a derived layer returns `-1`;
  invalid recipes throw; a recorded window rewinds the derived page exactly; `Clear` empties it
  and keeps the recipe; and derived layers created after their inputs hold pages — one reading
  another derived layer — equal their formulas after the next `Process`.
- `turned-and-round-stamps-match-oracle` — 30 turned, scaled, signed sources drawn from a
  21-stamp palette of turned boxes, rasters, tents, bells, and disks, cones, and domes with
  arcs, on three grid scales through three churn rounds of turns, scales, moves, gains, and
  replacements: every cell of every grid equals an oracle that evaluates the spec over the whole
  grid (so a bounds bug that skips a cell fails it).
- `round-kernels-share-box-units-and-centre` — disks, cones, and domes of radii 1–128 at gains
  16 and −7: the centre reads `value·gain` exactly, profiles are symmetric along both axes and
  diagonals, never rise outward, and stay inside the radius; every stamp frees afterwards.
- `stamps-turn-and-scale-smoothly` — seven stamp kinds (arcs included) turned through a full
  revolution in 1/256 steps and scaled from 1 to 2 in 1/64 steps: no cell ever changes by more
  than the kernel's continuous slope allows.
- `turn-and-scale-round-trip-exactly` — turning and scaling 24 sources away and back (whole
  turns read 0) restores the field bit-exactly, and a box, tent, and disk at scale 2 equal the
  stamps of twice their size.
- `stamps-wider-than-grid-cover-it` — a box and a raster wider than their grid, near its edge and
  scaled six times, cover every grid cell with the right value.
- `turned-exclude-matches-removal` — 50 trials over turned, scaled, and round sources (and a
  derived layer over them): excluded reads equal removal.
- `fade-steps-match-schedule` — 140 fades (from −16…16 to −16…16 over 1–100 ticks) read the
  spec gain at every one of 104 ticks, and the field is idle once they finish.
- `expire-removes-on-schedule` — expiry on its tick, overrides, cancellation, fade-plus-expire,
  `SetGain` cancelling only the fade, and a recycled slot surviving its predecessor's timer.
- `rewind-resumes-schedules` — a world that records, mutates schedules inside a five-tick window,
  and rewinds stays bit-identical to its twin (which never left the checkpoint) for 45 ticks.
- `changed-since-matches-epochs` — 60 ticks of mixed mutations on three grids and a derived
  layer: `Changed` equals an oracle over the disk's tiles for 2,400 probes, `ChangedTiles` with
  `since` equals the oracle set, and `Clear` reads as a change.
- `sense-nearest-matches-scan` — 1,200 probes over the four-grid sense world at nine thresholds:
  value, world position, and completeness equal a full disk scan on the picked grid.
- `sense-columns-match-scalar` — `SenseColumn` (with `excludes`), `SenseGradientColumn`,
  `SenseAreaColumn`, `SenseMaxColumn`, and `SenseNearestColumn` match their scalar `TrySense*`
  counterparts value-for-value and mask-bit-for-mask-bit, and allocate 0 B warm.
- `warm-derived-process-allocates-0-bytes`, `warm-turned-process-allocates-0-bytes`,
  `warm-fade-process-allocates-0-bytes` — steady-state frames with derived layers, turning and
  scaling sources, and fade/expire/replace churn allocate 0 B.
- `naive-grid-matches-gi` (+ `naive-grid-matches-gi-after-churn`, under `benchmarks --compare`)
  — a managed dense-grid reference rebuild (clear + redraw every source, clamp on read) produces
  bit-identical output to Gi before and after churn; the same command then times both.

`--timing` adds min-over-20-rep lines for unchanged, incremental, move-200 churn, place-200
churn, a 200-place window rewound and reprocessed (~90 µs — the inverse window costs the
same deposits as the forward one), a place+remove-200 collapse window (~4 µs — the mutations
apply no deposits), tent-200
churn (16×16 tents into persistent `int64` sum stores — ~110 µs), bell-200 churn
(16×16 bells, same stores — ~150–195 µs), full-grid
sum, a 1022² partial-region sum, the best-cell query against a one-million-call
naive scan (0.1 µs vs ~5,600 µs on a 1024² layer), the gradient query (~5 ns per point), a
16-layer × 25-dirty move-400 process,
256² region reads (4000 sources, 1024² grid), and the feature rows: the best cell of a derived
`food − 2·threat` layer against two `QueryRegion` reads plus a scan (asserted equal), derived
upkeep on a churn frame, turning 200 vision cones, moving 200 domes, scaling 200 tents, 2,000
fading sources per tick, `TrySenseNearest`, and `Changed`. Timing receipts live in
the README (deposit vs re-emitted marks, i9-14900K; perf-pass deltas, Ryzen 5 8500G); the perf
pass behind them was `perf`-profile guided, receipts first.

### Fuzzing

`dotnet run --project tools/fuzz -c Release -- --fuzz <minutes> [--seed N] [--out directory]` runs
generated command streams (gate, heavy, kind-biased and transient profiles over every precision
mode) against the interpreter until the time is up. A mismatch is minimized with the ddmin
minimizer and written as a ready-to-paste `[Fact]` under `--out`; the exit code is 1 when any
stream failed. The tool compiles the interpreter, generator, runner and minimizer straight from
`tests/Gi.Tests`, so it cannot drift from the differential tests. Its first four-minute run found a
real divergence (an exclusion by the id of a source whose removal was pending, after another source
reused its slot in the same frame, excluded nothing or the wrong deposit); the engine now resolves
the deposited generation through the pending snapshot, and `FuzzRegressionTests` keeps the stream.

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
- `GC.GetTotalAllocatedBytes` is process-wide and includes runtime background threads (an 8,200 B
  step appeared in a P0-5 scratch harness from that counter while Gi's own thread allocated 0 B).
  Always measure warm-path allocations with `GC.GetAllocatedBytesForCurrentThread()`.

### Frame harness

- `dotnet run --project benchmarks -c Release --no-build -- --frames <B1|B2|B4|C|W|all|verify>
[--precision exact|cell] [--measure N] [--warmup N] [--entities N] [--seed N] [--jit-ms N]` measures
the reference frames of ROADMAP.md §1 on one thread. `verify` runs only
the harness receipts (also wired into `--verify`).

Frames, as built on `main` (one engine, no transient layers):

- `B1` — the `--budget` canonical frame of `snapshot-bucket-index`: 1M boxes 8×8 on a 4096² grid, gains
  ±1, every source shifts 0.5 × 0.25 cells per frame; `Sync`, `Process`, `SenseColumn`,
  `SenseGradientColumn`, `ChangedMask`. Stages: sync, process, point, gradient, changed.
- `B2` — the same 1M sources, no reads; 1 mover and 200 movers (distinct sources, 0.5 × 0.25 shift,
  `Move` then `Process`). `B4` — the same sources, `Process` only.
- `C` — emulated with 1M persistent 4×4 boxes on a 1024² grid, synced in Morton order every frame
  (0.1 cell per frame), `Process`, value and gradient for every agent, 1,000 `TrySenseArea` at r = 16.
  The arrays are re-sorted by Morton key every 128 frames; the re-sort is timed apart and amortized
  (`resort.p50_ms_with_resort`).
- `W` — 1M persistent sources on a 2048² grid placed in Morton slot order: 250k agents (4×4 boxes, 0.1 cell
  per frame), 740k static (4×4 boxes on layer 0, 6×6 tents on layer 1) and 10k timed (fade and expire over
  100 frames, 100 replaced per frame); layers boxes, tents and their `Sum`. Per frame: `Sync` of the agents,
  1,000 spawns and 1,000 despawns (100 of them timed), `Process`, point and gradient senses for the agents on
  the derived layer, 1,000 `TrySenseArea` at r = 8, 16, 24, 32.

Agents are kept inside the grid with a margin so every disk sense is complete. Warmup is 30 frames and
measurement 300 frames; p50 and p99 are nearest-rank. Before the timed run the harness runs the code on a
20k-source world for `--jit-ms` so no hot method is still in tier-0.

Output is JSON lines with stable key order and invariant culture: one header line (CPU model, runtime,
intrinsics, receipts) and one line per frame (`p50_ms`, `p99_ms`, `p50_ns_per_entity`, `stages`,
`resort`, `bytes_per_frame`, `crossing_cell`, `crossing_q8`, `checksum`, `ok`). `crossing_cell` is the share
of movers whose cell changed per frame, `crossing_q8` the share whose 1/256-cell anchor changed. The
exit code is non-zero when a read was incomplete or a measured frame allocated.

Pinning: list the cores with `lscpu -e=CPU,CORE,MAXMHZ`. On the Ryzen 5 8500G the Zen 4 cores have the
higher MAXMHZ and the Zen 4c cores the lower; on a hybrid Intel part the P-cores do. Run
`taskset -c <cpu> dotnet run --project benchmarks -c Release --no-build -- --frames all`, keep the
hyper-thread sibling of that CPU idle, and compare `cpu_start` and `cpu_end` in the output. Repeat each
run in fresh processes; do not build while a timing run is in progress.

Receipts, run before any timing: percentile rank, culture-invariant JSON, Morton sort order and
permutation, and for every frame at 20k sources — two identical runs give identical checksums, warm frames
allocate 0 B, every read is complete, the driver's positions equal `TryGetSourceSpec` for every source, `W`
ends with exactly its entity count of live sources, and the field of every layer equals a fresh world built from the final
sources.

Unsafe proof for the harness buffers. Lifetime: `FrameArena` owns every native block; blocks are allocated
in `Setup` and freed by the scenario's `Dispose`, and no pointer outlives its scenario. Gi receives the
pointers only for the duration of one call and keeps none. Aliasing: each array is its own allocation;
the re-sort writes into `*Next` buffers and swaps the pointers between frames, never during a Gi call,
and the radix passes ping-pong between `pairs` and `scratch`. Alignment: `NativeMemory.AlignedAlloc` at
64 B, elements at most 8 B wide. Bounds: allocations are rounded up to a multiple of 16 elements and
zeroed, so the 8-byte hash loop over `Padded(count) * sizeof(T)` bytes stays inside the block and reads
zero padding; mask arrays hold `(count + 63) >> 6` words; the radix histogram has 2,048 entries for 11-bit
digits. Concurrency: single thread.

## Unsafe proof

- **Lifetime**: the world arena (255 `WorldCtx`) and stamp catalog (256 `StampVariant`) are
  process-lifetime pools allocated by the first creation call (`World.New`, `Stamp.New`,
  `Stamp.Box`) — never by static construction, so no static constructor on the assembly performs
  calls. This does not make mutable Gi statics callable from Burst through a precompiled DLL;
  Unity records with Burst and invokes Gi from non-Burst code. `World.Teardown` is the pools'
  release: it is refused — it returns `false` and touches nothing — while any world is live
  (the same `Grids != null` slot scan `CopyWorldIds` reads), and otherwise frees every stamp's
  raster and mips, both pools, the shared zero page, and the revision array, and returns the
  world count, the catalog count (id 0 stays reserved), and `NativeHeap.Calls` to their pristine
  values, so the next creation call performs the same `Ensure` a fresh process would. The
  precondition is exactly the absence of live worlds: stamps outlive worlds by design and are
  torn down with the catalog, not refused; every world-scoped block, kernel cache, and taps
  handle dies with its world before teardown can run, and no pointer into freed storage is
  handed out in between because pools, payloads, and counters are reset in the same call. The
  one counter that never resets is the revision floor: `Release` folds the freed array's maximum
  into it and the next `Ensure` seeds every slot with it, so revisions stay increasing across
  teardowns and a token captured before a teardown never validates after the restart. Calling
  `World.Teardown` before any creation call returns `true` without allocating. World contents are
  `NativeHeap` blocks owned by the context: the grid array, the layer recipe table, the changed-slot
  list (`Changed`, `int` slots), the `Pending` and `Journal` buffers of 24 B snapshots — `Pending`
  takes one snapshot of a slot's columns at its first mark of a window and is cleared by
  `Process`, `Journal` receives each slot's first snapshot after `Record` (and the pending ones at
  `Record`), is consumed by `Rewind` and cleared by `Record`, `StopRecording`, and `Clear` — the
  schedule journal, written on the first schedule change of a slot after `Record`, and
  the timed-slot list and its due scratch (reset by `Clear`, capacity retained), and `SourceColumns` — hot columns in
  16,384-slot chunks (positions, stamp, layer, gain, angle, scale, liveness, and the 32-bit
  generation and mark), the two record-epoch stamp columns (hot state and schedule state), the
  schedule columns (timed, fade bookkeeping, expiry, timed-list index) kept
  separately as plain buffers, and the free-slot ring. Per grid there is one `DepositRecord`
  buffer of 16 B per source slot, grown with the source columns and zero-filled, so a fresh slot
  reads "nothing deposited" (`Clear` zeroes it); and per (grid, layer) the layer's directory, its
  `PlaneStore` slabs, its tile-set slab, the dirty flags and tile epochs (allocated together by
  the layer's first marked tile on that grid and kept until `World.Free`), the dirty list with
  its parallel rectangle list, the changed list, and two pyramids built on one generic layout
  (`Pyramid<T, TOp>`: a flat slot array plus per-level offset and side tables, each node an 8×8
  block of its children's maxima, with the combiner a `struct` type argument so the library still
  targets netstandard): the max pyramid (`int16`, allocated zeroed by the layer's first
  published tile and freed by `World.Clear` and `World.Free`) and the epoch pyramid (`int32`,
  allocated with the tile epochs and freed by `World.Free`). Epoch stamps update the pyramid
  along their path and stop at the first ancestor that already holds the tick; `Changed` and
  `ChangedTiles(since)` visit only nodes whose maximum is after `since`, every node access
  clamped to the valid children of its parent. The directory is one dense `int` per tile of the
  grid, −1 meaning absent, allocated zeroed-to-−1 with the layer's first set on that grid and
  kept until `World.Free` (`World.Clear` empties it but retains it). Each live tile owns one
  `TilePlanes` set — five `int` unit indices, a unit of the layer's set slab, addressed only by
  its own unit index and owned by its directory slot — naming its page, `I`, summary and, once
  attached, `T` and `B` units in the `PlaneStore` slabs (2,048 B, 4,096 B, 256 B and 8,192 B
  units); a derived-layer tile takes a page and a summary only. A set and its units return to
  their free lists exactly when the tile's page publishes to all zeros or the world is cleared;
  `T` and `B` are created by the first tent or bell emission on the tile and return with it; a
  slab unit is zero-filled by the taker before any of its bytes are read. Slab regions grow by
  doubling — copying units and freeing the old base — which is safe because every reference into
  a slab is a unit index, never a pointer; free lists recycle indices, so a freed unit is handed
  out again only whole. `World.Clear` returns every unit to its slab's free list and retains the
  regions, so repopulating after a clear makes no heap calls. The shared zero page (an absent
  derived input) is allocated once with the arenas and never written afterwards. The per-world kernel
  cache is fixed-capacity native storage held by value in `WorldCtx`: 32 entries, each owning a
  4,096-`long` tap slot and 128 row-extent `int`s, plus a miss canvas and a row-extent scratch;
  all five buffers are allocated zeroed together by the first insert and released exactly once
  by `World.Free` before the context is zeroed (`World.Clear` keeps entries, because stamps are
  immutable and only a stamp id leaving the catalog can invalidate them). Entry taps are values
  copied out of the canvas, never pointers into stamp storage, so a freed stamp leaves no
  dangling reference; because a freed id can be re-baked as a different stamp, a successful
  `Stamp.Free` clears every live world's cache in the same process-wide scan that already
  serializes catalog mutation. A `KernelTaps` handle points either into its owning entry —
  valid until that slot is evicted by a later insert on the same cache, flushed by
  `Stamp.Free`, or released by `World.Free` — or into the miss canvas, valid until the next
  build on the same cache; callers consume taps before the next build. A kernel whose capture
  rectangle exceeds 4,194,304 cells is refused and the caller emits through the placement
  itself, which is exact by construction. Grid-edge clipping is folded into box extents
  before bands form, so a placement whose extents differ from
  `min(axis × sampling, kind cap)` — the only kernels that depend on absolute position — never
  consults or fills an entry; its taps are captured from its own clipped placement.
  `NativeBuffer` growth zero-fills the newly committed region, so every buffer that reads
  beyond its written length — the source pending column and the timed bytes
  after a column growth, and the freshly added source chunks, which are allocated zeroed —
  reads zeros without a separate clearing pass; `ResizeUninitialized`
  is the audited exception, used only where the caller initializes the whole grown region
  before any read: the slab free lists (push then write).
  The layer recipe table (32 entries per world) is allocated by `World.New` and freed by
  `World.Free`. `Stamp` variants — base samples, mip chain, and box constants — are catalog-owned until
  `Stamp.Free` succeeds (no live source, pending snapshot, or journal entry in any world names the
  stamp, so no emission, withdrawal, rewind, or exclusion can still read it — a deposited record
  outlives its source only in a window whose `Pending` snapshot pins the same stamp), and otherwise for
  the process. `QueryRegion` writes only the caller's
  destination; `ChangedTiles` copies from the changed list (stable between `Process` calls,
  swapped — never written in place — on the owning thread) into the caller's buffer. `World.Free` releases each allocation listed above exactly once on the
  owning thread: every live tile unit back to its slab, then per layer the set and plane slabs (each releasing
  its region and free list), the directory, dirty flags, epochs, dirty, rectangle and changed
  lists, and max pyramid, then the layer array and deposited-record buffer of each grid, the grid
  array, the recipe table, the kernel cache, and every `SourceColumns` and world buffer. It then zeroes
  the context; a context without a grid array is not a world, so `GetContext` returns null for it
  and every handle-taking call reads a freed handle as absent. No pointer escapes an owner: handles
  are bytes and source ids carry a generation.
- **Source columns**: the hot columns (x, y, stamp, layer, gain, angle, scale, liveness,
  generation, mark) live in 16,384-slot chunks. *Lifetime*: each column owns an array of up to 1,024
  chunk pointers, allocated zeroed when the first chunk is added and freed exactly once by
  `World.Free` after every chunk; growth appends one zeroed chunk per column and never copies,
  so a chunk pointer taken during a call stays valid for the world's lifetime. *Aliasing*: chunk
  arrays and chunks are owned solely by the world's `SourceColumns`; borrowed caller columns
  (`Sync`, `SyncTransforms`) are read-only inputs never overlapping them, and the batch planner
  copies column state into its own entries before writing. *Alignment*: every chunk is a single
  `AllocZeroed` block of one element type, so each element sits at its natural alignment
  (float/int/uint 4 B, ushort 2 B, byte 1 B) and no chunk spans a boundary. *Bounds*: a slot is
  addressed as `chunks[slot >> 14][slot & 16,383]`; the `slot < Count` test precedes every
  access, `Count` never exceeds the 2^24-slot ceiling, and the chunk index is therefore always
  below the allocated chunk count. *Concurrency*: growth, reads, and writes happen only on the
  owning thread under the same exclusive-mutation rule as the rest of the world.
  The free ring is one native `int` buffer plus a head and a count, held by value in the world's
  `SourceColumns`: allocated when source columns first grow, released once by `World.Free`, and
  regrown only on the owning thread by allocating the larger buffer, copying the live entries to
  its front in queue order, and freeing the old buffer, so no pointer into ring storage outlives
  a call. Batches pop and push the ring directly through the same `Place` and `Remove` helpers,
  so no copy of the ring exists. `ApplyBatch`
  pins managed input/output spans for its entire call;
  native callers retain their buffers unchanged for the same interval. It retains no input or
  result pointer after returning.
- **Transient layers**: the stale list is a context-owned `NativeBuffer<int>` per (grid, layer),
  appended to by publish on the owning thread, consumed and cleared by the next publish, reset by
  `Clear` and freed with the layer. Publish marks a tile's set stale after computing the tile's
  page and leaves its planes intact, so a self-excluding read sees the exact totals; the planes
  are zeroed through pointers materialized from the tile's own set at the first write of the next
  frame (`PrepareTransientSet`, called before every plane fetch and before a restarted stale tile
  publishes), and the next frame's splats write the zeroed planes through the same emitters as
  persistent deposits (same bounds, alignment and single-thread rules). A tile that is dropped
  (its page publishes to zero) takes its planes with it and never enters the stale list.
- **Histogram path**: *Lifetime*: the per-(grid, layer) `HistState` (class table, 4,096 B page slab,
  active-class list, output-tile list, per-tile mark array, the 63-row window with a 71-`int`
  row stride and the 63×32 strip scratch) is allocated on the first cell-precision splat, owned by
  the layer, emptied by every `Process` (each page returns to the slab free list and each class
  directory entry is zeroed through the class's tile list) and by `Clear`, and released with the
  layer. *Aliasing*: a splat writes one `int` of one page it owns; the flush reads pages through
  directory entries it has not yet cleared and writes `I` planes only through the tile's own set,
  after `EnsureSet`, so no plane pointer survives a slab growth. `SplatBlocks` takes the page
  region pointer only after every missing page of the block has been taken, so a slab doubling
  cannot leave it stale. *Bounds*: a splat lane is applied only when `0 ≤ cx, cy < Size`; the
  directory gather indexes with coordinates clamped into the grid, so it never leaves the
  `TilesPerSide²` directory; the page offset is `(slot − 1)·1,024 + (cy & 31)·32 + (cx & 31)`
  with `slot` read from an entry that was non-zero when the offset was formed. The box filter
  requires kernel `Width, Height ≤ 32` and offsets that keep the window within one neighbouring
  tile on each side, so `GatherWindow` copies row segments that each lie in one page row and
  write at most 63 `int`s per window row; `CopyBlock` and `ClearBlock` move whole 8-`int` vectors,
  so a segment store ends at most 7 `int`s past its span: inside the 8-`int` row padding or in the
  next segment of the same row, which is written later (segments go left to right), and a
  segment load ends at most 7 `int`s past its span in the page, which is the next page row, the
  next slab unit, or the 64-byte tail slack every `FixedSlab` allocates. The strip is
  `windowHeight × 32 ≤ 63 × 32`. *Alignment*: natural `int`, with unaligned vector loads and
  stores. *Concurrency*: owning thread only.
- **Vector column reads**: *Lifetime*: `GradientBlocks` reads the layer's tile directory, set slab
  and page slab and allocates nothing; the three base pointers are loaded once per call, and
  nothing in the call can grow or move a slab. *Aliasing*: reads only. *Bounds*: a block takes the
  gathered path only when all eight lanes are inside the grid. The tile index of every lane, and
  of every neighbour tap that crosses a tile edge (`±1`, `±TilesPerSide`, taken only for lanes
  whose tap cell is inside the grid), is below the directory length. The set index is clamped to
  zero before it addresses the set slab, the page index is multiplied by 2,048 into a byte
  offset, and an absent tile (directory entry `< 0`) addresses byte 0 with its lane zeroed by
  mask. The in-page cell is `0..1,023`, so a tap's offset is at most 2,046 bytes into its page
  and the four-byte gather reads at most two bytes past the page: the next unit or the 64-byte
  tail slack of the slab. The path is disabled when the page slab exceeds `int.MaxValue` bytes.
  *Alignment*: gathers take unaligned byte offsets with scale 1. *Concurrency*: none beyond the
  serial-per-world rule.
- **Spatial order**: *Lifetime*: `SpatialKeys` and `SortByKeys` borrow the caller's spans for the
  duration of the call and keep nothing; the sort's histograms (3 × 2,048 `int`) are stack memory.
  *Aliasing*: the key vector loop reads `xs` and `ys` and writes `keys` through pinned spans that
  may not overlap; `SortByKeys` reads `keys` and writes `permutation` and the first `2 n`
  `ulong`s of `scratch`. *Bounds*: every block load or store covers indices `i .. i + 7` with
  `i + 8 ≤ min(xs, ys, keys)`; the scalar tail takes the rest; histogram indices are masked to
  11 bits (the top digit is `key >> 22 < 1,024`), and prefix sums give each bucket a slot
  inside `[0, n)`. *Alignment*: unaligned vector loads and stores. *Concurrency*: none.
- **Plane folds**: *Lifetime*: a fold takes up to five `int64` units from the world's `Int64s`
  slab (`TakeWide`), records their index plus one in the tile's summary block (zero means none),
  and returns them with the tile (`PlaneStore.Give`) or when a transient tile is retired. *Aliasing*:
  a fold reads and zeroes the live `I`, `T` or `B` plane of one tile and adds into units that
  belong to that tile alone; every plane pointer is recomputed from its unit index after the
  fold, because taking a unit may double the slab and move it (`IPlane` and `QPlane` charge
  first and fetch last). *Bounds*: all loops cover the 1,024 cells of one unit; the bound
  counters live in the tile set, are written only by the charge, and are zero in a fresh set.
  *Alignment*: units are 64-byte aligned. *Concurrency*: owning thread only.
- **Field view**: *Lifetime*: a `FieldView` holds the layer and grid constants it was built from
  and one cached page pointer; both name directory and slab storage that only `Process`, `Clear`,
  `Rewind` and `World.Free` can grow, move or release, so a view must not be used across them (a
  stale page pointer is the one hazard, and it is confined to the caller's read phase).
  *Aliasing*: the view only reads published `int16` pages. *Alignment*: page rows are 64 B
  aligned and every access is a naturally aligned `int16` load. *Bounds*: every cell read is
  preceded by `(uint)x < size && (uint)y < size`, the tile index is below the grid's tile count,
  and the gradient fast path reads `at ± 1` and `at ± 32` only when the local cell is in
  `[1, 30]²` of its page, so no load leaves the 32×32 page. *Concurrency*: none beyond the
  serial-per-world rule; readers never share a view.
- **Aliasing**: each tile's planes are written by emissions and published in place on the
  calling thread; `I`, `T` and `B` writes land on their own tile, and publish's `tentOut` and
  `bellOut` rows are stack-allocated per call; sources are read-only during emissions of other
  sources. Directory, slab, and scratch mutation happens only through
  its owning `LayerData` or `WorldCtx` pointer on the thread that called `Process`; every
  pointer derived from a slab (`Unit(index)`) is recomputed from the index at each use, so a
  doubling that moves the region cannot leave a stale pointer in the set slots, which
  hold indices, or in any local across a `Take`. A derived recombination reads input pages (and
  the never-written shared zero page) and writes only its own layer's tiles, pyramid, and
  dirty list; its inputs have lower ids and are complete before it runs, and it never runs
  concurrently with a publish. The partial-region sum reads page rows only
  inside `[0,32)×[0,32)` of a live page and writes nothing. Pyramid updates alias nothing
  outside their owning `LayerData`: each dirty tile touches exactly its own level-0 slot, and
  the propagation walk reads one 64-slot node plus one parent slot per level; `QueryMax` reads
  pyramid slots, directories, and live pages only. Node scans honor the node's valid child
  rectangle, so no slot past the grid's tile count is read as data; the `Vector128` row compare in the
  descent may load past the valid prefix of a row but masks those lanes out of the match, and
  the loaded bytes stay inside the 128 B node, while `FirstEqualFlat` and `FirstEqualSpan` loop
  only over full 16-lane or 8-lane vector steps and finish any sub-vector remainder with scalar reads.
  Columnar sensing (`SenseColumn`, `SenseGradientColumn`, `SenseAreaColumn`, `SenseMaxColumn`,
  `SenseNearestColumn`) borrows caller input and output columns only for the synchronous call,
  with `SenseRange` and `GradientRange` hoisting the world and, for a single-grid world, its
  origin, scale, and layer pointer out of the per-row loop — the row math (Q8 conversion,
  directory probe, page extract, interior-margin mask bit) is identical to the per-agent form,
  validates non-null pointers when `count > 0`, clears `(count + 63) >> 6` mask words when an
  optional mask pointer is supplied, and writes only indices `0..count-1` of the output columns.
  Batch input bytes and the written result prefix must be disjoint; overlapping spans are
  rejected before any writes. One batch row owns each distinct source slot, including all
  remove/recreate operations on that slot. Preparation writes only its row's private state.
  Each batch command applies on the calling thread in order, so source columns, the mark
  column, and the changed list have one writer.
  A slot is in the free ring at most once: it is pushed only by the alive-to-dead transition
  (`Remove`, an expiry that comes due, a batch `Remove`, or the ascending rebuild of `Rewind`
  over the slots already dead) and popped only by the dead-to-alive transition of a `Place`, so
  the entries from head through head plus count (modulo the ring size) are distinct slots. A kernel build reads one placement and its stamp
  variant read-only, emits through the tile-bake emitters into its own stack tile planes, and
  folds the result into the cache-owned canvas through a local copy of the placement whose
  tile rectangle is the unclamped footprint; nothing in a tile, page, or slab is written.
- **Alignment**: slab regions, directories, and native buffers use 64-byte aligned allocations. A plane
  slab's stride is a multiple of 64 B — 2,048 B pages, 4,096 B `I` planes, 8,192 B `T`/`B`
  planes, 256 B summaries — so every unit base off the 64 B aligned region base is itself 64 B
  aligned; the set slab holds 20 B `TilePlanes` of five naturally aligned `int`s; and directory
  entries are whole aligned `int`s. World/grid/layer/catalog metadata uses `AllocZeroed` with
  natural alignment; raster samples require only byte alignment. SIMD accesses use unaligned
  load/store semantics. The AVX2 publish forms eight `int32` totals per group from unaligned loads
  of `I` and the rounded rows, packs them with saturation, permutes the 64-bit chunks into cell
  order, and stores eight `int16` values; groups cover exactly cells 0–31 of a row and never read
  beyond the 32-cell plane and page rows. The box emitter's eight-lane row add loads and stores
  eight `int32` at `plane + row·32 + column` with `column ≤ 24` and `row < 32`, so the access ends
  inside the 128 B row, and it uses unaligned-safe semantics. Raster taps widen unaligned 4-byte sample reads within
  the padded allocation. Pages are 64 B aligned units, so page rows are
  64 B aligned, and `QueryRegion`'s vectorized copy/zero fills use unaligned-safe 256-bit (or
  128-bit) stores with scalar tails, so any caller destination alignment is correct. Partial
  region sums widen page rows to `int32` vectors with the same unaligned-safe loads and scalar
  tails; a strip is at most 32 shorts per row, so the `int32` row accumulators and per-tile
  horizontal add cannot overflow (32 · 32768 < 2^20), and the result joins the `int64` region
  total. Derived combines
  load and store 16 or 8 cells per step at cell offsets that are multiples of the vector width
  from a 64 B aligned page, through unaligned-safe loads and stores. Max
  pyramid nodes are 64 `int16` values at 128 B offsets inside a 64 B aligned buffer, so the
  full-node AVX2 max loads stay inside the allocation; partial nodes scan scalar. The `T` and `B` units are 8,192 B slab units of `int64` cells at a
  32-cell pitch, so every element is naturally aligned; they are only ever read and written by scalar
  8-byte accesses. On
  netstandard2.1 (Unity) `NativeHeap` backs onto `Marshal.AllocHGlobal`
  with platform-natural alignment instead of 64-byte `AlignedAlloc`, and no SIMD paths compile:
  every access in that build is a naturally aligned scalar load or store, so the weaker
  alignment guarantee cannot be observed.
  Managed command/result arrays have their normal CLR alignment. Native spans must point to
  naturally aligned `SourceCommand`/`SourceResult` storage; no SIMD loads depend on their address.
  Borrowed `TransformRange` columns follow the same rule at their real widths — `int` ids,
  `float` positions/headings/scales, `ulong` mask words — with no per-row packing or decoding.
  The free ring is an
  `int` buffer from the same aligned allocator (natural alignment on netstandard2.1), read and
  written only through scalar naturally aligned `int` accesses. The kernel cache's entry, tap,
  row-extent, and canvas buffers are `NativeBuffer`s from the same 64-byte aligned allocator;
  tap rows are read and written as naturally aligned `int` (an integer kernel's cells share its
  long slot's alignment) or `long` scalars, and row extents are whole `int` pairs. The
  builder's per-tile scratch — a 33×33 difference plane, a 32×32 dense plane, a 32×32 Q40
  plane, and a 32-int carry row — is stack-allocated per call and cleared per tile.
- **Concurrency**: `ApplyBatch`/`Sync`/`SyncTransforms`/`Place`/`Move`/`SetGain`/`Remove`/`Process`/`Record`/`StopRecording`/
  `Rewind` are serial per world; different
  worlds may run on different threads. `ProcessAll` is that serial-per-world rule walked over
  the allocated ids, ascending, on the calling thread — it adds no parallelism claim, it only
  packages the loop, and freed slots read as absent to it exactly as they do to `Process`.
  World creation, `World.Free`, `World.CopyWorldIds` (which reads the world count and each
  slot's `Grids` pointer), `World.ProcessAll`, `World.Teardown`, and stamp catalog mutation (`Stamp` creation
  and `Stamp.Free`, which scans every world for references) are process-wide scans:
  serialize them with one another; `World.Teardown` frees the arenas every one of those scans
  walks, so it must not overlap any other Gi call at all — its precondition, no live world,
  already excludes the per-world calls, and what remains to exclude is creation, stamp
  mutation, process-wide scans, and reads through handles that predate the teardown.
  `Grid.Count`, `Grid.CopyIds`, `Grid.TryGetSpec`,
  `Layer.Count`, and `Layer.CopyIds` read the owning world's counts and grid records —
  per-world reads under the same serial-per-world rule as that world's other calls.
  `World.Memory` is a per-world read of the same kind: it walks the world's grid records,
  store slabs, kernel cache and source columns without writing anything, so it is safe on any
  thread that already respects that world's serial rule, and a value read while a writer or
  `Process` runs on that world is torn, not protected — no lock is taken by design.
  `World.TryGetSchedule` is a one-slot read of the same kind: it probes the source columns'
  gain and schedule fields without writing anything, under the same serial-per-world rule.
  Mutations only write the owning world's source columns, mark its changed list, and snapshot into
  its `Pending` buffer; the mark column is the one writer-side dedup. `Rewind` runs entirely on
  the owning thread: it writes source columns, marks restored slots on the same changed list, and
  rebuilds the free ring in place, and the withdrawals and deposits that undo the window reach the
  field only through a later `Process`. `Process` sweeps on the owning thread: it walks the
  changed list in mark order, forms each slot's target record, compares it with the deposited
  record of every grid, performs every directory admit, slab take, and `T`/`B` attach, writes the
  planes and dirty rectangles, overwrites the records, appends `Pending` to the journal when
  recording, and then publishes every dirty tile on the same thread — emissions therefore mutate
  planes only on the owning thread, never concurrently with publish. Publish runs sequentially:
  tiles that publish to zero perform their own directory drop and slab give-back and leave the
  dirty list inside the same pass, and the dirty lists are then walked again, on the owning
  thread alone, to fold each tile's page max into its max pyramid. Warm `Process` and warm reads
  make no `NativeHeap` calls at all: slabs, directories, and every list are sized by grow-to-max
  before steady state, and the hook counter (`NativeHeap.Calls`) is asserted flat by a test
  across warm frames that move and read. Kernel-cache builds run on the owning world's thread
  inside `Process` — the sweep and publish write paths on a cache miss — reading the placement
  and the stamp variant read-only and writing only cache-owned buffers and their own stack; the
  `Stamp.Free` flush rides the process-wide catalog scan already serialized above. Entries and
  canvas are allocated by the first insert, so warm frames after the first miss make no
  cache-driven heap calls either. Sensing never builds: its cache use is a read-only
  `TryFindTaps`, and a miss recomputes from the stamp into stack scratch, so reads add no
  inserts and no canvas traffic.
  Determinism is unaffected: tiles are
  independent, arithmetic is integer-exact, the dead set is a pure function of page content, and
  the order of removals has no observable effect on map contents. Warm `Process`
  allocates 0 B: every buffer it touches is sized by grow-to-max before steady state. World creation, `World.Free`, and stamp catalog mutation (creation and `Stamp.Free`, which
  scans every world's source columns, pending snapshots, and journal) are also serialized
  across worlds because their arenas and counts are process-wide, `Stamp.Free` must not
  overlap any world's mutation or `Process`, and `World.Free` must not overlap any call that
  takes the freed handle. Arena initialization rides the
  same rule: the first creation call allocates both arenas before any handle exists, and handles
  only originate from creation calls, so no query can observe an uninitialized arena.
  `Fade`/`Expire`/`Turn`/`Scale` are mutations like `Move`; timers run on the owning thread at
  the start of `Process`, and epochs are written on that thread after the publish. `Query`/`QueryRegion`/`QueryMax`/`QueryGradient`/`ChangedTiles`/`Changed`/
  `Covers`/`Tick`/`TrySense*` read resolved pages, page sums, page maxima, tile sum stores,
  pyramid slots, changed lists, tile epochs, source columns, and deposited records only, and
  may run concurrently with each other,
  never with mutation or process. Sensing writes nothing outside its own stack frame: each call
  stackallocs its scratch (span rows, under 9 KB of per-tile arrays for an excluded area,
  a single cell for an excluded point), so concurrent sensing threads never share scratch. An
  excluded read's kernel use is a read-only `TryFindTaps`; a miss recomputes the excluded
  contribution from the stamp through `BoxAt`, `EmitRaster` or the smooth weights into that
  same stack scratch, so no read reaches the cache's entries, taps, canvas, or insert budget,
  and any number of concurrent exclusion readers observe only resolved pages.
  Column rounds run one serial loop per call: the world is read-only under the
  serial-per-world rule, inputs are read-only, and the value and mask outputs are written at
  disjoint indices with natural alignment for `float`, `short`, and `ulong` elements, each
  accessed whole.
  Rejected column reads (`SenseColumn`, `SenseGradientColumn`, `SenseAreaColumn`, `SenseMaxColumn`,
  `SenseNearestColumn`, `ChangedMask`) write their outputs before returning on a world that is not
  live, a layer out of range, or a null required column. Every non-null output column and mask gets
  the rows an unfillable row would hold in the per-row `TrySense*` form: zero values, gradients,
  totals, best/nearest positions, and mask words; `short.MinValue` for nearest values. The return value is unchanged, and nothing is
  written when `count <= 0`. Lifetime and aliasing: the writes run on the calling thread alone
  over exactly `count` rows and `(count + 63) >> 6` mask
  words (the word count is formed in unsigned arithmetic, so it cannot overflow), the same extents
  the accepted path writes. Alignment is natural alignment of
  `float`, `short`, `long`, and `ulong`, each accessed whole. There is no concurrency: the
  calling thread is the only one to hold these pointers on a rejected call.
  Source-batch application runs on the calling thread, command by command, and only the
  caller changes a heap, journal length, free ring, source count, or directory, between commands.
  Hosts may record independent buffers while
  Gi readers run; they must join producer writes and Gi readers before `ApplyBatch` or `Process`.
  The sweep reads the changed list, the source columns, and the deposited records by slot
  index. *Lifetime*: `Changed` is a context-owned buffer that grows only on the owning thread,
  outside the sweep (marks append before `Process` starts and the sweep clears the list after
  its last slot); record buffers are sized with the source columns, so `slot < Count` implies
  the record exists on every grid; plane pointers are materialized per tile from unit indices
  after the `EnsureSet` that may have grown a slab, and none is held across a `Take`.
  *Aliasing*: a record is read and overwritten by the one slot that owns it; an emitter writes
  only the `I`, `T` or `B` unit of the tile it is visiting and that tile's rectangle entry.
  *Bounds*: the placement clips every emission to `[0, Size)` before tiles are visited, and
  bands and row extents are clipped to the tile, so box bands satisfy `0 ≤ lo < hi ≤ 32` on
  both axes; the eight-lane row add starts at `column = min(lo₀, 24)` and a narrow tile has
  `hi_last − column ≤ 8`, so its lanes lie inside the row; tap emission reads `Width×Height`
  taps and `2·Height` row extents inside the entry or the miss canvas that holds them. The
  prefetch reads only the source columns of slot `i + 8 < count`, range-checks the tile cell
  against the grid before the directory probe, and issues `Prefetch0` on addresses inside that
  tile's `I` plane (rows `< 32 − row₀`, columns `≤ 31`); a prefetch never faults and writes
  nothing. *Alignment*: every access is a naturally aligned `int`, `short` or `byte` load or
  store except the unaligned-safe eight-lane row add. *Concurrency*: one thread, no statics.
  The single-tile narrow box emitter runs only when the clipped footprint equals the placement's
  footprint, lies in one tile, spans at most eight cells, and its leading and tail bands do not
  share a cell; it then needs no `ClipBands`. Its two eight-`int` `stackalloc` buffers are fully
  stored by `LaneWeights3` and `BandLanes` (one 256-bit store each) before any read, so
  `SkipLocalsInit` leaves no unwritten lane readable; `localX + cellsX ≤ 32`, `localY + cellsY ≤
  32` and `column = min(localX, 24)` keep every eight-lane access inside the 32-`int` row, and
  the rectangle it grows is exactly the one the general emitter grows for the same bands. The
  vectorized tile summaries load only the 32 `short`s of a row (`row = page + y·32`, `y < 32`,
  unaligned-safe loads over 64 B aligned pages), `FirstEqual` is called only on a row whose
  maximum equals the searched value so it always finds a lane, `RescanMax` reads exactly the
  32 `RowMaxima`, and the summary update uses full-row maxima over the rectangle's rows, which
  gives the same maximum and first argmax as the rectangle alone because every cell outside the
  rectangle is unchanged, at most the old maximum, and at or after the old first argmax.
  The module skips local zero-initialization (`SkipLocalsInit`): every `stackalloc` buffer is
  written before it is read — band and span arrays are filled up to the count their readers
  use, the lane buffer is cleared before each row band, cell scratch is cleared or fully emitted
  first, and the publish rows are cleared when their store is absent.
  The free ring is read and written only by the world's owning thread — `Place`, `Remove`,
  expiry inside `Process`, `Rewind`, `Clear`, and batch `Place` and `Remove` — inside the
  serial-per-world rule. No other phase touches the ring, and readers never do.
- **Bounds**: source batches reject more than 2^24 commands before multiplying byte lengths.
  Creation references name earlier `Place` indexes, so results are initialized before those
  references are read. Generation and liveness validation precede source-column access. Source capacity is
  capped at 2^24 slots. Layer and stamp validation precede slot allocation, and column growth
  only appends chunks, so no earlier slot pointer moves. Result writes stop at the command count.
  Derived recombination reads exactly the 1,024 cells of each input page — the
  shared zero page holds 4,096 B, so an absent input reads zeros in bounds — and writes the
  1,024 cells of its own page; `int32` totals stay inside `±2^31` by the weight bound above.
  Stamps clip to grid rects before marking (extents clamp to the grid size in Q8;
  `AddGrid` rejects a `ScaleQ8` outside `[1, 2^20]`, and the `ScaleQ8 == 0` guards on the read
  and deposit paths are defensive); tile-local box bands land inside `[0,32)×[0,32)` of the `I` plane. Tent and
  bell deposits write only cells `[max(First, tile), min(Last, tile + 31)]` per axis — inside
  `[0,31]×[0,31]` of their 32×32 planes — and publish reads rows 0–31 and writes
  `tentOut`/`bellOut` 0–31.
  Turned and round emission visits only cells inside its placement's bounds — computed from the
  turned support plus one cell of anti-aliasing margin (plus one raster sample for turned
  rasters, whose bilinear taps reach one sample past the rectangle) and clamped to
  `[−1, Size + 1]` — intersected with the tile; turned raster taps require the sample index in
  `[−1, w−1] × [−1, h−1]` at the selected mip level, so both taps stay inside the padded level.
  Raster emission clips to
  the tile and reads only inside the padded stamp allocation: sample coordinates advance by
  `256·65536/ScaleQ8` per cell in Q16.16 from a phase-derived origin, so at any mip level `L` the
  integer sample index stays within `[(−1), ceil(w/2^L)]` and every `+1` tap lands on the level's
  zero border. Level selection stops at the stamp's mip count, so tiny stamps sample level 0 with
  a wider step rather than reading past the chain.
  Sensing clips every disk to `[0, Size)` before touching a tile, computes spans in `int64` Q8
  (reach clamps to 2^24 Q8 so `reach²` cannot overflow), and reads sum stores only at rows
  `0..31`, columns `0..31`. One-cell source evaluation writes one `int` on the stack:
  `EmitRaster` with `x1 = min(x1, cx+1)` clips its loops to that single cell. Sum stores
  accumulate in `int64` with wrap-around; only each cell's final sum must fit, which the
  4,096-kernel stacking bound above guarantees. The free ring's size is never below the source
  column capacity: every column growth reserves the ring to the new capacity, and a batch place grows
  it to the grown capacity. A slot is in the ring at most once, so the
  count never exceeds the number of slots and stays below the size at every push; the tail
  `head + count` is below twice the size and one conditional subtraction maps it into
  `[0, size)`, and a pop wraps `head + 1` to 0 at the size.
- **Finite inputs**: `Sync` and `SyncTransforms` read `xs[i]` and `ys[i]` once per row on the
  calling thread, before the finiteness test. Lifetime: the borrowed columns outlive the
  synchronous call, as before. Aliasing: the loads are reads of columns no writer touches, since
  mask aliasing is rejected first, before any scan writes. Alignment: `float*`
  natural alignment, as for the other columns. Bounds: rows `0..Count-1` are inside the `Count`
  floats each column names, the same extent valid rows already read, and the source-column
  reads that follow are still ordered behind the slot bound, liveness, and generation tests.
  Concurrency: the scan runs alone on the calling thread. Every float→integer conversion of a position is
  preceded by a float range test — `CellQ8` on reads and `TileBake.Span` on deposits, both at
  ±2^30 — or converts a value already reduced to a bounded range (`Turn` and `Scale` reduce in
  double first), so no result depends on a platform's out-of-range conversion.
- **v2 tile planes and publish**: the `PlaneStore` slabs (2,048 B pages, 4,096 B `I` planes,
  8,192 B `T`/`B` planes, 256 B summaries) follow the block-slab rules above: units are
  addressed only by index, zero-filled by their taker, recycled whole through the free lists,
  and released by `Reset`. Lifetime: a `TilePlanes` set holds five unit indices and no
  pointers; `PlaneCells` pointers are materialized per call from the indices and never survive
  a `Take`, `Give`, or `Reset`, so a slab doubling that moves a region cannot dangle one; the
  store is owned by its (grid, layer) `LayerData` on the owning thread and freed by `Reset`. Aliasing: one publish call owns its tile — it writes the
  plane page, the summary block, and its two stack rows (`tentOut`, `bellOut`) only; the page
  is read for the old-cell compare group by group before the same group's store, so no cell is
  compared against an already-updated value, and the summary recomputation reads the fully
  stored page. Alignment: strides are multiples of 64 B over the 64 B aligned regions
  (naturally aligned scalar-only access on netstandard2.1), the summary fields sit at offsets
  0, 8, 10, 12, 16, 20, and 148 of the 256 B unit — each naturally aligned — and the vector
  loads and stores on `I` rows, rounded rows, and page rows use unaligned-safe semantics.
  Concurrency: a tile is published by exactly one call on the owning thread; there are no
  statics and no locks. Bounds: rectangles are tile-local with `0 ≤ x0 ≤ x1 ≤ 32` (the
  engine's dirty rectangles are clipped to the tile before publish); the guard and commit
  loops read `I`, the rounded rows, and the page only inside `[x0, vectorEnd)` with
  `vectorEnd = x1 − ((x1 − x0) & 7)` — full 8-cell groups inside the row — and any remainder
  cells take the scalar tail, so no access passes the 32-cell row end; the row-summary
  recomputation reads exactly the 32 published cells of each touched row, and the rescan reads
  exactly the 1,024 published cells.

### Floor probes

The `benchmarks --floor` suites (P1–P8) make no Gi calls; this proof covers their unsafe code with
the same headings. All probe state lives in the suites themselves, and every timed pass allocates
0 B (measured per case).

- **Lifetime**: every probe buffer comes from its suite's `FloorArena`. Each arena block is a
  64-byte-aligned, zeroed `NativeMemory.AlignedAlloc` tracked in a list and freed exactly once, in
  allocation order, when the suite is disposed — one `using` per suite, so construction, receipts,
  timing, and free all happen inside that scope and no pointer escapes the run. Everything is
  single-threaded: one suite exists at a time, and receipts run before any timing of that suite.
- **Aliasing**: every buffer is a distinct arena block; kernels never write two buffers through one
  pointer and never write their input arrays. Outputs are written only at indices `0..count−1`
  (P6 vector stores and scalar edge writes alike; P8 stores at `list + found`). Checksum passes
  clear, run, and re-hash the same buffers, so each pass is deterministic and repeatable. The P3
  interleaved pair and its merge target are separate blocks; the merge adds source into target in
  place and reads only what those two blocks hold.
- **Alignment**: arena blocks are 64-byte aligned and each request is rounded up to the next 64-byte
  boundary, so even an exact 64-byte-multiple request carries 64 spare tail bytes. Page bases,
  histograms, the convolution ring, and query arrays all start block-aligned; SIMD access uses
  unaligned-safe `Vector256` loads and stores at arbitrary element offsets (P2 merge steps, P8
  left-pack stores at `found`-relative offsets, P4 row steps at multiples of the vector width from
  the aligned bases).
- **Concurrency**: none to prove. No locks, no shared mutable statics (registry tables are
  immutable), no background work, no cross-thread observation; the process may be pinned with
  `taskset` without affecting any claim above.
- **Bounds**: index and query arrays are read past their logical end only by prefetch distances,
  and those extra lanes never reach an output. P2/P3 prefetch `cells[i+16]` (interleaved
  `cells[i+17]`), at most 15 ints past the 1M-int arrays — inside the 16 spare ints the arena's
  64-byte rounding leaves after a 4 MB request. P6's scalar prefetch reads `cells[i+32]` and its
  vector prefetch loads 8 ints at `i+32`, at most 31 ints past; its query arrays are allocated with
  72 spare ints. P6's prefetch hints may compute an address from a −1 directory entry and so point
  just outside a block — prefetch hints never fault, and the hinted values are never used as data.
  P6's gathers are masked to lanes whose tile is present and whose cell is interior (column and row
  both in `1..30`), so the west/east/north/south gathers stay inside the 32×32 page; border cells
  take the scalar `Edge` path, which bounds-checks every tile crossing and the field edge. P8's
  left-pack stores one full 8-int vector per block, so the tail store may write up to 7 lane slots
  past the last packed result; `found` never exceeds the block's start index, so every store stays
  within `list[0..count−1]`, and the result lists carry 8 spare ints — 16 after arena rounding. P7
  requires disks fully inside the field and `radius ≤ 64`: centres are generated in
  `[radius, side−radius]`, the guard `(uint)radius > 64` returns early, and the row-span walk then
  indexes directory rows and row sums without edge clamping — a disk crossing the field edge would
  read out of bounds, which is the probe's stated precondition, and the receipts sample the same
  interior centre range at radii 0–64. P4's origin block carries 4 halo rows above the field and a
  16-int left margin, so row taps `h[x−t]` for `t ≤ 7` stay inside the block, and the column pass
  keeps its last 8 rows in a ring indexed by `y & 7`. P5 allocates full-grid planes and summaries
  and indexes them by paged cell or tile index only.
- **Carry-forward assumptions for T-3**, both receipt-checked here and to be restated when the
  engine adopts these kernels: the P1 vector path uses the raw `vcvttps2dq`
  (`ConvertToVector256Int32WithTruncation`), which is exact only for in-range inputs — an
  out-of-range or NaN lane yields the indefinite `0x80000000` instead of a defined cell — and the
  P4 vector column pass multiplies the row-pass `int64` values through their low 32 bits, so it
  assumes every row-pass value fits in int32; the receipt bounds the impulse count
  (`max count < 2048`, keeping row-pass values ≤ 2^29) and the fully widened scalar path stays the
  reference.

## Tool inspection

`tools/stats` reads internal state through friend access, without reflection, counters on the
data path, or additional public types. `Inspection.Read` validates the world handle, scans
source liveness and grid/layer metadata, walks each (grid, layer) directory to count
live sets and tent/bell planes, and returns a pointer-free value snapshot.
Its cost is O(source slots + grids × layers + tiles + catalog stamps); it does not scan cells.
Memory includes allocated capacity, retained slab regions and directories after `Clear`, a
live-plane breakdown of every live tile
set (page, `I`, summary) plus its tent and bell planes where they exist, the per-(grid, layer) max pyramids,
the changed-slot, pending, journal and schedule queues, the per-grid deposited records, source-batch
scratch capacity, and shared
world/stamp arenas plus padded rasters. The selected world and
process-wide shared allocations are reported separately; allocator metadata, alignment slack,
other worlds, and the runtime are excluded from native totals. GC heap and process memory are
separate runtime observations. `World.Memory` is the public surface of the same accounting: a
subset of these numbers as `out` parameters, taken from the same counters `Inspection` reads,
under the same lifetime and thread rules as the rest of this paragraph.

Inspection shares the owner's lifetime and thread rules: capture it on the world-owning thread
between mutations/process calls, and serialize stamp creation with the capture. It neither
mutates nor frees storage, creates aliases that escape the capture, nor allocates managed or
native memory. All pointer reads use the naturally aligned owning structs and bounded allocated
capacities. The CLI is single-threaded and formats only after capture and timing. JSON uses
`Utf8JsonWriter` directly with static UTF-8 property names and a fixed object/array layout;
serializer reflection is disabled in the tool project.

## Unity boundary

Gi stays a standalone .NET library; the Unity adapter (`unity/com.gi.entities`) is a consumer, not
a fork. Its compiled surface ships as committed plugin DLLs (`Plugins/Gi`, provenance-checked by
`unity/build-plugins.sh`), and today that is the scalar netstandard2.1 build, because Unity's
script assemblies cannot yet reference the net10 TFM the vector build targets. Every hand-off of
unmanaged memory between the two sides lives in the adapter code named below; each heading states
its lifetime, aliasing, alignment and concurrency in this section's voice, following the proof
template of "Unsafe proof" above. Two adapter-wide rules frame them: all Gi calls from ECS run in
managed jobs (never Burst, D-U3), and every writer or reader of a Gi world chains through the
process-wide fences in `GiRuntime.Fences.cs`, so the headings below describe the fences' clients,
not the fences themselves.

### U-1

The unsafe code in the frame and bootstrap is three things: the fixed buffers in `GiTables` and `GiFaults`, the spans over blob arrays in `GiBootstrapSystem`, and the managed `ProcessJob` that calls `Process`.

- **Lifetime.**
  - `GiTables` and `GiFaults` are plain value fields of `GiFrame`. They live in the singleton's chunk, are copied by value into jobs and back, and hold no pointer to anything.
  - The spans in `GiBootstrapSystem.Rebuild` cover `BlobArray` storage of the profile blob. The blob is held by the `GiProfileReference` component for the whole call, and `Rebuild` runs on the main thread, where nothing can dispose or replace the blob. The spans do not escape the method; `GiWorldBuilder` copies every value it keeps (grid, layer and stamp ids go into `GiTables`).
  - `ProcessJob` holds only an `Entity` and a `ComponentLookup`. The Gi world and its token are read from `GiFrame` when the job runs.
- **Aliasing.**
  - The blob spans are read-only and nothing in the call writes to blob memory.
  - `ProcessJob` is the only job that writes `GiFrame` while it runs, because it takes `GiRuntime.BeginWrite`; every other system reads or writes `GiFrame` on the main thread after the fences or through the ECS dependency on the same component.
- **Alignment.** The fixed buffers are byte arrays, so they need no alignment beyond the struct's own. `BlobArray<T>` storage is aligned for `T` by the blob builder, and the spec structs are blittable with natural alignment, so the span reads are aligned.
- **Concurrency.**
  - `GiFrame` is written by `GiBootstrapSystem` only after `GiRuntime.CompleteAll()` and `CompleteAllTrackedJobs()`, so no job is running when the world is built, released or replaced.
  - `Process` mutates the Gi world, so `ProcessJob` is scheduled behind `BeginWrite` and registered with `EndWrite`; any later Gi writer or reader depends on it through the same fences.
  - `GiStampRegistry` is plain managed state touched only from the main thread (`GiBootstrapSystem`, `GiOrphans`, the lifecycle callbacks).

### U-2

The unsafe code in reconcile is the pointer access to the command and owner lists in `GiReconcileView.Visit`, the `Span` views of the lists in `GiApplyJob`, and the managed apply job itself.

- **Lifetime.** Every list and array is allocated from `state.WorldUpdateAllocator`, which stays valid for the whole world update and rewinds only when the next update begins. All jobs that touch them are in one dependency chain returned as `state.Dependency`, so none is still running when the allocator rewinds. The `EntityCommandBuffer` is created with `Allocator.TempJob`, played back and disposed at the start of the next update, after `state.Dependency.Complete()`. The spans in `GiApplyJob` cover list storage that outlives the call and are not stored.
- **Aliasing.**
  - The count pass only reads. The write pass writes disjoint ranges: chunk `c` owns `[Offsets[c], Offsets[c] + Counts[c])`, which are disjoint by the prefix sum, and the count and write passes make the same decision because they run `Visit` with identical inputs. The pulse commands occupy `[entityTotal, total)`, written by the prefix job before the write pass starts, so they never overlap an entity range.
  - The apply job reads the commands and writes three other lists (`Results`, `Scratch`, `BatchResults`). The write-back job reads commands, results and owners and writes component data only through `ComponentLookup`.
- **Alignment.** All element types are blittable structs with natural alignment, and list and array storage is allocated with at least that alignment, so the pointers and spans are aligned.
- **Concurrency.** Only the count and write passes run in parallel, and they work on distinct chunks, so they touch distinct ranges and distinct component arrays; the `GiFault` write for an entity happens in the chunk that owns it. `GiApplyJob` is the only job that calls into Gi, and it runs behind `GiRuntime.BeginWrite` and `EndWrite`. The write-back job is serial.

### U-3

The unsafe code in sync is the raw column pointers handed to `World.SyncTransforms` in `GiSyncPass.Apply` (a `stackalloc` array of `TransformRange`), the pointer arithmetic over the `Meta` array, and the `NativeArray` writes in the write pass.

- **Lifetime.** The column lists and the `Invalid` list come from `state.WorldUpdateAllocator` and stay valid for the update. `SyncTransforms` reads and writes them only inside the call (`src/Gi/World.SyncTransforms.cs`: validation, mask clear, then `ApplyTransformRanges`, which keeps no pointer), and the `TransformRange` array is on the stack of that call, so nothing outlives it.
- **Aliasing.** Each column is its own list and each category is a disjoint slice of every column; each category's mask occupies its own word range in `Invalid`. Gi rejects overlapping buffers with `OverlappingBuffers` before touching anything, and the tests pass real data, so the rule is checked on every run.
- **Alignment.** `SourceId`, `float` and `ulong` columns are naturally aligned list storage. Category slices start at element boundaries.
- **Concurrency.** The write pass runs in parallel over chunks; chunk `c` writes the rows `[Offsets[c*4+k], Offsets[c*4+k] + Counts[c*4+k])` of its single category `k`, which are disjoint by the prefix sum. `GiSyncApplyJob` is the only Gi writer here and runs behind `GiRuntime.BeginWrite` and `EndWrite`. The scatter pass reads the finished columns and mask and writes only `GiFault` data and enabled bits of its own chunk.

### U-4

The unsafe code in the guard is the pointer access to one `GiGuardTable` in persistent native memory and the `ref *word` arguments to `Interlocked` and `Volatile`.

- **Lifetime.** `GiGuard.Create` allocates one cleared, 64-byte aligned table with `Allocator.Persistent` when code loads or at the first `Register`; `GiGuard.Destroy` frees it in `GiRuntime.Unload`, after `CompleteAll`, so no job holding the pointer outlives it. Jobs read the static pointer and never keep it past `Execute`. When the table is absent `Begin`, `End` and `TryTake` do nothing.
- **Aliasing.** The table holds 256 state words and 256 holder words, indexed by the Gi world id (a `byte`), and one report slot. Every access to a state or holder word is an `Interlocked` or `Volatile` operation. The report fields are written only by the thread that moved the slot from free to filling and read only after it is ready, so the slot has one writer at a time.
- **Alignment.** Every field is an `int` at a 4-byte offset inside a 64-byte aligned block.
- **Concurrency.** `Begin` always registers its party (a reader adds one, a writer adds `WriterBit`), and `End` always removes it, so the word balances even after a conflict. The party that enters second sees the conflict: a reader finds the writer bit set, a writer finds anything other than its own bit. The first conflict since the last `TryTake` keeps its details (world, both tags, whether the intruder writes) and later ones only add to the total. The guard reads and writes no Gi state, so it can report an overlap but cannot cause or hide one. `ENABLE_UNITY_COLLECTIONS_CHECKS` gates the calls with `[Conditional]`, so release builds contain none.

The fences in `GiRuntime.Fences.cs` are two `SharedStatic<JobHandle>` values written only on the main thread, in system `OnUpdate`, and reset when code loads. `GuardTests` in `unity/tests/Gi.Entities.Columns.Tests` pins the table against single-thread sequences and eight-thread stress; `Tests/Runtime/Concurrency` pins the fences, the Editor world and the two-world run.

### U-5

The unsafe code in sense is the raw pointers `GiSenseReadJob` hands to `GiSensePass.ReadBatch`, the `byte*` component access and `ulong*` word reads in `GiSenseScatterJob`, and the span over the profile blob's binding array in `GiSenseSystem.Rebuild`.

- **Lifetime.**
  - Every list, array and the `NativeReference<GiReconcileContext>` is allocated from `state.WorldUpdateAllocator` (lists directly, arrays through `CollectionHelper.CreateNativeArray`), so all of it is valid for the whole world update; the update's jobs form one chain stored in `state.Dependency`, so none is still running when the allocator rewinds.
  - The read job takes its pointers from `NativeList` storage inside `Execute`, for one batch, and stores none of them; the scatter job takes its chunk's component array pointer the same way.
  - The span in `Rebuild` covers the profile blob's `BlobArray<GiBindingSpec>`; `Rebuild` runs on the main thread while the `GiProfileReference` component holds the blob, and the span does not escape — the bindings copy every value they keep.
- **Aliasing.**
  - The count pass only reads. The write pass writes the rows `[Offsets[c], Offsets[c] + Counts[c])` of `Xs` and `Ys`, disjoint by the prefix sum, and the count and write passes make the same decision because they run the same `GiSenseView` over the same query with nothing between them in the chain that changes it.
  - Read batches are disjoint: batch `b` reads `Xs` and `Ys` read-only and writes the rows `[b·batch, min((b+1)·batch, rows))` of the outputs and the words from `b·batch / 64` of `Whole`; the batch size is a multiple of 64 (`GiSensePass.Batch`), so no `Whole` word straddles two batches.
  - Each scatter job writes only its own chunk's component bytes, at `i·TypeSize + Offset` for offsets already validated to lie inside the component, and there is one scatter job per output type, so two scatter jobs never write the same component type; `GiBindingTable` has rejected a second writer to the same field before the binding exists. `Whole` is read-only here.
- **Alignment.** `short`, `float` and `ulong` list storage is naturally aligned, and every batch starts at a multiple of 64 rows, so each `whole + (start >> 6)` word is aligned. Component arrays are aligned for their type, `i·TypeSize` preserves that alignment, and the field offsets arrive from the authoring path's `GetFieldOffset` over real component layouts, so they are naturally aligned; `Resolve` rejects any offset whose field leaves its component.
- **Concurrency.**
  - `GiSenseReadJob` is the only job that touches Gi. It is scheduled behind `GiRuntime.BeginRead` and registered with `EndRead`, and its reads are the pure column reads of C8 (`Sense*Column` with null excludes), so its batches run in parallel and any Gi writer waits for them through the same fences.
  - The scatter jobs write the game's own components through `state.Dependency` on the binding's query, each chunk writing only its entities, and the coverage tag's enabled-bit write happens in the chunk that owns the entity.
  - `Rebuild`, `Resolve` and `Report` run on the main thread and touch managed ECS state only — queries, `TypeManager` lookups, and the frame's fault word through the EntityManager; no pointer into Gi or blob storage survives them.

### U-6

The unsafe code in splat is the pointer access in `GiSplatView.Visit`, the `stackalloc` cursor array in the write pass, and the `Span` views `GiSplatPass.Apply` builds over the finished columns.

- **Lifetime.** The column lists and the count and offset arrays come from `state.WorldUpdateAllocator` and outlive every job in the chain. `Apply` builds its spans over list storage that is not resized while it runs and does not keep them; `World.Splat` reads the spans inside the call. The `stackalloc` cursor array lives for one `Execute`.
- **Aliasing.** Layer `k` owns the slice `[Meta[k], Meta[k] + Meta[32 + k])` of every column. Chunk `c` writes rows from its own cursor, which starts at `Offsets[c * 32 + k]` and advances once per entity of layer `k` that the count pass counted, so chunks write disjoint rows. The count pass only reads.
- **Alignment.** `float`, `byte` and `sbyte` columns are naturally aligned list storage; slices start at element boundaries.
- **Concurrency.** The count and write passes are parallel over chunks and use identical decisions (`GiSplatPass.Check` on identical inputs), so the cursor never passes the counted end of its slice. `GiSplatApplyJob` is the only Gi writer here and runs behind `GiRuntime.BeginWrite` and `EndWrite`; the faults are written by the chunk that owns the entity.

### P-1

The unsafe code in the mirror is two pointer hand-offs to Gi from `Presentation/GiMirror.cs`: `ChangedTiles` into a persistent `NativeArray<int>`, and `QueryRegion` into a persistent `NativeArray<int>` read as `short`. Everything else (the page table, the pending queue, the budget) is managed code in `Runtime/Columns` with no pointers.

- **Lifetime.** The two arrays and the one-element entry array are allocated with `Allocator.Persistent` in the constructor and freed in `Dispose`; Gi writes into them only during the call that receives the pointer, and the mirror keeps no pointer between calls. The `GraphicsBuffer`s are owned by the mirror, created in the constructor (the pool again when it grows) and disposed with it. Growth copies the old pool into the new one on the GPU, then disposes the old.
- **Aliasing.** `ChangedTiles` writes at most one `int` per tile of the grid into an array that holds exactly that many. `QueryRegion` writes one 32 x 32 tile, 1,024 `short` values, into an array of 512 `int` values (2,048 bytes); the tile is read whole before the next call reuses the array. No other reference to either array exists while Gi writes.
- **Alignment.** Both arrays come from `NativeArray` with at least 4-byte alignment, which satisfies `short` and `int` access. Two `short` values share one `int` word of the pool, the lower index in the low half; the compute kernel sign-extends each half.
- **Concurrency.** The mirror is main-thread only and reads Gi, so the caller must hold a window in which no Gi write job runs (`GiRuntime.CompleteAll`, or the inspector system's completed `GiFrame` dependency). The mirror takes no fence itself and writes no Gi state.

A tile that reads back all zero returns its pool slot and clears its table entry, so the pool holds only tiles with content. `gi.mirror.budget` caps the tiles uploaded per frame; the rest wait in a queue that keeps first-in order and holds each tile at most once, and the queue drains in `ceil(pending / budget)` frames once changes stop (`MirrorPlannerTests` pins the planner under 100 random schedules). The compute decode and the heatmap are the same function of the table and pool, so the test that compares the decode with `QueryRegion` over 50 seeds also covers what the heatmap draws.

### E-7

The unsafe code in the flight recorder is four small things: the three tap calls from the Gi jobs into `GiRecorderGate`, the `fixed` blocks in `GiTileHashes` and `GiReplay`, and the compression calls in `GiLz4Codec`. The recorder, the ring, the keyframes and the export are managed code in `Runtime/Columns/Recorder` with no pointers of their own.

- **Lifetime.** A tap receives pointers into the job's own columns (the sync rows, the splat rows) for the length of the call and copies every row into managed arrays before it returns; nothing keeps the pointer. `GiTileHashes` pins its own managed `int[]` and `short[]` for the duration of one `ChangedTiles` or `QueryRegion` call. `GiReplay` pins a decoded record's arrays only around the call into the sync or splat pass. `GiLz4Codec` pins the source and destination arrays around one compress or decompress call.
- **Aliasing.** `ChangedTiles` writes at most one `int` per tile into an array sized to the largest grid's tile count; `QueryRegion` writes one 32 x 32 tile, 1,024 `short` values, into an array of exactly that size. The replay world is its own Gi world with its own stamps (a separate `GiStampRegistry`), so nothing the replay writes aliases the live world.
- **Alignment.** Every pinned array is a managed `float[]`, `short[]`, `int[]`, `ulong[]` or `byte[]`, aligned for its element type.
- **Concurrency.** The taps run inside the reconcile, sync, splat and process jobs, behind the fences those jobs already hold (`GiRuntime.BeginWrite` and `EndWrite`), one world at a time on one thread; the recorder is touched by those jobs and by nothing else while they run. `GiRecorderSystem` creates and detaches recorders on the main thread after `GiRuntime.CompleteAll`, and editor code reads a recorder only after `GiRuntime.CompleteAll`. With recording off, a tap is one read of a static field.

A keyframe stores the live sources (layer, stamp, effective gain, position, heading, scale), the schedule read-back of each (fade ends, ticks and ticks left; ticks left to expiry) and the splat columns of its tick, because a transient layer still holds them after `Process`. Restoring places the sources and the splats, calls `Process`, then reinstates expiries with `Expire(ticksLeft)` and re-drives each fade in flight with `SetGain` at the staircase value of the next elapsed tick (`from + sign(delta) * floor((|delta| * k + floor(ticks / 2)) / ticks)`), cancelling an entry when a recorded `SetGain`, `Fade` or `Remove` reaches its source, which is the exactness G-5's read-back was added for. A restored heading is biased by half a quantum (`pi / 65536`) because `Turn` truncates and the read-back is the quantum's lower edge. `RecorderTests` and `RestoreTests` pin the restore at every elapsed tick of several fades, the heading over 300 values, and 10,000 ticks of a steady crowd.

### S-1

The unsafe code in save is one thing: `GiPulseMirror` holds a pointer to an `UnsafeList<GiPulseEntry>` that the reconcile write-back job appends to and prunes. Everything else (the saved components, `GiSave`, the restore driver and the two restore systems) is managed code or plain component data.

- **Lifetime.** The list is created with `Allocator.Persistent` in `GiBootstrapSystem.OnCreate`, stored on the frame entity, and destroyed in `OnDestroy`; it is cleared whenever the Gi world is rebuilt or released. The job copies the component (a pointer) and never keeps it past `Execute`.
- **Aliasing.** The list is written by one job, `GiWriteBackJob`, which runs once per tick behind `GiApplyJob` in the reconcile chain; main-thread code reads it only after `GiRuntime.CompleteAll` and `CompleteAllTrackedJobs` (`GiSave.Prepare`, the restore system), which join that chain first.
- **Alignment.** `UnsafeList<T>` allocates with the alignment of `GiPulseEntry` (a `SourceId` of two `uint` and an `int`), at least 4 bytes.
- **Concurrency.** One writer at a time, ordered by the job dependency; the entries are pruned in the same job when more than 64 exist and their end tick has passed, so the list stays proportional to the pulses in flight.

Save and load follow the plan's split. `GiEmitter`, `GiSplat`, `GiGainRamp`, `GiOriented` and `GiScaled` carry `[Save]`; `GiSource`, a cleanup component, does not. `GiSave.Prepare` runs before the game saves: for every placed source whose entity has a ramp it writes `GiSavedSource` (layer and stamp keys, the applied target gain, the effective gain, the pose, the fade ends, ticks and ticks left, ticks left to expiry), and it writes one `GiSavedPulse` buffer element per live pulse on the `GiSaveHolder` entity. `GiSave.Release` removes them again. On load `GiSaveRestoreSystem`, before reconcile in the first tick of a Gi world, places each saved source and pulse at its effective gain, sets `Expire(ticksLeft)`, adds the `GiSource` back with the applied target gain (so the next reconcile diff behaves as in an uninterrupted run), and hands every fade in flight to `GiRestoreDriver`; `GiRestoreStepSystem`, after reconcile, writes the next staircase step with `SetGain` before `Process`. A driver entry is dropped when its source is gone, when Gi shows a fade or a gain the driver did not write, or when the owner entity's applied gain changed (a new ramp request that lands on the current gain leaves no trace in Gi). Ticks are not restored: the new world counts from zero and every schedule is relative. `since` has nothing to reset: bindings read `Tick - Since`, and the mirror and the Field window re-attach when the world generation changes. `SaveTests` pins the restore over 40 seeds with the driver, the discriminating restart case, and the guard; `GiSaveTests` saves and loads 36 combinations of seed and tick through the real systems and compares every field hash on every following tick.
