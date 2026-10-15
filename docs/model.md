# Gi

Sparse tiled integer influence fields for .NET. One library, no dependencies.

## Model

- A **world** (`World.New`, up to 255 live at once) owns **grids** and **layers**. `World.Free(world)`
  releases the world's memory and its handle: `World.New` reuses the lowest free handle, and a freed
  handle reads as absent to every call. `World.CopyWorldIds(ids)` copies the allocated ids,
  ascending, into a span and returns the live count; `World.ProcessAll()` processes every
  allocated world in id order on the calling thread. `World.Revision(world)` returns an `int`
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
  exactly 2³² lifecycle transitions on one slot. Capture is legal whenever reading the world is
  (between lifecycle controls, under the host's producer/reader fences); consumption runs in the
  existing exclusive mutation phase — a token check is not a lock, and concurrent same-world
  mutation or a `Free`/`Clear`/`Rewind` racing a consumer remains forbidden. A grid
  (`Grid.New(world, power, x, y, size)`, up to 32 per world) is a power-of-two cell grid,
  `2^power` cells per side (power 5–14), laid over a world-space rect `x,y,size`. Grids are
  never freed singly, so their ids stay the dense range `0..Grid.Count(world)-1`;
  `Grid.CopyIds(world, ids)` copies them into a span and returns the count, and
  `Grid.TryGetSpec(world, grid, out power, out x, out y, out size)` returns the exact
  constructor arguments — the world extent is stored, not recovered from the truncated
  scale, so the spec round-trips bit-exact. `Layer.Count(world)` and
  `Layer.CopyIds(world, ids)` enumerate layers the same way, deposited and derived sharing
  one dense id space, and `Layer.TryGetRecipe(world, layer, out recipe)` inspects any live
  layer's `Layer.Recipe` (`Layer.Derivation` operator plus input layers, weights, shift, and
  mask bounds). A layer
  (`Layer.New(world)`) is an independent field channel present on every grid. A layer either
  receives deposits (`Layer.New`) or is **derived** (`Layer.Sum`/`Min`/`Max`/
  `Mask`): its cells are a pure function of other layers' cells, maintained by `Process` (below).
  Deposited and derived layers share one table: a world holds at most `World.MaxLayers` (32)
  layers in total, so a derived combo takes one of the same slots as a raw channel and the plan
  is `raw + derived ≤ 32`.
- **Stamps** (`Stamp.New(sbyte* data, w, h)` / `Stamp.Box(w, h, value)` / `Stamp.Tent(w, h, value)` /
  `Stamp.Bell(w, h, value)` / `Stamp.Disk`/`Cone`/`Dome(radius, value, arc)`, up to 255 live at
  once) are baked
  cell-space content: `w×h` `sbyte` samples, centered on the placement position (origin offset
  `−w/2` cells in Q8). Uniform rasters classify as `ConstantRectangle` and take the
  difference-plane path; the rest are `Raster` and deposit with sub-cell bilinear weights.
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
  `2^51`, and int64 sums stay exact up to 4,096 maximum-strength kernels stacked on one cell —
  256× past int16 saturation.
  While a tile resolves, every kernel of that kind covering it adds its product
  `value·gain·C·Wx·Wy` into a per-tile `int64` sum plane held for the duration of the resolve —
  per axis the cells `First..Last` whose weight is positive, the per-axis weights computed once
  per column and once per row — and resolve rounds each cell once,
  `RoundQ40(Σ value·gain·C·Wx·Wy)`, over all sources of that kind in the tile. A tent or
  bell's cell bounds are its support — per axis `First..Last` — rather than its box footprint,
  so tiles and exclusion follow a kernel narrower than a cell to the neighbour its one-cell
  minimum half-width reaches. Every weight is zero outside that support, so tiles beyond it
  receive nothing; nothing of the sum planes survives the resolve — only the rounded page.
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
  deposits `RoundQ24(value · weight_Q16 · coverage_Q8) · gain` into the tile's dense sum.
  `Stamp.Free(stamp)` returns a stamp's id to the catalog — only while no source in any world
  uses it, no pending op or journal entry names it — frees its samples, and the next creation
  reuses the lowest free id; a freed id places nothing (`Place` returns `-1`) until it is
  reissued. `Stamp.TryGetSpec(stamp, out spec)` returns a live stamp's `Stamp.Spec`
  (`Stamp.Shape`, cell width and height, peak `sbyte` value, and exact `ArcDegrees` 1–360) and
  `false` for `0` or a freed handle.
- **Sources** are persistent placements: `World.Place(world, layer, x, y, stamp, gain)` returns an
  int id (`-1` when the world, layer, or stamp is invalid, the layer is derived, or `x`/`y` is
  non-finite); `Move`/`SetGain`/`Remove`/`Clear` mutate it (`Move` ignores non-finite `x`/`y`
  coordinates; `World.TryGetSourceSpec(world, id, out spec)` reads a live source's current
  position, quantized heading in radians, quantized scale, gain, layer, and stamp). `gain` is an integer −16–16 (stored as one
  `sbyte`-ranged byte; cell totals accumulate exactly, so a negative gain subtracts). The id packs a
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
  `World.Turn(world, id, radians)` and `World.Scale(world, id, scale)` set a source's facing and
  size; both queue and merge like `Move`, so a turn, a scale, a move, and a gain change in one
  window collapse to one footprint diff. Angles store as binary angles (65,536 per turn,
  `⌊frac(θ/2π)·65536⌋`, so whole turns read 0) with sine and cosine from a fixed Q14 polynomial
  that is exact at the four quarter turns; scales store in Q8 (`⌊scale·256⌋`, 1/256 to 255.99).
  Scale multiplies the stamp's sampling rate on every grid: extents and origin offsets use
  `sampling = ScaleQ8·scale_Q8 >> 8` in place of `ScaleQ8`, so a box of width `w` at scale 2 is
  bit-identical to a box of width `2w`. A source with angle 0 deposits through the exact
  axis-aligned paths above; a turned source — or any round stamp — evaluates every cell of its
  rotated bounds: boxes weigh the product of their two one-cell anti-aliased edge coverages
  along the turned axes, tents and bells add `value·gain·C·Wu·Wv` with the same per-axis units
  and normalizer as unturned kernels into the same sum planes, and rasters sample bilinearly at
  the turned cell centre — its Q16.16 sample coordinates are `(dx·Ux + dy·Uy + U0) >> 32` with
  `Ux = ⌊cos·2^33/S⌋`, `Uy = ⌊sin·2^33/S⌋`, `U0 = w·2^47 − ⌊2^55/S⌋` (and `−sin`, `cos`, `h` for
  the other axis), folding the turn and the sampling rate `S` into fixed-point coefficients;
  sample indices are bounded to the padded border and the mip level is chosen as for unturned
  rasters. Every turned or round weight is a pure function of the cell, the
  placement, and the stamp, computed in integers, so re-resolving without the source drops it
  exactly.
  Footprints never clamp a stamp to the grid size: an unturned box clips its edges to the grid
  window (one cell of margin) without moving its interior, a stamp wider than its grid covers
  the grid, tents cap their extent at `2^23` Q8 (where the exact unit scheme ends — wider than
  any grid), and other kinds at `2^30` Q8.
