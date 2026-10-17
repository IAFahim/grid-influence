# Changelog

## 2.0.0-alpha.1 (unreleased)

The engine is rebuilt around change-rate work: `Process` re-emits only the sources whose snapped
state changed, directly into per-tile planes, and publishes only the tiles they touched. The public
surface is still `World`, `Grid`, `Layer` and `Stamp`; the additions are nested.

### Breaking

* **Source ids.** `Place` returns `World.SourceId(uint Slot, uint Generation)` instead of an `int`.
  Every scalar mutator, `TryGetSourceSpec`, the column calls (`SourceId*`), batch commands and
  results, and `TransformRange.Sources` take it. Generations are 32 bits, a slot is retired when its
  generation reaches `uint.MaxValue`, and free slots are reused first-in first-out. There are no
  `int` overloads.
* **Precision.** `Layer.New(world)` still means `Layer.Precision.Exact` (1/256 cell, 65,536 angles);
  `Layer.New(world, precision)` takes `Exact`, `Half`, `Quarter`, `Cell` or `new Precision(phaseBits,
  turnBits)`. A source costs work only when its snapped key changes, so crowds should pass `Cell`.
* **Residency.** `Layer.New(world, Layer.Residency.Transient, precision)` creates a layer that
  forgets its contents every `Process`; it is written with `World.Splat` / `World.BeginSplat` and
  rejects `Place` (`WriteStatus.WrongResidency`).
* **`Changed` means published bytes.** A tile has changed when its published `int16` bytes differ
  from those of the previous `Process`, whichever code path ran. `Changed`, `ChangedTiles` and
  `ChangedMask` follow it; `ChangedTiles(world, grid, layer, dst)` now lists tiles in ascending index.
* **Statuses.** Mutators return `World.WriteStatus` (`Place` also has an overload with `out
  WriteStatus`); column reads return `World.ReadStatus` instead of `bool`. Scalar reads still
  return `bool`.
* **Caps.** A stamp footprint after scaling may not exceed 512 cells per axis (`Place` →
  `InvalidStamp`, `Scale` → `CapacityReached`), and a disk read's reach may not exceed 4,096 cells.
* **Exact overflow.** The 1.x plane limits (a wrap at 1,048,576 stacked max-amplitude boxes or 4,096
  tents) are gone: planes fold into wide planes before they could overflow, and totals saturate once.
* **Removed** (all were internal or documented as unsupported surfaces of 1.x): the resolve worker
  pool and the `SerialOnly` switch, the rebuild-from-bins crowd path and its heuristics, the timer
  heap, the difference and dense planes, and the per-world op pipeline. `World` is single-threaded.

### Added

* `World.Splat`, `World.BeginSplat`/`SplatWriter`, `World.View` and `World.FieldView`, the fused
  `SenseGradientColumn` overload with values, `TrySense`/`TrySenseGradient`/`TrySenseArea` overloads
  that exclude a splat made at the query point, `Grid.SpatialKeys`, `Grid.SortByKeys`, `Grid.Disorder`.
* `World.Memory(world, out storeBytes, out liveTiles, out pages, out kernelEntries, out liveSources)`:
  the tooling read-out. Summed plane-store bytes, live tiles, live slab units, occupied kernel-cache
  entries and live sources, allocation-free, zeros for a dead handle.
* `World.TryGetSchedule(world, source, out gain, out fadeFrom, out fadeTo, out fadeTicks,
  out fadeTicksLeft, out expireTicksLeft)`: the schedule read-back for exact saves. False for dead
  or unscheduled sources; reports the current gain, a stepping fade's ends, duration and ticks left,
  and an expiry's remaining ticks, allocation-free.
* `benchmarks --frames|--gate|--floor|--area|--region|--splat|--kinds|--order|--changed|--view`,
  `tools/fuzz`, a reference interpreter and a committed golden corpus under `tests/Gi.Tests`, and a
  determinism matrix in CI.
* Exact exclusion of a source's own contribution from cached kernel taps, an epoch pyramid behind
  `Changed`/`ChangedTiles(since)`, and a generic `Pyramid<T, TOp>`.

### Fixed

* A published page that rounded to all zeros dropped its tile together with sub-unit tent or bell
  residue, so later deposits started from a lost partial sum.
* An exclusion by the id of a source whose removal was pending, after another source reused its slot in
  the same frame, excluded nothing (or the old deposit for the new id).
* A raster stamp placed at a scale other than 1 could sample taps outside its one-sample border and add
  whatever bytes lay next to the stamp's buffer to a few edge cells. Such taps are zero by definition and are
  now skipped; seven golden-corpus streams (23 frames) had recorded the stale bytes and were regenerated.
