# STATUS P0-1: frame harness (partial)

Branch `p0-1-frames`, cut from `main` @ d9d4329. No PR opened (the coordinator asked for a push only).

## Done and verified

- `--frames <B1|B2|B4|C|W|all|verify> [--precision exact|cell] [--measure N] [--warmup N] [--entities N] [--seed N] [--jit-ms N]`
  in new files `benchmarks/Frame{Harness,Driver,Scenarios,Support,Receipts}.cs`.
  The only edit to existing benchmark code is one dispatch line at the top of `benchmarks/Program.cs`.
  `docs/model.md` has a new "Frame harness" section (frame definitions, pinning, receipts, unsafe proof for the harness buffers).
- `--budget` exists only on `snapshot-bucket-index`. Its `canonical` scenario was re-implemented as B1 (no engine code ported, JSON written by hand instead of `JsonSerializer`).
- Validation, all green on this branch: build (0 warnings), test 242/242, sample, `--verify`, `DOTNET_EnableHWIntrinsic=0 --verify`.
- `--frames verify` (9 receipts) is green with intrinsics on and off. It checks percentile rank, culture-invariant JSON, Morton sort, and per frame at 20k sources: determinism over two runs, 0 B on warm frames, complete reads, driver positions equal `TryGetSourceSpec`, W live count equals entity count, and the field equals a fresh rebuild.
  These receipts run before any timing inside `--frames`; they are not hooked into `--verify` (that would edit `Verification.cs`, which the parallel cards may touch).
- Checksums and final-state hashes are identical across intrinsics on and off and across processes.
- Every measured frame allocated 0 B (`bytes_per_frame` 0, intrinsics on and off).
- `--precision cell` exits 2 with a message until `Layer.Precision` exists (P0-10).

## Where it ran (deviation)

The machine is an Intel i9-14900K, not a Zen 4. Runs used `taskset -c 2` (P-core, max 5700 MHz; sibling CPU 3 was 0-1 % busy, whole machine 7-14 % busy from other agents, sampled every 2 s). Nothing here is a Zen 4 or Zen 4c number. The 315 ns/entity B1 reference comes from the Ryzen 5 8500G, so the "B1 reproduces 315 ns +-10 %" check is not met numerically here and has to be rerun on the 8500G.

## Numbers (pinned CPU 2, intrinsics on, 300 frames after 30 warmup, nearest-rank, three fresh processes; value / value / value)

ns/entity unless noted; stage values are p50.

| Frame | p50 | p99 | Stage split (ns/entity) | Ledger (8500G) |
|---|---|---|---|---|
| B1 Exact, 1M, 4096^2 | 238.9 / 239.8 / 225.2 | 283.2 / 284.8 / 291.9 | sync 5.8, process 97-100, point 26, gradient 72-82, changed 27 | 315 total: 7.5 / 134 / 172 |
| B2, 1 mover | 1.19 / 1.26 / 1.15 us | 2.99 / 3.11 / 3.00 us | move+process (timer cost ~20 ns per stamp) | 0.6 us |
| B2, 1 mover, 3000 frames | 1.11 / 1.06 / 1.06 us | 2.77 / 2.91 / 2.78 us | | |
| B2, 200 movers | 210 / 199 / 186 us | 277 / 246 / 212 us | move ~13 us, process 174-201 us | 166 us |
| B2, 200 movers, 3000 frames | 185 / 181 / 189 us | 221 / 223 / 225 us | | |
| B4 idle | 27 / 27 / 27 ns | 50 / 32 / 30 ns | Process only; mostly two timestamp calls | target <= 1 us |
| C, persistent emulation, 1M, 1024^2 | 92.5 / 90.0 / 87.6 ms | 99.3 / 94.2 / 91.9 ms | sync 6.7, process 56-59, point 5.3, gradient 20.3, area 0.4 | 150-250 ms |
| C incl. amortized re-sort | 92.6 / 90.1 / 87.7 ms | 99.3 / 94.3 / 92.0 ms | re-sort 9.8-11.1 ms per 128 frames (~0.08 ms per frame) | |
| W, 1M, 2048^2 | 47.1 / 45.1 / 43.6 ms | 54.0 / 50.8 / 46.2 ms | sync 1.7, churn 0.04, process 35-37, point 1.8, gradient 5.1, area 0.5 | 80-130 ms |

