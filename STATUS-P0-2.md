# STATUS: card P0-2 (floor probes P1-P8), branch `p0-2-floor`

Status: partial. Code and receipts are complete and green; the measurement protocol, the unsafe proof in
docs/model.md, and the PR are not done.

## Done and verified

`dotnet run --project benchmarks -c Release --no-build -- --floor [--probes P1,P4] [--iters N]` makes no Gi calls.
Each probe has a scalar path, an AVX2 path (guarded by `Avx2.IsSupported`) and an independent reference. Receipts run
before any timing of that probe; a failed receipt skips its timing and the run exits 1. The same receipts also run inside
`--verify` (one added line in `benchmarks/Program.cs`, plus the `--floor` dispatch line).

Files, all new, under `benchmarks/`: FloorHarness, FloorProbes, FloorQuantize (P1), FloorHistogram (P2, P3),
FloorConvolve (P4), FloorPublish (P5), FloorReads (P6), FloorDisks (P7), FloorSweep (P8).

| Probe | What | Receipt |
|---|---|---|
| P1 | quantize 1M (Vector256) | scalar and vector equal a double-precision reference for all 1M cells, including edge values |
| P2 | histogram RMW, random and Morton order, uniform and clump | equals a managed-array histogram, both prefetch settings |
| P3 | two interleaved histograms, run coalescing, merge | equals the same reference, odd counts and run tails |
| P4 | convolve 1024^2: box 4x4 int32 (impulses + 2D prefix), tent6 and bell8 int64 (separable, vpmuldq) | equals direct kernel emission for the whole grid; bound `max count < 2048` checked |
| P5 | publish 1,024 tiles: sat16(I + 2 x RoundQ40), compare, store, sum, max, argmax, row sums, row maxima | scalar and vector equal a sign-magnitude managed reference, all outputs |
| P6 | 1M value + gradient reads, random and coherent, 32 MB and 2 MB (AVX2 gathers plus scalar border path) | scalar and vector equal a flat-array reference, including outside and absent tiles |
| P7 | disk sums r = 16, 32 and 64, integer spans + row sums + masked SIMD | spans equal integer sqrt for r = 0..64; sums equal a cell scan for 3,000 disks plus 2,100 random disks |
| P8 | anchor-change sweep over 1M slots (left-pack), 0.01 %, 10 % and 100 % moved | equals the scalar list, partial-block counts included |

Supplementary L2-resident cases: P1, P4 (box, tent, bell), P5, P6 (coherent 2 MB), P8 (10 %). They show the
compute-bound figure separately from the L3-bound 1M-array figure.

Validation on this branch (before the final commit): build 0 warnings, `dotnet test` 242/242, sample exit 0,
`--verify` green, `DOTNET_EnableHWIntrinsic=0 --verify` green (14 vector receipts report SKIP, the scalar
receipts pass). All timed passes allocate 0 B.

## Machine deviation

This machine is an **Intel Core i9-14900K** (Raptor Lake), not Zen 4. `taskset -c 4` is a P-core (L1d 48K, L2 2048K,
L3 36864K, no AVX-512). CPUs 16-31 are E-cores. The Zen 4 and Zen 4c tables cannot be produced here. The
`DOTNET_EnableAVX512F=0` run is identical to the default run, because no AVX-512 path exists or is runnable (the
probes use Avx2 only). The p50/p99 numbers below are P-core, AVX2. Other agents shared the machine
(load average about 3.5).

## Numbers so far (P-core CPU 4, AVX2, p50 / p99 in ms unless stated; one run per row)

Pass/fail against the section 2 prediction is by p50. The 32 MB read rows and P7 were re-tuned during the session, so
earlier first-pass figures are not listed.

