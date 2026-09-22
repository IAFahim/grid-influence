# GridInfluence

Sparse, tiled, **integer** influence fields for .NET. Each live tile keeps a persistent
accumulator — constant-rectangle stamps deposit as four difference-array corners, rasters with
sub-cell bilinear weights — so `Place`/`Move`/`Remove` cost `O(footprint)`, never a source
rescan. `Process` resolves each dirty tile once into a 32×32 `int16` page; `Query` is one hash
lookup + read. All state is unmanaged: warm `Process`/`Query` allocate **0 B**.

## Quick start

```csharp
using GridInfluence;

byte world = World.New();
byte grid  = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);  // 256×256 cells
byte layer = Layer.New(world);
byte stamp = Stamp.New(samples, 16, 16);          // baked sbyte raster — or Stamp.Box(8, 8, 100)

int source = World.Place(world, layer, x: 128.5f, y: 64f, stamp, gain: 8);
World.Process(world);

short v = World.Query(world, grid, layer, 64, 32);        // cell read
long  t = World.Query(world, grid, layer, 0, 0, 32, 32);  // region sum
short p = World.QueryAt(world, grid, layer, 128f, 64f);   // world-space point
World.Remove(world, source);                              // sources are persistent
```

- **Multi-resolution worlds**: one world holds up to 32 grids — `Grid 256x / 1024x / 32x` — each a
  power-of-two cell grid over its own world rect; a source deposits into every grid it overlaps.
- **Sub-cell placement**: positions convert to Q8 cell space; constant rectangles split into
  weighted edge bands and rasters shift by bilinear weights — no cell snapping.
- **Sparse pages**: tiles exist only where sources touch; a missing page reads as 0. `Move` and
  `Remove` deposit exact negations — contributions cancel bit-perfectly, no rebuild.
- **Deterministic**: integer field math, placement-order deposits — bit-identical across runs.

Full semantics and the unsafe proof: [`docs/model.md`](docs/model.md).

## Validation

```sh
dotnet build GridInfluence.slnx -c Release -m:1
dotnet test GridInfluence.slnx -c Release --no-build
dotnet run --project samples/world -c Release --no-build
dotnet run --project benchmarks -c Release --no-build -- --verify
```

## Repository layout

- `src/GridInfluence` — the engine package (the only library).
- `tests/` — oracle-based engine tests (box + raster, multi-resolution, saturation, removal).
- `benchmarks/` — `--verify` receipts and `--timing` spot numbers.
- `samples/world` — aggro range, multi-resolution traffic, baked raster stamp.
- `viz/` — 3D field visualizer: `dotnet run --project viz -c Release` writes a self-contained
  `viz/index.html` (WebGL heightfield per layer + source markers, no dependencies).
- `bench/` — scratch cross-engine comparison harnesses + measured results (not in the solution).
- `docs/model.md` — model, receipts, unsafe lifetime/aliasing/alignment/concurrency proof.

## License

[MIT](LICENSE) — © IAFahim.
