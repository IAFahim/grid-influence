# STATUS-V2-6 · Change-rate `Process`, old pipeline deleted

Worktree `/home/i/GitHub/grid-influence-v26`, branch `v2-6-change-rate` from `engine-v2@ad2b5be`.
Solo in-session work (no sub-agents). This file is the design + progress anchor; remove at merge.

## Card (ROADMAP §4, verbatim intent)

Writes set hot columns and append the slot to a changed list (dedup by per-slot tick stamp); nothing
else happens until `Process`. `Process`: per changed slot per grid, compute the target state (masked
anchor, kernel, gain, liveness), compare with the deposited record; where they differ emit −old and
+new taps directly into `I`/`T`/`B`, grow the tile dirty rectangle, overwrite the record; publish
dirty rectangles (V2-5); run derived layers (current algorithm); write epochs and `ChangedTiles()`.
Delete in the same PR: `ScanCrowding`, `Crowded`, `LayerLive`, `LiveStale`, `RebuildLayer`,
`ScatterSource`, `MergeLayer`, `RebuildTile`, rebuild chunks/chains/arenas, `DepositOp`, `Ops`,
`Pending`, `Fragments`, `Placements`, difference and dense planes, `TileBake.Resolve`, remaining
users of `IntegrateBoxes` and `Quadrant`. Until R-6, exclusion reads the deposited record and calls
`Place`.

Done when: corpus/interpreter/receipts green at Exact and Cell; B1 Exact `Process` ≤ 80 ns
(ledger 134 → 40–80, stop > 100); B1 Cell `Process`+`Sync` ≤ 10 ns (stop > 20); B2 ≤ 2 µs / ≤ 20 µs;
RSS ≤ 180 MB (stop > 300).

## What the old pipeline looks like (engine-v2@ad2b5be anchors)

- Writes: `EnqueuePlace`/`EnqueueMutation` (World.cs:677/718) append `DepositOp` to `w->Ops`,
  deduped by `w->Pending` (op index + 1 per slot). Batch path builds the same ops
  (World.SourceBatch.cs). Timers/fades/expiry call `EnqueueMutation` (Schedule.cs:196 `RunTimers`).
- `Process` (World.cs:1565): `RunTimers` → `ScanCrowding` → `ApplyDeposits` (immediate `Deposit`
  or deferred `BuildDeposits`/`ApplyFragment`; crowded layers skip deposits) → `RebuildLayer`/
  `RebuildTile` for crowded layers → per-dirty-tile `TileBake.Resolve` → `Derive` → epochs swap.
- `Deposit` (World.cs:852) loops **all grids**: `TileBake.Place(x, y, origin, scaleQ8, size, stamp,
  angle, scale, precision)` → per touched tile `EnsureDirty` + `Emit` into diff/dense/tent/bell.
  Sources deposit on every grid; each grid snaps the anchor differently → the deposited record is
  per (slot, grid) (D6).
- Exclusion: `Applied` (Sense.cs:774) returns the From-side of the pending op (what is in the
  field); `ExcludedValue` recomputes that source's contribution with `TileBake.Place` and subtracts.
- Rewind (World.cs:1270) replays the op journal backwards (`RestoreSlot`), re-enqueues rewind ops,
  rebuilds `LayerLive`/free ring.

## New design

1. **Marking.** `SourceColumns` gains a hot `Mark` column (int). Every write path (scalar, batch,
   timers, rewind) sets columns then marks: `if (s->Mark[slot] != mark) { s->Mark[slot] = mark;
   w->Changed.Add(slot); }` with `mark = w->Tick + 1`. `w->Changed : NativeBuffer<int>`.
   This replaces `Ops`/`Pending` entirely.