- **Sources snapshot; dirty tiles re-resolve whole**: every live tile is one 2,304 B block —
  a 32×32 `int16` page at offset 0, then a 64 B slot holding the `int64` page sum and the
  `int16` page maximum and minimum, then the row summaries: 32 `int32` row sums and 32 `int16`
  row maxima. There are no persistent accumulation buffers at all — no difference arrays, dense
  buffers, or kernel sum stores survive a resolve.
  Mutations do not touch tiles: `Place`/`Move`/`SetGain`/`Remove`/`Turn`/`Scale`
  update the source columns and merge into a per-world op queue — one op per slot per window,
  carrying the state applied by the previous `Process` and the latest state, angle and scale
  included. The queue is the window's change journal rather than a deposit plan: `Process`
  reads it to find which slots to re-evaluate, and `Record`/`Rewind` replay it as the undo
  log. A slot placed and removed in one window collapses to an op whose recomputed state still
  produces no footprint — no tiles are marked, no blocks exist.
  Each grid keeps a per-slot **prepared placement** (the resolved `Placement` — footprint,
  effective kind, turned frame, smooth or raster parameters) plus a compact **prepared
  footprint** (the placement's inclusive tile range, layer, gain, and the bitmask of
  accumulation planes it needs). The first `Process` after world creation or `Clear` prepares
  every slot; afterwards only the slots named by the window's ops are recomputed. A slot whose
  recomputed placement, layer, and gain all match the stored ones marks nothing; otherwise
  every tile the stored footprint touched and every tile the new footprint touches is marked
  dirty — the footprint diff *is* the retraction, and a source that changed layers marks its
  old layer's tiles under the stored footprint and its new layer's under the new one. Because
  tiles re-resolve from scratch instead of absorbing deltas, any mutation sequence on a slot
  collapses to one footprint change — bit-identical to stepping the same sequence through a
  `Process` per mutation.
  For every (grid, layer) with a non-empty dirty list, `Process` rebuilds the layer's
  **source bins**: a per-tile record holding the count of covering sources and their combined
  plane bitmask, plus a slice of a flat slot array. Three passes fill it — every live footprint
  on the layer increments the count and plane mask of each tile it touches (`TileBake.Touches`,
  the same cell-support test the emitters clip to), a prefix pass assigns each tile its array
  range, and a final pass writes the slots in ascending order — so a tile's bin is exactly the
  set of sources contributing to it, ordered deterministically. Binning walks **every** live
  footprint on a dirty layer, not just the changed ones — a layer that moves one source out of
  thousands still pays the full scan, which is why `Process` cost is O(changed placements) for
  preparation but O(layer sources) for any layer with a non-empty dirty list. The op queue
  then commits:
  appended to the journal while recording, per-slot pending indexes cleared, queue emptied.
  Each dirty tile resolves on the calling thread from its bin alone, emitting every binned
  source into **transient `int64` planes allocated on the stack for the length of the call** —
  a 33×33 difference plane for boxes, a 32×32 dense plane for rasters, turned stamps, and round
  stamps, and a 32×32 unrounded-sum plane each for tents and bells — sized by the tile's own
  plane bitmask, so a box-only tile pays only the difference plane. Boxes write four-corner
  adds into the difference plane; dense and turned sources add per cell; unturned separable
  kernels choose per tile between a direct per-cell write and — only when the bin holds at
  least 16 sources and the kernel's in-tile window exceeds 64 cells — an **impulse** write into
  a separate plane: the kernel's ≤6 per-axis slope-break impulses are deposited and resolve
  integrates them with a 2D prefix of order 2 (tent) or 3 (bell) before adding the result to
  the sum plane, so a pile of wide kernels costs O(breakpoints) per source instead of
  O(support). A tile whose bin exceeds 4,088 sources with any smooth plane present resolves
  through the **wide** path instead: difference and dense accumulate in `int64`, tents and
  bells in per-cell 128-bit `WideSum`s (low `ulong` plus high `long` carry), so piles of any
  depth stay exact.
  Resolve folds the planes in one pass — the difference plane integrates as a 2D prefix
  (a horizontal carry per row plus a 32-wide accumulator row for the vertical dimension),
  dense adds per cell, each smooth plane rounds once (`RoundQ40`) — and
  saturates to `short` **after** summation, so cancellation is preserved. It writes the page,
  the sum, the maximum, the minimum, and both row summaries into the tile's block, and returns
  whether the page holds any non-zero cell. The block is allocated on the tile's first resolve
  and reused while the tile lives; a tile that resolves to all-zero frees its block and leaves
  the map — a missing page reads as 0. Every dirty tile then pushes its new page max — 0 for a
  dead tile — into its layer's **max pyramid**: level 0 groups 8×8 tile maxes per node, each
  higher level groups 8×8 child-node maxes, stopping at a single root. A tile write updates
  the cached node max in its parent slot and propagates only when it must: a value above the
  cached max replaces it without a scan, an unchanged slot stops the walk, and only a falling
  former maximum rescans its 64-slot node (vectorized for full nodes) before continuing
  upward. Each `Process` then rotates every (grid, layer) dirty list into its **changed
  list** — a zero-copy buffer swap, so the tiles the call resolved stay readable until the
  next `Process` overwrites them. The bins, footprints, dirty and changed lists, op queue, and
  pending index live in unmanaged buffers whose capacity is retained across windows, and the
  resolve scratch is the stack — a warm `Process` allocates 0 B.
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
  bits where `value != short.MinValue`). `World.ChangedMask(world, layer, xs, ys,
  reach, since, count, words)` writes one `ulong` per 64 agents — bit set when that agent's
  disk changed since the tick — zeros for quiet words, and returns the changed count.
  `World.Sync(world, sources, xs, ys, count)` is the fused column form of `Move`: it
  validates generations, skips no-ops, and enqueues the same coalescing ops `Move` would,
  batch after batch, with zero managed allocation. At 8,192 rows or more (and a free pool) the
  query batches fan out over the worker pool in 1,024-row chunks aligned to word boundaries so every
  mask word has one writer; smaller or contended batches run the identical loop serially.
  `World.SyncTransforms(token, ranges)` borrows compact native pose columns through the same
  boundary: each `World.TransformRange` names ID/X/Y columns plus optional heading/scale
  columns (null preserves the current value), a row count, and an optional invalid mask.
  Rows apply in range order then row order — duplicates fold last-writer-wins into one pending
  op — with `UpdateTransform`'s field semantics but `Sync`'s bit-exact position compare:
  `-0.0` and `+0.0` are different poses and NaN payload bits copy verbatim, where ordered
  `Move`/`UpdateTransform` compare with `==`. Gain, fades, and expiry are never touched.
  Stale/dead rows set their mask bit (bit `row & 63` of word `row >> 6`; cleared words leave
  tail bits zero and untouched words keep their contents) instead of producing a per-row
  result, and the call still returns `Applied`. Validation runs in a fixed order before any
  write — token (`StaleRevision`/`InvalidWorld`), descriptor structure (`InvalidRange` for
  negative counts, missing required pointers, or a mask shorter than `(Count + 63) / 64`
  words), total rows over 16,777,216 (`TooManyCommands`), then mask aliasing
  (`OverlappingBuffers` when a mask overlaps any column or another mask; read-only columns
  may share storage) — so a rejected call leaves masks and world byte-identical. The columns
  are borrowed only for the synchronous call: under 8,192 total rows one inline pass on the
  caller enqueues the same coalescing ops `Move`/`Turn`/`Scale` would with zero scratch;
  above that a single sequential planning scan folds rows into one entry per unique source —
  seeded from the current columns, or from the slot's pending op so an unprocessed earlier
  edit merges rather than chains, preserving that op's original `From*` for rewind — and the
  existing pooled batch-apply phase writes source columns and ops in parallel before the
  workers join. Invalid bits are set during the caller-thread scan, so no mask word ever has
  two writers, and no caller pointer survives the return.
