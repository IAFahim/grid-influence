# GridInfluence

Chunked, sparse, **integer** influence fields for .NET — a faithful port of the core of
[BovineLabs Timeline Grid Influence](https://github.com/vex-studio/com.bovinelabs.timeline.grid.influence)
(MIT, © 2026 BovineLabs) with the Unity/DOTS plumbing replaced by plain .NET. Derived code is used
under the MIT license; the engine carries no Unity dependencies.

## Model

- The world is an unbounded cell grid. Stamps (solid rect, rect shell, disc, annulus, capsule,
  ellipse, rounded rect, thick line, sector — all exact integer geometry) carry an integer weight
  and are rasterized into horizontal `WeightedRect` spans.
- Each tick: prepare slots (budgets, retention eviction, compaction every 60 ticks, stencil
  frontier) → rasterize stamps → clear active chunk difference arrays → scatter ±weight at span
  corners → resolve (inclusive prefix sum per chunk, then decay/spread stencil against the previous
  buffer). The difference-array trick makes a tick cost O(spans + touched chunks), independent of
  stamped area.
- Decay/spread are per tick: `kept = v·(1000−decay)/1000`, each 4-neighbour receives
  `kept/spread`. Cross-chunk flow uses edge halos. Chunks deactivate only at exact zero.
- Stamps sorted before budget accounting, so budget drops are insertion-order independent; pure
  integer math makes results bit-identical across machines.
- Stamps are per-tick emissions: a field rescheduled with no stamps shows no cells unless decay
  keeps the frontier alive. `WriteRegion`/`ReadRegion` inject/snapshot bulk cells as unmanaged
  spans.

## Usage

```csharp
var spec = GridSpec.FromPowerOfTwo(chunkPower: 5, retentionFrames: 256);
using var front = new InfluenceField(spec);
using var back = new InfluenceField(spec);

Stamp[] stamps = [new Stamp(InfluenceShape.Disc(Int2.Zero, 8, 100), new Int2(x, y))];
back.Tick(stamps, tick, Stencil.Create(front, decayPerMille: 300, spreadDenominator: 4));
(front, back) = (back, front);   // front now holds tick `tick`

var reader = front.AsReader();   // ref-struct borrow, zero allocation
int value = reader.ReadCell(new Int2(12, -6));
Int2 gradient = reader.Gradient(cell);          // un-normalized central difference
float smooth = reader.SampleBilinear(4.5f, 7.5f);
```

Or drive everything from a JSON scene with PPM weight layers via
[`GridInfluence.Io`](../src/GridInfluence.Io/pack-readme.md) and see
[`samples/scene`](../samples/scene/README.md).

## Receipts

Measured on the validation machine (i9-14900K, .NET 10, `benchmarks`), 256 stamps,
4 ticks per invocation, median:

| World   | Naive per-cell scatter + full-grid decay | GridInfluence | Speedup |
|---------|------------------------------------------|-------------------|---------|
| 256²    | 1 755 µs                                 | 603 µs            | 2.9×    |
| 1024²   | 26 781 µs                                | 7 446 µs          | 3.6×    |
| 2048²   | 113 464 µs                               | 10 303 µs         | 11.0×   |

Queries: ReadCell 3.0 ns, Gradient 6.1 ns, SampleBilinear 10.7 ns, 32×32 capture 1.37 µs — all
0 B allocated. Warm ticks allocate 0 B (`--verify` receipt asserts it). PNM decode runs at
~2 GB/s into unmanaged memory. PMU counters (cycles, instructions, branches, branch misses) are in
`benchmarks/pmu-receipts.jsonl`.

The baseline is the cost model the difference-array design exists to replace: paint every covered
cell of every stamp every tick plus a full-grid decay pass. Burst/Unity numbers are not compared —
same-machine, same-fixture, one-variable comparisons only.

## Unsafe proof

- **Lifetime**: all unmanaged blocks are owned by the `InfluenceField`/`FlowField`/`WeightMap`
  instances and freed in `Dispose`. `FieldReader`/`ChunkView`/`FlowReader` are `ref struct`s;
  they cannot escape the owning field's scope. No borrowed span, pointer, or reader is retained
  beyond its call.
- **Aliasing**: a field's data pointer is only captured inside one pipeline phase at a time; the
  stencil reads the previous buffer while writing the current one (disjoint objects enforced by
  the pipeline). Chunk acquisition zeroes fresh and reused chunks, so no stale data leaks through
  `WriteRegion`.
- **Alignment**: every unmanaged block is 64-byte aligned; `ElementsPerChunk` strides are aligned
  to at least 8 ints, which satisfies `Vector128`/`Vector256` loads in the resolve pass.
- **Concurrency**: the serial warm path is single-threaded and deterministic. CoordMap and buffers
  are single-writer. A defensive-copy bug on a readonly struct field (map table filled while its count
  stayed 0) was found by stress and fixed; buffer growth on a readonly struct field is forbidden by
  the same rule — all growable buffers live in non-readonly fields.
  With `parallelism > 1` the field owns a fixed worker set created in the constructor and joined in
  `Dispose`; no thread or event state is created on the tick path. Work items are claimed through one
  packed `(generation << 32 | index)` counter via `Interlocked.Add`, so an item is never claimed twice
  and a stale worker can only observe the current generation's published buffers — generation,
  phase, count, stencil, and buffer pointers are written before the counter release and read after a
  full fence on the claim. Each item writes disjoint storage: resolve/clear items touch only their
  own chunk, rasterize items touch only their own span slice, and scatter items use `Interlocked.Add`
  on diff-array corners — integer adds commute, so output is bit-identical regardless of claim order.
  Reads of the stencil source field are safe because the source is quiescent during the tick; a
  self-referencing stencil forces the serial path. Warm parallel ticks allocate 0 B; the receipt is
  `warm-parallel-tick-allocates-0-bytes`, and bit-exactness is `pipeline-parallel-matches-naive-*`.
- **Total reads**: every reader returns 0 for missing or stale chunks; no input can cause an
  out-of-bounds access. Budget-oversized stamps drop whole, deterministically.