For B1, C and W, p50 ns/entity equals p50 ms (1M entities).

Noise: run-to-run spread of p50 was 6 % (B1), 5 % (C), 8 % (W), 8-12 % (B2); p99 spread 3-26 %. C and W drifted downward across the three runs (92 -> 90 -> 88 ms, 47 -> 45 -> 44 ms).

Scalar (`DOTNET_EnableHWIntrinsic=0`, two runs, published not gated; p50 / p99): B1 247.7 ns (p99 316), process 113, gradient 70; B2 1 mover 2.0 us (p99 6.3 us); B2 200 movers 457 us (p99 500 us); B4 28 ns; C 96.0 ms (p99 104.1); W 55.3 ms (p99 72.2). 0 B per frame.

Other facts: RSS after placing 1M sources 296-307 MB (ledger about 299 MB); W peaks at 551-555 MB. Crossing rate at Cell: B1 62.5 % (0.5 x 0.25 cell shift per frame, not the roughly 10 % the P0-10 row assumes), C and W agents 12.6 % (0.1 cell/frame). At the 1/256 anchor: 100 %.

## Deviations and open points

- Not Zen 4; no Zen 4c numbers (see above).
- Baselines C (88-92 ms) and W (44-47 ms) are below the ledger ranges (150-250 ms, 80-130 ms). The i9 core runs B1 at about 0.76x of the 8500G figures, which explains part of it; the rest looks like the ledger assuming B1-like per-mover costs (8x8 boxes) where C uses 4x4 boxes. Needs the owner's call after an 8500G run.
- B2 1 mover is 1.1 us here against 0.6 us in the ledger; the harness picks a different cold source every frame (random access into about 300 MB).
- B1 uses the branch's canonical motion (alternating +0.5, +0.25), which gives 62.5 % cell crossings, not about 10 %.
- W definition chosen by this card (the roadmap leaves details open): 250k agents on layer 0 (boxes), 740k static (box/tent mix), 10k timed sources (fade + expire, replaced 100 per frame), 1,000 spawns and 1,000 despawns per frame including the 100 timed, disk radii 8/16/24/32. Agents are kept 40 cells inside the grid so every disk sum is complete (`TrySenseArea` reports `false` for a disk that spills outside the grid).
- The 300-frame p99 is the 297th value; for B2/B4 it is noisy, hence the extra 3000-frame runs.

## Left to do (in order)

1. Add `p50_ns`, `p99_ns`, `mean_ns` to the frame and stage JSON objects (the ns/entity fields round sub-microsecond stages to 0.000), and print a measured timer overhead (back-to-back `Stopwatch.GetTimestamp` cost, about 20 ns) in the header so B4 and the B2 `move` stage can be read. Rebuild and rerun `--frames B2` and `--frames B4`.
2. Rerun the full set (`taskset -c <cpu> dotnet benchmarks/bin/Release/net10.0/GiBenchmarks.dll --frames all`, three fresh processes, intrinsics on and one or two off) from the final binary on the Ryzen 5 8500G: one Zen 4 core and one Zen 4c core (`lscpu -e=CPU,CORE,MAXMHZ`). The numbers above come from the binary built at commit ae1a752.
3. Check the "B1 = 315 ns +-10 %" Done-when on the 8500G and decide the C/W ledger question.
4. Decide whether the harness receipts should also run inside `--verify` (one line in `Verification.Run`).
5. Open the PR into `main` (body ends with the Claude Code line from the brief), run the five validation commands once more, and do not merge.
6. When P0-10 lands: replace the `--precision cell` rejection in `FrameHarness.Run` and pass the precision in `FrameScenario.NewLayer`.

Raw output of the runs is in the scratchpad `/tmp/claude-1000/-home-i-GitHub-grid-influence/81c8b2ec-ca1b-4872-9ca7-d30f74d29a34/scratchpad/` (`exact_all_*.jsonl`, `long_b2_*.jsonl`, `long_b4_*.jsonl`, `scalar_all_*.jsonl`, `load.log`); it is not in the repository.