- **Source batches** use `World.ApplyBatch` over a sealed `ReadOnlySpan<World.SourceCommand>`
  and caller-owned result span. Input order defines command order; pending creation targets
  reference an earlier `Place` index. Structural rejection writes no output and changes no source;
  stale handles and failed creations are explicit per-command results and do not abort unrelated
  commands. `Update` owns position, heading, scale, and gain, including fade cancellation. Partial
  methods change only their named fields. Ingestion does not tick or touch tiles; a later `Process`
  resolves the result. Small all-`Update` batches fuse validation and mutation on the caller.
  Large unique all-`Update` batches avoid hash grouping; other batches group commands by source
  slot, preserving command order and virtual free-chain/generation transitions. Preparation and
  disjoint source writes reuse the worker pool above 8,192 source rows. Timer and journal commits
  stay on the caller. Footprint and sparse-map preparation still use the existing `Process` path.
- **Process** advances the tick, runs due timers (their mutations merge into the op queue
  exactly like API calls), then walks the snapshot pipeline described above on the calling
  thread: per grid, prepare the window's placements and mark the footprint diffs; build the
  source bins of every dirty (grid, layer); commit the op queue, journaling it while
  recording; resolve every dirty tile from its bin into transient scratch; recombine derived
  layers; then stamp every resolved tile's epoch with the tick, refresh row summaries, and
  rotate the dirty lists into the changed lists. Everything is serial — the worker pool
  serves only column queries and source batches, never a resolve — so a page is bit-identical
  regardless of pool state.
