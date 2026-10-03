# Gi

Sparse tiled integer influence fields for .NET. One API, byte handles over unmanaged state,
integer-exact deposits, **0 B** on warm `Process`/`Query`.

This repository previously carried two engines (a rasterized tick field and a re-emitted marks
engine). It now ships exactly one: the **deposit engine**. Mutations write incrementally into
per-tile difference arrays; `Process` resolves only dirty tiles; queries read resolved `int16`
pages. The re-emitted design was measured and retired — see [Receipts](#receipts).

## Get started

```sh
dotnet add package Gi --version 0.2.0-alpha.1
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

## How it works

- **Handles, not objects.** `World`, `Grid`, `Layer`, `Stamp` return `byte` ids into static
  unmanaged arenas; `Place` returns an `int` source id. No managed allocation anywhere on the
  data path.
- **Deposits are incremental.** Each live tile owns one 12,480 B block (difference array + dense
  buffer + `int16` page). `Place`/`Move`/`SetGain`/`Remove` apply exact integer deltas
  immediately — moving a source never rescans the field.
- **`Process` touches only dirty tiles.** A 2D prefix sum resolves each dirty difference array,
  saturates to `short` after summation (so cancellation is preserved), and frees tiles that
  resolve to zero. An unchanged world costs nothing.
- **Sub-cell placement.** Positions convert to cell space in Q8; raster stamps deposit with
  bilinear edge weights, uniform rasters take a difference-array box path.
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
| `saturated-sum-clamps` | saturation sticks at ±32767 after summation |

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

## Run the full thing

```sh
dotnet build Gi.slnx -c Release -m:1
dotnet test Gi.slnx -c Release --no-build
dotnet run --project samples/world -c Release --no-build
dotnet run --project benchmarks -c Release --no-build -- --verify
DOTNET_EnableHWIntrinsic=0 dotnet run --project benchmarks -c Release --no-build -- --verify
dotnet run --project viz -c Release --no-build   # writes viz/out/index.html
```

## Layout

- `src/Gi` — the package: `World`, `Grid`, `Layer`, `Stamp`, tile bake, page map.
- `tests/Gi.Tests` — oracle tests: box/raster deposits, cross-tile, multi-resolution, saturation,
  move/remove/setgain exactness, sub-cell, determinism.
- `benchmarks` — `--verify` receipts plus `--timing` scratch loop.
- `samples/world` — aggro range, multi-resolution traffic, baked raster stamps.
- `viz` — HTML field visualizer.
- `tools/stats` — JSON internal stats, memory accounting, allocation receipts, and Linux perf.
- `docs/model.md` — semantics, receipts, unsafe proof.

## License

[MIT](LICENSE) — © IAFahim; portions derived from BovineLabs Timeline Grid Influence
(© BovineLabs, MIT).
