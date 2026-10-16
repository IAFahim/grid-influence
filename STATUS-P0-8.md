# STATUS: P0-8 (R7 `LayerLive` without recounts), partial

Branch `p0-8-layerlive`, cut from `origin/phase0` @ f3370f2. No PR, no merge, no tags.

## Done and verified

- `LayerLive` is now exact after every public call. A new private helper `World.CountGain(w, layer, before, after)` is called from:
  - `SetGain`
  - immediate or equal-target `Fade` (the old gap: this never touched the count)
  - each fade step and each expiry in `RunTimers` (`Schedule.cs`)
  - the small-update batch path (`ApplySmallSourceUpdates`)
- `Place` and `Remove` already adjusted the count. The folded batch path already used per-partition deltas and was exact.
- `LiveStale` is deleted from `WorldCtx`, `Process`, `Clear`, `Rewind` and `ScanCrowding`. `Process` never recounts.
- `Rewind` keeps the only recount. It is fused into the existing O(Count) free-ring rebuild, so it costs no extra pass.
- `docs/model.md`: the "Crowds" paragraph is rewritten, and the concurrency, alignment and aliasing proof for `CountGain` is added next to the live-count paragraph.
- New `tests/Gi.Tests/LayerLiveTests.cs` (130 tests):
  - explicit regressions for immediate fade, fade steps, expiry, a fading source that expires, the batch fold, a batch above `ColumnThreshold`, `Rewind` with and without a `Process`, and `Clear`;
  - a poison test showing that `Process` does not recount while timers are scheduled;
  - 120 seeded random streams (600 ops each) over Place/Move/SetGain/Fade/Expire/Remove/Turn/Scale/Process/ApplyBatch/Record/Rewind/StopRecording/Clear. After every operation the maintained count is compared with a brute-force pass over the source columns.
  - On the old code, 16 of the first 22 of these tests failed. With the fix all pass, and a one-off run of 4,000 seeds also passed.
- Validation on this branch:
  - build ✓ (0 warnings)
  - `dotnet test`: 514 passed, 0 failed
  - sample ✓
  - `--verify` ✓ and `DOTNET_EnableHWIntrinsic=0 --verify` ✓, 65 PASS each, "all receipts green"
  - the warm-allocation receipts still pass
  - receipts are unchanged

## B2 numbers

Method: a scratch harness (not committed, kept in the session scratchpad):
- 1M 8x8 box sources, 4096^2 grid, Exact precision, one world per case.
- `ResolvePool.SerialOnly = true`, pinned with `taskset -c 10` (an i9-14900K P-core at 6.0 GHz max).
- 150 warm-up frames, then 300 measured frames of Move x k + Process.
- 10 interleaved before/after runs. "Before" is the phase0 Gi.dll and "after" is this branch's Gi.dll.
- Numbers are medians across runs of per-run p50 / p99 in microseconds, with the range across runs in brackets.
- Other agents were running on the machine (load ~1.9), so treat noise as at least this wide.

| case | before p50 / p99 | after p50 / p99 | B/frame |
|---|---|---|---|
| B4 idle | 0.00 / 0.00 | 0.00 / 0.00 | 0 |
| B2 1 mover | 0.40 [0.40-0.50] / 0.80 [0.70-0.90] | 0.40 [0.40-0.40] / 0.70 [0.70-0.80] | 0 |
| B2 200 movers | 126.9 [122.5-132.3] / 147.2 [142.9-161.9] | 125.3 [121.2-131.5] / 149.4 [141.2-158.1] | 0 |
| 1 mover + 10k Expire timers | 796.6 [756.0-809.9] / 829.1 | 0.40 [0.40-0.50] / 0.70 | 0 |
| 200 movers + 10k Expire timers | 930.2 [896.4-968.0] / 2012 | 124.6 [120.8-129.5] / 145.2 | 0 |
| 200 movers + 10k Fade timers | 919.7 [876.4-944.8] / 998.2 | 127.0 [121.4-130.3] / 170.5 | 0 |

- The output checksum (full-grid `Query`) is identical before and after in every case.
- Plain B2 (no timers) is unchanged within noise. This was expected: `LiveStale` was only ever set by timers and `Rewind`, so the plain B2 shape never paid the recount.
- The card's expected effect shows up in the timed cases. With 10k scheduled timers among 1M sources, the removed O(N) recount cost about 0.8 ms per frame (~0.80 ms in the 1-mover case, ~0.80 ms in the 200-mover case). That matches "≈ 1 ms less per frame at 1M".
- Ledger comparison: B2 was 0.6 µs / 166 µs on the Ryzen. Here it is 0.4 µs / 126 µs on the i9-14900K (different machine). V2 targets (≤ 2 µs / ≤ 20 µs) are not met by this card, and it does not claim to. The 200-mover cost is tile resolve, not `LayerLive`.
- No stop-rule line in §2 was crossed (the P0-8 row has none).

## Deviations

- The worktree started on `main` (d9d4329), not `phase0`. I cut `p0-8-layerlive` from `origin/phase0` as instructed.
- I fixed `LayerLive` by direct adjustment plus a `Rewind` recount, not by keeping the `LiveStale` flag. The field is gone entirely.
- Tests read `WorldCtx.LayerLive` and the source columns through the existing `InternalsVisibleTo`. No public API was added.
- The B2 harness was a scratch program, not committed. P0-1 owns the committed harness.

## Left, in order

1. Run `dotnet pack src/Gi` and review the version. This was not done. Proposed version: 1.2.0-alpha.5, a behaviour fix with no public API change. Do not bump `Directory.Build.props` from here.
2. Optionally re-run the A/B timing on a quiet machine (and on the Zen 4 box) to tighten the noise. The scratch harness source can be rebuilt from this description.
3. The coordinator merges `p0-8-layerlive` into `phase0` and updates the ROADMAP board row.
4. P0-10 should drop any remaining reference to `LiveStale` (PLAN.md and ROADMAP.md D10 still mention it as to-be-deleted; they are untouched here).
