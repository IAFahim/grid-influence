# Gi

Sparse tiled integer influence fields for .NET. One API — `World`, `Grid`, `Layer`, `Stamp` —
byte handles over unmanaged state, integer-exact deposits, and **0 B** on warm `Process`/query
paths. Sources deposit into per-tile difference arrays once; `Process` resolves only dirty
tiles into `int16` pages; every query reads maintained state. Deterministic across runs,
machines, and SIMD on or off.

```sh
dotnet add package Gi.Influence
```

## The whole API in one screen

```csharp
using Gi;

byte world = World.New();
byte grid   = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f); // 256² cells over 256² world
byte layer  = Layer.New(world);                                     // one field channel
byte stamp  = Stamp.Bell(12, 12, 90);                               // Box / Tent / Bell / New(samples)

int me = World.Place(world, layer, 128.5f, 64f, stamp, gain: 8);    // persistent source
World.Move(world, me, 130f, 64f);        // queued — nothing touches a tile yet
World.SetGain(world, me, 4);             // also queued; ops merge per source per window
World.Process(world);                    // applies the queue, resolves dirty tiles once

short v    = World.Query(world, grid, layer, 64, 32);                 // one page read
long  sum  = World.Query(world, grid, layer, 0, 0, 32, 32);           // O(tiles) region sum
short best = World.QueryMax(world, grid, layer, out int bx, out int by);
World.QueryGradient(world, grid, layer, 130f, 64f, out int gx, out int gy);
fixed (short* dst = pixels)
    World.QueryRegion(world, grid, layer, vx, vy, vw, vh, dst);       // fills your buffer

World.Remove(world, me);                 // exact negation — no rebuild, no residue
World.Clear(world);                      // every live tile freed

World.Record(world);                     // checkpoint — journal applied ops
Explore();                               // mutate + Process freely
World.Rewind(world);                     // inverse ops; field returns bit-exactly
World.Process(world);                    // costs O(changes), not O(field)
```

One world can hold many grids and many layers — a 1024² grid for steering plus a 64² grid for
the minimap, a threat channel next to a food channel. Sources deposit into every grid they
overlap at each grid's own scale.

## Four stamp kinds

These dumps are actual `Query` output — each stamp placed at `(15.5, 15.5)` on a 32² grid with
`gain: 1`. All four peak at `value·gain` (90 here): the kernels share the box's units, so you
swap shapes without retuning.

**`Stamp.Box(w, h, value)`** — flat plateau, sharp edges. Threat zones, walls, zones of control.
Deposits as 4 corner writes per clipped tile band; the cheapest stamp there is.

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .  90  90  90  90  90  90  90  90  90  90  90   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

**`Stamp.Tent(w, h, value)`** — piecewise-linear pyramid. Aggro ranges, noise radius, anything
that should ramp linearly to an edge.

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   1   2   4   5   7   8   7   5   4   2   1   .   .
  .   .   2   7  11  16  20  25  20  16  11   7   2   .   .
  .   .   4  11  19  26  33  41  33  26  19  11   4   .   .
  .   .   5  16  26  36  47  57  47  36  26  16   5   .   .
  .   .   7  20  33  47  60  74  60  47  33  20   7   .   .
  .   .   8  25  41  57  74  90  74  57  41  25   8   .   .
  .   .   7  20  33  47  60  74  60  47  33  20   7   .   .
  .   .   5  16  26  36  47  57  47  36  26  16   5   .   .
  .   .   4  11  19  26  33  41  33  26  19  11   4   .   .
  .   .   2   7  11  16  20  25  20  16  11   7   2   .   .
  .   .   1   2   4   5   7   8   7   5   4   2   1   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

**`Stamp.Bell(w, h, value)`** — paraboloid dome. Auras, influence centers, soft gradients —
the smoothness you used to have to bake by hand.

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   3   7  11  14  15  16  15  14  11   7   3   .   .
  .   .   7  20  30  37  41  42  41  37  30  20   7   .   .
  .   .  11  30  44  55  61  63  61  55  44  30  11   .   .
  .   .  14  37  55  68  76  78  76  68  55  37  14   .   .
  .   .  15  41  61  76  84  87  84  76  61  41  15   .   .
  .   .  16  42  63  78  87  90  87  78  63  42  16   .   .
  .   .  15  41  61  76  84  87  84  76  61  41  15   .   .
  .   .  14  37  55  68  76  78  76  68  55  37  14   .   .
  .   .  11  30  44  55  61  63  61  55  44  30  11   .   .
  .   .   7  20  30  37  41  42  41  37  30  20   7   .   .
  .   .   3   7  11  14  15  16  15  14  11   7   3   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

