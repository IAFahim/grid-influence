# Gi: the 1 ms plan

> **Sources.** This plan uses only three things: the Gi code on `main` (f33244f) as pasted, the Frent code as pasted, and the measurements posted in issue #40. Nothing was built, run or read beyond those. A number marked ≈ is an estimate derived from those measurements and from Zen 4 hardware limits. Milestone M0 replaces each estimate with a measured number.

## 0 · Verdict

| Question | Answer |
|---|---|
| Can Gi run 1 M entities in 1 ms per frame on one core? | **Yes, for a typical frame** (§1.2, T1). In T1 all 1 M positions are synced, about 5 % of entities cross a cell, there are about 10 k spawns and despawns, about 10 k fade steps, about 100 k point+gradient senses and a few hundred area senses, on a 1024² layer.<br>**No, if every entity senses every frame.** The senses alone take about 2 ms on one core, whatever engine does them. That frame costs about 3 ms on one core, or about 1 ms on four. |
| How far away is it today? | The canonical frame has all 1 M entities moving. It costs 315 ms on `main` (serial) and 424–484 ms on `snapshot-bucket-index`. That is **300–480× over budget.** |
| Is memory management the cause? | Partly; §2.2 lists six real problems. Most of the 300× comes from what triggers work:<br>• every moving entity re-deposits its stamp every frame, at 1/256-cell precision;<br>• then every dirty tile is re-integrated (on `main`) or re-summed from its whole source bin (on the branch). |
| Should Gi switch to Frent? | **No.** Frent is a game-side ECS: managed `T[]` storage, virtual runners, delegates, archetype moves and locks. It has nothing for a tiled integer field, and adopting it breaks AGENTS.md. Use it in the game and feed Gi its chunk spans (§2.3). |
| Should `snapshot-bucket-index` be merged? | **No.** It is slower than `main` on every axis that matters. Cherry-pick its tests and its profiler fix, and drop the engine. |
| Can the plan comment on issue #40 be trusted? | **Only in part.**<br>• Its "transcription gaps" T1–T16 do not exist in this code.<br>• Its defects D2 and D8 are false. D1, D3–D7 and D9–D11 are real.<br>• Its architecture rebuilds tiles from source bins. The branch built that architecture, and it measured slower. |
| What gets Gi to 1 ms? | Three decisions, then engineering (§4):<br>1. **Snap deposits** to a per-layer quantum (default: one cell). A source then costs work only when it crosses a quantum.<br>2. **Keep each layer as int32 accumulators** updated by precomputed integer stamp images. Saturation happens at read time, so there is no resolve pass, and excluding a source is a subtraction.<br>3. **Make every summary lazy.** |
| In what order? | 1. M0: measure and fix the real defects.<br>2. M1: build the reference oracle.<br>3. **M2: snap the current engine.** This is a small PR, and it proves the snapping lever with real numbers.<br>4. M3–M5: build the accumulator engine on a branch.<br>5. M6: cut over as 2.0, leaving one engine.<br>6. M7 (optional): multi-core.<br>No GPU. |

### Why "every entity senses every frame" cannot fit in 1 ms on one core

- 1 ms ÷ 1 M = 1 ns, which is about 4.5 cycles per entity at 4.5 GHz.
- A point+gradient sense reads three cache lines of the field and runs about 15 instructions:
  - about 2 ns when the field sits in L3;
  - about 8–10 ns when it sits in DRAM.
- Streaming 12–16 bytes per entity (position plus snap key) takes about 0.25–0.35 ms of single-core bandwidth before any work is done.

This is hardware arithmetic. It applies to Gi, Frent, Unity DOTS or any other CPU engine. The only levers are fewer senses per frame, more cores, or a GPU.

## 1 · The goal, made precise

### 1.1 What 1 ms buys on one core

| Fact (one full Zen 4 core, ≈ 4.5 GHz) | Value |
|---|---|
| Budget per entity | 1 ns ≈ 4.5 cycles |
| Stream 12 B per entity (position + 4-byte key) for 1 M entities | ≈ 0.25 ms |
| One random line from a 4 MB field (1024² int32, in L3) | ≈ 2 cycles amortised |
| One random line from a 64 MB field (4096² int32, in DRAM) | ≈ 5–10 ns amortised, at full memory-level parallelism |
| Point+gradient sense | ≈ 2 ns in L3, ≈ 8–10 ns in DRAM |
| Snapped 8×8 stamp update, applied in tile order | ≈ 5–6 ns |
| Re-depositing an unsnapped 8×8 stamp (image built per call) | ≈ 30–50 ns |

What follows from these numbers:

- **Senses.** 1 M senses per frame take about 2 ms on one core, even if everything else costs nothing.
- **Unsnapped deposits.** Re-depositing 1 M unsnapped stamps per frame takes 5–50 ms on one core, in any engine.
- **Map size.** A 4096² int32 layer is 64 MB. Touching most of it each frame adds about 1.5–3 ms of DRAM time.

So the contract has to name four quantities, not one:

- synced entities;
- changes (crossings, spawns, fade steps);
- senses;
- hot cells, that is, the size of the layers being read.

### 1.2 The contract

**Measurement conditions:**

- **Hardware.** One full Zen 4 core of the Ryzen 5 8500G. Not a Zen 4c core: those clock lower.
- **Runtime.** .NET 10 Release, hardware intrinsics on.
- **Threads.** One thread; no `ResolvePool`.
- **Allocation.** 0 B allocated per frame.
- **Statistics.** p50 and p99 over 600 frames.
- **Scalar build.** The build with intrinsics off (`DOTNET_EnableHWIntrinsic=0`) must stay correct and allocate 0 B. Its timings are published separately.

