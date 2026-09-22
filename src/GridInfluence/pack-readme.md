# GridInfluence

Sparse tiled integer influence fields for .NET: difference-array constant rectangles, bilinear
raster deposits with sub-cell Q8 placement, per-tile 2D prefix-sum resolve into 32×32 int16
pages, multi-resolution grids and independent layers per world. Zero dependencies, unmanaged
data path, 0 B allocation on warm Process/Query.

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);   // or Stamp.Box(8, 8, 100)
World.Place(world, layer, x, y, stamp, gain: 8);
World.Process(world);
short v = World.Query(world, grid, layer, cx, cy);
```

Full semantics, receipts, and the unsafe proof:
[docs/model.md](https://github.com/IAFahim/grid-influence/blob/main/docs/model.md).