**`Stamp.New(sbyte* samples, w, h)`** — your own w×h shape: rings, walls, sprites, arbitrary
masks. Bilinear sub-cell placement plus a baked mip chain, so it stays anti-aliased on coarse
grids. This disc was baked at stamp creation:

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .  10  36  45  36  10   .   .   .   .   .
  .   .   .   .  45  90  90  90  90  90  45   .   .   .   .
  .   .   .  45  90  90  90  90  90  90  90  45   .   .   .
  .   .  10  90  90  90  90  90  90  90  90  90  10   .   .
  .   .  36  90  90  90  90  90  90  90  90  90  36   .   .
  .   .  45  90  90  90  90  90  90  90  90  90  45   .   .
  .   .  36  90  90  90  90  90  90  90  90  90  36   .   .
  .   .  10  90  90  90  90  90  90  90  90  90  10   .   .
  .   .   .  45  90  90  90  90  90  90  90  45   .   .   .
  .   .   .   .  45  90  90  90  90  90  45   .   .   .   .
  .   .   .   .   .  10  36  45  36  10   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

Tents and bells are true sub-cell kernels — sampled at cell centres against the real kernel
centre — so a ⅛-cell `Move` glides the whole shape instead of stepping it, and 1-cell-wide
kernels interpolate between cells instead of vanishing. On grids so fine that a bell's
half-width reaches 128 cells it deposits through its baked paraboloid raster instead — slower
per deposit, never invisible.

## Sense the world, not the grids

Gameplay code asks about world coordinates, not grid handles. `TrySense*` pick the right grid
by one deterministic rule — a grid that holds the whole query wins, then the finest, then the
lowest id — and the `bool` is `true` only when the answer is complete:

```csharp
// "how threatened am I" — excluding my own aura, bit-identical to Remove+Process
int myAura = World.Place(world, herd, x, y, Stamp.Bell(8, 8, 80), gain: 4);
if (World.TrySense(world, threat, x, y, exclude: myAura, out short danger))
    Flee(danger);

World.TrySenseArea(world, threat, x, y, reach: 20f, out long nearby);        // world-area total
World.TrySenseMax(world, food, x, y, reach: 40f,
                   out short best, out float fx, out float fy);              // hunt target
World.TrySenseGradient(world, food, x, y, out float gx, out float gy);       // per-world-unit
bool onMap = World.Covers(world, x, y);
```

Every `TrySense` return value rules out a failure the grid-handle API leaves to the caller:

| trap | grid-handle read | grid-free read |
| --- | --- | --- |
| point outside every grid | `0` — reads as safe | `false`, `Covers` is `false` |
| query straddles a fine grid's edge | `0`s outside — sources vanish | answered whole on the grid that holds it |
| same field, coarse vs fine area sum | raw sums differ by cell-area ratio | totals normalized to world area — identical |
| agent senses its own aura | reads its own `720` | `exclude: id` → exactly what `Remove`+`Process` gives |
| source moved this frame | — | exclusion follows the *applied* position until `Process` |

Three rules still apply: reads see the last `Process` (one frame of latency), cells saturate at
±32,767, and a layer meant to detect presence should hold same-signed sources (opposing signs
cancel).

## Speed

i9-14900K, .NET 10, Release, min over reps. The workload is 4,000 box sources on a 1024² grid
unless noted; `process` rows include the mutations.