- **Derived layers**: `Layer.Sum(world, a, weightA, b, weightB, shift)` reads
  `clamp(round(weightA·A + weightB·B) / 2^shift)` — rounding half away from zero, weights within
  ±32,767 and `shift` 0–15, so the `int32` total `|weightA·A + weightB·B| < 2^31` is exact —
  and `Layer.Sum(world, a, weight, shift)` is its one-input form; `Layer.Min`/`Layer.Max(world,
  a, b)` read the cellwise minimum/maximum; `Layer.Mask(world, a, b, min, max)` reads `A` where
  `min ≤ B ≤ max` and 0 elsewhere. `A` and `B` are the inputs' resolved, saturated cells, so a
  derived cell is exactly what a caller computes from `Query` on its inputs. Every operator maps
  two zero inputs to zero, so a tile where no input holds a page holds no derived page either:
  derived layers are as sparse as their inputs. Inputs must already exist (lower ids), so
  creation order is a topological order and chains nest to the 32-layer limit; each layer
  records the bitmask of source layers it reads. The 32-layer budget is shared between
  deposited and derived layers, and the cap is load-bearing rather than an allocation knob: a
  recipe's read set is one `uint` over layer indices, so a wider budget widens that mask on
  the place and process paths; layer ids stay bytes. `Process`, after the source
  drain, visits derived layers in id order: each marks the union of its inputs' dirty lists —
  this window's resolved tiles, derived inputs included — and recombines exactly those tiles in
  one pass over the 1,024 cells of each input page (an absent page reads the shared zero page),
  writing the derived page, its sum, and its max (16 or 8 cells per vector step, bit-identical
  to the scalar loop); the publish pass fills the tile's row summaries and minimum from the
  written page. All-zero results free the tile; pyramids and changed lists update as for
  any layer, so every query works on derived layers. `Place` on a derived layer returns `-1`.
  Recipes belong to the world's layer table and survive `Clear`; pages follow their inputs
  through `Rewind` and `Clear`. A derived layer created while its inputs already hold pages
  marks every live input tile, so the next `Process` fills it completely; resolve skips derived
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
  columns; a per-world binary min-heap keyed by (due tick, slot) holds each scheduled slot's next
  event, entries made stale by later calls are skipped when popped (the slot's recorded due tick
  no longer matches), and the heap is rebuilt from the columns once it outgrows twice the slot
  count. At the start of `Process`, after the tick advances, every due event becomes an ordinary
  queued gain change or removal — applied, journaled, and merged exactly like the API calls —
  so scheduled and manual mutations are indistinguishable downstream, and determinism follows
  from the (due, slot) order. Ticks count `Process` calls, not time: Gi has no clock.
- **Rewind**: `Record` drops any prior journal and starts appending a copy of every op the
  world's `Process` calls apply — the op already stores the applied-from and applied-to states
  plus the slot's generation, so the journal is an undo log costing one 32 B unmanaged append
  per applied op. `Rewind` consumes the journal in reverse: each entry restores its slot's
  columns to the recorded from-state (position, stamp, layer, gain, liveness, and — only when
  the source was alive before the window — the generation byte, so a removed source revives
  with its exact pre-window id while a rolled-back place's id stays stale) and enqueues the
  inverse op `{retract latest applied, apply recorded from}`; per-slot queue merging
  collapses multi-window journals to one net op per slot exactly like ordinary mutations.
  Unapplied queued mutations are discarded first — their slots restore to their queued
  from-states — so no applied or pending state escapes the checkpoint. The free-slot chain
  cannot be replayed positionally (journal order is first-mutation order, not free-event
  order), so `Rewind` rebuilds it canonically — dead slots chained in descending index order,
  smallest slot at the head — a deterministic pure function of the restored columns. The next
  `Process` applies the inverse ops through the normal deferred path, so a rewind costs the
  same as the window it undoes — O(changes), never O(field). `StopRecording` discards the
  journal without restoring; `Record` again re-anchors the checkpoint; `Clear` resets both.
  With no journal there is no checkpoint and `Rewind` only cancels pending mutations.
  Schedules rewind too: while recording, every API change to a slot's schedule (`Fade`,
  `Expire`, a fade-cancelling `SetGain`, `Remove`, `Place`) appends the slot's prior schedule to
  a schedule journal, and scheduled events are ordinary ops already in the op journal. `Rewind`
  restores the schedule journal in reverse, then shifts every live schedule by the ticks elapsed
  since `Record` — the tick itself never runs backwards, so change epochs stay monotonic — and
  rebuilds the heap: pending fades and expiries resume with exactly the remaining time they had
  at the checkpoint, and a rewound world re-simulates tick for tick like a twin that never left
  it.
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
  gradients at the border measure the drop toward empty space. ~5 ns per point;
  `ChangedTiles(world, grid, layer, int* dst)` returns how many tiles the most recent
  `Process` resolved on that (grid, layer) and copies the tile ids to `dst` when it is not
  null — repainting, incremental network sync, and dirty-rect presentation read one call
  instead of diffing pages. The list includes tiles that resolved to zero and left the map;
  it is empty before the first `Process`, after a `Process` with nothing dirty on that pair,
  and after `Clear`. The caller's buffer must hold the grid's tile count; passing null fetches
  only the count. Buffers swap, so the feed allocates nothing warm.
  `ChangedTiles(world, grid, layer, since, int* dst)` serves consumers that read at their own
  cadence: every (grid, layer) keeps an `int32` **epoch** per tile — the tick of the last
  `Process` that resolved it, written for every dirty tile (base and derived, including tiles
  that resolved to zero) after the drain — and the overload returns, ascending, every tile whose
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
  out value zeroed (`Covers(world, x, y)` tells "not covered" from "covered but clipped"), so an
  uncovered point never reads as an empty field. A footprint no grid holds whole is answered on
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
  exclude nothing. Method: pages hold only rounded sums, so exclusion re-derives the cell from
  the tile's persistent **source bin** — it walks every covering source except the excluded one,
  accumulating integer-plane contributions exactly and tent/bell products into 128-bit sums,
  then rounds and saturates with resolve's own math — bit-identical to re-resolving the tile
  minus the source. A page cell strictly inside `(−32768, 32767)` under an integer-plane
  (box/raster/turned) source is the exact unsaturated total, so it short-circuits to
  `page − contribution` (`BoxAt` reuses the emitter's band clip on a one-cell window; rasters
  run `EmitRaster` on a one-cell clip). Areas apply the same rule over the source's footprint
  inside the disk: a box whose tile minimum and maximum prove no cell saturates without it
  subtracts whole row spans arithmetically and never touches the page; every other tile falls
  back to the per-cell bin walk.
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
  conversion (`multiply + floor`, correctly rounded IEEE ops) and the grid-scale truncation to
  integer Q8 at grid creation. Mip baking, band weights, and the Q16.16 sampler are pure integer
  ops. A tile's resolved page is a pure
  function of its bin — slot order ascending, accumulation exact, rounding once — so pages are
  bit-identical across runs and machines.
