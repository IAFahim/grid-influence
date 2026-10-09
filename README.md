# Gi

Sparse tiled integer influence fields for .NET. One API, byte handles over unmanaged state,
integer-exact deposits, **0 B** on warm `Process`/`Query`.

This repository previously carried two engines (a rasterized tick field and a re-emitted marks
engine). It now ships exactly one: the **deposit engine**. Mutations write incrementally into
per-tile difference arrays; `Process` resolves only dirty tiles; queries read resolved `int16`
pages. The re-emitted design was measured and retired — see [Receipts](#receipts).

## Get started

```sh
dotnet add package Gi.Influence --version 0.4.0-alpha.1
```

```csharp
using Gi;

byte world = World.New();
byte grid   = Grid.New(world, power: 8, x: 0f, y: 0f, size: 256f);  // 256×256 cells
byte layer  = Layer.New(world);                                      // channel on every grid
byte stamp  = Stamp.Box(16, 16, 60);                                 // or Stamp.New(samples, w, h)

int source = World.Place(world, layer, 128.5f, 64f, stamp, gain: 8); // persistent source
World.Move(world, source, 130f, 64f);    // negates old deposit, deposits new
World.SetGain(world, source, 4);         // deposits the delta
World.Process(world);                    // resolves dirty tiles once

short cell = World.Query(world, grid, layer, 64, 32);        // one page read
long  sum  = World.Query(world, grid, layer, 0, 0, 32, 32);  // region sum
short at   = World.QueryAt(world, grid, layer, 130f, 64f);   // world-space point

World.Remove(world, source);             // exact negation — no rebuild
World.Clear(world);                      // frees every live tile block
```

A world mixes resolutions freely — e.g. a 1024² grid near the camera and 64² grids far away —
and a source deposits into every grid it overlaps, at each grid's own scale.

## Use cases

**Threat maps for AI.** Stamp every enemy's reach once; score candidate positions with page
reads. Placement never rescales — the field is always query-ready after `Process`.

```csharp
byte threats = Layer.New(world);
foreach (var enemy in enemies)
    World.Place(world, threats, enemy.X, enemy.Y, enemy.Reach, enemy.IsElite ? 12 : 6);
World.Process(world);

var safest = candidates.MinBy(c => World.QueryAt(world, grid, threats, c.X, c.Y));
```

**Teams and auras as layers.** Layers are independent channels on every grid, so opposing
fields coexist and comparisons are two reads.

```csharp
byte red = Layer.New(world);
byte blue = Layer.New(world);
var balance = World.Query(world, grid, red, x, y) - World.Query(world, grid, blue, x, y);
var redDominates = World.Query(world, grid, red, x, y, w, h) >
                   World.Query(world, grid, blue, x, y, w, h);   // O(tiles), not O(cells)
```

**One world, two LODs.** The same sources feed a detail grid for pathing and a coarse grid for
the strategic view — world-anchored extents and mip chains keep both exact and anti-aliased.

```csharp
byte detail   = Grid.New(world, power: 11, 0f, 0f, 2048f);   // 2048² for steering
byte overview = Grid.New(world, power: 7, 0f, 0f, 2048f);    // 128² for the UI minimap
```

**Rendering a field.** `QueryRegion` fills your pixel buffer directly — row-major, clipped,
zero allocation:

```csharp
fixed (short* dst = tile)
    World.QueryRegion(world, grid, threats, viewX, viewY, viewW, viewH, dst);
```

### Bézier curves: roads, patrols, brush strokes

Gi is unusually well suited to spline-shaped influence. A cubic evaluation is all you need:

```csharp
static (float X, float Y) Bezier(
    (float X, float Y) a, (float X, float Y) b,
    (float X, float Y) c, (float X, float Y) d, float t)
{
    var u = 1f - t;
    return (u * u * u * a.X + 3f * u * u * t * b.X + 3f * u * t * t * c.X + t * t * t * d.X,
            u * u * u * a.Y + 3f * u * u * t * b.Y + 3f * u * t * t * c.Y + t * t * t * d.Y);
}
```

**A static corridor** — river current, road speed bonus, wind shear — is one pass of placements
with gain shaped by the parameter. Rule of thumb: sample at about half the stamp width so
neighbors overlap. Static fields cost nothing per frame afterwards (idle `Process` is 0 µs).

```csharp
byte slow = Stamp.Box(12, 12, 45);
for (var i = 0; i <= 96; i++)
{
    var t = i / 96f;
    var (x, y) = Bezier(p0, p1, p2, p3, t);
    World.Place(world, slowZone, x, y, slow, 4 + (int)(6f * MathF.Sin(MathF.PI * t)));
}
World.Process(world);
```

**A source gliding along the curve** — patrol drone, escort buff, tethered effect — is the
cheapest mutation Gi has: `Move` negates the old deposit and writes the new one as a handful of
difference-array corners, then `Process` resolves the one or two tiles crossed.

```csharp
var t = clock.Elapsed.TotalSeconds % 1.0;
var (x, y) = Bezier(p0, p1, p2, p3, (float)t);
World.Move(world, source, x, y);
World.Process(world);
```

**Brush strokes.** Bake a smooth falloff as a raster stamp and draw it along the curve: Q8
sub-cell placement makes consecutive samples blend without banding, and the baked mip chain
keeps the stroke anti-aliased when a coarser grid minifies it — the same technology image
editors use for brush tips.

```csharp
var falloff = new sbyte[16 * 16];
for (var y = 0; y < 16; y++)
for (var x = 0; x < 16; x++)
{
    var d = MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f)) / 8f;
    falloff[y * 16 + x] = (sbyte)(100f * MathF.Max(0f, 1f - d));
}
byte brush = Stamp.New(falloff, 16, 16);     // mip chain baked here, once
```

**Asking the curve questions.** Sampling density is a free parameter — deposits are commutative
integer adds, so refine or coarsen without changing results. Region reads score exposure in
O(tiles): "how much slow-zone does this detour cross", "total threat under this spline segment".

```csharp
long crossing = World.Query(world, grid, slowZone, clipX, clipY, clipW, clipH);
short  along  = World.QueryAt(world, grid, slowZone, x, y);
```

## How it works

- **Handles, not objects.** `World`, `Grid`, `Layer`, `Stamp` return `byte` ids into static
  unmanaged arenas; `Place` returns an `int` source id that packs a generation above the slot
  index — removed slots recycle through a free list, and a stale id is an inert no-op. No managed
  allocation anywhere on the
  data path.
- **Deposits are incremental.** Each live tile owns one 6,528 B block (difference array + `int16`
  page + `int64` page sum); raster deposits attach a dense buffer once, growing the block to
  10,624 B — box-only tiles never carry it. `Place`/`Move`/`SetGain`/`Remove` apply exact integer
  deltas immediately — moving a source never rescans the field. Gain is signed (−16–16), so one
  layer can hold opposing pressures.
- **`Process` touches only dirty tiles.** A 2D prefix sum resolves each dirty difference array,
  saturates to `short` after summation (so cancellation is preserved), records the page sum, and
  frees tiles that resolve to zero. A world with ≥32 dirty tiles in total fans the whole drain
  over a small worker pool — one flattened queue across grids and layers;
  an unchanged world costs nothing.
- **Queries read maintained state.** Cell reads are one page lookup; region sums read one
  `int64` per fully covered tile and scan only the clipped edge strips; `QueryRegion` bulk fills
  use vectorized row copies.
- **Sub-cell placement, world-anchored extents.** Positions convert to cell space in Q8; raster
  stamps deposit with bilinear edge weights, uniform rasters take a difference-array box path.
  A stamp covers the same world rect on every grid of its world: extents scale with the grid,
  fractional edges become Q8 band weights, and raster stamps carry baked zero-padded box-average
  mip chains so coarse grids minify without aliasing (scale-1 grids keep the bit-identical 0.2
  deposit loop).
- **Deterministic.** Integer-only field math; deposits are commutative adds, so pages are
  bit-identical across runs and machines, with or without SIMD.

Full semantics and the unsafe lifetime/aliasing/alignment/concurrency proof:
[`docs/model.md`](docs/model.md). The [`viz`](viz) tool renders a three-layer scene to a
self-contained HTML page.
The [`tools/stats`](tools/stats) CLI reports internal statistics, native memory, and timings
as compact JSON without reflection:

```sh
dotnet run --project tools/stats -c Release -- stats
dotnet run --project tools/stats -c Release -- profile
bash tools/stats/perf.sh stat --iterations 12000
```

## Receipts

`dotnet run --project benchmarks -c Release -- --verify` asserts, before any timing:

| Receipt | Checks |
| --- | --- |
| `process-matches-oracle` | 300 random boxes vs a per-cell band oracle |
| `process-deterministic` | identical worlds produce identical pages |
| `remove-restores-baseline` | remove rebuilds tiles without the source |
| `warm-process-allocates-0-bytes` | unchanged and place/remove churn, 0 B |
| `warm-query-allocates-0-bytes` | 200k cell reads, 0 B |
| `query-region-matches-cells` | bulk fill equals per-cell reads across tile boundaries, 0 B warm |
| `page-sum-matches-scan` | per-page sums equal a naive per-cell rescan across churn worlds |
| `saturated-sum-clamps` | saturation sticks at ±32767 after summation |
| `cross-grid-sums-conserve-world-integral` | the same sources summed over four grid scales conserve the world integral exactly |

The same suite passes with `DOTNET_EnableHWIntrinsic=0` (scalar fallback).

Why the deposit engine replaced the re-emitted marks engine (4,000 sources, 256² grid,
100k queries/frame; i9-14900K, .NET 10, Release, min over reps):

| per frame | marks (retired) | deposit |
| --- | ---: | ---: |
| unchanged `Process` | 41.6 µs | 0.0 µs |
| move 200 + process | 51.1 µs | 212 µs |
| single-source change | ~62 µs | 6 µs |
| cell query | 1,040 ns | 2.9 ns |

The marks engine re-emits every source per frame and scans marks per query; the deposit engine
pays per mutation and reads a page. Writes are ~7× cheaper in marks, queries ~360× slower — the
crossover is below ~700 queries/frame at full churn, which game-shaped workloads clear easily.

## Gi vs the naive grid

`dotnet run --project benchmarks -c Release --no-build -- --compare` runs the straightforward
implementation — a dense array per layer, cleared and redrawn from every source each frame —
against Gi on the same workload. The naive field doubles as an independent oracle:
`naive-grid-matches-gi` (and its after-churn twin) assert bit-identical output before any
timing is printed. 4000 box sources (16×16, gain 8) on a 1024² grid, 200 moves per frame,
Ryzen 5 8500G, .NET 10, min over 20 reps:

| frame work | naive grid | Gi | why |
| --- | ---: | ---: | --- |
| nothing moved | 620–680 µs | 0 µs | naive redraws everything anyway; Gi's dirty list is empty |
| 200 moves + process | 625–685 µs | 100–130 µs | ~5–6× — Gi touches only the tiles movers left and entered |
| full-grid sum (1M cells) | 375–390 µs | 0.9 µs | ~420× — one maintained `int64` per live 32×32 tile |

The naive way — O(grid + sources × stamp area) every frame, whether or not anything moved:

```csharp
class NaiveInfluence
{
    private readonly int[] _field = new int[1024 * 1024];
    private List<Source> _sources = [];

    public void Frame()
    {
        Array.Clear(_field);                              // 1M writes even when idle
        foreach (var s in _sources)                       // every source, every frame
            for (var y = 0; y < s.Height; y++)
                for (var x = 0; x < s.Width; x++)
                    _field[(s.Y + y) * 1024 + s.X + x] += s.Value * s.Gain;
    }

    public int Value(int x, int y)
        => Math.Clamp(_field[y * 1024 + x], short.MinValue, short.MaxValue);

    public long Total()
    {
        var sum = 0L;
        foreach (var cell in _field) sum += Math.Clamp(cell, short.MinValue, short.MaxValue);
        return sum;
    }
}
```

The Gi way — O(what changed), queries read maintained state:

```csharp
byte world = World.New();
byte grid  = Grid.New(world, power: 10, x: 0f, y: 0f, size: 1024f);
byte layer = Layer.New(world);
byte stamp = Stamp.Box(16, 16, 60);

int id = World.Place(world, layer, x, y, stamp, gain: 8);       // stamp corners, not the field
World.Move(world, id, newX, newY);                             // exact negation + redeposit
World.Process(world);                                          // resolves only dirty tiles (pooled)
short v = World.Query(world, grid, layer, cx, cy);             // one page lookup
long total = World.Query(world, grid, layer, 0, 0, 1024, 1024); // one int64 per live tile
```

The gap is structural, not tuning. Deposits write difference-array corners (four `int` writes
per clipped stamp band); `Process` resolves each dirty 32×32 tile once through a 2D prefix sum
— fanned across a worker pool past 32 total dirty tiles — saturating to `short` exactly once;
`Query` is a hash lookup plus page read; region sums read per-page `int64` accumulators
maintained at resolve, scanning only edge strips that clip a tile boundary. Sparse worlds also
flip the memory story: the naive grid allocates N²
ints per layer up front (67 MB for a 4096² layer, 1 GB at 16384²), while Gi allocates ~6.5 KB
per live tile — an empty 16384² world costs zero.

Perf pass after the VectorCraft review (Ryzen 5 8500G, .NET 10, 4000 sources / 1024² grid,
min over 20 reps; before = `d90701c` measured with `DOTNET_TieredCompilation=0` to skip a
tier-0 trap — see measurement hazards in `docs/model.md`):

| `--timing` line | before | after | change |
| --- | ---: | ---: | --- |
| `query-region 256x256` | 31–40 µs | 7–15 µs | vectorized row copy/zero fills |
| `full-grid sum (1024-grid)` | ~330 µs | ~0.8 µs | per-page sums written at resolve |
| `move-200 churn process` | ~155 µs | 110–132 µs | pooled parallel resolve, slimmer tile blocks |
| `place-200 churn process` | ~105 µs | 68–82 µs | pooled parallel resolve, slimmer tile blocks |

Parallel resolve scales with the dirty-list size and the memory ceiling, not core count: a
2048² / 8000-source / 400-move scene (2,400 dirty tiles, ~21 MB streamed per frame) goes
245–255 → 155–168 µs — six workers measured slower than four (bandwidth-capped). Tile blocks
shrank 12,480 → 10,624 B (difference-array pitch 48 → 33 ints) and then to 6,528 B for box-only
tiles (dense buffers attach lazily per raster tile; a 9,267-tile box scene dropped from ~98.5 MB
to ~61 MB). Pages are bit-identical pooled or sequential.

Measured-fixes pass (i9-14900K, .NET 10, interleaved A/B against `2aba36d`, min over reps;
same scenes, bit-identical output):

| scene | before | after | change |
| --- | ---: | ---: | --- |
| region sum 1022² of 1024² (4000 sources) | 391 µs | 15 µs | per-tile `int64` sums + vectorized edge strips (~25×) |
| region sum 4094² of 4096² (dense) | 8,074 µs | 206 µs | same (~39×) |
| tile-aligned region 960² of 1024² | 348 µs | 6.4 µs | sums only (~54×) |
| move-400 process, 16 layers × ~25 dirty | 93 µs | 64 µs | world-total pooled fan-out |
| move-200 churn process | 116–121 µs | 68–81 µs | pooled fan-out + fewer per-layer decisions |

The same pass fixed `QueryAt` to use the deposits' truncated `ScaleQ8` mapping (on a
2^14-cells-over-10000-units grid the float mapping read the wrong cell at 199 of 200 sampled
source positions), made source ids recycle through a free list with generation bits (2M
place/remove pairs used to leave the last id at 1,999,999), and made gain signed.

