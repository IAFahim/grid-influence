# STATUS: P0-5 (R3, int64 total before saturation) — COMPLETE

Branch `p0-5-int64-total`, based on `origin/phase0` @ f3370f2. Three commits: the partial
resolve change (cbf0840), the tests (366d879), the docs (2e7733c). No PR, no merge, no tags
touched. Proposed package version: 1.2.0-alpha.5 (coordinator bumps
`Directory.Build.props`, which still reads 1.2.0-alpha.4; `dotnet pack src/Gi -c Release`
succeeds as alpha.4).

## Done — everything on the card

- `src/Gi/TileBake.cs`: `Resolve` dispatches over `ResolveScalar` (exact int64 sum,
  saturate once) and `Resolve128`/`Resolve256`, which keep int32 fast loops behind the
  `ExactBound = 1 << 29` guard; a tripped tile restores `previousRow` from a private
  32-int entry copy and re-resolves through the scalar path. All three are internal.
  Committed by the previous agent; unchanged since.
- `tests/Gi.Tests/ResolveTests.cs` (committed):
  - `ResolveTests`: Theory over paths (dispatch, scalar, Vector128, Vector256) against a
    managed-array tile builder and an exact `long` reference that mirrors the int32 wrap of
    the box prefix. Handcrafted: boxes +1.5e9 + dense +1.5e9 saturates to +32767 and the
    negative mirror to −32768 (both wrapped wrong under the old int32 total);
    `int.MaxValue` boxes + `int.MaxValue` dense + tent term 2 reports `any == true` (old
    int32 sum wrapped to 0). Edge-value grid (int.Min/MaxValue, ±2^29, ±2^30, ±1 …) and
    random planes in four regimes (small, near ±2^29, full int32, mixed edges), tent/bell
    null and non-null including `long.MinValue`/`long.MaxValue`, with non-zero entry
    `previousRow` (exercises the guard fallback and the restore). Output, `previousRow`,
    `pageSum`, `pageMax` and `any` all compared. 342 cases.
  - `PlaneLimitTests`: pins the limits through `World.Place` with `Stamp.*(1,1,−128)` at
    gain −16 on one cell-centred peak — box: 2^20−1 sources read +32767, 2^20 wraps to
    −32768; tent: 4,095 reads +32767, 4,096 wraps negative; bell: same. 6 cases.
  - Old-code check (not committed): temporarily reverting `ResolveScalar` to the int32
    total fails 256 of the 336 path cases (the 80 passing are vector cases inside the
    guard bound, which were always exact), so the tests detect the bug they were written
    for.
- Test matrix: `dotnet test` green with intrinsics on, `DOTNET_EnableHWIntrinsic=0`, and
  `DOTNET_EnableAVX2=0` (the last forces the dispatcher onto Resolve128). 726/726 each.
- Five validation commands all pass: build (0 warnings), test 726/726, sample, `--verify`
  65 PASS / 0 FAIL, `--verify` with `DOTNET_EnableHWIntrinsic=0` 65 PASS / 0 FAIL.
- Receipts unchanged: the PASS/FAIL lines of `--verify` on `origin/phase0` and on this
  branch diff identical (65 lines, both configurations).
- `docs/model.md` (committed): Process bullet rewritten (int64 total, saturate once,
  guard + fallback), new "1.x plane limits" section (box/dense: 2^31/2048 = 1,048,576
  max-amplitude sources on one cell, 1,056,832 at 2,032; tent/bell: 2^63/(2048·2^40) =
  4,096 at one peak — the ROADMAP card's "≈ 2,000" is wrong, corrected in the doc), and
  the unsafe proof for the 128 B `entry` stackalloc (lifetime/aliasing/alignment/
  concurrency).
- B1-like A/B timing (below): Process median +0.97 %, inside the 3 % gate and the noise
  band; checksum identical.

## B1-like A/B numbers

Scenario: 1M 8×8 box sources (value 100, gain 4), 4096² grid, every source moved one cell
per frame via `World.Sync`, `ResolvePool.SerialOnly = true`, `DOTNET_TieredCompilation=0`,
`taskset -c 8` (the 6.0 GHz P-core of this i9-14900K), 30 warmup + 300 measured frames,
6 interleaved A/B rounds, one process per side per round, nothing built during timing.
Scratch harness outside the repo in `/tmp/p0-5ab/` (assembly name `GiBenchmarks` for
internals; "before" = Gi.dll from `git archive origin/phase0`, "after" = Gi.dll from this
branch). Full-grid `Query` checksum after every run: 25,600,831,176 on all 12 runs.

| | before (phase0) | after (branch) |
|---|---|---|
| Process p50, median of rounds | 98.111 ms | 99.060 ms |
| Process p50, min–max | 96.755–99.393 ms | 97.724–101.543 ms |
| Process p99, median | 105.283 ms | 106.625 ms |
| ns/entity (p50) | 98.11 | 99.06 |
| Sync p50, median | ~8.42 ms | ~8.42 ms |
| Allocated per frame | 8,200 B | 8,200 B |

Gate: B/A median p50 = +0.97 % ≤ 3 %. Machine noise: 1-min loadavg drifted 2.28 → 4.48
over the run; each side's own round-to-round p50 spread is ~2.7 % (A) and ~3.9 % (B, one
noisy round with p99 131 ms), so the +0.97 % median sits inside the noise band.

## Findings and deviations

- The ROADMAP card's "≈ 2,000 max-amplitude sources" for tent/bell is wrong: the Q40
  peak product is exactly `value·gain·2^40` for a centred kernel, so the limit is
  2^63/(2048·2^40) = 4,096 (4,128 at amplitude 2,032). Empirically pinned: 4,095 →
  +32767, 4,096 → wrapped negative. docs/model.md states 4,096; ROADMAP.md was not
  edited (the owner owns the board).
- The A/B harness shows 8,200 B allocated per frame on BOTH sides of the 1M-mover
  scenario. Pre-existing on phase0, identical before/after, and not one of the
  `--verify` 0 B receipts (which pass), so out of this card's scope; noted for the owner.
  Candidate cause worth a look in a later card: steady-state growth in the crowded
  rebuild or changed-list buffers at 1M ops/frame.
- Out of scope but related (unchanged): `Sense.cs` `ExcludeCell`/`Absent` compute
  `(int)Quadrant(...) + DenseOf(block)[cell]` in int32 and can disagree with the now-exact
  page value on overflowing tiles.
- The pack ran against the current `Directory.Build.props` (1.2.0-alpha.4) because the
  coordinator bumps versions once per phase; the proposed version for this change is
  1.2.0-alpha.5.
- The scratchpad/driver the previous agent left is gone; the A/B harness was rebuilt from
  scratch in `/tmp/p0-5ab/` and may not survive either. The numbers above are the record.