| Frame | Definition | Target |
|---|---|---|
| **T1 typical** | • 1 M sources on one 1024² base layer, 8×8 stamps, Cell snap.<br>• All 1 M positions synced with skip hints (§4.6).<br>• 50 k crossings; 5 k spawns and 5 k despawns; 10 k fade/expire steps.<br>• 100 k point+gradient senses, roughly AI sampling each agent at 6 Hz.<br>• 500 area senses at r = 32 cells. | **≤ 1.0 ms p50, ≤ 1.3 ms p99** |
| T2 everyone senses | T1, but with 1 M point+gradient senses | ≤ 3 ms on one core; ≤ 1.2 ms on four (M7) |
| T3 big map | T1 on a 4096² layer | ≤ 3.5 ms on one core |
| T4 idle | 1 M placed, nothing changes, no reads | ≤ 1 µs |
| T5 exact | T1 with Exact snap, all 1 M moving | priced, not budgeted: about 30–50 ms (today `Process` alone is about 134 ms) |

Every other call gets a published price (§5.1) of the form:

> fixed + per row of reach + per cell returned + per tile touched.

No price grows faster than the work the caller asked for.

### 1.3 Replacement text for "The 100 ns budget" (README and issue #40)

> **The 1 ms frame.** On one core, a typical frame with one million entities costs Gi about one millisecond. That frame syncs every position, has five percent of entities crossing a cell, ten thousand spawns and despawns, a hundred thousand point-and-gradient senses and a few hundred area senses, on a 1024² layer. Every other call has a published price. Snapped deposits make this possible. Exact sub-cell deposits remain available, and they cost one stamp update per moving entity per frame.

Retitle issue #40 to "1 ms typical frame at 1 M entities on one core". Close the 100 ns/entity framing: 100 ns × 1 M is 100 ms, which is 10 FPS. That was never a game budget.

## 2 · Frent

### 2.1 Verdict: do not move Gi onto Frent

| What Gi's warm path needs | What Frent does (from the pasted code) |
|---|---|
| Unmanaged memory and no GC traffic (AGENTS.md) | Each component lives in a managed `T[]` per archetype (`ComponentStorageRecord.Buffer`). Buffers grow with `Array.Resize` (`ComponentBufferManager<T>.ResizeBuffer`). |
| One load per source access by id | `Entity.Get<T>` walks: world table → `EntityTable[id]` → archetype → `ComponentTagTable` → `Components[i].Buffer` → row. That is five or more dependent loads. |
| No virtual dispatch or delegates | `IRunner` interface calls per archetype. `Initer` and `Destroyer` delegates run on every create, add and remove. |
| No locks | `GlobalWorldTables.BufferChangeLock` is taken on archetype and type creation. `Interlocked` runs on every `EnterDisallowState` and `ExitDisallowState`. |
| A sparse, tiled integer field with sums and maxima | Nothing. Storing tiles as entities would pay for handles, versions and archetype moves, and get nothing back. |
| Generations that do not wrap | 16-bit versions. Better than Gi's 7 bits, but they still wrap. |

Frent's real strength is chunked SoA iteration that hands out a `Span<T>` per archetype. That is what the game should use. It is not where Gi loses time.

### 2.2 The memory management that *is* wrong (each item is fixed in place in §4)

1. **One source is spread over 17 `NativeBuffer` columns.** A single `Sync` or `Move` touches about 10 of them, roughly 10 random cache lines per moving entity. Fix: one packed hot record per slot.
2. **`TileBake.Place` rebuilds a placement of about 270 bytes**, twice per moving source per frame, and copies it into per-worker buffers. Fix: an image cache keyed by the snapped shape.
3. **A tile is a separate `NativeMemory.AlignedAlloc`.** The block holds the page plus the difference plane (6.5 KB). Dense, tent and bell planes add 4 KB and 8 KB each, up to about 27 KB per tile, allocated and freed one tile at a time. Fix: 4 KB int32 pages from a slab, with a free list.
4. **`PageMap` is open addressing over three arrays, with tombstones.** Every probe costs three dependent loads, and churn forces rehash spikes. Fix: one dense `int` directory per (grid, layer).
5. **Every mutation adds overhead.** A 40-byte `DepositOp` copies the from/to state, and every write goes through the `Pending` indirection. Fix: keep "deposited" and "current" state per slot; the difference between them is the op.
6. **State is global and static.** `Runtime` is static, and `ResolvePool` runs ten phases through static pointers, so only one world can use it at a time. Fix: per-world state, and no pool on the single-core path.

### 2.3 How a Frent game feeds Gi (2.0 API, §4.6)

```cs
using GiWorld = Gi.World;

public struct Position { public float X, Y; }
public struct GiSource { public long Id; }
public struct GiKey { public long Value; }

public static unsafe class InfluenceSync
{
    public static void Run(Frent.Systems.Query movers, GiWorld.Token token)
    {
        foreach (var (positions, sources, keys) in movers.EnumerateChunks<Position, GiSource, GiKey>())
        {
            fixed (Position* p = positions)
            fixed (GiSource* s = sources)
            fixed (GiKey* k = keys)
            {
                var range = new GiWorld.TransformRange
                {
                    Sources = &s->Id,
                    Xs = &p->X,
                    Ys = &p->Y,
                    PositionStride = sizeof(Position),
                    Keys = &k->Value,
                    Count = positions.Length,
                };
                GiWorld.SyncTransforms(token, new ReadOnlySpan<GiWorld.TransformRange>(ref range));
            }
        }
    }
}
```

Notes on the sketch:

- **One call per chunk.** Each chunk is pinned only for the length of its own call, which is cheap, and nothing outlives the `fixed` block.
- **Skip hints.** Gi updates `Keys` in place and skips every row whose snapped key is unchanged. It never touches its own slot arrays for those rows, so the pass streams 16 B per entity.
- **Name clash.** Frent's `World` is a class and Gi's `World` is a static class, so games need the `using` alias.
- **No dependency.** Frent stays out of `src/Gi`. This adapter belongs in a sample or in the game.

## 3 · Where today's time goes

### 3.1 Measured: issue #40, Ryzen 5 8500G, serial, 1 M 8×8 box sources on a 4096² grid

| ns per entity | `main`, serial | `main`, pooled | `snapshot-bucket-index` |
|---|---|---|---|
| Sync | 7.5 | 7.9 | 7.5 |
| `Process` | 134 | 45 | 285 |
| Reads (value + gradient + changed columns) | 172 | 131 | 131 |
| **Total** | **315** | **184** | **424**, or 484 with the bucket index |
| 1 mover among 1 M sources | 0.6 µs | — | 10 µs, after the bucket index |
| 200 movers among 1 M sources | 166 µs | 131 µs | 6.3 ms |

### 3.2 Root causes, from the code

**`Process`: 134 ns per moving source.**

1. Anchors are 1/256 of a cell, so every float wiggle becomes a deposit.
2. `ScanCrowding` flips layers onto the crowd rebuild.
3. `ScatterSource` runs `TileBake.Place` per source, which builds about 270 bytes and includes divisions. It writes 36-byte `RebuildItem`s into 16-item linked chunks, and copies placements for non-box stamps.
4. `RebuildTile` clears each tile and re-emits it. Then `TileBake.Resolve` runs prefix sums over 1 024 cells across int32 and int64 planes.
5. Each tile also pays `MaxPyramid.Update` and an epoch write.

**Reads: about 45–57 ns per call per entity.**

- `SenseRange` loops over grids and converts the position once per grid.
- `PageMap.TryGet` costs a hash plus up to three dependent loads (`_used`, `_keys`, `_blocks`), then the page.
- The gradient column calls `TrySenseGradient` per element. That runs `GetContext` and `Pick`, then four `Query` calls, each of which re-validates and re-probes.
- `ChangedMask` runs `Pick`, `Disk` and a per-tile epoch test for each element.
- Entity order is random with respect to space, so most of these loads miss.

**Sync: 7.5 ns.** `SyncColumns` touches `Alive`, `Gen`, `X` and `Y`. Then `EnqueueMutation` touches `Pending`, `Gain`, `Angle`, `Scale`, `Stamp`, `Layer` and `Gen`, and appends a 40-byte op.

**The branch.** Re-summing a dirty tile from its source bin costs O(bin). At 1 M density that is about 100 members, for every dirty tile, every frame. The bucket index removed the scaling with population but not this per-tile floor. That is why the branch is slower than `main` for dense movers.

### 3.3 Earlier analyses: what to keep and what to discard

| Claim | Source | Status |
|---|---|---|
| 100 ns per entity is the budget | issue #40 body | **Wrong target.** 100 ms per frame is 10 FPS. The real goal is about 1 ms per frame. |
| T1–T16 "transcription gaps" (empty `MaxPyramid` branch, unstarted workers, unzeroed allocations, …) | issue plan comment §2.1 | **False.** The code clears, copies, starts and assigns everything that comment lists. |
| D2: `ChangedMask` never clears its output | issue plan comment | **False.** It clears its output words. |
| D8: `Clear` leaves `Pending`, `LayerLive` and the rebuild pairs stale | issue plan comment | **False.** `Clear` resets all three. |
| D1: column bitmask race | issue plan comment | **Real.** Chunks are claimed downward from `count`, so their boundaries are not multiples of 64. Two workers can then write the same `ulong` without atomics. |
| D3: two meanings of Changed | issue plan comment | **Real.** The delta path counts tiles deposited into; the crowd path counts tiles whose bytes changed. |
| D4: 7-bit generation with a LIFO free list | issue plan comment | **Real.** Under churn a stale id reaches a new source within about 128 reuses. |
| D5: int32 sum before saturation | issue plan comment | **Real.** It needs about 1.06 M max-gain sources stacked on one cell. |
| D6: non-finite positions accepted by `Sync` and `SyncTransforms` | issue plan comment | **Real.** |
| D7: `AddGrid` lets NaN and ∞ through and never checks origins | issue plan comment | **Real.** |
| D9: immediate `Fade` skips `LayerLive` | issue plan comment | **Real** (it only affects the crowd heuristic). |
| D10: `ChangedTiles(since)` scans every tile | issue plan comment | **Real.** |
| D11: duplicated read paths | issue plan comment | **Real.** |
| Rebuild dirty tiles from source bins, lazily, via impulses and gather | issue plan comment §4 | **Wrong shape.** The branch measured 424–484 against 315 ns per entity. |
| Reference interpreter, counting sort, dense directory, 32-bit generations, integer disk spans | issue plan comment | **Keep.** |
| "1 ms needs about 480×, which means GPU only" | session AI | **Wrong.** Most of that factor is avoidable work (§3.2). |
| "A sum pyramid makes `QueryRegion` sub-µs" | session AI | **Wrong.** `QueryRegion` is bound by its output: it has to write w×h cells. A pyramid speeds up sums, not copies. |
| "Lazy resolve is the same total work when everyone reads" | session AI | **Right.** |

## 4 · Design v2

### 4.1 Semantics (the axioms)

```
key(s, g)     = snap_layer(CellQ8(x_s), CellQ8(y_s))  and  turnBucket_layer(angle_s)
U(s, g)       = UnitImage(stamp_s, sampling(g, scale_s), phase(key), turnBucket)
Acc(l, g, c)  = Σ over sources s deposited in l of  gain_s × U(s, g)(c − anchor(key))
Field(l, g, c) = Sat16(Acc(l, g, c))
Changed(tile, t) ⇔ the tile received a non-zero image fragment during Process at tick t
```

