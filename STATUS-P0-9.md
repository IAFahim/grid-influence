# STATUS P0-9: one-core pool, cleared outputs (R8 + R10)

Branch `p0-9-pool-and-clear`, cut from `phase0` @ f3370f2. Status: partial. Code, tests, docs and all five validation commands are done; the before/after timing is not.

## Done and verified

- R8. `ResolvePool.TryAcquire` returns false when `Processors < 2`, before `Create()`, so no worker thread, scratch or wake event exists on a one-processor host. `Processors` is an internal static initialised from `Environment.ProcessorCount`; it is the test seam, like `SerialOnly`. `ResolvePool.Created` is a new internal read-only accessor. (`src/Gi/ResolvePool.cs`)
- R10. Every column read writes its outputs before an early return on a dead world, a layer out of range, or a null required column: `SenseColumn` (both overloads), `SenseGradientColumn`, `SenseAreaColumn`, `SenseMaxColumn`, `SenseNearestColumn`, `ChangedMask`. (`src/Gi/Column.cs`, `src/Gi/Column.Sense.cs`, new `src/Gi/Column.Unfilled.cs`)
  - Rows hold what the per-row `TrySense*` form writes for an unfillable row: zero values, gradients, totals and mask words; `short.MinValue` for nearest values; the query point for best and nearest positions (zero when the query column is null).
  - Return values are unchanged, nothing is written when `count <= 0`, and the mask word count is formed in unsigned arithmetic.
  - Every non-null output is written, including when another column is null.
- Tests: 384 before, 454 after.
  - `tests/Gi.Tests/ColumnRejectTests.cs` (67 tests): rejected world and rejected layer at counts 1, 63, 64, 65 and 200, compared row by row against the `TrySense*` oracle, with a guard word after each mask; null-column cases; empty-batch return values.
  - `tests/Gi.Tests/PoolAvailabilityTests.cs` (3 tests): `Processors = 1` refuses the pool without creating it, and pooled-size columns run inline with results identical to the pooled run; a single-processor-host test asserts `!Created` and is only active when `Environment.ProcessorCount == 1`.
  - `ColumnShareTests.Pool_IsAvailableForColumnBatches` now expects `TryAcquire() == (ProcessorCount > 1)`.
  - Red on old code: with `Column.cs` and `Column.Sense.cs` reverted, 66 of the 67 `ColumnRejectTests` fail. The R8 seam tests are red on old code by compile error (no `Processors`).
  - Not shown: I did not revert only the `Processors < 2` gate to prove those tests fail at runtime.
- `docs/model.md`: pool absent on one processor, plus the rejected-column output contract with its lifetime, aliasing, alignment and concurrency note.
- Validation, all green on this branch:
  - build: 0 warnings, 0 errors.
  - test: 454/454 with 32 CPUs.
  - test: 454/454 with `DOTNET_PROCESSOR_COUNT=1`.
  - sample: runs.
  - `--verify` with intrinsics on: 65 PASS, 0 FAIL.
  - `--verify` with `DOTNET_EnableHWIntrinsic=0`: 65 PASS, 0 FAIL.
  - `dotnet pack src/Gi`: builds 1.2.0-alpha.4.
- Proposed version: 1.2.0-alpha.5. Behavior changes are the outputs written on rejected column calls and no pool on a one-CPU host. There is no API change. Directory.Build.props is not bumped.

## Measured so far

Smoke runs only (3 warm, 10 measured frames, 1M moving 8x8 boxes, 4096^2, `Sync`+`Process`+`SenseColumn`+`ChangedMask` per frame). They are on the OLD code (`phase0` Gi.dll). They are not the required before/after, and the interleaved runs were not done.

| condition (old code) | Process p50 / p99 | SenseColumn p50 / p99 | ChangedMask p50 / p99 | threads | B/frame |
|---|---|---|---|---|---|
| 32 CPUs, pooled | 29.2 / 41.0 ms | 5.85 / 6.33 ms | 6.30 / 6.62 ms | 11 | 0 |
| `taskset -c 12`, 1 CPU, 1 worker thread still created | 127.9 / 159.8 ms | 32.1 / 39.6 ms | 32.4 / 39.6 ms | 8 | 0 |

- Checksums were identical between the two conditions, so the output is the same pooled or not.
- Noise: not characterised. Other agents were running on the host (load average about 3), and 10 frames is too few for a meaningful p99.
- Nothing has been measured on the new code.

## Failed or deviated

- Nothing failed.
- Interpretation of "zeroes its values": nearest values get `short.MinValue` (the documented not-found value) and best/nearest positions echo the query point, instead of plain zeros, to match the per-row `TrySense*` contract. Say if you want literal zeros.
- A null `words` or `values` pointer on a valid world is still undefined behaviour in the success path. I did not add new early returns, since the card does not list them.
- `SyncTransforms` clears its invalid masks only after validation. That is by design and I left it alone.
- The R8 default-CI test is a seam test. The end-to-end "pool never created" assertion is only active on a one-CPU host: run `DOTNET_PROCESSOR_COUNT=1 dotnet test`, which passes. It has not been run under `taskset -c 12`, because the sandbox refused a `taskset` command line containing "test".

## Left, in order

1. Harness: `/tmp/claude-1000/-home-i-GitHub-grid-influence/81c8b2ec-ca1b-4872-9ca7-d30f74d29a34/scratchpad/p0-9/harness` (Program.cs, Harness.csproj; it references `../gi-before/Gi.dll`).
   - Old build: `p0-9/bin-before`.
   - Create the new build by copying this branch's `src/Gi/bin/Release/net10.0/Gi.dll` to `p0-9/gi-after` and building the harness into `p0-9/bin-after`, with the HintPath pointing at `gi-after`.
   - If the scratchpad is gone, rewrite the harness: 1M `Box(8,8,1)` sources, grid power 12, random walk of at most 0.5 cell per frame, per-frame `Sync`, `Process`, `SenseColumn` and `ChangedMask`, p50/p99 over frames, `GC.GetTotalAllocatedBytes(true)` per frame, thread count.
2. Run `Harness 1000000 20 150 <label>` interleaved A/B over at least 3 rounds, on a quiet host (check `uptime`, and pick a free P-core if CPU 12 is busy).
   - The four conditions are old and new, each pooled (unpinned or a P-core set) and pinned to one CPU with `taskset -c 12` or `DOTNET_PROCESSOR_COUNT=1`.
   - Expect old-1CPU to show 8 threads and new-1CPU to show fewer (no pool worker). Expect pooled old and new to be equal within noise. Expect 1 CPU new <= old, since there are no spin barriers.
   - Do not build during a timing run.
3. Report Process and SenseColumn p50/p99, B/frame and noise in the Appendix B block.
4. Optional: revert only the `Processors < 2` gate once to show `PoolAvailabilityTests` red at runtime.
5. If it merges: bump the version to 1.2.0-alpha.5 and run `dotnet pack src/Gi` again.
