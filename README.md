# Gi

Sparse tiled integer influence fields for .NET. One API — `World`, `Grid`, `Layer`, `Stamp` —
byte handles over unmanaged state, integer-exact deposits, and **0 B** on warm `Process`/query
paths. Sources deposit once; `Process` re-emits only the sources whose state changed and publishes only
the tiles they touched; every query reads maintained state — including layers derived from other layers, so "the best cell where food is
high and threat is low" is a 0.2 µs pyramid descent instead of a million-cell scan. Stamps turn,
grow, and fade on schedules that cost nothing on the ticks they don't step. Deterministic across
runs, machines, and SIMD on or off.

```sh
dotnet add package Gi.Influence
```

Editor and tooling integrations can use the separate [Gi.Validation](src/Gi.Validation/README.md) package for numeric validation. It shares Gi's constraints without referencing the runtime assembly.

## The whole API in one screen

```csharp
using Gi;

byte world  = World.New();
byte grid   = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f); // 256² cells over 256² world
byte food   = Layer.New(world);                                     // field channels
byte threat = Layer.New(world);
byte safety = Layer.Sum(world, food, 1, threat, -2);                // derived: food − 2·threat
byte cone   = Stamp.Cone(16, 90, arc: 90);                          // Box/Tent/Bell/New/Disk/Cone/Dome

int me = World.Place(world, threat, 128.5f, 64f, cone, gain: 8);    // persistent source
World.Move(world, me, 130f, 64f);        // queued — nothing touches a tile yet
World.Turn(world, me, MathF.PI / 4f);    // face north-east (radians)
World.Scale(world, me, 1.5f);            // grow by half
World.Fade(world, me, 0, ticks: 120);    // fade out over 120 Process calls…
World.Expire(world, me, ticks: 120);     // …and remove on the last one
World.Process(world);                    // sweeps changed sources, publishes dirty tiles once

short v    = World.Query(world, grid, threat, 64, 32);                // one page read
long  sum  = World.Query(world, grid, threat, 0, 0, 32, 32);          // O(tiles) region sum
short best = World.QueryMax(world, grid, safety, out int bx, out int by); // best cell, derived
World.TrySenseNearest(world, food, x, y, reach: 40f, threshold: 50,
                      out short near, out float nx, out float ny);    // closest cell ≥ 50
bool moved = World.Changed(world, threat, x, y, reach: 16f, since: lastTick);
int  tick  = World.Tick(world);
int  rev   = World.Revision(world);     // moves on clear/rewind/free/reuse
World.Token tok = World.Capture(world); // checked at consumption — ApplyBatch/Sync/SyncTransforms/Process
                                        // token overloads reject a stale world inside Gi

World.Remove(world, me);                 // exact negation — no rebuild, no residue
World.Record(world);                     // checkpoint — journal applied ops and schedules
Explore();                               // mutate + Process freely
World.Rewind(world);                     // inverse ops; field and schedules return exactly
Stamp.Free(cone);                        // return an unused stamp's id to the catalog
World.Free(world);                       // release the world's memory and its handle
```

One world can hold many grids and many layers — a 1024² grid for steering plus a 64² grid for
the minimap, a threat channel next to a food channel, and derived channels over both. Sources
deposit into every grid they overlap at each grid's own scale. Every public call, including
the ones this page never quotes, has a runnable example in
[`docs/examples.md`](docs/examples.md).