- **`UnitImage` is a pure integer function.** It reuses today's per-cell arithmetic from `TileBake` at gain 1, rounded once per source. Its values fit in 16 bits.
- **Snap.** `snap ∈ {Exact = 1/256 cell, Quarter, Half, Cell}`, chosen per layer.
- **Turn buckets.** `turnSteps` is a power of two per layer; the default is 256, and Exact is 65 536.
- **What changes from today:**
  - Box, raster, disk, cone and dome stamps keep today's arithmetic exactly; at the snapped anchor they give today's values.
  - Tent and bell stamps move from round-of-sum to per-source rounding. Apart from snapping, this is the only change in arithmetic (decision D3).
- **Derived layers.** Same recipes as today, evaluated over `Field` values.
- **Exclusion.** The field without source `s` is `Sat16(Acc − gain_s × U_s)`. This is exact and costs O(1) per cell.
- **Ids.** A source id is a `long`: 32-bit slot plus 32-bit generation. Freed slots go into a FIFO ring.
- **Exactness guard.** Each page keeps a bound, `Bound = Σ |gain| × max|U|`, over the fragments it holds.
  - `Bound < 2³¹` means the int32 sums are exact.
  - `Bound ≤ 32 767` means no cell in the page can saturate, which enables raw sums.
  - A mutation that would push the bound past 2³¹ − 1 returns `CapacityReached`. Reaching it takes more than 1 M max-gain sources stacked on one page.
- **Caches.** Every cache holds a pure function of the state above. Deleting any cache changes speed, never results.

### 4.2 Data layout (per world)

| Structure | Shape | Notes |
|---|---|---|
| Hot source record | slot → 32 B | position x, y (floats as given); angle, scale; gain; layer; stamp; flags; generation; `ChangedAt`; timer link. One cache line holds two sources. |
| Deposited record | (slot, grid) → 16 B | anchor qx, qy; image id; gain. This is what the field currently contains. |
| Cold schedule columns | slot → … | fade from, to, start and ticks; `ExpireAt` |
| Image cache | 2-way set-associative | Keyed by (stamp, sampling, phase x, phase y, turn bucket).<br>Each entry holds width, height, offset, row start/end, row prefix sums, max\|U\| and int16 cells.<br>A miss rebuilds the image exactly. Images larger than 64×64 are built into per-call scratch and never cached. |
| Shift-delta cache (M3.x) | (image, one of 8 directions) | `U(shifted) − U`, so a one-cell crossing costs one fused fragment |
| Tile directory | (grid, layer) → `int[TileCount]` | −1 means absent. 64 KB at 4096², 1 MB at 16384². |
| Page slab | 4 KB int32 pages | 64-B aligned, 2 MB arenas, free list. Header columns: `FragmentCount`, `Bound`, `TouchedTick`, `SummaryTick`. |
| Lazy page summary | allocated on first need | Sum (long); min and max; argmax; row prefix sums (int32, 32×33); row maxima |
| Lazy pyramids | tile-max, tile-epoch | Refreshed from the touched list the first time they are queried |
| Derived pages | int16, separate slab | `ComputedTick` per derived tile |

Memory at 1 M sources on one full 4096² layer is about 32 MB of source records plus about 70 MB of pages, compared with roughly 300–450 MB today.

### 4.3 The frame

**Mutations.** `Place`, `Move`, `Turn`, `Scale`, `SetGain`, `Remove`, `Fade`, `Expire`, `ApplyBatch`, `Sync` and `SyncTransforms` all do the same two things:

1. write the hot record;
2. append the slot to the changed list, at most once per tick (deduplicated by `ChangedAt`).

Nothing else happens until `Process`.

**`Process(tick)`:**

1. **Timers.** Pop the due buckets of the timing wheel (§4.5), apply their effects to the hot records, and append those slots to the changed list.
2. **Diff.** For each changed slot and each grid:
   1. Compute the target: the key, image and gain, or nothing if the source is gone or has zero gain.
   2. Compare the target with the deposited record.
   3. Emit image fragments: `−old` and `+new`. When only the anchor moved by one cell, emit a single fused shift-delta instead.
   4. Overwrite the deposited record.
3. **Sort.** Counting-sort the fragments by global tile id. This is two stable passes.
4. **Apply.** For each tile:
   1. Ensure the page exists, allocating a zeroed one if needed.
   2. Apply each fragment with a SIMD row loop: `acc += gain × U`.
   3. Update `FragmentCount` and `Bound`, set `TouchedTick`, and append the tile to the touched list.
   4. Release the page when `FragmentCount` reaches 0. Exactness guarantees it is all zero.

Cost: O(changes × fragments + touched tiles). An idle `Process` costs O(1). `Process` does no resolve, and it rebuilds no summaries or derived layers.

### 4.4 Reads (the same public methods)

**Every read.** Validate world, layer and pointers once per call, not per element. A world with one grid takes a fast path. With several grids, the read picks from grids pre-sorted by `ScaleQ8` at `AddGrid`.

**Point and gradient.**

- Point: directory → page → `Sat16`.
- Gradient: when the cell's local x and y are both in 1..30, all four neighbours come from the same page.

**Columns.**

- Convert cells 8 at a time.
- Prefetch the page line k rows ahead.
- Split pooled work into chunks that start on multiples of 64 rows. This fixes D1 by construction.

**Area.**

- Disk spans come from an integer two-pointer walk, with no `Math.Sqrt`.
- On pages with `Bound ≤ 32 767`, use the row prefix sums; on other pages, clamp per cell.
- Tiles fully inside the disk use the lazy tile sum.