- **Idle process**: an unchanged world finds an empty op queue and no dirty tiles — it prepares
  nothing, builds no bins, and returns immediately; 0 B warm.
  Moving a source to its stored position leaves the dirty lists empty. Page-map tombstones
  trigger compaction at the current capacity; only live-slot pressure requires growth.
  Tile resolution uses eight-cell AVX2 prefixes where available, with the existing four-cell
  SSE2/AdvSimd and scalar paths preserving the same integer sum and saturation semantics.

## Usage

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);   // 256×256 cells
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);   // or Stamp.Box(8, 8, 100) / Stamp.Tent(12, 12, 90)

int source = World.Place(world, layer, x: 128.5f, y: 64f, stamp, gain: 8);
World.Move(world, source, 129f, 64f);      // relocates — queued, applied at Process
World.SetGain(world, source, 4);           // adjusts weight in place
World.Process(world);                      // applies queued ops, resolves dirty tiles

short v = World.Query(world, grid, layer, 64, 32);      // cell read
long  t = World.Query(world, grid, layer, 0, 0, 32, 32); // region sum
short p = World.QueryAt(world, grid, layer, 128f, 64f);  // world-space point
short m = World.QueryMax(world, grid, layer, out var mx, out var my); // best cell
World.QueryGradient(world, grid, layer, 128f, 64f, out var gx, out var gy); // ±1-cell slope
World.Remove(world, source);
World.Free(world);                         // releases the world's memory and its handle
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
  includes tiles that resolved to zero and left the map.
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
- `rewind-restores-recorded-state` — after a two-window journal mixing a remove, a slot
  reoccupation, moves, gain changes, and a new place across box and tent stamps, `Rewind` +
  `Process` restores the recorded field's whole-layer sum bit-exactly; rolled-back ids stay
  inert (the field is unchanged by mutating them) while the revived source's original id
  mutates the field again, and a second `Rewind` with no checkpoint is inert.
- `signed-gain-exact` — ± gains add and subtract exactly, `SetGain` crosses zero and clamps at
  ±16, and removing a signed pair restores the zero baseline.
- `multi-layer-pooled-matches-scans` — 16 layers × 30 sources through three churn rounds
  match full, partial, and random-rect scans per layer.
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
  it, and each derived layer's changed tiles equal the union of its inputs'.
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

`--timing` adds min-over-20-rep lines for unchanged, move-200 churn, place-200
churn, a 200-place window rewound and reprocessed (~90 µs — the inverse window re-marks and
re-resolves the same footprint the forward one did), a place+remove-200 collapse window
(~4 µs — the mutations mark no tiles), tent-200
churn (16×16 tents re-resolved through transient `int64` sum planes — ~110 µs), bell-200 churn
(16×16 bells, same planes — ~150–195 µs), full-grid
sum, a 1022² partial-region sum, the best-cell query against a one-million-call
naive scan (0.1 µs vs ~5,600 µs on a 1024² layer), the gradient query (~5 ns per point), a
16-layer × 25-dirty move-400 process,
256² region reads (4000 sources, 1024² grid), and the feature rows: the best cell of a derived
`food − 2·threat` layer against two `QueryRegion` reads plus a scan (asserted equal), derived
upkeep on a churn frame, turning 200 vision cones, moving 200 domes, scaling 200 tents, 2,000
fading sources per tick, `TrySenseNearest`, and `Changed`. Timing receipts live in
the README (Ryzen 5 8500G, .NET 10, Release); the perf
pass behind them was `perf`-profile guided, receipts first.

### Measurement hazards

- dotnet-trace leaf attribution is unreliable on this runtime (two profiles contradicted ground
  truth). Attribute with Linux `perf` (`tools/stats/perf.sh record`, `DOTNET_PerfMapEnabled=1`)
  and take receipts from interleaved A/B runs or per-phase timers only.
- Tiered compilation can trap hot loops in tier-0 during a timing window: the tile resolve
  measured 558–1010 µs for a move-200 frame in one build and 155 µs with
  `DOTNET_TieredCompilation=0`, same binary. The engine's hot methods carry
  `MethodImplOptions.AggressiveOptimization` (under `NET`) so they never run tier-0; when timing
  a change that predates that, set `DOTNET_TieredCompilation=0` or the baseline may be garbage.
  The same trap bites callees too large to inline into an optimized caller: the partial-region
  sum's edge scanner measured 700 µs stuck in tier-0 and 12 µs once it carried
  `AggressiveOptimization` itself — audit any new standalone hot helper against
  `DOTNET_TieredCompilation=0` before trusting a number.
- Resolve is memory-bound at scale: every dirty tile zeroes and streams up to ~49 KB of
  transient planes through the stack (~80 KB on the wide path) before writing its 2,304 B
  block out to memory, and the bin build re-walks every
  live footprint on the layer. Nothing in the drain parallelizes — Process is serial by
  design — so wide-churn frame time is a single-core bandwidth number, not a pool number;
  measure before promising.
- Interleaved A/B on a hybrid-core part swings 5–8% between identical builds. Judge structural
  trades on interleaved minima, never on single runs.

## Unsafe proof