`World.ApplyBatch(world, commands, results)` ingests sealed source commands with ordered partial
updates, creation results, fades, and expiry. Large batches prepare and write source
slots in one sequential pass and mark them on the changed list; `Process` remains the explicit
deposit/tick boundary.
Hosts can record concurrently into their own buffers, then join producers and Gi readers
before ingestion; `World.Capture(world)` seals a checked token, and the token overloads of
`ApplyBatch`, `Sync`, and `Process` reject work recorded against a cleared, rewound, freed, or
reused world inside Gi — before reading payloads or touching state.
`World.SyncTransforms(token, ranges)` extends the same boundary to borrowed native pose columns:
ID + X + Y plus optional heading/scale per `World.TransformRange`, applied in range order with
gain and timers untouched, and stale rows reported as one invalid bit per row instead of a
per-entity result array.
See [the batching contract and C# example](docs/batching.md).

## Seven stamp kinds

These dumps are actual `Query` output — each stamp placed at `(15.5, 15.5)` on a 32² grid with
`gain: 1`. All of them peak at `value·gain` (90 here): the kernels share the box's units, so you
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

**`Stamp.Disk(radius, value, arc)`** — a round plateau with a one-cell anti-aliased rim.
Areas of effect, auras, zones that should not look square.

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .  16  38  45  38  16   .   .   .   .   .
  .   .   .   9  60  90  90  90  90  90  60   9   .   .   .
  .   .   9  76  90  90  90  90  90  90  90  76   9   .   .
  .   .  60  90  90  90  90  90  90  90  90  90  60   .   .
  .  16  90  90  90  90  90  90  90  90  90  90  90  16   .
  .  38  90  90  90  90  90  90  90  90  90  90  90  38   .
  .  45  90  90  90  90  90  90  90  90  90  90  90  45   .
  .  38  90  90  90  90  90  90  90  90  90  90  90  38   .
  .  16  90  90  90  90  90  90  90  90  90  90  90  16   .
  .   .  60  90  90  90  90  90  90  90  90  90  60   .   .
  .   .   9  76  90  90  90  90  90  90  90  76   9   .   .
  .   .   .   9  60  90  90  90  90  90  60   9   .   .   .
  .   .   .   .   .  16  38  45  38  16   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

**`Stamp.Cone(radius, value, arc)`** — linear radial falloff: noise, light, smell, sight range.

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   3   9  14  15  14   9   3   .   .   .   .
  .   .   .   5  15  23  28  30  28  23  15   5   .   .   .
  .   .   3  15  26  36  43  45  43  36  26  15   3   .   .
  .   .   9  23  36  48  56  60  56  48  36  23   9   .   .
  .   .  14  28  43  56  69  75  69  56  43  28  14   .   .
  .   .  15  30  45  60  75  90  75  60  45  30  15   .   .
  .   .  14  28  43  56  69  75  69  56  43  28  14   .   .
  .   .   9  23  36  48  56  60  56  48  36  23   9   .   .
  .   .   3  15  26  36  43  45  43  36  26  15   3   .   .
  .   .   .   5  15  23  28  30  28  23  15   5   .   .   .
  .   .   .   .   3   9  14  15  14   9   3   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

**`Stamp.Dome(radius, value, arc)`** — a round paraboloid: the smooth influence center most
influence-map tutorials start from.

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   5  17  25  27  25  17   5   .   .   .   .
  .   .   .  10  27  40  47  50  47  40  27  10   .   .   .
  .   .   5  27  45  57  65  68  65  57  45  27   5   .   .
  .   .  17  40  57  70  77  80  77  70  57  40  17   .   .
  .   .  25  47  65  77  85  87  85  77  65  47  25   .   .
  .   .  27  50  68  80  87  90  87  80  68  50  27   .   .
  .   .  25  47  65  77  85  87  85  77  65  47  25   .   .
  .   .  17  40  57  70  77  80  77  70  57  40  17   .   .
  .   .   5  27  45  57  65  68  65  57  45  27   5   .   .
  .   .   .  10  27  40  47  50  47  40  27  10   .   .   .
  .   .   .   .   5  17  25  27  25  17   5   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

Tents and bells are true sub-cell kernels — sampled at cell centres against the real kernel
centre — so a ⅛-cell `Move` glides the whole shape instead of stepping it, and 1-cell-wide
kernels interpolate between cells instead of vanishing. On grids so fine that a bell's
half-width reaches 128 cells it deposits through its baked paraboloid raster instead — slower
per deposit, never invisible.

Round stamps are evaluated per cell with exact integer roots and Q16 weights, so they also glide
at sub-cell steps and remove exactly. `Stamp.Free(stamp)` returns an id once no source, pending
op, or rewind journal in any world uses it — the 255-stamp catalog is a working set, not a
lifetime budget.

## Turn, scale, aim

`World.Turn(world, id, radians)` and `World.Scale(world, id, factor)` work on every stamp kind
and queue like `Move` — a turn, a scale, a move, and a gain change in one window cost one
retract-and-apply pair. An `arc` below 360° keeps a sector around the facing, so a vision cone
is one stamp that turns with its owner. `Stamp.Dome(7, 90, arc: 120)` turned 30°, and
`Stamp.Box(12, 4, 90)` turned 30°:

```
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .        .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .        .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .        .   .   .   8   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .        .   .  15  90  48   3   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .  11   7   .        .   .  60  90  90  81  36   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .  18  41  37  17   .        .  27  90  90  90  90  90  69  24   .   .   .   .   .   .
  .   .   .   .   .   .   .   .  11  51  72  59  42  22   .        .  27  78  90  90  90  90  90  90  57  12   .   .   .   .
  .   .   .   .   .   .   .  45  88  83  73  61  44  24   .        .   .   .  45  90  90  90  90  90  90  90  45   .   .   .
  .   .   .   .   .   .   .  44  86  81  72  59  42  22   .        .   .   .   .  12  57  90  90  90  90  90  90  78  26   .
  .   .   .   .   .   .   .  41  81  75  66  53  37  17   .        .   .   .   .   .   .  24  69  90  90  90  90  90  27   .
  .   .   .   .   .   .   .  37  72  66  57  44  28   7   .        .   .   .   .   .   .   .   .  36  81  90  90  60   .   .
  .   .   .   .   .   .   .  30  59  53  44  31  15   .   .        .   .   .   .   .   .   .   .   .   4  49  90  15   .   .
  .   .   .   .   .   .   .  22  42  37  28  15   .   .   .        .   .   .   .   .   .   .   .   .   .   .   8   .   .   .
  .   .   .   .   .   .   .  12  22  17   7   .   .   .   .        .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
  .   .   .   .   .   .   .   .   .   .   .   .   .   .   .        .   .   .   .   .   .   .   .   .   .   .   .   .   .   .
```

- **Exact.** Angles are binary (65,536 per turn) with a fixed Q14 polynomial sine; every turned
  or round cell is integer math, so retraction and self-exclusion recompute identical values.
- **Angle 0 is the axis-aligned path.** An unturned source deposits through the exact
  axis-aligned emitters — turning away and back restores the field exactly.
- **Scale is size.** A box of width `w` at scale 2 is bit-identical to a box of width `2w`; one
  stamp serves every radius and every facing.
- **Stamps never clamp to the grid.** A stamp wider than its grid covers the grid; 0.6 shifted
  oversized boxes and truncated oversized rasters.

## Layers that combine

Influence-map AI combines channels — "food minus twice the threat", "contested where both sides
are present", "food, but only where it is safe". A derived layer is an ordinary layer handle
whose cells always equal a formula over other layers, maintained by `Process` only on the tiles
whose inputs changed:

```csharp
byte safety    = Layer.Sum(world, food, 1, threat, -2);           // weights ±32,767, optional >> shift
byte calm      = Layer.Sum(world, threat, -1);                    // negate: QueryMax finds the minimum
byte contested = Layer.Min(world, mine, theirs);                  // > 0 only where both are present
byte frontier  = Layer.Max(world, contested, calm);               // chains nest
byte grazing   = Layer.Mask(world, food, threat, short.MinValue, 40); // food where threat ≤ 40

short best = World.QueryMax(world, grid, safety, out int x, out int y);
World.TrySenseMax(world, grazing, sheepX, sheepY, 30f, out _, out float gx, out float gy);
```

- A derived cell reads exactly `f(Query(a), Query(b))`, saturated — what you would compute from
  the inputs yourself. Every operator maps zero inputs to zero, so derived layers are as sparse
  as their inputs.
- Every query works on them: `QueryMax`, region sums, `TrySense*`, gradients, nearest,
  `ChangedTiles`, `Changed` — and `exclude:` still equals `Remove` + `Process` bit for bit.
- Best cell of `food − 2·threat` on a 1024² field: **0.18 µs** for `QueryMax` on the derived
  layer, against 860 µs for two `QueryRegion` calls plus a scan. Upkeep is one vectorized pass
  per changed input tile — about 50 µs on a frame that moved 60 food bells.
- Create one at any time; its first `Process` fills it from the inputs' live tiles.
- Derived layers are not free slots: they draw from the same `World.MaxLayers` (32) table as
  deposited channels, so plan `raw + derived ≤ 32`. The cap is structural — recipes use 32-bit
  layer masks — not a memory knob.

## Time without decay passes

`World.Fade(world, id, gain, ticks)` ramps a source's gain to a target over the next `ticks`
`Process` calls; `World.Expire(world, id, ticks)` removes it on the last. A tick is one
`Process` call — Gi has no clock — and `World.Tick(world)` counts them.

```csharp
// scent trail: drop a crumb each step, let it fade and vanish on its own
int crumb = World.Place(world, scent, x, y, Stamp.Dome(3, 40), gain: 16);
World.Fade(world, crumb, 0, ticks: 300);
World.Expire(world, crumb, ticks: 300);
```

The classic decay pass multiplies every cell every tick — about 330 µs per tick on a 1024² map
whether anything changed or not. Gi schedules each fade's integer steps instead: a fade from 16
to 0 changes the field on exactly 16 ticks, a dense timed list fires only those, and a tick where
nothing steps costs nothing. 2,000 crumbs fading over 600–900 ticks cost 31 µs per tick on
average and 0 µs on quiet ticks. Scheduled steps are ordinary queued mutations, so they journal
and rewind: `Rewind` restores schedules changed inside the window and resumes pending ones with
exactly the time they had left.

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

World.TrySenseNearest(world, food, x, y, reach: 40f, threshold: 50,
                      out short meal, out float mx, out float my);     // closest cell ≥ 50
if (!World.Changed(world, threat, x, y, reach: 16f, since: lastDecision))
    return;                                                           // nothing moved near me
```

`TrySenseNearest` returns `short.MinValue` and the query point when no cell qualifies, so
`value >= threshold` tells found from not found. `Changed` reads per-tile epochs — the tick of
each tile's last publish — so agents can sleep until their neighbourhood changes, and
`ChangedTiles(world, grid, layer, since, dst)` serves minimaps and network sync at their own
cadence.

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

## Change-rate `Process`, sensors batch

`Process` costs in proportion to what changed, not to how it changed. Moving a source only marks
its slot; `Process` compares each marked slot's target (masked anchor, kernel, gain, liveness)
with what it last deposited and, where they differ, adds the negated old taps and the new taps
straight into the tile planes — box stamps as band-weighted row adds, other stamps from a cached
tap table — then publishes only the touched rectangles. A source whose masked anchor did not move
deposits nothing at any precision below `Exact`, and a tile only lands in `Changed` if its
published page actually differs. Every deposit is an integer add, so the result is bit-identical
to stepping the same mutations one `Process` at a time — asserted, not hoped.

The same batching serves agent loops. Kernels can take a Gi column like any other column:

```csharp
// fill a kernel column straight from the field (returns false only off every grid)
World.SenseColumn(world, threat, agentXs, agentYs, agentCount, threats, null);

// one call per swarm, not one Changed per agent — emits the 64-bit words kernels consume
World.ChangedMask(world, threat, agentXs, agentYs, reach: 24f, since: lastDecision,
                  count: agentCount, words: changedWords);

// move every agent that has a position column (sv slot space, ECS chunk, your own SoA)
World.Sync(world, sourceIds, newXs, newYs, count);

// borrow whole pose columns — optional heading/scale, null preserves; one invalid bit per row
World.SyncTransforms(token, ranges);
```

`SenseColumn` reads agent coordinates as `float*` and writes `short*` values (plus optional
whole-tile flags); `ChangedMask` writes one `ulong` per 64 agents, zeros for the quiet ones;
`Sync` is the fused form of `Move` for a whole column — gen-validated, coalescing, zero managed
allocation. `SyncTransforms`
consumes the same host-native columns for full poses — 12 B per position-only row, 20 B with
heading and scale, no flattening into commands, no per-row results — and reports stale rows as
one mask bit instead.

## Speed

i9-14900K, .NET 10, Release, min over reps. The workload is 4,000 box sources on a 1024² grid
unless noted; `process` rows include the mutations.

| operation | cost | note |
| --- | ---: | --- |
| idle `Process` | 0 µs | empty dirty list, no due timers |
| `Query` per cell | 3.5 ns | one page lookup |
| `TrySense` point | 11 ns | grid picked for you |
| `TrySenseGradient` | 20 ns | central difference, per world unit |
| `TrySenseArea` r=8 / r=64 | 0.11 / 1.1 µs | disk sum, world-area units |
| `TrySenseMax` r=32 | 0.61 µs | strongest cell + position |
| `TrySenseNearest` r=32 | 0.86 µs | closest cell at a threshold |
| `Changed` r=16 | 58 ns | tile epochs since a tick |
| `exclude: self` point / area r=24 bell | 0.03–0.04 / 2.7 µs | exact aura removal, no rebuild |
| place/move 200 boxes + `Process` | ~80–95 µs | deferred ops, serial resolve |
| 4,000 bells 16×16 + 200 moves, `Process` stages | 818 µs total | prep/apply/resolve split via `--stages`; the 280 µs figure predates removing the worker pool |
| 65,536 transform updates via `ApplyBatch` | 2,061 µs | 31.8M rows/s — `UpdateTransform` snapshots preserve fades, 0 B warm (`--batch`) |
| 65,536 borrowed transform rows via `SyncTransforms` | 2,183 µs | 30.0M rows/s — host-native columns, 20 B/row pose, 1 bit/row mask, 0 B warm (`--sync`) |
| 100,000 borrowed transform rows | 3,389 µs | 29.5M rows/s — serial loop 1,854 µs, `ApplyBatch` 2,971 µs at this size (`--sync`) |
| place 200 tents 16×16 + `Process` | ~110 µs | exact sums per cell |
| place 200 bells 16×16 + `Process` | ~150–195 µs | exact sums per cell |
| turn 200 vision cones (r=16, 90°) + `Process` | ~320 µs | per-cell round kernel |
| move 200 domes (r=8) / scale 200 tents | ~135 / ~165 µs | |
| place+remove 200 in one window | 3.9 µs | ops collapse, no deposits at all |
| `Rewind` a 200-place window + `Process` | ~85–95 µs | inverse ops, O(changes) |
| `Query` region sum 1022² of 1024² | 11.6 µs | per-tile sums + edge strips |
| `Query` full-grid sum 1024² | 0.8 µs | one `int64` per live tile |
| `QueryMax` over 1024² | 0.5 µs | max-pyramid descent |
| same via 1M `Query` calls | 6,100 µs | ~12,000× slower |
| best cell of `food − 2·threat`, derived | 0.18 µs | vs 860 µs `QueryRegion` ×2 + scan |
| 2,000 fading sources, per tick | 31 µs mean, 0 µs idle | vs ~330 µs dense decay pass |
| move 400 across 16 layers + `Process` | 74 µs | serial dirty-tile drain |
| `QueryRegion` fill 256² | 6.8 µs | straight into your pixel buffer |
| `SenseColumn` per agent | 9.7 ns | 100k agents |
| `ChangedMask` per agent | 15 ns | reach 24, 100k agents |
| `Sync` per position | 13.8 ns | 100k, excludes `Process` |

And the crowd sweep — every source on one layer moving, `Process` alone timed (best of 10, pinned
core of an i9-14900K; `--crowd` reproduces it):

| workload | 25% moving | 100% moving | naive redraw |
| --- | ---: | ---: | ---: |
| 20,000 boxes 8×8, 1024² | 3,038 µs | 7,174 µs | 982 µs |
| 100,000 boxes 4×4, 1024² | 6,023 µs | 19,063 µs | 1,651 µs |
| 4,000 boxes 16×16, 1024² | 1,318 µs | 2,947 µs | 724 µs |

`Process` cost is linear in the sources that moved — there is no crowd rebuild — so a layer where
most sources jump every frame is the one shape where a full redraw is cheaper. The previous engine's
rebuild held 2,184 µs (20,000 boxes, 100% moving) flat; the change-rate engine pays 359 ns per
random-jump mover there, and issue #40 tracks closing that gap.

Against the naive implementation every field library starts with — a dense `int[]` per layer,
cleared and redrawn from every source each frame (4,000 sources, 200 moves, same machine):

| frame work | naive grid | Gi |
| --- | ---: | ---: |
| nothing moved | 625 µs | 0 µs |
| 200 moves + process | 646 µs | 108 µs |
| full-grid sum | 373 µs | 0.8 µs |

The gap is structural: naive pays O(grid + sources×stamp-area) every frame, Gi pays O(what
changed) and reads maintained state. Memory flips too — the naive grid allocates N² ints per
layer up front (1 GB at 16384²); Gi allocates ~6.5 KB per live tile (2.1 KB for a derived
tile), so an empty world of any size costs zero.

## Reference frames (1M entities, one core)

Deployments are measured against reference frames (`--frames all --precision exact|cell`) on
one pinned core (0 B allocated per frame). Each base layer
chooses its snapping precision (`Layer.New(world, Layer.Precision.Cell)` for cell-snapped
deposits, `Layer.Precision.Exact` for 1/256-cell sub-cell motion); a source only enqueues work
when its snapped key changes.

What each user-issued call costs today (same machine and mode as the tables above):

| user-issued call | cost | note |
| --- | ---: | --- |
| `World.Place` | 18 ns | deferred — journals the deposit, no tile work |
| `World.Move` / `Turn` / `Scale` / `SetGain` | 2.5–3.5 ns | deferred |
| place + remove pair | 16 ns | both commands, collapse when co-scheduled |
| `ApplyBatch` source update | 22 ns | 4,096-command batches, 44.6M updates/s |
| `SyncTransforms` row | 14 ns | 4,096 borrowed rows, 71.6M rows/s |
| `Stamp.Box`/`Tent`/`Disk`/`Cone`/`Dome` | 10–13 ns | once per stamp, not per frame |
| `Stamp.Bell` 16×16 / raster `New` 16×16 | 0.71 / 0.25 µs | kernel and mip bake, once per stamp |
| `TryGetSourceSpec`/`TryGetRecipe`/`TryGetSpec`/`TryGetPrecision` | 1.6–3.2 ns | introspection |
| `Count` / `CopyIds` / `Tick` / `Revision` / `Covers` | 0.5–2.9 ns | |
| world lifecycle `New`+`Grid`+`Layer`+`Free` | 0.42 µs | once |
| `Clear` + re-place 2,000 sources | 35 µs | retained-capacity clear, no realloc |

Measured on one pinned Zen 4 core of the Ryzen 5 8500G (`--frames all --precision exact|cell`,
single thread, 0 B/frame):

| reference frame (1M entities, 1 Zen 4 core) | `Exact` (1/256 cell) | `Cell` (`PhaseBits` 0) | stages at `Cell` (`Sync` / `Process` / reads) |
| --- | ---: | ---: | --- |
| **B4** idle (1M placed, no changes, no reads) | 50 ns | 40 ns | `Process` 40 ns |
| **B2** sparse (1 mover / 200 movers, no reads) | 1.84 µs / 280 µs | 1.41 µs / 158 µs | 62.5% crossing rate |
| **W** world (2048², 250k movers @ 0.1 cell/f, 740k static, 10k timed, 2k churn, derived reads + 1k disks) | 63.1 ms | **25.2 ms** | 2.0 ms / 12.6 ms / 10.3 ms (12.6% crossing) |
| **C** crowd (1024², 1M movers @ 0.1 cell/f in Morton order, 1M value+gradient + 1k disks) | 120.5 ms | **52.7 ms** | 7.4 ms / 13.5 ms / 31.9 ms (12.6% crossing) |
| **B1** legacy (4096², 1M 8×8 boxes moving 0.5×0.25 cell/f, random order, all columns) | 320.1 ms | 282.7 ms | 13.9 ms / 95.0 ms / 177.8 ms (62.5% crossing) |

See [`ROADMAP.md`](ROADMAP.md) §1 for the 2.0 1 ms single-core contract and published price list.

## A day in the life

The tables above time one operation at a time. Real use is a whole tick: agents move and aim,
a few spawn and die, scent is dropped and fades, the field processes once, and every agent
asks one or two questions. `--daily` runs exactly that (abridged below; the full loop is
`benchmarks/DailyTiming.cs`) — a 512² world with a 512² steering grid and a 128² minimap
grid, four deposited layers (240 food bells that drift, 20 wolves sweeping 110° vision cones,
200 sheep auras, 4 scent crumbs dropped per tick and fading out over 240 ticks), one derived
`safety = food − 2·threat`, and spawn/death churn every 8 ticks — about 1,420 live sources,
~205 queued ops and ~395 agent reads per tick:

```csharp
for (var tick = 0; tick < frames; tick++)
{
    for (var i = 0; i < 20; i++)                              // wolves steer, face, walk
    {
        wolfHeading[i] += Wander();
        World.Turn(world, wolf[i], wolfHeading[i]);
        World.Move(world, wolf[i], wolfX[i], wolfY[i]);
    }

    for (var s = 0; s < 150; s++)                             // 150 sheep wander, 50 sleep on
        World.Move(world, sheep[WalkIndex(tick, s)], sheepX, sheepY);

    if (tick % 8 == 0)                                        // two die, two are born
        for (var j = 0; j < 2; j++)
        {
            World.Remove(world, sheep[rot]);
            sheep[rot] = World.Place(world, herd, rx, ry, aura, gain: 4);
        }

    for (var c = 0; c < 4; c++)                               // scent fades and vanishes alone
    {
        var crumb = World.Place(world, scent, cx, cy, crumbStamp, gain: 16);
        World.Fade(world, crumb, 0, ticks: 240);
        World.Expire(world, crumb, ticks: 240);
    }

    World.Process(world);                                     // once per frame

    for (var a = 0; a < 200; a++)                             // sheep sleep through calm ticks
        if (World.Changed(world, threat, sheepX[a], sheepY[a], reach: 20f, since: tick - 6))
            World.TrySenseNearest(world, food, sheepX[a], sheepY[a], reach: 40f, threshold: 60,
                                  out var meal, out _, out _);

    for (var i = 0; i < 20; i++)                              // wolves hunt the herd edge
        World.TrySenseNearest(world, herd, wolfX[i], wolfY[i], reach: 48f, threshold: 30,
                              out _, out _, out _);

    for (var b = 0; b < 60; b++)                              // graze along the safety slope
        World.TrySenseGradient(world, safety, grazeX[b], grazeY[b], out var dx, out var dy);

    if (tick % 4 == 0)                                        // minimap refresh
        World.QueryRegion(world, map, safety, 0, 0, 128, 128, page);

    if (tick % 10 == 0)                                       // strategic best cell
        World.QueryMax(world, fine, safety, out _, out _);
}
```

Measured over 600 ticks after a 300-tick warm-up (tiered JIT settles; the engine itself has
no warm-up — idle frames cost 0). 58% of sheep decisions woke to a real change near them;
42% slept through:

| day-to-day tick, 512² world | AVX2 | intrinsics off |
| --- | ---: | ---: |
| **whole tick, mean** | **390 µs** | 610 µs |
| whole tick, worst | 820 µs | 1.7 ms |
| mutations (~208 ops) | 3 µs | 3 µs |
| `Process` (4 deposited layers + derived safety) | 230 µs | 450 µs |
| agent reads (~395) | 155 µs | 160 µs |

The whole simulation — field upkeep plus every agent's senses — is 2.4% of a 16.6 ms frame,
leaving the rest for rendering and game code. `Changed`-gated sleeping is why reads stay cheap while the
world moves.

`--daily` prints its own receipts before the timings: the replay's checksum is identical
across two fresh worlds and across SIMD on and off (13,600,833), the derived argmax equals
a full 512² region scan, and the 600-tick loop allocates 0 B.

## How it works

- **Deposits are change-rate.** Each live tile owns a 2,048 B `int16` page, a 4,096 B `int32`
  `I` plane that sums box, raster and round contributions, and — on first use — `int64` tent and
  bell planes. `Place`/`Move`/`Turn`/`Scale`/`SetGain`/`Remove` only write the source columns and
  mark the slot once per window; `Process` compares each marked slot's target with the 16 B record
  of what it last deposited and, where they differ, adds the negated old taps and the new taps
  into the planes. Integer adds commute, so any mutation sequence collapses exactly, and a
  source placed and removed inside one window never touches a tile.
- **Each stamp kind deposits at its own sparsity.** A box adds at most three column bands by
  three row bands per tile, as one eight-lane vector add per row when it spans eight cells or
  fewer. A raster samples bilinearly into the tile's `I` plane. Tents and bells add their exact
  `value·gain·C·Wx·Wy` products into per-tile `int64` planes, rounded once per cell at publish —
  so self-exclusion reads a cell's unrounded sum with one load. Turned and round stamps evaluate
  each cell of their rotated bounds with integer math. Unturned shapes come from a per-world
  kernel cache of gain-1 taps.
- **Publish is per dirty rectangle.** The planes sum into `short` cells *after* summation (so
  cancellation stays exact), a cell is stored only when it differs, and the tile's sum and max
  are kept exact. Tiles that publish to zero free their planes — a missing page reads as 0.
- **Derived layers recombine changed tiles.** After the publish, each derived layer, in creation
  order, marks the union of its inputs' dirty tiles and recombines them in one vectorized pass
  per tile, bit-identical to the scalar loop.
- **Schedules are a dense list of due ticks.** Fades and expiries fire only when an integer gain
  steps or a removal is due, as ordinary marked mutations applied in ascending slot order.
- **Queries never scan.** Region sums add one `int64` per fully covered tile; `QueryMax`
  descends a per-(grid,layer) max pyramid; nearest prunes tiles by their max; `Changed` reads
  per-tile epochs.
- **Handles, not objects.** Everything public returns `byte` ids into unmanaged arenas; source
  ids pack a generation over the slot index, so stale handles are inert no-ops and slots
  recycle. No managed allocation on the data path.
- **Deterministic.** Integer-only field math; deposits are commutative adds. Pages are
  bit-identical across runs and machines, scalar or SIMD.

Full semantics and the unsafe lifetime/aliasing/alignment/concurrency proof live in
[`docs/model.md`](docs/model.md).

## Receipts

Every claim above is asserted, not documented. `dotnet run --project benchmarks -c Release --
--verify` runs all 64 receipts before printing a single timing — with and without hardware
intrinsics:

| receipts | what they pin down |
| --- | --- |
| `process-matches-oracle`, `process-deterministic`, `remove-restores-baseline` | deposits equal a per-cell oracle; identical worlds, identical pages; removal is exact |
| `warm-*-allocates-0-bytes` (6 receipts) | 0 B on process, query, sense, derived, turned, and scheduled paths |
| `query-region-matches-cells`, `page-sum-matches-scan`, `region-sum-matches-cell-scans`, `query-at-matches-deposits` | every query surface agrees with per-cell truth |
| `query-max-*`, `gradient-matches-central-differences` | argmax and gradients equal full rescans |
| `changed-tiles-match-drain`, `deferred-window-matches-stepped-processing`, `changed-since-matches-epochs` | the changed feeds are exact; batched == stepped processing |
| `tent-matches-impulse-oracle`, `bell-matches-paraboloid-oracle`, `turned-and-round-stamps-match-oracle` | kernels equal independent oracles written from the spec, not the engine |
| `kernels-*`, `round-kernels-share-box-units-and-centre`, `stamps-turn-and-scale-smoothly`, `sub-cell-kernels-reach-their-support` | peaks read `value·gain`; glides, turns, and growth never step; sub-cell kernels reach across tile edges |
| `turn-and-scale-round-trip-exactly`, `stamps-wider-than-grid-cover-it` | angle 0 and scale 1 restore the field; oversized stamps cover their grid |
| `derived-layers-match-cell-formulas`, `derived-layers-follow-sources-rewind-and-clear` | 11 recipes incl. chains equal their formulas on every cell, through churn, rewind, clear, and late creation |
| `sense-*`, `derived-exclude-matches-removal`, `turned-exclude-matches-removal` | grid picking, disk scans, nearest, and exclusion == removal bit for bit |
| `fade-steps-match-schedule`, `expire-removes-on-schedule`, `rewind-resumes-schedules` | 140 fades follow the spec every tick; expiry edge cases; a rewound world tracks its twin |
| `source-slots-reuse-and-stale-handles-inert`, `rewind-restores-recorded-state`, `signed-gain-exact` | handle lifecycle, rewind, signed gains |
| `saturated-sum-clamps`, `cross-grid-sums-conserve-world-integral`, `multi-layer-churn-matches-scans` | saturation, scale conservation, churn determinism |
| `crowded-rebuild-matches-stepped-incremental`, `crowded-small-boxes-match-stepped-incremental`, `crowded-unchanged-frames-report-no-changed-tiles` | the rebuild equals stepped incremental on every cell; no-change frames report no changed tiles |
| `sense-column-matches-try-sense`, `changed-mask-matches-changed`, `sync-matches-move` | column batches equal the per-agent API bit for bit |
| `warm-crowded-process-allocates-0-bytes`, `warm-column-batches-allocate-0-bytes` | 0 B for crowded `Process` and column batches, warm |
| `source-batches-match-serial-and-allocate-0-bytes`, `source-batches-retract-across-layers` | sealed batch ingestion equals serial calls cell for cell and rewinds across layers, 0 B warm |
| `transform-updates-match-ordered-calls` | `UpdateTransform` batches equal ordered `Move`+`Turn`+`Scale` on every cell while fades keep running, 0 B warm |
| `sync-transforms-match-ordered-calls` | borrowed `SyncTransforms` ranges equal ordered calls and `ApplyBatch` on every cell through stale rows, fades, and pending merges, 0 B warm |
| `mixed-stamp-process-allocates-zero-bytes` | moving mixed-shape workloads across shapes, grids, layers, removals, and churn process with 0 B warm |

## Cookbook

**Best spot by several criteria.** Derive the score once; every query reads it:

```csharp
byte score = Layer.Sum(world, food, 2, threat, -3);
World.TrySenseMax(world, score, x, y, reach: 60f, out _, out float tx, out float ty);
```

**Nearest safe cell.** Negate the threat layer and ask for the nearest cell at or above `-10`
(threat ≤ 10):

```csharp
byte calm = Layer.Sum(world, threat, -1);
if (World.TrySenseNearest(world, calm, x, y, 50f, -10, out var v, out var sx, out var sy) && v >= -10)
    GoTo(sx, sy);
```

**Vision cones.** One stamp per sensor kind; every unit turns its own:

```csharp
byte sight = Stamp.Cone(24, 60, arc: 90);
int eye = World.Place(world, seen, unit.X, unit.Y, sight, 8);
World.Turn(world, eye, MathF.Atan2(unit.Dir.Y, unit.Dir.X));
```

**Sleeping AI.** Re-decide only when something nearby changed:

```csharp
if (World.Changed(world, threat, agent.X, agent.Y, 24f, agent.DecidedAt))
{
    agent.Decide();
    agent.DecidedAt = World.Tick(world);
}
```

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
dotnet run --project benchmarks -c Release --no-build -- --timing    # every number above
dotnet run --project benchmarks -c Release --no-build -- --compare   # Gi vs the naive grid
dotnet run --project benchmarks -c Release --no-build -- --crowd     # Process under 25–100% movers
dotnet run --project benchmarks -c Release --no-build -- --daily     # the day-to-day tick loop
dotnet run --project benchmarks -c Release --no-build -- --batch     # sealed batch ingestion
dotnet run --project benchmarks -c Release --no-build -- --stages    # prep/apply/resolve per Process
dotnet run --project viz -c Release --no-build -- live               # http://127.0.0.1:8740
dotnet run --project tools/stats -c Release -- stats                 # internals as JSON
```

- `src/Gi` — the package (`net10.0` + `netstandard2.1`; Unity calls use non-Burst code)
- `src/Gi.Validation` — optional validation for editors and tools
- `tests/Gi.Tests` — oracle, crowd, and validation tests
- `benchmarks` — the 64 receipts and all the numbers above
- `samples/world` — console walkthrough; `samples/unity-demo` — sheep/wolf ecosystem on
  `TrySense`, with turned vision cones, a derived comfort layer, and fading paint
- `viz` — static HTML render (round stamps, derived layers, fades) + live WebSocket ecosystem
- `tools/stats` — internal statistics and profiling