**Exclude self.**

- Point and gradient: O(1).
- Area: area − gain × Σ U over the overlap of the disk and the image window. On unsaturated pages this uses the image's row prefixes. On other pages it walks the overlap cell by cell.

**Max and nearest.** Lazy tile maxima prune whole tiles, and `SegmentMax` scans the remaining rows. Tie-breaking is unchanged: higher value first, then lower y, then lower x.

**`QueryRegion`.**

- Copies tile by tile.
- `PackSignedSaturate` produces `Sat16` for 16 cells per instruction.
- Absent tiles are zero-filled.

**`Query(rect)` and `QueryMax`.** Use the lazy tile sums and the lazy max pyramid.

**Changed.**

- `Changed` and `ChangedMask`: tile epochs plus a disk test.
- `ChangedTiles(since)`: walks the epoch pyramid, so it costs O(output + log tiles).
- `ChangedTiles()`: returns this tick's touched list.

**Derived layers.**

- Point and gradient evaluate the recipe on the base values directly; nothing is materialised.
- Area, max and region materialise a derived page lazily, whenever one of its input tiles has a newer `TouchedTick`.
- Each recipe chain compiles to one fused kernel.

### 4.5 Time, Record/Rewind, Clear

**Timing wheel.**

- Two levels of 256 buckets, plus an overflow list.
- Entries are linked intrusively through the hot record.
- A due bucket is processed in ascending slot order, so the order is deterministic.

**Fade and expire.**

- A fade schedules its next step with today's `NextStep`.
- Each step becomes one Δgain fragment set.
- Expiry has the semantics of `Remove`.
- Cost is O(due): no heap, no O(timed) sweep and no `LiveStale` recount.

**Record and Rewind.**

1. After `Record`, the first write to a slot journals that slot's full hot and schedule state.
2. `Rewind` restores every journaled slot, shifts schedules as today, re-links their timers and marks the slots changed.
3. The next `Process` then emits exact inverse deltas.

**Clear.** Set every directory to −1, return all pages to the free list, and reset counts and journals. Generations keep counting.

### 4.6 Public API changes (inside `World`, `Grid`, `Layer` and `Stamp` only)

- **Snap and turn steps.** Add `Layer.New(world, Layer.Snap snap, int turnSteps)` and `Layer.TryGetSnap`. `Layer.Snap` is an enum nested in `Layer`: `Exact`, `Quarter`, `Half`, `Cell`.
- **Ids.** Source handles become `long` in 2.0. The `int` overloads stay through 1.x.
- **Transform ranges.** `World.TransformRange` gains two fields:
  - `PositionStride`, so array-of-structs positions (Frent `Position`, Unity `float2`) can be borrowed without copying;
  - `Keys`, an optional `long*` of skip hints, one per row per grid.
  
  Gi compares each row's snapped key with its hint, rewrites the hint, and validates and marks only the rows that changed. Rows it skips keep their previous floats in `TryGetSourceSpec`. A wrong hint can only cause a missed or redundant update. It cannot corrupt the field, because the deposited record stays the source of truth for removal.
- **Mutator status (M6).** Mutators return a status instead of silently doing nothing.
- **Validation.** New rules go into `ValidationRules`, and `GiValidation` mirrors them.
- **Removals.** `ResolvePool.SerialOnly` disappears, because the engine is single-threaded by default.

### 4.7 Determinism and the unsafe proofs (`docs/model.md`)

**Determinism.**

- **Floats appear once.** The only float step is `CellQ8`, after the finite and range checks. Images are built from integer anchors.
- **Order cannot matter.** Integer addition commutes, so fragment order cannot change results. The sort stays stable anyway, so memory layout is reproducible.

**Unsafe proofs to add to `docs/model.md`.**

- **Page slab:**
  - *Lifetime:* from allocation to release, exactly one directory entry points at a page, and a page is released only when its `FragmentCount` is 0.
  - *Alignment:* 64 B.
  - *Concurrency:* one writer.
- **Image cache:**
  - *Immutability:* a record never changes after it is built.
  - *Writes:* a slot is overwritten only by the single writer, inside `Process` or inside a read call.
  - *Escape:* no pointer outlives the call that obtained it.
- **Borrowed ranges:** valid for the duration of the call only, as today.

### 4.8 Scaling (opt-in, M7)

- **Apply.** Tile groups are independent. Partition them across workers by fragment count. Allocate pages for absent tiles serially, before the parallel step.
- **Reads.** Run as chunked columns.
- **Determinism.** Results are bit-identical by construction.
- **GPU.** Not planned. Only T3-sized maps read at T2 rates would need it.

## 5 · Cost model

### 5.1 Price list (estimates, one Zen 4 core; 1024² hot layer unless noted)

"Today" figures come from issue #40. The rows from the issue body were probably measured pooled.