| Row | Predicted | p50 | p99 | Verdict | L2-resident p50 |
|---|---|---|---|---|---|
| P1 quantize 1M | 0.05-0.1 | 0.235 | 0.257 | above | 0.247 (compute-bound, not memory-bound) |
| P2 histogram, coherent (Morton, no prefetch) | 0.3-0.5 | 0.394 | 0.424 | within | n/a |
| P3 interleaved2 (Morton uniform) | 0.3-0.5 | 0.353 | 0.384 | within | n/a |
| P2 histogram, random (prefetch 16) | 1.5-3 | 0.855 | 0.914 | below | n/a |
| P4 box 4x4 int32 | 0.1-0.2 | 0.144 | 0.147 | within | 0.129 |
| P4 tent6 int64 | 0.3-0.6 | 0.657 | 0.671 | slightly above | 0.629 |
| P4 bell8 int64 (no prediction) | none | 0.855 | 0.917 | n/a | 0.845 |
| P5 publish 1,024 tiles | 0.15-0.25 | 0.418 | 0.480 | above | 0.308 |
| P6 reads, coherent 32 MB | 0.2-0.4 | 2.91 | 3.33 | above | coherent 2 MB, L2-resident block: 1.41 |
| P6 reads, random 32 MB | 3-6 | 5.86 | 7.13 | within (noisy, 5.1-5.9 over three runs) | n/a |
| P7 disk r = 16 (ns/disk) | 50-70 | 92.5 | 99.8 | above | n/a |
| P7 disk r = 32 (ns/disk) | 100-130 | 197 | 208 | above | n/a |
| P8 anchor sweep, 10 % moved | 0.2-0.4 | 0.246 | 0.280 | within | 0.135 |

Other observations:
- P3 coalescing: 2.37 ms on uniform Morton data (branch mispredicts) but 0.376 ms on clumped data. Interleaved2 is
  0.353 ms uniform and 0.521 ms clumped. For T-2: interleave on uniform, coalesce on clumps. Neither is best for both.
- Prefetching hurts coherent histograms: 0.56 ms with prefetch against 0.39 ms without.
- Noise: p50 spread between repeated runs was about 3-10 %. P6 random 32 MB ran 5.1-5.9 ms over three runs; other rows
  were within about 3 %.
- Why rows are above prediction: P1 and P5 are limited by Raptor Cove vector-port throughput (L2-resident equals the 1M
  figure for P1). P4 tent is bound by vpmuldq and vpmovsxdq port pressure. P6 pays for AVX2 gathers plus the
  border-cell path. P7 is instruction-bound, with about 2 branch mispredicts per disk.
- Predictions were not tuned beyond this. P7, P6 and P5 are the likely places for further gains.

## Deviations and failures

- Zen 4 / Zen 4c tables not produced (wrong CPU, see above). No AVX-512 path.
- The hardware-intrinsics-off timing run and the E-core timing run were not done. Only the scalar `--verify` receipts
  were run with intrinsics off.
- The three repeated timing runs per configuration were not done; only the noise above was observed.
- docs/model.md has no unsafe-proof subsection for the new benchmark pointer code (required by AGENTS.md).
- No PR opened (coordinator's instruction).
- The P1 vector path uses the raw `vcvttps2dq`, so its cell conversion is exact only for in-range inputs.
  The P4 vector column pass assumes |row-pass value| < 2^31, which the receipt checks.
  Both assumptions are noted for T-3.

## Left to do, in order

1. Add a "Floor probes" subsection before "## Tool inspection" in docs/model.md with lifetime, aliasing, alignment,
   concurrency and bounds for the benchmark pointer code:
   - `FloorArena`: 64-byte aligned, zeroed, 64 B tail padding, single-threaded, freed at suite disposal.
   - P6 gathers stay inside the page for interior positions only.
   - P6 prefetch hints never fault and may point just outside a block.
   - P6 query arrays are padded by 72 ints; histogram index arrays by 16 ints.
   - P8 left-pack stores 8 ints past the last result, covered by the 16-int list padding.
   - P7 requires disks fully inside the field and radius <= 64.
2. Build, run the five validation commands, then commit.
3. Timing protocol: three repeated `taskset -c 4 dotnet benchmarks/bin/Release/net10.0/GiBenchmarks.dll --floor` runs.
   Also run once each:
   - `DOTNET_EnableHWIntrinsic=0` (scalar),
   - `DOTNET_EnableAVX512F=0` (identical here),
   - an E-core (`taskset -c 20`) as a stand-in for Zen 4c.
   Do not build during timing runs. If a Zen 4 machine is available (8500G), run there for the real table.
4. Optionally squeeze P7 and P5 (reduce spills in `FloorDiskKernel.SumVector`; the P5 sign fix-up) and add the
   symmetric-kernel fold for P4 tent and bell (not measured).
5. Open the PR into main with body ending in the Claude Code line, using the Appendix B report with the table above.