- **Lifetime**: the world arena (255 `WorldCtx`), stamp catalog (256 `StampVariant`), the
  shared zero page, and the world-revision column are
  process-lifetime pools allocated by the first creation call (`World.New`, `Stamp.New`,
  `Stamp.Box`) — never by static construction, so no static constructor on the assembly performs
  calls. This does not make mutable Gi statics callable from Burst through a precompiled DLL;
  Unity records with Burst and invokes Gi from non-Burst code. World contents are
  `NativeHeap`/`NativeBuffer` blocks owned by the context: the grid array and `LayerData`
  arrays, the `SourceColumns` (positions, stamp, layer, gain, angle, scale, schedule columns,
  liveness, the free-slot chain, and the generation bytes), the op queue with its per-slot
  pending index column, the op journal (op copies appended by `Process` while recording,
  consumed by `Rewind`, cleared by `Record`, `StopRecording`, and `Clear`, with the pending tail
  zeroed at column growth so fresh slots read "no op" and the whole column wiped by `Clear`),
  the timer heap and schedule journal (reset by `Clear`, capacity retained), and the
  source-batch scratch (row, link, and lookup buffers). Per grid, the snapshot state: the
  per-slot `Placement` array and `PreparedFootprint` array, sized to the source count and kept.
  Per (grid, layer): the `PageMap` arrays (keys, block pointers, used bytes, and the optional
  tile directory), the `SourceBin` array plus its flat `BinEntries` slot list, the dirty and
  changed lists, the dirty flags and tile epochs (allocated together by the layer's first marked
  tile on that grid and kept until `World.Free`), and
  the max pyramid: a flat `int16` slot array plus per-level offset and side tables, allocated
  zeroed by the layer's first resolved tile and freed by `World.Clear` and `World.Free`.
  A tile block is a single 2,304 B allocation — page, summary slot, and row summaries are one
  block with no attachments — owned by its page-map slot and freed exactly when the tile
  resolves to zero, the world is cleared, or the map is disposed. Every accumulation plane —
  the 33×33 `int64` difference plane, the dense plane, the tent and bell sum planes, the
  impulse planes, and the wide-path `WideSum` planes — is `stackalloc` scratch inside the
  per-tile resolve call: it is zeroed or fully emitted before any read, never aliases another
  tile's scratch, and dies with the frame, so no accumulation storage survives `Process`.
  The shared zero page is allocated once with the arenas and never written afterwards; derived
  recombination reads it for absent input pages.
  `Stamp` variants — base samples, mip chain, and box constants — are catalog-owned until
  `Stamp.Free` succeeds (no live source, pending op, or journal entry in any world names the
  stamp, so no resolve, rewind, or exclusion can still read it), and otherwise for
  the process. The worker pool (background threads plus its native `_parked` flag array) is
  created lazily by the first ≥8,192-row column query or source batch and lives for the
  process; resolve work never reaches it. `QueryRegion` writes only the caller's
  destination; `ChangedTiles` copies from the changed list (stable between `Process` calls,
  swapped — never written in place — on the owning thread) into the caller's buffer. `World.Free` releases each allocation listed above exactly once on the
  owning thread: every live tile block through `FreeBlock`, then per layer the page map, dirty
  flags, epochs, dirty and changed lists, bins, and max pyramid, then the layer array of each
  grid, the grid array, the recipe table, and every `SourceColumns` and world buffer. It then zeroes
  the context; a context without a grid array is not a world, so `GetContext` returns null for it
  and every handle-taking call reads a freed handle as absent. No pointer escapes an owner: handles
  are bytes, source ids carry a generation, and the pool holds a world pointer only inside
  one column or batch round.
  Source-batch rows, command links, and lookup slots are
  context-owned native buffers, retained for reuse and released by `World.Free`. `ApplyBatch`
  pins managed input/output spans for its entire call, including every internal worker join;
  native callers retain their buffers unchanged for the same interval. It retains no input or
  result pointer after returning. Source-column and operation-buffer growth finishes before
  write workers see pointers; preparation workers have joined before operation growth.
- **Aliasing**: a tile block is written only by its own resolve call on the owning thread;
  the resolve's accumulation planes are that call's `stackalloc` frame, so two tiles never
  share scratch and no plane outlives its writer. The bins a resolve reads were built earlier
  in the same `Process` on the same thread, and the `Prepared`/`Footprints` arrays it reads
  are written by the prepare step before any resolve runs — within one `Process`, each buffer
  has exactly one writer phase then one reader phase. The per-tile `previous`-row accumulator
  is a cleared stack row per resolve call. `PageMap` mutation happens only through its owning `LayerData`
  pointer on the thread that called `Process`. A derived recombination reads input pages (and
  the never-written shared zero page) and writes only its own layer's blocks, pyramid, and
  dirty list; its inputs have lower ids and are complete before it runs, and it never runs
  concurrently with a resolve. The partial-region sum reads page rows only
  inside `[0,32)×[0,32)` of a live block and writes nothing. Pyramid updates alias nothing
  outside their owning `LayerData`: each dirty tile touches exactly its own level-0 slot, and
  the propagation walk reads one 64-slot node plus one parent slot per level; `QueryMax` reads
  pyramid slots, page maps, and live pages only. Node scans honor the node's valid child
  rectangle, so no slot past the grid's tile count is read as data; the `Vector128` row compare in the
  descent may load past the valid prefix of a row but masks those lanes out of the match, and
  the loaded bytes stay inside the 128 B node, while `FirstEqualFlat` and `FirstEqualSpan` loop
  only over full 16-lane or 8-lane vector steps and finish any sub-vector remainder with scalar reads.
  Columnar sensing (`SenseColumn`, `SenseGradientColumn`, `SenseAreaColumn`, `SenseMaxColumn`,
  `SenseNearestColumn`) borrows caller input and output columns only for the synchronous call,
  validates non-null pointers when `count > 0`, clears `(count + 63) >> 6` mask words when an
  optional mask pointer is supplied, and writes only indices `0..count-1` of the output columns.
  Batch input bytes and the written result prefix must be disjoint; overlapping spans are
  rejected before any writes. One batch row owns each distinct source slot, including all
  remove/recreate operations on that slot. Preparation writes only its row's private state.
  The caller assigns distinct operation indexes before source writers start, so source columns,
  pending indexes, free links, and operations have one writer each. Repeated targets fall back to grouping;
  the unique-update probe temporarily tags pending indexes and restores every tag before
  preparing workers or falling back. No external Gi call may observe that probe.