| Operation | Today | v2 target |
|---|---|---|
| Sync a row, all moving, no hint | 7.5 ns | ≤ 1.5 ns (one packed record) |
| Sync a row with a skip hint | — | ≤ 0.25 ns (streaming) |
| Snapped 8×8 crossing (including sort and split) | — (today every mover costs about 134 ns in `Process`) | ≤ 6 ns |
| `Place` or `Remove` | ≈ 10 ns each | ≤ 8 ns each |
| Fade or expire step that falls due | ≈ 10 ns per op | ≤ 8 ns |
| Turn-bucket change, 8×8 | ≈ 10 ns + `Process` | ≤ 8 ns |
| Point sense, column | ≈ 45–57 ns | ≤ 1.5 ns (L3), ≤ 6 ns (DRAM) |
| Point + gradient, column | (inside the 172 ns for three calls) | ≤ 2 ns (L3), ≤ 10 ns (DRAM) |
| Changed check, small reach | (inside the 172 ns for three calls) | ≤ 3 ns |
| Area, disk of radius r | 0.6–1.4 µs at r = 64 | ≈ 2 ns × (2r + 1), so ≈ 0.26 µs at r = 64 |
| Area with exclude-self | 2–3 µs | area + ≈ 2 ns × overlap rows |
| Max or Nearest, r = 64 | ≈ 1 µs | ≤ 0.4 µs |
| `QueryRegion` w×h | ≈ 0.107 ns per cell | ≤ 0.06 ns per cell + ≈ 10 ns per tile |
| `ChangedTiles(since)` | O(all tiles) per call | O(output + log tiles) |
| Derived-layer upkeep | +25 % of `Process` | 0 unless read; a point read costs ≈ 1.5 ns × depth |
| Idle `Process` | ≈ 0 | O(1) |
| Exact-snap mover, 8×8 | ≈ 134 ns | ≈ 30–50 ns |
| Spawn and kill 1 M per frame | ≈ 20 ms | ≈ 16 ms |
| 1 M movers with 16×16 tent/bell stamps, Exact snap | 300–500 ms | ≈ 40–80 ms |

### 5.2 T1 budget

| Stage | Count | Unit | ms |
|---|---|---|---|
| Sync with skip hints | 1 000 000 | 0.25 ns | 0.25 |
| Crossings | 50 000 | 6 ns | 0.30 |
| Spawns + despawns | 10 000 | 8 ns | 0.08 |
| Fade/expire steps | 10 000 | 8 ns | 0.08 |
| Point + gradient senses | 100 000 | 2 ns | 0.20 |
| Area senses, r = 32 | 500 | 0.15 µs | 0.08 |
| **Total** | | | **≈ 0.99** |

Two sensitivities decide whether T1 fits:

- **Skip hints.** Without them, Sync costs about 1.5 ms by itself and T1 misses. The alternative is to sync only the entities that actually moved.
- **Crossing rate.** Cost scales linearly with it. At 10 % crossing, the frame is about 1.3 ms.

### 5.3 The other frames

| Frame | One core | Notes |
|---|---|---|
| T2: 1 M senses | ≈ 2.8 ms | The extra 900 k senses at 2 ns add 1.8 ms. Four cores (M7): ≈ 0.9 ms. |
| T3: 4096² | ≈ 2.5–3.5 ms | Fragments hit cold pages (DRAM), and senses cost ≈ 8 ns each |
| T5: Exact snap, all moving | ≈ 30–50 ms | Images are built per op. Today's `Process` alone is 134 ms. |

## 6 · Milestones

Every milestone must pass the five AGENTS.md validation commands, in both intrinsic modes, with receipts green and 0 B allocated on the warm path.

### M0 · Measure and stop the bleeding (≈ 3–5 days, on `main`)

1. **Benchmarks.**
   - Add T1–T5 harnesses to `benchmarks/`, with `ResolvePool.SerialOnly = true`.
   - Today's engine has no snapping. So run T1 as "50 k sources move by exactly one cell and the rest stay put", which is the same logical work.
   - Pin the thread to a full Zen 4 core: find it with `lscpu -e` (higher max MHz), then run it under `taskset`.
   - Report p50 and p99 over 600 frames.
   - Every benchmark consumes a checksum of its output, so no work can be optimised away.
2. **D1.** Start column chunks on multiples of 64 rows.
3. **D6.** `Sync`, `SyncColumns` and `SyncTransforms` reject non-finite positions, and `SyncTransforms` flags them in `Invalid`.
4. **D7.** `AddGrid` uses `ValidationRules.IsGridSize`, rejects non-finite origins, and requires `ScaleQ8 ∈ [1, 2²⁰]`.
5. **D4, interim.** Use a FIFO free ring. The full fix, 32-bit generations, comes in M6.
6. **D9.** An immediate `Fade` keeps `LayerLive` correct.
7. **D5.** Document the int32 limit in `docs/model.md` until the v2 guard replaces it.
8. **The branch.** Cherry-pick from `snapshot-bucket-index` the stats-profiler stale-handle fix and every test that holds for `main`'s semantics. Then close the branch as "measured, rejected".
9. **The issue.** Rewrite issue #40 and the README budget section (§1.3).

**Exit:** T1–T5 single-core numbers for `main` are posted on issue #40, and receipts are unchanged.

### M1 · The oracle (≈ 3–4 days, in the test project only)

1. **Reference interpreter**, about 250 lines and cache-free. For each cell, it sums every source's exact per-source contribution, using today's per-cell kernels: `BoxAt`, `TurnedDenseAt`, `TurnedSmoothAt`, raster sampling and `SmoothWeights`. It has two modes: `Current` (round-of-sum for smooth stamps) and `V2` (per-source rounding plus snapping).
2. **Differential harness.**
   - Generate seeded random command streams: `Place`, `Move`, `Turn`, `Scale`, `SetGain`, `Remove`, `Fade`, `Expire`, `Record`, `Rewind`, `Clear`.
   - Compare pages, and every read kind, against the interpreter.
   - On a mismatch, shrink the stream to a minimal failing sequence.
3. **Property laws:**
   - superposition when nothing saturates;
   - translation away from edges;
   - an idle `Process` is the identity;
   - commands on distinct sources commute;
   - `Rewind ∘ Record = id`;
   - excluding s equals the field of the snapshot without s.

**Exit:** the current engine matches the interpreter in `Current` mode on 10⁴ streams.

### M2 · Snap the current engine (≈ 1 week, released as 1.3): the proof

