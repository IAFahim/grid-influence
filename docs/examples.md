# Gi by example

Every public call in `World`, `Grid`, `Layer`, and `Stamp`, with the shortest example that
shows its contract. Every snippet below is exercised by `tests/Gi.Tests/ApiExamples.cs`,
so they cannot drift from the real API. They assume this preamble:

```csharp
using Gi;

byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);
byte food  = Layer.New(world);
byte dome  = Stamp.Dome(6, 40);
int  me    = World.Place(world, food, 128.5f, 64f, dome, gain: 8);
World.Process(world);
```

## Worlds

```csharp
byte world = World.New();          // up to 255 live at once
World.Clear(world);                // drop every source, op, and schedule; pages read 0;
                                   // grids, layers, and stamps survive
World.Free(world);                 // release the world's memory and its handle
int  budget = World.MaxLayers;     // 32 — deposited and derived layers share it

var ids = new byte[64];
int  live = World.CopyWorldIds(ids);  // fills allocated ids ascending; returns the live count
int  rev  = World.Revision(world);     // changes on Clear, Rewind, Free, and handle reuse

World.Token token = World.Capture(world);   // seals (world id, revision); IsValid is false
                                            // for an unallocated or freed slot
// Later, after producers finish and readers join:
//   World.ApplyBatch(token, commands, results) and World.Sync(token, …) return
//   BatchStatus.StaleRevision for a cleared/rewound/freed/reused world and
//   BatchStatus.InvalidWorld for a dead slot — before touching any state.
//   World.Process(token) applies the same check before ticking.
```

`Free` only frees a live handle; a stale one is an inert no-op, like every stale handle in
Gi. `CopyWorldIds` copies at most the span's length but always returns the live count —
call it once with an empty span to size the buffer. It is a process-wide scan: serialize it
with `World.New`, `World.Free`, and `Stamp.Free`. `Revision` never changes on mutation or
`Process`; a dead slot keeps its last value, so a captured `(world, revision)` pair never
matches again until the slot is reused — which bumps it once more. Tokens are checked by Gi,
not advisory: rejected consumption leaves results, sources, pending operations, schedules,
ticks, fields, changed tiles, and maxima untouched, and `Process(token)` never ticks a slot's
new occupant under an old token.

## Grids

```csharp
byte steering = Grid.New(world, power: 9, x: 0f, y: 0f, size: 512f);  // 512² cells
byte minimap  = Grid.New(world, power: 7, x: 0f, y: 0f, size: 512f);  // 128² cells, 4 units/cell
byte region   = Grid.New(world, power: 8, x: 256f, y: 256f, size: 128f); // offset sub-region

int  grids = Grid.Count(world);                     // allocated ids are 0..count-1
Grid.CopyIds(world, ids);                           // fills them, returns the count
Grid.TryGetSpec(world, region,
                out int power, out float x, out float y, out float size);
```

`power` is log2 of the cell count — 5 (32²) through 14 (16384²). `size` is the world-space
extent; the origin `(x, y)` places the grid anywhere in world space. Up to 32 grids per
world; every source deposits into each grid it overlaps at that grid's own scale. Grids are
never freed singly, so their ids stay the dense range `0..Grid.Count(world)-1`;
`Grid.CopyIds` copies at most the span's length but always returns the count, like
`World.CopyWorldIds`. `Grid.TryGetSpec` returns the exact `power`, `x`, `y`, `size` a grid
was created with, so a host draws or validates every grid without keeping a parallel
registry.

## Stamps

```csharp
byte box   = Stamp.Box(16, 16, 90);        // flat plateau, width/height 1–256
byte tent  = Stamp.Tent(12, 9, 90);        // linear pyramid to the edges
byte bell  = Stamp.Bell(12, 12, 90);       // paraboloid dome
byte disk  = Stamp.Disk(8, 90);            // round plateau, radius 1–128
byte cone  = Stamp.Cone(24, 90, arc: 90);  // linear falloff inside a facing sector
byte dome  = Stamp.Dome(8, 90, arc: 120);  // round paraboloid, arc 1–360 (default 360)

var samples = new sbyte[6 * 6];
for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 7);
byte raster = Stamp.New(samples, 6, 6);    // your own w×h raster (span or sbyte*)

Stamp.Free(raster);                       // returns the id once nothing uses it
```

`value` is `sbyte`. All kernels share the box's units — every stamp peaks at `value·gain` at
its centre, so shapes swap without retuning. The catalog holds 255 live stamps; `Free`
returns ids once no source, pending op, or rewind journal in any world references the stamp.
Out-of-range axes throw `ArgumentOutOfRangeException` at bake time.

