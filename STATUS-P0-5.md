# STATUS: P0-5 (R3, int64 total before saturation)

Branch `p0-5-int64-total`, based on `origin/phase0` @ f3370f2 (the worktree started on main d9d4329, so the branch was reset to origin/phase0 before any edit). No PR, no merge, no tags touched.

## Done

- `src/Gi/TileBake.cs`: `Resolve` is now a dispatcher over three internal paths. Each has the same signature and contract.
  - `ResolveScalar` is the exact definition: `total = (long)boxes + dense + tentOut + bellOut`, saturated once to int16, `any` taken from the int64 total.
  - `Resolve128` (SSE2/AdvSimd) and `Resolve256` (AVX2) keep their int32 fast loops. They also accumulate a guard, `wide |= (boxes + 2^29) | (dense + 2^29)`, where `ExactBound = 1 << 29` and `ExactMask = int.MinValue | (1 << 30)`.
    - The int32 sum cannot wrap while `|boxes| < 2^29` and `|dense| < 2^29`: `|boxes + dense| <= 2^30`, and the tent+bell terms are `RoundQ40` results in `[-2^23, 2^23]`.
    - Each vector path copies `previousRow` on entry. If any lane trips the guard at the end of the tile, it restores `previousRow` and returns `ResolveScalar(...)` for the whole tile.
    - A tile that stays inside the bound is computed exactly as before, so receipts for non-overflowing inputs are unchanged by construction.
  - `Resolve128`, `Resolve256` and `ResolveScalar` are `internal` so tests can call each path directly.
- `dotnet build src/Gi -c Release` succeeds for net10.0 and netstandard2.1, 0 warnings.

## Not done (nothing below has been run or written yet)

- Tests. Nothing has been added, and the existing suite has not been run on this change.
- `docs/model.md`: the plane-limits text and the unsafe/alignment/concurrency proof for the new `stackalloc` copy and the fallback.
- The five validation commands (build slnx, test, sample, `--verify` with intrinsics on and with `DOTNET_EnableHWIntrinsic=0`).
- `dotnet pack src/Gi` and the version proposal.
- Any B1 timing. No numbers were measured, so there is no before/after, p50/p99, ns per entity, bytes per frame, or noise figure.
  - I archived the phase0 baseline tree to the scratchpad (`.../scratchpad/base`) and started a driver project (`.../scratchpad/driver`: `Driver.csproj` with `AssemblyName=GiBenchmarks` so internals are visible, and `Program.cs` with a `limits` mode and a B1 stub). It has never been built.
  - It is outside the repo and the scratchpad may not survive.

## Findings and deviations

- The worktree was on main, not phase0. I reset the new branch to `origin/phase0`.
- The card's plane limits need checking against the code.
  - Box and dense int32 planes: `MaxGain = 16` and stamp values are sbyte. The largest `|value*gain|` is 2048 (-128 x -16), or 2032 for 127 x 16. The plane is exact below `2^31 / 2048 = 1,048,576` sources on one cell (1,056,832 at 2032). This matches the card's "about 1.06M".
  - Q40 tent/bell planes: `2^63 / (2048 * 2^40) = 4,096` sources at one peak (4,128 at 2032). That matches the existing docs/model.md line "4,096 maximum-strength kernels" and not the card's "about 2,000".
  - I did not verify these empirically. The `limits` mode in the scratch driver is meant to do that. Pin the numbers with a test before documenting them.
- Out of scope but related: `Sense.cs` `ExcludeCell` computes `(int)Quadrant(...) + DenseOf(block)[cell]` and `Absent(...)` in int32. It can disagree with the now-exact page value on overflowing tiles. It is not touched here.
- The roadmap's own B1 receipt/timing harness (`--frames`) is in P0-1, not merged into phase0, so timing needs the scratch driver or a rebase onto P0-1.

## Remaining, in order

1. Add `tests/Gi.Tests/ResolveTests.cs`. A Theory over the paths (dispatch, scalar, plus vector128/vector256 when supported) with a managed-array tile builder and an exact `long` reference that mirrors the int32 wrap of the box prefix.
   - Handcrafted: boxes +1.5e9 and dense +1.5e9 must saturate to +32767 (old code wrapped negative); the negative mirror; `int.MaxValue` + `int.MaxValue` + tent term 2 must give `any == true` (old int32 sum wrapped to 0).
   - Edge-value grid and random planes in four magnitude regimes (small, near +-2^29, full int32, mixed edges), with tent/bell null and non-null including `long.MinValue`/`long.MaxValue`.
   - Compare output, `previousRow`, pageSum, pageMax and `any` with a non-zero entry `previousRow`. This exercises the guard fallback and the restore.
   - Confirm the new test fails on `ResolveScalar` semantics of the old code, i.e. fails with the old `Resolve`, by checking out the old file.
2. Run `dotnet test Gi.slnx -c Release --no-build` normally, then with `DOTNET_EnableHWIntrinsic=0` and with `DOTNET_EnableAVX2=0`. The last one makes the dispatcher choose Resolve128.
3. Pin the plane limits with an emitter-level or `World.Place` test (box: 2^20 sources at -128/-16 on one cell; tent: 4,096 sources), then document them in `docs/model.md`.
4. `docs/model.md`: update the Process bullet (the sum is formed in int64 and saturates once). Add the 1.x plane limits section. Add the proof for the entry copy:
   - `entry` is a 128 B stackalloc that is private to the call and read only after being fully written.
   - There is no aliasing with `previousRow`.
   - It uses unaligned loads and stores.
   - There is no concurrency: the tile is owned by one worker.
5. Run the five validation commands; all must pass.
6. B1-like timing: 1M 8x8 boxes, 4096^2, all moving, `ResolvePool.SerialOnly = true`, `taskset -c 8` (the 6.0 GHz P-core on this i9-14900K). Build baseline from `scratchpad/base` and "after" from this branch. Interleave A/B for 5 or more rounds, do not build during timing, and report p50/p99, ns per entity, bytes per frame and noise. Stop-and-report if Process is more than 3% slower.
   - Estimate: Resolve is about 2% of Process on B1, so the guard (4 extra vector ops per 8 cells) should be well under 3%. This is unmeasured.
7. `dotnet pack src/Gi` and propose a version. The behaviour change is only for overflowing inputs, so a patch-level alpha bump such as 1.2.0-alpha.5 is suggested. Do not edit `Directory.Build.props`.