1. **The API.** Add `Layer.Snap` (default `Exact`, so nothing changes for existing users) and `turnSteps`.
2. **The snap.** Inside `TileBake.Footprint`, mask the floor of the Q8 position before adding the stamp origin. Quantise the angle before `Place`.
3. **The skip.** `Move`, `Turn`, `SyncColumns`, `SyncTransforms` and `ApplyBatch` compare the snapped keys for every grid. When none changed, they store the new floats and skip `EnqueueMutation`.
4. **Receipts.** Exact-mode receipts stay bit-identical. New Cell-mode receipts are checked against the interpreter.

**Exit:**

- T1 `Process` on `main` with Cell snap is ≤ 10 ms on one core, against 134 ms today (estimate: 3–8 ms).
- The measured crossing rate replaces the 5 % assumption in §5.

This milestone validates the plan's main lever with real numbers, before the rewrite.

### M3 · v2 storage and write path (≈ 2 weeks, branch `engine-v2`)

1. **Source store:**
   - hot record, deposited records and cold schedule columns;
   - changed list, deduplicated per tick;
   - FIFO free ring;
   - `long` ids internally.
2. **Image cache.** Unit images come from today's `TileBake` per-cell math at integer anchors. Add the shift-delta images for one-cell moves.
3. **Tiles.**
   - Tile directory, page slab and page headers.
   - `NativeBuffer` grows zeroed by default (`ResizeZeroed`); `GrowUninitialized` is the explicit exception.
4. **`Process`.** Diff, then fragments, then counting sort, then the apply kernel, then the page lifecycle and the touched list.
   - The apply kernel has a `Vector256` version, a `Vector128` version (`AdvSimd` on ARM) and a scalar version.
5. **Point read** on the new pages.

**Exit:**

- `Place`, `Move`, `Remove`, `SetGain`, `Turn`, `Scale` and point reads match the interpreter in `V2` mode.
- T1 `Process` is ≤ 0.5 ms.

### M4 · v2 reads (≈ 2 weeks, branch)

1. **Column point and gradient reads:** hoisted validation, the single-grid fast path, prefetch and 64-row chunks.
2. **The remaining reads:**
   - lazy page summaries and integer disk spans;
   - area, and exclude-self for point, gradient and area;
   - max and nearest;
   - `QueryRegion` and `Query(rect)`;
   - the `QueryMax` pyramid;
   - `Changed`, `ChangedMask` and `ChangedTiles`.
3. **Experiment: an int16 "published" mirror** of hot pages against direct int32 reads. Keep the mirror only if it improves T2 by ≥ 20 %.

**Exit:**

- Every read kind matches the interpreter.
- T1 reads take ≤ 0.35 ms, and T2 is ≤ 3 ms on one core.
- An area read at r = 64 takes ≤ 0.3 µs.
- On unsaturated pages, an exclude-self area read costs at most the plain area read + 0.1 µs.

### M5 · v2 derived layers, time, rewind, clear (≈ 1–2 weeks, branch)

1. **Pull-based derived layers:**
   - point reads evaluate the recipe directly;
   - derived pages are built lazily;
   - each recipe chain compiles to one fused kernel.
2. **Time.** The timing wheel replaces the heap; fades and expiry become Δgain fragments.
3. **Record and Rewind** use the slot journal. `Clear` resets the slab.

**Exit:**

- The full differential suite is green.
- T1 is ≤ 1.0 ms at p50 and ≤ 1.3 ms at p99.
- T4 is ≤ 1 µs.

### M6 · Cut over to 2.0 (≈ 1 week, one PR, so two engines never ship)

1. **Delete the old engine in the same PR:**
   - `ResolvePool` (every phase);
   - `DepositOp`, `Ops`, `Pending` and the journals;
   - `DepositFragment`, `PrepKey`, `PrepTask`, `RebuildItem` and `RebuildChunk`;
   - the crowd heuristic: `ScanCrowding`, `LayerLive` and `Crowded`;
   - `PageMap`;
   - `MaxPyramid`, replaced by the generic lazy pyramid;
   - the difference, dense, tent and bell planes, and `TileBake.Resolve`;
   - the `SourceBatch` planning and fold stages. A batch becomes a loop over the same column writes.
2. **Public API:**
   - `long` source ids;
   - `Layer.Snap` defaults to `Cell`;
   - `TransformRange.PositionStride` and `TransformRange.Keys`;
   - mutators that return a status.
3. **Docs:**
   - regenerate the receipts for v2;
   - write the axioms and the unsafe proofs into `docs/model.md`;
   - add the README price list and "The 1 ms frame" text;
   - list every semantic change in the CHANGELOG: snap default, per-source smooth rounding, the meaning of Changed, ids.
4. **Release.** Run `dotnet pack src/Gi`, set version 2.0.0-alpha.1, and review it. No tag moves.

**Exit:**

- CI on a pinned runner produces the T1–T5 numbers.
- A regression of more than 5 % fails the build.

### M7 · Optional deterministic multi-core (≈ 1 week)

1. Tile-partitioned apply.
2. Column reads chunked across workers.

**Exit:** T2 is ≤ 1.2 ms on 4 cores, with page hashes bit-identical to single-core.

## 7 · Verification and measurement

- **Correctness comes before timing** (AGENTS.md). A milestone lands only when all of these hold:
  - it matches the interpreter;
  - receipts are green;
  - warm `Place`, `Process` and every query allocate 0 B, with and without hardware intrinsics.
- **Commands.** These are the AGENTS.md commands, and every milestone runs all of them:
  ```sh
  dotnet build Gi.slnx -c Release -m:1
  dotnet test Gi.slnx -c Release --no-build
  dotnet run --project samples/world -c Release --no-build
  dotnet run --project benchmarks -c Release --no-build -- --verify
  DOTNET_EnableHWIntrinsic=0 dotnet run --project benchmarks -c Release --no-build -- --verify
  ```
