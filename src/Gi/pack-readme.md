# Gi

Sparse tiled integer influence fields for .NET: difference-array constant rectangles, bilinear
raster deposits with sub-cell Q8 placement, per-tile 2D prefix-sum resolve into 32×32 int16
pages, multi-resolution grids and independent layers per world. Zero dependencies, unmanaged
data path, 0 B allocation on warm Process/Query.

Targets `net10.0` (with SSE2/AVX2/Neon resolve paths) and `netstandard2.1` for engines such as
Unity — the netstandard build compiles the same scalar paths that the intrinsics-off
verification run exercises. Query paths allocate nothing and perform no static-construction
calls, so Burst jobs can call `World.Query` and `World.QueryRegion` directly.

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);   // or Stamp.Box(8, 8, 100)
World.Place(world, layer, x, y, stamp, gain: 8);
World.Process(world);
short v = World.Query(world, grid, layer, cx, cy);
fixed (short* page = destination) World.QueryRegion(world, grid, layer, 0, 0, 256, 256, page);
```

Gameplay code can skip grid handles: `TrySense`, `TrySenseArea`, `TrySenseMax`, and
`TrySenseGradient` take a world-space point, pick the finest grid that holds the whole query,
return `false` instead of a silent 0 where no grid covers it, normalize area totals to world
units, and can exclude one source (its own aura) bit-exactly.

```csharp
if (World.TrySense(world, layer, x, y, exclude: mySource, out short others)) React(others);
World.TrySenseArea(world, layer, x, y, reach: 20f, out long nearby);
```

Full semantics, receipts, and the unsafe proof:
[docs/model.md](https://github.com/IAFahim/grid-influence/blob/main/docs/model.md).
