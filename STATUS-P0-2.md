# STATUS: card P0-2 (floor probes P1-P8), branch `p0-2-floor`

Status: code, receipts, docs and measurements complete; Zen 4 / Zen 4c tables not producible on this
host (deviation, not a failure). PR not opened (coordinator's instruction).

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
| P7 | disk sums r = 16, 32 and 64, integer spans + row sums + masked SIMD | spans equal integer sqrt for r = 0..64; sums equal a cell scan for 2,100 random disks plus the 3,000 timed disks |
| P8 | anchor-change sweep over 1M slots (left-pack), 0.01 %, 10 % and 100 % moved | equals the scalar list, partial-block counts included |

Supplementary L2-resident cases: P1, P4 (box, tent, bell), P5, P6 (coherent 2 MB), P8 (10 %). They show the
compute-bound figure separately from the L3-bound 1M-array figure.

The unsafe proof for the benchmark pointer code is in `docs/model.md`, subsection "Floor probes"
(end of the "Unsafe proof" section, before "Tool inspection"): lifetime, aliasing, alignment,
concurrency, bounds, and the two T-3 carry-forward assumptions (P1 raw `vcvttps2dq` exact only for
in-range inputs; P4 vector column pass assumes |row-pass value| < 2^31, receipt-checked).

Final validation on this branch (HEAD after the docs commit): build 0 warnings, `dotnet test` 242/242,
sample exit 0, `--verify` green, `DOTNET_EnableHWIntrinsic=0 --verify` green (14 vector receipts report
SKIP, the scalar receipts pass). All timed passes allocate 0 B in every configuration below.

## Timing protocol (all runs use the already-built binary; no build during timing)

- Three repeated full `--floor` runs pinned to a P-core: `taskset -c 4 dotnet benchmarks/bin/Release/net10.0/GiBenchmarks.dll --floor`.
- One run each with `DOTNET_EnableHWIntrinsic=0` (scalar) and `DOTNET_EnableAVX512F=0`, both on CPU 4.
- One run on an E-core stand-in: `taskset -c 20` (Gracemont-class E-core, NOT Zen 4c).
- Machine load sampled before/after every run: 1-minute load average stayed between 2.2 and 2.9
  (shared host, other agents active; roughly the same 3.5 reported earlier had subsided).
  P-core max 5700 MHz, E-core max 4400 MHz; `lscpu` showed scaling at ~71 % at sample time.

## Machine deviation

This machine is an **Intel Core i9-14900K** (Raptor Lake), not Zen 4. CPU 4 is a P-core (L1d 48K,
L2 2048K, L3 36864K); CPUs 16-31 are E-cores. The Zen 4 and Zen 4c tables require a Ryzen 5 8500G
that is not available here; recorded as a deviation, not a failure. `/proc/cpuinfo` has no `avx512f`
flag and the probes dispatch on `Avx2.IsSupported` only, so the `DOTNET_EnableAVX512F=0` run is
identical to the default run by construction; measured, it agrees within run-to-run noise
(e.g. P4 tent 0.646 vs 0.648-0.654 ms default; P1 0.236 vs 0.235-0.243; P7 r16 91 vs 87-90 ns/disk).

## Final numbers

P-core column: median p50 of the three repeated runs, with the p50 range and max p99 in parentheses.
Pass/fail against the section 2 prediction is by p50. Values ms unless stated.

| Row | Predicted | P-core (CPU 4, AVX2) | Scalar (CPU 4) | E-core stand-in (CPU 20, AVX2) | L2-resident p50 (P-core) | Verdict |
|---|---|---|---|---|---|---|
| P1 quantize 1M | 0.05-0.1 | 0.243 (0.235-0.243; p99 0.255) | 3.324 | 0.784 | 0.237 | above |
| P2 histogram, coherent (Morton, no prefetch) | 0.3-0.5 | 0.393 (0.391-0.412; p99 0.428) | 0.374 | 0.842 | n/a | within |
| P3 interleaved2 (Morton uniform) | 0.3-0.5 | 0.367 (0.361-0.375; p99 0.392) | 0.373 | 0.747 | n/a | within |
| P2 histogram, random (prefetch 16) | 1.5-3 | 0.906 (0.875-0.922; p99 0.989) | 0.969 | 1.474 | n/a | below |
| P4 box 4x4 int32 | 0.1-0.2 | 0.139 (0.137-0.151; p99 0.165) | 0.738 | 0.450 | 0.137 | within |
| P4 tent6 int64 | 0.3-0.6 | 0.652 (0.648-0.654; p99 0.671) | 5.496 | 1.571 | 0.634 | above |
| P4 bell8 int64 (no prediction) | none | 0.851 (0.851-0.854; p99 0.888) | 7.201 | 2.132 | 0.848 | n/a |
| P5 publish 1,024 tiles | 0.15-0.25 | 0.375 (0.361-0.435; p99 0.545) | 2.421 | 1.051 (p99 2.885) | 0.308 | above |
| P6 reads, coherent 32 MB | 0.2-0.4 | 2.868 (2.830-2.899; p99 3.329) | 4.911 | 12.040 | 1.375 (2 MB block) | above |
| P6 reads, random 32 MB | 3-6 | 5.465 (5.268-5.656; p99 8.167) | 13.264 | 30.520 | n/a | within |
| P7 disk r = 16 (ns/disk) | 50-70 | 88.1 (87-90; p99 106) | 412 | 159 | n/a | above |
| P7 disk r = 32 (ns/disk) | 100-130 | 192 (185-195; p99 208) | 1199 | 341 | n/a | above |
| P7 disk r = 64 (ns/disk, no prediction) | none | 482 (479-483; p99 516) | 3018 | 874 | n/a | n/a |
| P8 anchor sweep, 10 % moved | 0.2-0.4 | 0.217 (0.213-0.243; p99 0.329) | 0.933 | 0.418 | 0.137 | within |

Run-to-run spread (3 P-core runs): under 1 % for P3 coalesce, P4 tent/bell, P5 L2-resident, P6
coherent, P7 r64; 3-7 % for P1, P2/P3 histogram rows, P4 box, P6 random, P7 r16/r32, P8 L2-resident;
10-14 % for P4 box (one run), P8 0.01 %/10 %; worst 20-34 % on P5 full and P8 100 % moved (their
p99 tails hit by shared-host interference; their L2-resident and min values are stable).

Other observations:
- P3 coalescing: 2.409 ms on uniform Morton data (branch mispredicts) but 0.446 ms on clumped data.
  Interleaved2 is 0.367 ms uniform and 0.508 ms clumped. For T-2: interleave on uniform, coalesce on
  clumps. Neither is best for both.
- Prefetching hurts coherent histograms: 0.515 ms with prefetch against 0.393 ms without; it also
  does not help random order at these distances on this host (0.906 vs 0.969 scalar-random).
- Scalar vs vector: P2/P3 histogram RMW is essentially vector-irrelevant (0.374 scalar vs 0.393
  default: the kernel is scalar in both, only the merge vectorizes, 0.115 vs 0.359 ms). The gathers
  (P6), the Q40 multiplies (P4), the pack/saturate epilogue (P5), vcvttps2dq (P1) and the left-pack
  (P8) are where AVX2 pays 2.4-8.4x.
- E-core stand-in scales 1.8x (P7) to 5.6x (P6 random) against the P-core; its p99 tails are wider
  (P5 p99 2.885 ms). It is a Gracemont-class core, useful only as an ordering hint for Zen 4c, not
  as a prediction.
- Why rows are above prediction: P1 and P5 are limited by Raptor Cove vector-port throughput
  (L2-resident equals the 1M figure for P1). P4 tent is bound by vpmuldq and vpmovsxdq port pressure.
  P6 pays for AVX2 gathers plus the border-cell path. P7 is instruction-bound, with about 2 branch
  mispredicts per disk.
- Predictions were not tuned beyond this. P7, P6 and P5 are the likely places for further gains
  (optional; not in this card's scope).

## Deviations and failures

- Zen 4 / Zen 4c tables not produced: no Ryzen 5 8500G on this host (see above). The E-core run is
  labelled an E-core stand-in, not Zen 4c.
- The `DOTNET_EnableAVX512F=0` run is identical to the default run (no AVX-512 path exists or is
  runnable; the probes use Avx2 only).
- No PR opened (coordinator's instruction); branch `p0-2-floor` pushed instead.
- The P1 vector path uses the raw `vcvttps2dq`, so its cell conversion is exact only for in-range inputs.
  The P4 vector column pass assumes |row-pass value| < 2^31, which the receipt checks.
  Both assumptions are noted for T-3 (and now in docs/model.md).
- Optional squeezing (STATUS item 4 of the previous state: P7/P5 spills, symmetric-kernel fold) was
  deliberately not done: out of scope for this session.