- **Alignment**: tile blocks, native buffers, and page-map arrays
  use 64-byte aligned allocations. World/grid/layer/catalog metadata uses `AllocZeroed` with
  natural alignment; raster samples require only byte alignment. SIMD accesses use unaligned
  load/store semantics. The transient planes are `stackalloc long`/`WideSum` arrays —
  8-byte-aligned at minimum, only ever accessed by scalar 8-byte ops or unaligned-safe 128/256-bit
  vector loads whose four or two lanes each carry a naturally aligned element. The AVX2 resolve
  walks the difference plane four `int64` cells per step: each 128-bit lane computes
  its own prefix, the low lane's final sum carries into the high lane, and the fourth sum carries
  to the next group through a permuted carry. Saturating pack results permute 32-bit chunks into
  cell order before storing
  four `int16` values; the same permuted cells widen into the page-sum, row-sum, and row-max
  accumulators, one horizontal add per row and per tile. Groups cover exactly cells 0–31 and
  never read padded columns or
  beyond the 32-cell scratch/page rows. Raster taps widen unaligned 4-byte sample reads within
  the padded allocation. Pages sit at offset 0 of their 64 B aligned block, so page rows are
  64 B aligned, and `QueryRegion`'s vectorized copy/zero fills use unaligned-safe 256-bit (or
  128-bit) stores with scalar tails, so any caller destination alignment is correct. Partial
  region sums widen page rows to `int32` vectors with the same unaligned-safe loads and scalar
  tails; a strip is at most 32 shorts per row, so the `int32` row accumulators and per-tile
  horizontal add cannot overflow (32 · 32768 < 2^20), and the result joins the `int64` region
  total. The sum
  slot sits at a 64 B block offset and is written as one naturally aligned `int64`; the page max
  sits at `SumOffset + 8` and the page minimum at `SumOffset + 10` as naturally aligned `int16`s
  inside the same padded slot, and the row summaries follow it — the `int32` row sums at
  `RowSumOffset` (a 64 B multiple) and the `int16` row maxima after them — each element naturally
  aligned. Derived combines
  load and store 16 or 8 cells per step at cell offsets that are multiples of the vector width
  from a 64 B aligned page, through unaligned-safe loads and stores. Max
  pyramid nodes are 64 `int16` values at 128 B offsets inside a 64 B aligned buffer, so the
  full-node AVX2 max loads stay inside the allocation; partial nodes scan scalar. On
  netstandard2.1 (Unity) `NativeHeap` backs onto `Marshal.AllocHGlobal`
  with platform-natural alignment instead of 64-byte `AlignedAlloc`, and no SIMD paths compile:
  every access in that build is a naturally aligned scalar load or store, so the weaker
  alignment guarantee cannot be observed.
  Managed command/result arrays have their normal CLR alignment. Native spans must point to
  naturally aligned `SourceCommand`/`SourceResult` storage; no SIMD loads depend on their address.
  Borrowed `TransformRange` columns follow the same rule at their real widths — `int` ids,
  `float` positions/headings/scales, `ulong` mask words — with no per-row packing or decoding.
  Batch scratch uses the same aligned native buffers.