| operation | cost | note |
| --- | ---: | --- |
| idle `Process` | 0 µs | empty dirty list |
| `Query` per cell | 3.3 ns | one page lookup |
| `TrySense` point | 11 ns | grid picked for you |
| `TrySenseGradient` | 20 ns | central difference, per world unit |
| `TrySenseArea` r=8 / r=64 | 0.26 / 2.0 µs | disk sum, world-area units |
| `TrySenseMax` r=32 | 1.1 µs | strongest cell + position |
| `exclude: self` point / area r=24 bell | 0.02–0.10 / 5.3 µs | exact aura removal, no rebuild |
| place/move 200 boxes + `Process` | ~90–100 µs | deferred ops, pooled resolve |
| move 200 tents 16×16 + `Process` | ~220 µs | ≤20 impulses per axis per tile |
| move 200 bells 16×16 + `Process` | ~750 µs | third-order impulse chain |
| place+remove 200 in one window | 10.5 µs | ops collapse, no deposits at all |
| `Rewind` a 200-place window + `Process` | ~100 µs | inverse ops, O(changes) |
| `Query` region sum 1022² of 1024² | 11.2 µs | per-tile sums + edge strips |
| `Query` full-grid sum 1024² | 0.8 µs | one `int64` per live tile |
| `QueryMax` over 1024² | 0.6 µs | max-pyramid descent |
| same via 1M `Query` calls | 4,869 µs | ~8,000× slower |
| move 400 across 16 layers + `Process` | 93 µs | one flattened worker queue |
| `QueryRegion` fill 256² | 6.8 µs | straight into your pixel buffer |

Against the naive implementation every field library starts with — a dense `int[]` per layer,
cleared and redrawn from every source each frame (4000 sources, 200 moves, Ryzen 5 8500G):

| frame work | naive grid | Gi |
| --- | ---: | ---: |
| nothing moved | 620–680 µs | 0 µs |
| 200 moves + process | 625–685 µs | 100–130 µs |
| full-grid sum | 375–390 µs | 0.9 µs |

The gap is structural: naive pays O(grid + sources×stamp-area) every frame, Gi pays O(what
changed) and reads maintained state. Memory flips too — the naive grid allocates N² ints per
layer up front (1 GB at 16384²); Gi allocates ~6.5 KB per live tile, so an empty world of any
size costs zero.

## How it works

- **Deposits are incremental and deferred.** Each live tile owns one 6,528 B block: a 33×33
  `int32` difference array, a 32×32 `int16` page, an `int64` page sum, an `int16` page max.
  `Place`/`Move`/`SetGain`/`Remove` only merge into a per-world op queue — one net op per
  source per window — and `Process` applies a retract-and-apply pair per op. Integer adds
  commute, so any mutation sequence collapses exactly, and a source placed and removed inside
  one window never touches a tile.
- **Each stamp kind deposits at its own sparsity.** A box is 4 corner writes per tile band. A
  raster samples bilinearly into the tile's dense buffer (allocated lazily). A tent writes its
  second derivative's impulses; a bell its third's — ≤20 `int64` impulse writes per axis per
  touched tile, so a smooth kernel costs a handful of adds per tile instead of O(area) cell
  writes. Products telescope exactly; one rounding per cell at resolve.
- **Resolve is per dirty tile.** A 2D prefix sum turns the difference array into cells, the
  impulse chains add their contribution, the sum saturates to `short` *after* summation (so
  cancellation stays exact), and the page's sum and max are recorded for queries. Tiles that
  resolve to zero free their block and leave the map — a missing page reads as 0.
- **Queries never scan.** Region sums add one `int64` per fully covered tile; `QueryMax`
  descends a per-(grid,layer) max pyramid; `TrySense*` reuse the same maintained structures.
- **Big frames fan out.** ≥32 dirty tiles total (across all grids and layers) splits resolve
  over a fixed worker pool; large mutation batches fragment by tile so no two workers share a
  tile. Pooled and serial output are bit-identical.
- **Handles, not objects.** Everything public returns `byte` ids into unmanaged arenas; source
  ids pack a generation over the slot index, so stale handles are inert no-ops and slots
  recycle. No managed allocation on the data path.
- **Deterministic.** Integer-only field math; deposits are commutative adds. Pages are
  bit-identical across runs and machines, scalar or SIMD.

Full semantics and the unsafe lifetime/aliasing/alignment/concurrency proof live in
[`docs/model.md`](docs/model.md).

## Receipts

Every claim above is asserted, not documented. `dotnet run --project benchmarks -c Release --
--verify` runs all 33 receipts before printing a single timing — with and without hardware
intrinsics:

| receipts | what they pin down |
| --- | --- |
| `process-matches-oracle`, `process-deterministic`, `remove-restores-baseline` | deposits equal a per-cell oracle; identical worlds, identical pages; removal is exact |
| `warm-process-allocates-0-bytes`, `warm-query-allocates-0-bytes`, `warm-sense-allocates-0-bytes` | 0 B on every hot path |
| `query-region-matches-cells`, `page-sum-matches-scan`, `region-sum-matches-cell-scans`, `query-at-matches-deposits` | every query surface agrees with per-cell truth |
| `query-max-*`, `gradient-matches-central-differences` | argmax and gradients equal full rescans |
| `changed-tiles-match-drain`, `deferred-window-matches-stepped-processing` | the changed feed is exact; batched == stepped processing |
| `tent-matches-impulse-oracle`, `bell-matches-paraboloid-oracle` | both kernels equal independent oracles written from the spec, not the engine |
| `kernels-share-box-units-and-centre`, `kernels-move-smoothly`, `kernels-hold-strength-at-every-scale` | peaks read `value·gain`; sub-cell glides never step; widths 1–255 hold strength at scales 1/16–32 |
| `sense-*` (7 receipts) | grid-picking rule, disk scans, cross-grid conservation, exclusion == removal bit-for-bit |
| `source-slots-reuse-and-stale-handles-inert`, `rewind-restores-recorded-state`, `signed-gain-exact` | handle lifecycle, rewind, signed gains |
| `saturated-sum-clamps`, `cross-grid-sums-conserve-world-integral`, `multi-layer-pooled-matches-scans` | saturation, scale conservation, pooled determinism |

## Cookbook

**Threat maps.** Stamp every enemy's reach once; score candidates with point reads. Idle
`Process` costs nothing, so a static field is free every frame it doesn't change:

```csharp
foreach (var e in enemies)
    World.Place(world, threats, e.X, e.Y, e.Reach, e.IsElite ? 12 : 6);
World.Process(world);
var safest = candidates.MinBy(c => World.QueryAt(world, grid, threats, c.X, c.Y));
```

**Presence without self.** A sheep senses the herd's aura minus its own — the lone sheep in the
demo reads 6,430 from itself without `exclude`:

```csharp
World.TrySense(world, herd, x, y, exclude: myAura, out short others);
```

**Roads and patrols along a spline.** Sample a Bézier at about half the stamp width so
neighbors overlap; gain shapes the lane profile:

```csharp
for (var i = 0; i <= 96; i++)
{
    var t = i / 96f;
    var (x, y) = Bezier(p0, p1, p2, p3, t);
    World.Place(world, slowZone, x, y, slow, 4 + (int)(6f * MathF.Sin(MathF.PI * t)));
}
World.Process(world);
```

**Brush strokes.** Bake any falloff into a raster stamp once, then draw it along the path —
the mip chain keeps it smooth on coarse grids:

```csharp
var falloff = new sbyte[16 * 16];
for (var y = 0; y < 16; y++)
for (var x = 0; x < 16; x++)
{
    var d = MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f)) / 8f;
    falloff[y * 16 + x] = (sbyte)(100f * MathF.Max(0f, 1f - d));
}
byte brush = Stamp.New(falloff, 16, 16);
```

## Run it

```sh
dotnet build Gi.slnx -c Release -m:1
dotnet test Gi.slnx -c Release --no-build
dotnet run --project samples/world -c Release --no-build
dotnet run --project benchmarks -c Release --no-build -- --verify
DOTNET_EnableHWIntrinsic=0 dotnet run --project benchmarks -c Release --no-build -- --verify
dotnet run --project benchmarks -c Release --no-build -- --compare   # Gi vs the naive grid
dotnet run --project viz -c Release --no-build -- live               # http://127.0.0.1:8740
dotnet run --project tools/stats -c Release -- stats                 # internals as JSON
```

- `src/Gi` — the package (`net10.0` + `netstandard2.1`; Burst-callable query paths)
- `tests/Gi.Tests` — oracle tests (52)
- `benchmarks` — the 33 receipts and all the numbers above
- `samples/world` — console walkthrough; `samples/unity-demo` — sheep/wolf ecosystem on `TrySense`
- `viz` — static HTML render + live WebSocket ecosystem
- `tools/stats` — internal statistics and profiling
