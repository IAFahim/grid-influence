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

Stamp[] stamps = [new Stamp(InfluenceShape.Disc(Int2.Zero, 8, 100), new Int2(x, y))];
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
- `docs/model.md` — semantics, receipts, unsafe lifetime/aliasing/alignment/concurrency proof.

## License

[MIT](LICENSE) — © IAFahim; portions derived from BovineLabs Timeline Grid Influence (© BovineLabs, MIT).