- **Concurrency**: `ApplyBatch`/`Sync`/`SyncTransforms`/`Place`/`Move`/`SetGain`/`Remove`/`Process`/`Record`/`StopRecording`/
  `Rewind` are serial per world; different
  worlds may run on different threads. `ProcessAll` is that serial-per-world rule walked over
  the allocated ids, ascending, on the calling thread — it adds no parallelism claim, it only
  packages the loop, and freed slots read as absent to it exactly as they do to `Process`.
  World creation, `World.Free`, `World.CopyWorldIds` (which reads the world count and each
  slot's `Grids` pointer), `World.ProcessAll`, and stamp catalog mutation (`Stamp` creation
  and `Stamp.Free`, which scans every world for references) are process-wide scans:
  serialize them with one another. `Grid.Count`, `Grid.CopyIds`, `Grid.TryGetSpec`,
  `Layer.Count`, and `Layer.CopyIds` read the owning world's counts and grid records —
  per-world reads under the same serial-per-world rule as that world's other calls.
  Mutations only append to or merge within the owning
  world's op queue — a merge targets the one op its slot's pending index names, an index that
  only `Process`'s commit clears. `Rewind` runs entirely on the owning thread: it writes source
  columns, merges into the same op queue, and rebuilds the free chain in place, and the
  inverse ops it enqueues take effect at a later `Process`. `Process` itself is
  wholly single-threaded: prepare, bin building, the op commit, every tile resolve, derived
  recombination, epoch stamping, and the dirty/changed rotation all run on the calling
  thread, so page-map growth, block allocation, `InDirty` clears, pyramid updates, and buffer
  resizes never race anything — and because resolve scratch lives in the stack frame, there
  is no shared scratch to guard at all.
  The only concurrency inside Gi is the worker pool's two client rounds, and neither touches
  tiles: column queries fan `SenseColumn`/`ChangedMask` rows out in 1,024-row chunks claimed
  through an atomic cursor (one writer per output cell and per mask word), and source batches
  fan preparation and application out in 128-row chunks (one row per distinct slot, one
  writer per row). Pool ownership is a compare-and-swap flag; a contending caller runs its
  ranges inline and does not read or modify the pool statics. Warm pooled rounds allocate
  0 B: the pool, its parked flags, and its wake events are sized at creation.
  Every pool round hands off
  through a parked gate: each worker publishes itself parked (a volatile flag per worker)
  immediately before blocking on its wake event, and a round initiator spins until every worker
  is parked before it writes any round state (world and command pointers, cursor, count,
  phase). Wake events are then set — set/wait are full fences, so workers observe the
  published state — each worker consumes exactly one signal per round, so an
  `AutoResetEvent`'s coalescing can never absorb a round, and each worker decrements the
  remaining count after its last access while the caller spin-joins on that count before
  clearing pointers, resizing buffers, committing results, or releasing pool ownership.
  World creation, `World.Free`, and stamp catalog mutation (creation and `Stamp.Free`, which
  scans every world's source columns, op queue, and journal) are also serialized
  across worlds because their arenas and counts are process-wide, `Stamp.Free` must not
  overlap any world's mutation or `Process`, and `World.Free` must not overlap any call that
  takes the freed handle. Pool initialization rides the
  same rule: the first creation call allocates both arenas before any handle exists, and handles
  only originate from creation calls, so no query can observe an uninitialized arena.
  `Fade`/`Expire`/`Turn`/`Scale` are mutations like `Move`; timers run on the owning thread at
  the start of `Process`, and epochs are written on that same thread in the publish pass.
  `Query`/`QueryRegion`/`QueryMax`/`QueryGradient`/`ChangedTiles`/`Changed`/
  `Covers`/`Tick`/`TrySense*` read resolved pages, page sums, page maxima and minima, row
  summaries, source bins,
  pyramid slots, changed lists, tile epochs, source columns, and the pending op queue only, and
  may run concurrently with each other,
  never with mutation or process. Sensing writes nothing outside its own stack frame: each call
  stackallocs its scratch (span rows, under 9 KB of per-tile arrays for an excluded area,
  a single cell for an excluded point), so concurrent sensing threads never share scratch.
  No source-batch worker changes a heap, journal length, free head, source count, or page
  map — the caller commits those values after joining. Hosts may record independent buffers
  while Gi readers run; they must join producer writes and Gi readers before `ApplyBatch` or
  `Process`. The
  module skips local zero-initialization (`SkipLocalsInit`): every `stackalloc` buffer is
  written before it is read — band and span arrays are filled up to the count their readers
  use, the transient planes are cleared or fully emitted first, and the resolve row
  accumulator is cleared per tile.
- **Bounds**: source batches reject more than 2^24 commands before multiplying byte lengths.
  Creation references name earlier `Place` indexes, so results are initialized before those
  references are read. Generation and liveness validation precede source-column access. The
  planner reserves at most `min(command count, old source count + creation count)` rows and
  stores at most one row per distinct slot. The hash table is a power of two with at least
  twice that row capacity; command links point forward within the input. Source capacity is
  capped at 2^24 slots. All source, operation, journal, and timer reservations finish before
  workers can write their ranges; row chunks clamp to the actual affected-row count. Layer and
  stamp validation precede new-source grouping. Result writes stop at the command count.
  Derived recombination reads exactly the 1,024 cells of each input page — the
  shared zero page holds 4,096 B, so an absent input reads zeros in bounds — and writes the
  1,024 cells of its own page; `int32` totals stay inside `±2^31` by the weight bound above.
  Stamps clip to grid rects before marking (extents clamp to the grid size in Q8;
  grids whose `ScaleQ8` truncates to 0 are skipped); a prepared footprint's tile range is
  clipped to the grid's tiles, and `TileBake.Touches` re-tests every candidate tile against
  the placement's cell support before a bin counts it, so no bin entry can name a tile its
  source cannot reach and no emitter writes outside its plane. Tile-local box corners land in
  `[0,32]×[0,32]` of the transient 33×33 difference plane (rows 0–32 exist for the exclusive
  far edge; column 32
  is written but never read, by design of the half-open prefix form). Tent and bell emissions
  write only cells `[max(First, tile), min(Last, tile + 31)]` per axis — inside `[0,31]×[0,31]`
  of their 32×32 planes — and impulse writes land at offsets `ImpulseRun` masked into
  `[0,31]` per axis. The narrow
  path's `stackalloc` extent is the sum of its four plane sizes plus the impulse pair, bounded
  by the tile's own plane mask; the wide path fixes it at 2,113 `long` plus 4,096 `WideSum`s.
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
  Bin entries are bounded by construction: `BinEntries` is sized to the summed tile counts of
  its own build pass, every write index is `bin->Start + count` with `count < ` the tile's
  total, and `SnapshotExcluded` reads `bin->Count` entries inside that slice.
  Sensing clips every disk to `[0, Size)` before touching a tile, computes spans in `int64` Q8
  (reach clamps to 2^24 Q8 so `reach²` cannot overflow), and reads pages only at rows
  `0..31`, columns `0..31`. One-cell source evaluation writes one `int` on the stack:
  `EmitRaster` with `x1 = min(x1, cx+1)` clips its loops to that single cell. Smooth planes
  accumulate in `int64` — or 128-bit `WideSum` beyond the 4,088-source narrow bound — with
  wrap-around inside a tile; only each cell's final sum must fit, which the
  4,096-kernel stacking bound above guarantees.

## Tool inspection

`tools/stats` reads internal state through friend access, without reflection, counters on the
data path, or additional public types. `Inspection.Read` validates the world handle, scans
source liveness and grid/layer metadata, walks live page-map slots, and returns a pointer-free
value snapshot. Its cost
is O(source slots + grids × layers + map slots + catalog stamps); it does not scan cells.
Memory reports retained capacity, which is honest after `Clear` because capacity survives it:
the grid and layer arrays, `SourceColumns` at their slot capacity, the per-grid `Placement` and
`PreparedFootprint` arrays, the per-layer `SourceBin`/`BinEntries` bins, dirty flags and epochs,
dirty and changed lists, page-map slot arrays and tile directories, the per-(grid, layer) max
pyramids, every live tile's 2,304 B block reported as its page, its 64 B summary slot, and its
192 B of row summaries,
source-batch scratch capacity, the deferred
op queue plus its pending column and journal, the schedule heap and journal, and shared
world/stamp arenas, the revision column, the zero page, and padded rasters. The selected world and
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
