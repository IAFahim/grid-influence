# GridInfluence

Chunked, sparse, **integer** influence fields for .NET. Stamps (discs, capsules, sectors, rects,
and more) are rasterized into horizontal spans and resolved with a difference array + prefix sum,
so a tick costs `O(spans + touched chunks)` — independent of the stamped area. Decay and spread
run per tick with exact integer math, so results are bit-identical across machines. The data path
is fully unmanaged: warm ticks and queries allocate **0 B**.

Derived from the core of
[BovineLabs Timeline Grid Influence](https://github.com/vex-studio/com.bovinelabs.timeline.grid.influence)
(MIT, © 2026 BovineLabs) with the Unity/DOTS plumbing replaced by plain .NET. This library has no
dependency on and no relationship to any timeline package — it is a standalone spatial-influence
engine.

## Packages

| Package | Contents |
|---------|----------|
| [`GridInfluence`](src/GridInfluence/pack-readme.md) | The engine: fields, stamps, rasterizer, stencil resolve, readers, gradient/flow/territory/capture queries. Zero dependencies. |
| [`GridInfluence.Io`](src/GridInfluence.Io/pack-readme.md) | PNM/PPM weight-map codec (load weights, export any frame) and a JSON scene runner for file-driven simulation. Separate on purpose: file formats are a consumer concern, not engine concerns. |

## Quick start

```csharp
using GridInfluence;

var spec = GridSpec.FromPowerOfTwo(chunkPower: 5, retentionFrames: 256);
using var front = new InfluenceField(spec);
using var back = new InfluenceField(spec);

FieldStamp[] stamps = [new FieldStamp(InfluenceShape.Disc(Int2.Zero, 8, 100), new Int2(x, y))];
back.Tick(stamps, tick, Stencil.Create(front, decayPerMille: 300, spreadDenominator: 4));
(front, back) = (back, front);   // double-buffered pair: front now holds tick

var reader = front.AsReader();   // ref struct, zero allocation
int value = reader.ReadCell(new Int2(12, -6));
Int2 gradient = reader.Gradient(cell);
```

Bulk weights in, world snapshots out — both cross the boundary as unmanaged spans
(`WriteRegion`/`ReadRegion`) or PNM files via `GridInfluence.Io`:

```csharp
using GridInfluence.Io;

using var weights = Pnm.LoadWeights("weights.pgm");      // P5/P6, 8 or 16 bit, signed or unsigned
field.WriteRegion(offset, size, weights.Samples);

Pnm.SaveGraySigned($"frame-{tick:0000}.ppm", offset, size, snapshotSpan); // export any frame
```

Drive a whole simulation from JSON (fields, shapes, motion clips, image layers, captures) with the
[scene sample](samples/scene/README.md):

```sh
dotnet run --project samples/scene -c Release
```

## World API (virtual field)

A second engine for game-style worlds: many bounded power-of-two grids placed side by side in
floating world space (a high-resolution grid near the camera, coarse grids far away). **Nothing is
rasterized** — a mark stays a record and influence is evaluated at query time, so `Process` costs
`O(sources)`, not `O(touched cells)`. The whole surface is small handles over unmanaged state —
no objects, no per-tick allocation:

```csharp
byte w  = World.New();                                   // a world
byte g0 = Grid.New(w, power: 10, x: -512f, y: -512f, size: 512f);  // 1024², near camera
byte g1 = Grid.New(w, power: 6,  x:    0f, y: -512f, size: 512f);  // 64², far
byte l  = Layer.New(w);                                  // a channel on every grid

byte aggro = Stamp.Circle(strength: 1);                  // registered once -> byte id
byte ember = Fade.Stamp(percent: 30);                    // source decays 30% per process

// sources are persistent — place once, move or remove by id, reprocess every frame
int enemy = World.Place(w, l, x, y, bound: 40f, aggro, fade: ember);
World.Move(w, enemy, x2, y2);                            // reposition; routing re-resolves
World.FadeLayer(w, g0, l, Fade.Layer(percent: 10));      // scope fades set once, run at process
World.Process(w);                                        // one serial pass: route + convert + emit

short heat = World.Query(w, g0, l, cx, cy);              // lazily bucketed tile scan
short at   = World.QueryAt(w, g0, l, wx, wy);            // world-space point
long  zone = World.Query(w, g0, l, x, y, wdt, hgt);      // area sum
World.Remove(w, enemy);                                  // retract; next process drops it
```

How it works:

- **Routing is automatic.** `Process` tests each source's world-space rect against every grid
  rect; a source straddling a seam emits into *every* overlapped grid — no clipping, no edge
  jitter, and different resolutions convert independently. A sticky per-source grid hint makes
  the common case one rect test.
- **Fades are multipliers, not sweeps.** `Fade.Stamp` decays a source's own multiplier;
  `Fade.Layer`/`Fade.Grid` decay a whole channel or grid — O(1) work, and sources with fade id 0
  skip the path entirely.
- **Queries materialize lazily.** The tile index for a (grid, layer) is built on the first read
  after a process; write-only frames never pay for it. `Query` sums every mark covering the cell
  (`Circle` coverage is true `dx²+dy²≤r²`, `Box` is rect), clamped to `short`.
- **Caller parallelism.** `World.Process` is serial. For scale, call `World.BeginProcess(w)` once,
  then `World.ProcessSlice(w, start, count)` on disjoint source ranges from your own threads —
  marks claim slots with an atomic cursor and cell sums are commutative, so results are
  bit-identical to serial.
- **Engine-owned sources.** `Place` copies `x, y, bound, stamp, fade` into unmanaged columns and
  returns an int id — no pointer lifetime contract; moving an entity is `World.Move`.

Measured (i9-14900K, .NET 10, warm): **~5.4ns/item** serial on one grid, **~10.6ns/item** across
32 grids, 0 B allocated per apply. 100K marks × 32 grids ≈ **~1 ms serial, ~0.5 ms** sliced across
8 threads. See [`samples/world`](samples/world/Program.cs) for aggro-range, fear-meter, and
traffic-congestion scenarios.

## Receipts

Same-machine, one-variable comparisons against the naive per-cell baseline (256 stamps/tick,
.NET 10): **2.9×** at 256², **3.6×** at 1024², **11.0×** at 2048². Queries: ReadCell 3.0 ns,
Gradient 6.1 ns, SampleBilinear 10.7 ns, 32×32 capture 1.37 µs — all 0 B. `--verify` asserts
pipeline-vs-naive equality, PNM round trips, and the 0 B warm-path receipts; PMU counters live in
[`benchmarks/pmu-receipts.jsonl`](benchmarks/pmu-receipts.jsonl). Full model, numbers, and the
unsafe proof: [`docs/model.md`](docs/model.md).

## Repository layout

- `src/GridInfluence` — the engine package.
- `src/GridInfluence.Io` — PNM codec and JSON scene runner package.
- `tests/` — rasterizer oracles, field algebra, diffusion integration, budgets, IO.
- `benchmarks/` — BenchmarkDotNet suite, `--verify` receipts, PMU collector and committed counters.
- `samples/scene` — end-to-end JSON + PNM walkthrough.
- `samples/world` — World API scenarios: enemy aggro range, fear crowd meter, traffic congestion.
- `docs/model.md` — semantics, receipts, unsafe lifetime/aliasing/alignment/concurrency proof.

## License

[MIT](LICENSE) — © IAFahim; portions derived from BovineLabs Timeline Grid Influence (© BovineLabs, MIT).