- **Determinism.** Hash every page each frame. The hashes must match across these configurations:
  - x64 with AVX2;
  - x64 scalar (`DOTNET_EnableHWIntrinsic=0`);
  - ARM64, when a runner exists.
- **Reporting.** Each benchmark run records:
  - CPU model, core type (Zen 4 or Zen 4c) and clock;
  - p50 and p99;
  - ns per entity and ns per op;
  - the output checksum.

## 8 · Decisions

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D1 | Contract | T1 "typical" vs "everything every frame" | **T1**, with published prices for T2, T3 and T5 |
| D2 | Default snap in 2.0 | `Exact` or `Cell` | **`Cell`**; `Exact` stays available at its published price |
| D3 | Rounding for smooth stamps | Per-source (1 plane), or round-of-sum (two extra int64 planes per layer, exact parity with today) | **Per-source** |
| D4 | What Changed means | "Touched by a non-zero fragment", or exact bytes (needs a compare per tile) | **Touched** |
| D5 | Ids | `int` with a 7-bit generation, `long` with a 32-bit generation, or a struct | **`long`** |
| D6 | Accumulator width | int32 with a guard, or int64 | **int32 with a guard** (`CapacityReached`) |
| D7 | Threading | Keep `ResolvePool`, or single-threaded plus an opt-in executor | **Executor**; delete the pool |
| D8 | Frent | Inside `src/Gi`, as a sample adapter, or nothing | **Sample adapter only** |
| D9 | GPU | Now, later or never | **Not planned** |

## 9 · Risks

| Risk | Mitigation |
|---|---|
| Real games cross cells more often than 5 % per frame | M2 measures the real rate before the rewrite starts. Cost scales linearly with it, and coarser grids lower it. |
| Many distinct scales or angles thrash the image cache | Turn buckets bound the angles. A miss costs O(footprint) and is still exact. Cache size is tunable. |
| int32 pages double read traffic compared with int16 | M4 tests the published int16 mirror. |
| Some games need smooth sub-cell motion | `Quarter` and `Exact` snaps, at their published price |
| netstandard, Unity Mono and IL2CPP lack `Vector256` | Scalar fallbacks that are correct and allocate 0 B. Their numbers are published separately and not budgeted. |
| The 8500G mixes Zen 4 and Zen 4c cores | Pin the benchmarks; report the core type with every number |

## 10 · Do not

- Do not chase "worst case, any call pattern, 1 ms". No CPU design meets it.
- Do not merge `snapshot-bucket-index`, and do not rebuild tiles from source bins every frame.
- Do not add Frent, or any other package, to `src/Gi`.
- Do not ship two engines. v2 lands in one cutover PR (M6), and the interpreter lives in the tests.
- Do not add a sum pyramid to speed up `QueryRegion`. Copies are bound by their output; pyramids only help sums.
- Do not make the GPU, or lazy gather from bins, the primary path.

## 11 · Start here

1. **M0.1:** T1–T5 benchmarks on `main`, serial and pinned. These turn every ≈ in this file into a measured number.
2. **M0.2–M0.7:** defects D1, D4 (interim), D5 (documented), D6, D7 and D9. Each takes under a day.
3. **M1:** the interpreter and the differential harness.
4. **M2:** snap and skip in the current engine, which gives the first real number for the main lever.
5. **M3:** on branch `engine-v2`, the source store, image cache, slab and directory, and the apply kernel.

## Appendix · Sketches (comment-free, per AGENTS.md; not compiled)

### A · Applying a fragment to a page (§4.3, step 4)

```cs
using System.Runtime.Intrinsics;

internal static unsafe class FragmentKernel
{
    public static void Add(int* page, short* image, int pitch, int imageX, int imageY,
        int x0, int y0, int x1, int y1, int gain)
    {
        var scale = Vector256.Create(gain);
        for (var y = y0; y < y1; y++)
        {
            var target = page + (y << 5);
            var source = image + (y - y0 + imageY) * pitch + imageX - x0;
            var x = x0;
            for (; x + 8 <= x1; x += 8)
            {
                var row = Vector128.Load(source + x);
                var cells = Vector256.Create(Vector128.WidenLower(row), Vector128.WidenUpper(row));
                Vector256.Store(Vector256.Load(target + x) + cells * scale, target + x);
            }

            for (; x < x1; x++) target[x] += source[x] * gain;
        }
    }
}
```

The arithmetic wraps in int32. The page `Bound` guard (§4.1) is what makes the wrapped sum the true sum. Reads apply `Sat16` afterwards.

### B · The snapped key and the skip test (§4.6)

```cs
internal static class SnapKey
{
    public const int Exact = ~0;
    public const int Quarter = ~63;
    public const int Half = ~127;
    public const int Cell = ~255;

    public static long Of(float x, float y, float originX, float originY, int scaleQ8, int mask)
    {
        var qx = (int)MathF.Floor((x - originX) * scaleQ8) & mask;
        var qy = (int)MathF.Floor((y - originY) * scaleQ8) & mask;
        return ((long)qy << 32) | (uint)qx;
    }

    public static bool Moved(ref long hint, long key)
    {
        if (hint == key) return false;
        hint = key;
        return true;
    }
}
```

- **Range.** Positions are validated as finite and in range before `Of` runs, so the float-to-int cast never depends on the runtime's saturation rules.
- **Rounding.** Masking a two's-complement value floors it toward −∞, which matches today's `CellQ8` floor.
- **Fragment sort.** Fragments are bucketed with the two-pass counting sort from appendix B of the issue #40 plan comment, keyed by global tile id.
