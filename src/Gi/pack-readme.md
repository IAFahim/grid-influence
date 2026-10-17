# Gi

Sparse tiled integer influence fields for .NET: box, tent, bell, raster, disk, cone, and dome
stamps with sub-cell Q8 placement, per-source turn and scale, derived layers maintained per
changed tile, scheduled fades and expiry, multi-resolution grids, and independent layers per
world. Zero dependencies, unmanaged data path, 0 B allocation on warm Process/Query.

Targets `net10.0` (with SSE2/AVX2/Neon paths) and `netstandard2.1` for engines such as Unity —
the netstandard build compiles the same scalar paths that the intrinsics-off verification run
exercises. Unity can record source inputs in Burst jobs, then call Gi from non-Burst code after
the producer/read dependencies complete. Mutable Gi statics in a precompiled DLL are not a
Burst-compatible API.

```csharp
byte world  = World.New();
byte grid   = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);
byte food   = Layer.New(world);
byte threat = Layer.New(world);
byte safety = Layer.Sum(world, food, 1, threat, -2);      // derived, kept current by Process
var eye = World.Place(world, threat, x, y, Stamp.Cone(16, 90, arc: 90), gain: 8);
World.Turn(world, eye, MathF.PI / 2f);                     // vision cone faces +y
World.Fade(world, eye, 0, ticks: 60);                      // fades over 60 Process calls
World.Process(world);
short best = World.QueryMax(world, grid, safety, out int bx, out int by);
fixed (short* page = destination) World.QueryRegion(world, grid, food, 0, 0, 256, 256, page);
```

Gameplay code can skip grid handles: `TrySense`, `TrySenseArea`, `TrySenseMax`,
`TrySenseGradient`, and `TrySenseNearest` take a world-space point, pick the finest grid that
holds the whole query, return `false` instead of a silent 0 where no grid covers it, normalize
area totals to world units, and can exclude one source (its own aura) bit-exactly. `Changed`
tells an agent whether its neighbourhood changed since a tick.

```csharp
if (World.TrySense(world, layer, x, y, exclude: mySource, out short others)) React(others);
World.TrySenseNearest(world, food, x, y, reach: 40f, threshold: 50, out var v, out var fx, out var fy);
```

`World.ApplyBatch` borrows sealed source-command and result spans, supports ordered creation,
updates, removal, fades, and expiry, and applies large batches in one sequential pass over the source columns.
It does not advance time or touch tiles: call `Process` afterward. Complete producer writes and
Gi readers before ingestion; direct calls and batch calls remain exclusive per world.
See the [batch contract](https://github.com/IAFahim/grid-influence/blob/main/docs/batching.md).

Full semantics, receipts, and the unsafe proof:
[docs/model.md](https://github.com/IAFahim/grid-influence/blob/main/docs/model.md).