## Layers

```csharp
byte food    = Layer.New(world);
byte safety  = Layer.Sum(world, food, 1, threat, -2);                  // weights ±32767
byte quarter = Layer.Sum(world, safety, 1, shift: 2);                  // ÷4, single input
byte calm    = Layer.Sum(world, threat, -1);                           // negate
byte both    = Layer.Min(world, mine, theirs);                         // > 0 only where both
byte either  = Layer.Max(world, contested, calm);                      // chains nest
byte grazing = Layer.Mask(world, food, threat, short.MinValue, 40);    // food where threat ≤ 40

int layers = Layer.Count(world);   // deposited and derived share one id space
Layer.CopyIds(world, ids);         // fills the allocated ids, returns the count
```

A derived layer's cells always equal the formula over its inputs — maintained by `Process`
on the tiles whose inputs changed, readable by every query. Derived layers draw from the
same 32-layer budget as deposited ones (`World.MaxLayers`). `Layer.Count` and
`Layer.CopyIds` enumerate them exactly like the grid calls — dense ids `0..count-1`.

## Sources

```csharp
int me = World.Place(world, food, 128.5f, y: 64f, dome, gain: 8);  // returns a source id
World.Move(world, me, 130f, 64f);            // queued, sub-cell exact
World.Turn(world, me, MathF.PI / 4f);        // radians, works on every stamp kind
World.Scale(world, me, 1.5f);                // size × 1.5, one stamp serves every radius
World.SetGain(world, me, -4);                // sign flip without re-placing
World.Remove(world, me);                     // exact negation — no rebuild, no residue
```

`gain` is `sbyte` and lives at ±16; out-of-range values clamp silently on these paths.
Mutations queue per source — a turn, a scale, a move, and a gain change in one window cost
one retract-and-apply pair — and a source placed and removed inside one window never
touches a tile. Source ids pack a generation over the slot, so stale ids are inert.

## Sealed source batches

```csharp
World.SourceCommand[] commands =
[
    World.SourceCommand.Place(food, 10.5f, 20.5f, dome, 8),
    World.SourceCommand.Move(World.SourceTarget.CreatedBy(0), 12.5f, 22.5f),
    World.SourceCommand.Turn(World.SourceTarget.CreatedBy(0), MathF.PI / 2),
    World.SourceCommand.Scale(World.SourceTarget.CreatedBy(0), 1.5f),
    World.SourceCommand.SetGain(World.SourceTarget.CreatedBy(0), 4),
    World.SourceCommand.Fade(World.SourceTarget.CreatedBy(0), 0, 20),
    World.SourceCommand.Expire(World.SourceTarget.CreatedBy(0), 20),
    World.SourceCommand.Update(World.SourceTarget.Existing(me), 30.5f, 40.5f, 0f, 1f, 2),
    World.SourceCommand.Remove(me)
];
var results = new World.SourceResult[commands.Length];
if (World.ApplyBatch(world, commands, results) == World.BatchStatus.Applied)
{
    if (results[0].Status == World.SourceResultStatus.Applied) me = results[0].SourceId;
    World.Process(world);
}
```

Seal input and join producer jobs and Gi readers before calling. Input order is command order.
Malformed structure rejects before output/source writes; stale sources and failed creations are
per-command failures, so inspect result statuses even when the batch returns `Applied`. Ingestion
does not tick; `Process` publishes the field. Native spans avoid the example's managed arrays.

## Time

```csharp
World.Fade(world, me, 0, ticks: 240);     // ramp gain to 0 over 240 Process calls
World.Expire(world, me, ticks: 240);      // remove on the last one
int tick = World.Tick(world);             // counts Process calls — Gi has no clock
```

Scheduled steps fire as ordinary queued mutations, only on ticks where an integer gain
actually steps. A tick where nothing steps costs nothing; schedules journal and rewind.

## Processing and the journal

```csharp
World.Process(world);                     // apply the queue, resolve dirty tiles once
World.ProcessAll();                       // the same, once per allocated world, id order
World.Record(world);                      // start journaling applied ops and schedules
World.StopRecording(world);               // change your mind: discard the journal, stop
World.Rewind(world);                      // inverse ops; field and schedules return exactly
```

`Process` once per frame is the whole contract: reads see the last `Process` (one frame of
latency), and any number of mutations may queue between two calls. `ProcessAll` is the loop
hosts otherwise hand-roll — every live world ticked in ascending id order on the calling
thread, including worlds another subsystem created — so nothing silently freezes just
because its id never reached your registry; to tick worlds yourself, on different threads,
enumerate them with `World.CopyWorldIds`.

## Reading cells