## Run the full thing

```sh
dotnet build Gi.slnx -c Release -m:1
dotnet test Gi.slnx -c Release --no-build
dotnet run --project samples/world -c Release --no-build
dotnet run --project benchmarks -c Release --no-build -- --verify
DOTNET_EnableHWIntrinsic=0 dotnet run --project benchmarks -c Release --no-build -- --verify
dotnet run --project benchmarks -c Release --no-build -- --compare   # naive grid vs Gi
dotnet run --project viz -c Release --no-build   # writes viz/out/index.html
dotnet run --project viz -c Release --no-build -- live   # http://127.0.0.1:8740 — ecosystem field, streamed live
```

## Layout

- `src/Gi` — the package: `World`, `Grid`, `Layer`, `Stamp`, tile bake, page map.
- `tests/Gi.Tests` — oracle tests: box/raster deposits, cross-tile, multi-resolution, saturation,
  move/remove/setgain exactness, sub-cell, determinism.
- `benchmarks` — `--verify` receipts plus `--timing` scratch loop.
- `samples/world` — aggro range, multi-resolution traffic, baked raster stamps.
- `viz` — HTML field visualizer; `-- live` serves a ticking ecosystem field over a WebSocket
  (`QueryRegion` into a pinned buffer, 0 B managed per tick).
- `tools/stats` — JSON internal stats, memory accounting, allocation receipts, and Linux perf.
- `docs/model.md` — semantics, receipts, unsafe proof.

## License

[MIT](LICENSE) — © IAFahim; portions derived from BovineLabs Timeline Grid Influence
(© BovineLabs, MIT).