2. **Deposited record** per (slot, grid): packed 16 B — gen (uint; 0 = nothing deposited), layer,
   stamp, gain, alive, maskedAngle (ushort), scale (ushort), masked anchor Ax/Ay (int fixed-point
   at the layer's precision — the P0-10 snapped key). Stored as chunked columns parallel to
   sources, per grid, allocated with the slot. Compare target key vs record key as packed values.

3. **`Process`.**
   1. `RunTimers` (mutations mark changed; heap stays until V2-9).
   2. Sweep `w->Changed` per slot × grid: compute target key (snapped anchor via the P0-10 mask
      math + kernel identity + gain + liveness). Equal → skip. Differ → withdraw −oldGain × taps at
      the old anchor, deposit +gain × taps at the new anchor, into `I` (int32: box/raster/dense,
      per-source rounded) or `T`/`B` (int64 Q40: tent/bell), grow per-tile dirty rects, overwrite
      the record. Taps come from the V2-4 `KernelCache`; miss/oversize → build into scratch
      (`KernelFactory`), still exact. Edge clipping is per-cell bounds in the emitter; the cached
      kernel is the pure-phase shape (resolves V2-4's edge-clipped key concern for this consumer).
   3. Per (grid, layer) dirty tile list + parallel per-dirty-tile rects: `TileBake.Publish`,
      `MaxPyramid` from summaries, Dirty/Changed lists as today (`InDirty` indexes the list).
   4. `Derive` unchanged. 5. Epochs + swap, as today.
4. **Planes.** Attach `PlaneStore` to `LayerData`: `I` always with the tile; `T`/`B` first use;
   summaries replace the block sum/max header on the write side. Read paths (`Query` region sums,
   senses) move to summaries/pages. Decide minimal-vs-full swap by blast radius during wiring;
   diff/dense/tent/bell block storage and their offsets die.
5. **Exclusion.** `Applied` reads the record (gen + layer-reads checks) — it is exactly what is in
   the field, pending or not. `ExcludedValue` keeps calling `TileBake.Place` with record params.
6. **Journal.** Re-represent as per-slot first-write state (D11 shape): on first mark after
   `Record`, append the slot's pre-state record. `Rewind` restores those columns, marks the slots
   changed (the delta path republishes on the next `Process` — matches the P1-4 rewind law),
   restores schedules. Full rework stays V2-11.
7. **Deletions.** In order: crowd heuristic → rebuild machinery → op pipeline → diff/dense planes +
   `TileBake.Resolve` + `IntegrateBoxes`/`Quadrant` users. `LayerLive` and its maintainers
   (`CountGain`, Rewind rebuild, batch deltas) go; nothing reads it afterwards.

## Exactness argument (to keep the corpus green)

Integer accumulation commutes: the same per-cell integer contributions reach `I`/`T`/`B` regardless
of deposit order; withdraw = negate. Saturation happens once in `Publish` (`Sat16`), as in
`Resolve`. V2-4 tests pin gain × taps ≡ today's emission cell-for-cell (int32 and wrapping int64);
V2-5 tests pin `Publish` ≡ `Resolve`. Plane width: `I` int32 with the publish exactness guard;
promotion is V2-8.

## Receipts to re-derive

`mixed-stamp-process-allocates-zero-bytes`, `multi-layer-churn-matches-scans`, B1/B2/C/W frame
receipts, stats 12/12 (rebuild arena/placements/chunks accounting → changed list + records +
planes), `WarmFramesMakeNoNativeHeapCalls`, fifo-ring receipt (already `.Slot`-based).

## Progress

- [x] Recon: write path, Process, Deposit, RunTimers, Applied, Rewind read and understood.
- [x] This design committed.
- [x] Marking: `Mark` column + `w->Changed` on all write paths (scalar, batch, timers, sync, rewind).
- [x] Deposited records: 16 B `DepositRecord` per (slot, grid) in `GridCtx.Records`; packed-key compare.
- [x] Delta sweep into `I`/`T`/`B` + dirty rects (`World.Sweep.cs`); `PlaneStore` attached to `LayerData`.
- [x] Publish wiring + summaries feed reads and `MaxPyramid`.
- [x] Exclusion (`Applied` reads the record) + journal migration.
- [x] Deletions (crowd → rebuild → ops → diff/dense/`Resolve`/`LayerLive`) — World.cs −1133/+~350 lines.
- [x] Tests migrated (`LayerLiveTests` → `DepositRecordTests`, `ResolveTests`/`PublishTests`/stats tools).
- [x] Validation green: build 0/0, tests 1064/1064, sample, receipts 144 PASS + 127 PASS/17 SKIP
      (HW off), all warm paths 0 B with and without intrinsics; corpus regenerated.
- [ ] Pinned A/B on cpu0 vs pristine ad2b5be (B1 ≤ 80 ns / B1 Cell ≤ 10 ns / B2 / RSS ≤ 180 MB).
- [ ] docs/model.md proof updates for the new unsafe surface.
- [ ] PR + report.

## Semantics discovered while validating (now pinned by the suite)

- `MarkEpoch` must advance **after** `SweepChanged`: a timer-expiry inside Process can mark a slot
  that a later mutation in the same outer epoch reuses — if the epoch advanced before the sweep,
  `MarkChanged` saw the stale mark as current and silently dropped the new deposit.
- Journal = epoch-start snapshots taken from `w->Pending` during `Process` while recording —
  including mutations made before `Record` (matches the reference's `pendingFrom` journaling).
  `Rewind` restores pending first, then journal entries in reverse, re-queuing each journaled slot
  once (`JStamp`/`RewindEpoch` dedup) with a deposited-state pending baseline (`_pendingFrom =
  _deposited` equivalent), and never clears `w->Changed` (queued entries must stay queued).
- `ChangedTiles`/`Changed` read `ld->Epochs`, which must be allocated lazily: the reference only
  materializes the epochs array when a deposit's coverage passes `TileBake.Touches` on ≥1 tile
  (`MarkDeposited`). `Epochs` now allocates inside `DirtyIndex` plus a `Touches`-gated coverage
  scan in `EmitDelta`; epoch-0 tiles count at `since=-1` in both engines, so allocation timing is
  observable.
- Merged `I` plane (box+dense in one int32) wraps before publish at ~2.2e9/cell where the old
  split planes did not; `Publish` clamps the wrapped value. The 550k-source overflow test was
  re-baselined to `-32768` (exclusion parity — its actual assertion — is unaffected).