```csharp
short v    = World.Query(world, grid, food, 64, 32);                 // one page read
long  sum  = World.Query(world, grid, food, 0, 0, 32, 32);           // O(tiles) region sum
short here = World.QueryAt(world, grid, food, 64.5f, 32.25f);        // float world coords

short best = World.QueryMax(world, grid, food, out int bx, out int by);           // argmax
short zone = World.QueryMax(world, grid, food, 32, 32, 64, 64, out int zx, out int zy);

World.QueryGradient(world, grid, food, 64.5f, 32.25f, out int gx, out int gy);    // central diff

fixed (short* page = destination)
    World.QueryRegion(world, grid, food, 0, 0, 256, 256, page);      // fill a buffer
```

Reads never scan: region sums add one `int64` per covered tile, `QueryMax` descends a
max pyramid, gradients are one central difference. `Query` reads `0` outside the grid;
`QueryRegion` clips to the grid and fills the rest with `0`.

## What changed

```csharp
bool near = World.Changed(world, food, x, y, reach: 24f, since: lastDecision);

var tiles = stackalloc int[64];
int count = World.ChangedTiles(world, grid, food, since: World.Tick(world) - 60, tiles);
int frame = World.ChangedTiles(world, grid, food, tiles);   // tiles the last Process changed
```

`Changed` reads per-tile epochs around a world-space point — agents sleep until their
neighbourhood moves. `ChangedTiles` serves minimaps and network sync: the `since` form
reports every tile resolved after a tick, the drain form reports exactly the tiles whose
values differed in the last `Process` window.

## Sense — world coordinates, no grid handle

```csharp
bool covered = World.Covers(world, x, y);                       // is any grid over this point

World.TrySense(world, food, x, y, out short value);            // point read
World.TrySense(world, threat, x, y, exclude: myAura, out short others);   // minus my aura

World.TrySenseArea(world, threat, x, y, reach: 20f, out long nearby);     // world-area total
World.TrySenseArea(world, threat, x, y, 20f, myAura, out long sansMe);

World.TrySenseMax(world, food, x, y, reach: 40f,
                  out short best, out float fx, out float fy);            // strongest cell

World.TrySenseNearest(world, food, x, y, reach: 40f, threshold: 50,
                      out short meal, out float mx, out float my);        // closest ≥ 50

World.TrySenseGradient(world, food, x, y, out float gx, out float gy);    // per world unit
World.TrySenseGradient(world, food, x, y, exclude: myAura, out float sx, out float sy);
```

The `bool` is `true` only when the answer is complete: the query picks a grid that holds
the whole disk, `false` means "no single grid covers it" instead of a silent `0`.
`TrySenseNearest` returns `short.MinValue` and the query point when nothing qualifies, so
`value >= threshold` tells found from not found. `exclude:` equals `Remove` + `Process`
bit for bit, and follows the applied position of the excluded source.

## Agent columns — one call per swarm

```csharp
// read the field for every agent: values[i] = layer at (xs[i], ys[i]); returns false
// only when some agent is off every grid; whole[i>>6] bit i marks covered agents
World.SenseColumn(world, threat, xs, ys, count, values, whole);

// one call for all "did my neighbourhood change" checks: words[i>>6] bit i per agent,
// zeroed for the quiet ones; returns how many agents woke
World.ChangedMask(world, threat, xs, ys, reach: 24f, since: lastDecision, count, words);

// fused Move for a whole column (ECS chunk, SoA) — queues like Move, Process applies
World.Sync(world, sourceIds, newXs, newYs, count);
```

`xs`, `ys`, `values`, `words`, `sourceIds` are raw pointers (`float*`, `short*`, `ulong*`,
`int*`). Above 8,192 rows they fan out over the same worker pool as resolve; all three are
zero managed allocation and bit-identical to the per-agent API.

## Limits

| surface | bound |
| --- | --- |
| live worlds | 255 |
| grids per world | 32, `power` 5–14 (32²–16384² cells) |
| layers per world | 32 total (`World.MaxLayers`), deposited + derived share it |
| live stamps | 255 (`Stamp.Free` returns ids) |
| stamp width/height | 1–256; radius 1–128; arc 1–360; `value` is `sbyte` |
| gain | `sbyte`, clamps to ±16 on place/schedule paths |
| recipe weights | ±32767, shift 0–15 |

Bake-time construction (`Grid.New`, stamp constructors, derived recipes) throws
`ArgumentOutOfRangeException` with the offending parameter named; the per-tick data paths
(`Place`, `Move`, `SetGain`, `Fade`) never throw — out-of-range gain clamps, stale handles
are inert.
