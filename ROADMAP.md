# Gi roadmap: the 1 ms frame (issue #40)

This file coordinates the work. The owner gives one task card at a time to an AI agent. When the agent finishes, the owner pastes its report back to Claude. Claude reviews the report and answers **accept**, **fix** or **re-plan**, then names the next card.

- Numbers marked ≈ are predictions derived from the #40 measurements. Each card replaces its predictions with measurements.
- Last updated: 2026-10-16. Base: `main` @ d9d4329. Phase 0 lands on `phase0` (P0-1 through P0-9 minus P0-7 merged there), then one PR into `main`.
- Where this file and PLAN.md disagree, this file wins (§0, Appendix C).

## How the loop works

1. On the board (§3), pick the next `todo` card whose dependencies are `done`.
2. Start a fresh agent with the brief in Appendix A and the card ID. Each agent gets one card and opens one PR.
3. The agent ends with the report in Appendix B. Paste that report into the chat with Claude.
4. Claude checks the report against the card's "Done when" list and the prediction ledger (§2), then replies with one of:
   - **accept**: merge the PR, mark the card done, and move to the next card;
   - **fix**: Claude gives the exact list of changes;
   - **re-plan**: a number missed its prediction badly, so the roadmap is adjusted before any further work.
5. Update the board row: status, PR, and a one-line result.

Agents can work in parallel only on cards in different lanes whose dependencies allow it. Cards in the same lane touch the same files, so run them one at a time.

## 0 · Final decisions

### Locked

| # | Decision | Choice | Why |
|---|---|---|---|
| D1 | Contract | Two reference frames, C (crowd) and W (world), plus a published price list. Retired: the 100 ns/entity target and "worst case, any call pattern". | Lower bounds make the old clause unsatisfiable: `QueryRegion` is bound by its output size, an exact disk costs Θ(r), and cost grows with call count. Also, 100 ns × 1M = 100 ms per frame, which is 10 FPS. |
| D2 | The lever | Per-layer `Layer.Precision`. `PhaseBits` 0–8: 0 snaps to the cell, 8 is today's 1/256 cell. `TurnBits` 0–16: 16 is today's 65,536 angles. A source costs work only when its snapped key changes. | All three plans agree this lever closes most of the 300× gap. `TurnBits` is there so rotating vision-cone agents do not defeat snapping. |
| D3 | Field semantics | Exactly today's:<br>• box, raster, dense and round stamps are rounded per source;<br>• tent and bell sum in Q40 and are rounded once per cell;<br>• the total saturates once, after exact accumulation.<br>Snapping is the only new semantic, and it is opt-in. | Main stays a bit-exact oracle at every precision with one mask. PLAN.md's per-source smooth rounding is rejected: 1,000 weak tents worth 0.4 each at a cell would read 0 instead of 400. |
| D4 | `Changed` | A tile has changed when its published int16 bytes differ from those of the previous `Process`. | Exact, independent of which code path ran, and nearly free at publish. PLAN.md's "touched" definition is rejected. |
| D5 | Source ids | `World.SourceId(uint Slot, uint Generation)`. A slot is retired when its generation reaches `uint.MaxValue`. Free slots go in a FIFO ring. 2.0 removes int ids, with no compatibility wrappers. | Fixes R2 permanently. Wrappers would keep the 7-bit generation's ABA hazard alive. |
| D6 | Engine | One change-rate engine.<br>• Persistent layers keep hot columns, a deposited record per (slot, grid) and a changed list. Changes are emitted directly as ±kernel into the planes `I` (int32), `T` and `B` (int64 Q40). Dirty rectangles are then published to int16 pages with exact summaries.<br>• Transient layers (`Splat`) build a per-frame histogram and convolve it at `PhaseBits` 0, and use direct emission at other precisions. They share the persistent layers' publish and read paths. | Cost scales with changes. No per-frame rebuild from source bins: the branch measured that design at 424–484 ns per entity, against 315 on main. |
| D7 | Threads | Single-threaded. `ResolvePool` and `SerialOnly` are deleted. A deterministic tile executor is optional, after 2.0. | Removes R1, R8 and the static phase machine. |
| D8 | Memory | • A dense tile directory per (grid, layer).<br>• Fixed-size slabs with free lists for pages, `I`, `T`, `B`, histograms and summaries.<br>• Chunked SoA sources, 16,384 slots per chunk.<br>• Buffers zeroed by default; a per-world bump scratch.<br>• No per-tile malloc and no mutable statics. `StampCatalog` stays global because stamp data is immutable; the kernel cache is per world. | Fixes PageMap probes, tombstones, 26 KB fat tiles and global state. |
| D9 | Overflow | Exact, by promotion. Each tile keeps a magnitude bound per plane. When the bound could exceed the plane's width, the tile's `I` is promoted from int32 to int64, and `T` or `B` from int64 to Int128.<br>Main (1.x) gets an int64 total and documented plane limits. The interpreter (BigInteger) owns the overflow law. | Saturating partway through a sum is not exact. Promotion is exact, and it only costs anything in crush tiles. |
| D10 | Time | A sweep over a dense list of timed slots, costing O(timed). Deleted: the timer heap, `Due`, `LayerLive`, and the per-frame `LiveStale` recount (already deleted on main by P0-8). A timing wheel comes back only if a measured case needs it. | The sweep is the simplest correct form; 10k timed slots cost ≈ 10–20 µs. |
| D11 | Rewind | After `Record`, the first write to each slot journals that slot, deduplicated by record epoch. `Rewind` restores the journaled slots, shifts their schedules and marks them changed. | Costs O(changed slots), rather than O(chunks touched). |
| D12 | Reads | • Directory plus int16 pages; validation hoisted to once per call.<br>• Fused value and gradient; integer two-pointer disk spans.<br>• `Pyramid<T>` for Max and Epoch.<br>• Exclusion subtracts the source's own contribution from `I`, `T` and `B`.<br>• A derived layer is materialized over dirty rectangles if it was read last frame, and on demand otherwise. | Reads were 172 of the 315 ns. |
| D13 | Frent | Game side only; never inside `src/Gi`. An adapter sample is optional. | Frent has managed storage, virtual runners, locks and no spatial order. |
| D14 | `snapshot-bucket-index` | Not merged. Salvage three things: the profiler stale-handle fix, the inspection accounting, and the tests that hold for main. Keep the branch and label it rejected. | It is slower than main on every axis #40 cares about. |
| D15 | Verification | A chain of oracles: masked main, then the reference interpreter, then a committed golden corpus. v2 must match all three, and correctness receipts come before timing. | The v1 engine can be deleted at cutover because the corpus remains the oracle. |
| D16 | Branches | Phases 0–1 land on `main` as small PRs. Phases 2–5 land on `engine-v2`, one PR per card. 2.0 is one merge, so two engines never ship on main. | AGENTS.md's one-engine rule. |
| D17 | API shape | Every addition is nested inside the four public types: `Layer.Precision`, `Layer.Residency`, `World.SourceId`, `World.WriteStatus`, `World.ReadStatus`, `World.Splat…`, `World.FieldView` with `World.View`, and the `Grid` spatial-order helpers. | AGENTS.md's "One API" rule. Decision O1 confirms the last two. |

### Open: the owner decides (each has a default, so work can continue)

| # | Question | Default | Needed by |
|---|---|---|---|
| O1 | Are `World.FieldView` and the `Grid` spatial-order helpers allowed under "One API"? | Yes, as nested types. AGENTS.md gets one line saying so. | R-2, T-7 |
| O2 | What precision does a plain `Layer.New(world)` get in 2.0? | Exact, so no values change silently. The README tells crowd users to pass `Cell`. | C-3 |
| O3 | Typed `WorldId`, `GridId`, `LayerId` and `StampId` in 2.0? | No. Only `SourceId` changes; revisit in 2.1. | C-1 |
| O4 | A netstandard / Unity budget? | Correct and 0 B, with numbers published but not gated. | C-7 |
| O5 | Where do performance gates run? | A local `--gate` script on the 8500G, compared against a committed baseline. CI checks only correctness and allocations, because shared runners are too noisy for ±5 % gates. | C-4 |

## 1 · Contract and reference frames

Measurement conditions for every number in this file:

- **Hardware:** one full Zen 4 core of the Ryzen 5 8500G, pinned. Report Zen 4c numbers separately.
- **Runtime:** .NET 10 Release, hardware intrinsics on.
- **Run:** single thread, 0 B allocated per frame. Warm up first, then report p50 and p99 over 300 frames. Seeds are fixed, and the output checksum is consumed so no work can be optimized away.
- **Scalar build** (`DOTNET_EnableHWIntrinsic=0`): it must be correct and allocate 0 B. Its numbers are published, not gated.
- **Host note (2026-10-16).** All Phase 0 baselines, floor probes (Zen 4 `cpu0`, Zen 4c `cpu2`, and scalar), and P0-11 lever numbers have now been measured on the Ryzen 5 8500G (`taskset -c 0` for Zen 4, `taskset -c 2` for Zen 4c). Earlier i9-14900K numbers are kept in git history for reference.

| Frame | Definition | Target |
|---|---|---|
| **B1** legacy | The #40 canonical frame: 1M 8×8 box sources on a 4096² grid, all moving every frame. Each frame runs `Sync`, `Process`, `SenseColumn`, `SenseGradientColumn` and `ChangedMask`. Run it at Exact and at Cell, and report the crossing rate. | tracked, not gated |
| **B2** sparse | 1M sources placed, no reads. Two cases: 1 mover, and 200 movers. | ≤ 2 µs (1 mover); ≤ 20 µs (200 movers) |
| **B4** idle | 1M sources placed; nothing changes and nothing is read. | ≤ 1 µs |
| **C** crowd | • 1M agents on a transient layer, `PhaseBits` 0, 4×4 boxes, 1024² grid.<br>• Agents are submitted in spatial order and re-sorted every 128 frames; the re-sort cost is amortized into the number.<br>• Every agent reads its value and gradient, through `World.FieldView` once R-2 has landed and through the column API before that.<br>• 1,000 disk sums at r = 16. | ≤ max(1.0 ms, floor × 1.15) |
| **W** world | • 1M persistent sources on a 2048² grid: 250k agents moving 0.1 cell/frame, plus 750k static sources.<br>• Two base layers and one derived layer (Sum); `PhaseBits` 0; 4×4 boxes and 6×6 tents; slots allocated in spatial order.<br>• Reads: 250k point + gradient senses on the derived layer, plus 1,000 disk sums at r ≤ 32.<br>• Each frame: 1,000 spawns, 1,000 despawns, and 10,000 timed sources. | ≤ max(1.0 ms, floor × 1.15) |
| **C8 / W8** | C and W run at `PhaseBits` 8. | priced: ≈ 6–12 ms (C8), ≈ 3–6 ms (W8) |

**Floor and fallback target.** A frame's floor is the sum of its matching P0-2 probe floors. If a floor alone exceeds 0.87 ms, the README states floor × 1.15 as that frame's target instead of 1 ms.

Draft README text. It lands in C-3, once the numbers are measured:

> **The 1 ms frame.** On one desktop core, Gi runs a million influence sources in about a millisecond per frame. In that frame:
>
> - every source that crossed its layer's precision is re-deposited;
> - every agent senses its value and gradient;
> - a thousand AI queries sum disks;
> - spawns, despawns and fades happen at game rates.
>
> Two benchmark frames define "typical". Every other call has a published price. No price grows faster than the work asked for, and nothing left unchanged costs anything. Exact 1/256-cell motion remains available at its own price.

## 2 · Prediction ledger

These are the numbers measured today on main, single-threaded, from the #40 thread:

- **B1:** 315 ns/entity, split as `Sync` 7.5, `Process` 134 and reads 172.
- **B2:** 0.6 µs with 1 mover; 166 µs with 200 movers.
- **Memory:** RSS ≈ 299 MB after placing 1M sources.
- **Branch:** `snapshot-bucket-index` measured 424–484 ns/entity on B1.

Each row below is what I expect a card to report. The last column is the stop line: if a measurement crosses it, the agent stops and reports instead of continuing.

| After | Metric | Predicted | Stop and report if |
|---|---|---|---|
| P0-1 | W on main (Exact) | ≈ 80–130 ms | (baseline) |
| P0-1 | C on main (persistent emulation) | ≈ 150–250 ms | (baseline) |
| P0-2 | floor: quantize 1M | 0.05–0.1 ms | — |
| P0-2 | floor: histogram of 1M, coherent / random order | 0.3–0.5 / 1.5–3 ms | — |
| P0-2 | floor: convolve 1024², box / tent (int64) | 0.1–0.2 / 0.3–0.6 ms | — |
| P0-2 | floor: publish 1,024 tiles | 0.15–0.25 ms | — |
| P0-2 | floor: 1M value + gradient reads, coherent / random (32 MB field) | 0.2–0.4 / 3–6 ms | — |
| P0-2 | floor: disk sum, r = 16 / r = 32 | 50–70 / 100–130 ns | — |
| P0-2 | floor: anchor sweep over 1M | 0.2–0.4 ms | — |
| P0-10 | B1 at Cell (≈ 10 % crossing): `Process` / `Sync` | 15–30 / 6–10 ns | `Process` > 50 |
| P0-10 | W on main at Cell | ≈ 30–60 ms; reads dominate | > 80 ms |
| V2-1 | B1, single-threaded | unchanged, ±5 % | > 10 % slower |
| V2-2 | B1 reads | 172 → 115–135 ns | > 150 |
| V2-6 | B1 at Exact: `Process` | 134 → 40–80 ns | > 100 |
| V2-6 | B1 at Cell: `Process` + `Sync` | 4–10 ns | > 20 |
| V2-6 | B2 | ≤ 2 µs / ≤ 20 µs | > 10 µs / > 100 µs |
| V2-6 | RSS with 1M placed | ≈ 120–180 MB | > 300 MB |
| R-1 | B1 reads, random order | 172 → 12–25 ns | > 40 |
| R-8 | W | ≈ 1.2–3 ms | > 5 ms |
| T-3 | C, with `FieldView` reads | ≈ 0.9–1.8 ms | > 3 ms |
| C-4 | C and W | ≤ target | > 15 % above target → re-plan |

**Measured on Ryzen 5 8500G (pinned Zen 4 `cpu0`, single thread, p50; Zen 4c `cpu2` and scalar also recorded):**

- **P0-1 baselines (`Exact`):** B1 320.1 ns/entity (`sync` 10.7, `process` 133.2, `point` 37.5, `gradient` 106.5, `changed` 30.5; reproducing the 315 ns ±10% target); B2 1 mover 1.84 µs, 200 movers 280 µs; B4 50 ns; C 120.5 ms (`sync` 10.8, `process` 72.3, reads 37.0); W 63.1 ms (`sync` 2.7, `process` 48.0, reads 11.6); 0 B/frame across all frames. Cell-crossing rate: B1 62.53 % (0.5×0.25 step), C/W agents 12.6 %.
- **P0-2 floors (Zen 4 `cpu0` AVX2 / Zen 4c `cpu2` AVX2 / Zen 4 scalar):**
  - P1 quantize 1M: 0.20 ms / 0.28 ms / 4.03 ms
  - P2/P3 histogram 1M coherent (`interleaved2`): 0.47 ms / 0.60 ms / 0.41 ms; plain Morton: 0.56 ms / 0.81 ms / 0.60 ms; random: 0.69 ms / 0.99 ms / 0.60 ms
  - P4 convolve 1024² box: 0.14 ms / 0.20 ms / 0.84 ms; tent int64: 0.72 ms / 0.95 ms / 8.54 ms; bell int64: 0.94 ms / 1.26 ms / 11.75 ms
  - P5 publish 1,024 tiles: 0.44 ms / 0.54 ms / 3.01 ms
  - P6 1M value+gradient reads, 32 MB coherent / random: 4.62 / 23.13 ms (Zen 4), 6.07 / 25.43 ms (Zen 4c), 6.43 / 23.20 ms (scalar); 2 MB coherent / random: 2.94 / 4.25 ms (Zen 4)
  - P7 disk sums r=16 / r=32 / r=64: 107 / 222 / 535 ns (Zen 4), 148 / 322 / 758 ns (Zen 4c), 528 / 1,238 / 3,131 ns (scalar)
  - P8 anchor sweep 1M (10% moved): 0.18 ms / 0.32 ms / 0.87 ms
- **P0-8 effect:** 1M-source frames with 10k timers ≈ 0.8 ms faster per frame; plain B2 unchanged.
- **P0-10 + P0-11 (`Cell` vs `Exact` on Zen 4 `cpu0`):**
  - **C (1M movers @ 0.1 cell/frame, 12.61% crossing):** `Process` **72.32 → 13.47 ns/entity** (5.4× faster, beating the 15–30 ns prediction), `Sync` **10.83 → 7.37 ns/entity** (within 6–10 ns), total **120.5 → 52.7 ms** (reads now 60% of frame).
  - **W (250k movers @ 0.1 cell/frame, 12.60% crossing):** total **63.1 → 25.2 ms** (`Process` 48.0 → 12.6 ms, `Sync` 2.7 → 2.0 ms, reads 10.3 ms; beating the 30–60 ms prediction).
  - **B1 (0.5×0.25 step = 62.53% crossing):** 320.1 → 282.7 ns/entity (`Process` 133.2 → 95.0 ns, `Sync` 10.7 → 13.9 ns; at 62.53% crossing `2 × 625k ≥ 1M` still enters `RebuildLayer`).
  - **B2 (`Cell`):** 1 mover 1.84 → 1.41 µs, 200 movers 280 → 158 µs; **B4 (`Cell`):** 40 ns. All 0 B/frame.

Three claims from the earlier plans that these predictions reject:

- **"Reads get ≥ 3× faster from the directory alone."** I expect ≈ 1.3× from the directory. Reaching 3× needs R-1.
- **"Senses through a derived recipe cost 0.6–1.2 ns."** This only holds once derived tiles are materialized over dirty rectangles (R-8).
- **"Frame C's 1M reads take 0.2–0.4 ms."** This holds only with `FieldView` inside the game loop. Through the column API it is ≈ 1–1.5 ms.

## 3 · Status board

**Lanes.**
- **A**: benchmarks and admin.
- **B1, B2, B3**: fixes on main. Each lane is a chain of cards run in order; the three lanes can run in parallel.
- **C**: the oracle.
- **V**: the v2 core.
- **R**: reads.
- **T**: transient layers.
- **K**: contract and cutover.

**Sizes.** S is up to 1 agent-day, M is 2–4 days, L is 1–2 weeks.

**Status values.** `todo`, `doing`, `review`, `done`, `blocked`.

**Dependencies.** "Needs" lists the cards that must be merged first. "(early)" means the work can start before they are merged.

| ID | Card | Lane | Needs | Size | Status | PR | Result |
|---|---|---|---|---|---|---|---|
| P0-1 | Frame harness (B1, B2, B4, C, W) | A | — | M | done | phase0 | Ryzen 5 8500G Zen 4 (`cpu0`): B1 320.1 ns/entity, C 120.5 ms, W 63.1 ms, B2 1.84 µs / 280 µs, B4 50 ns; 0 B/frame; 12 frame receipts in `--verify` |
| P0-2 | Floor probes P1–P8 | A | — | S | done | phase0 | Ryzen 5 8500G Zen 4 (`cpu0`), Zen 4c (`cpu2`) and scalar measured: quantize 0.20 ms, P3 hist 0.47 ms, box conv 0.14 ms, tent 0.72 ms, publish 0.44 ms, 2 MB/32 MB coherent reads 2.94/4.62 ms, disks 107/222 ns, sweep 0.18 ms |
| P0-3 | R1 column-mask race | B1 | — | S | done | #41 → phase0 | chunks claimed upward on 1,024-row multiples; every mask word has one writer; race test red on old code, 30/30 green on new |
| P0-5 | R3 int64 total + limits doc | B1 | P0-3 | S | done | phase0 | int64 sum, 2^29 guard + whole-tile scalar fallback; Process +0.97 % (gate 3 %), checksums identical, 926 tests on phase0; limits pinned: box/dense 1,048,576/cell, tent/bell 4,096/peak (card's ~2,000 corrected) |
| P0-7 | R6 `Changed` = bytes differ | B1 | P0-5 | M | done | phase0 | per-cell published-byte compare inside all four Resolve/Combine paths (staged page so the guard fallback stays exact); epochs, masks, ChangedTiles and derived layers inherit the rule; 975 tests on phase0; B1 process unchanged (≈97 ns/entity); one receipt changed as the card allows |
| P0-4 | R2 FIFO free ring (interim) | B2 | — | S | done | #42 → phase0 | FIFO free ring on every free path; receipts unchanged except slot numbers; +≈ 0.5 ns per Place; ABA after 128 reuses documented |
| P0-8 | R7 `LayerLive` without recounts | B2 | P0-4 | S | done | phase0 | `LayerLive` exact on every change, `LiveStale` deleted; 1M-source frames with 10k timers ≈ 0.8 ms faster; 514 tests |
| P0-6 | R4/R5 finite + range validation | B3 | — | M | done | #43 → phase0 | non-finite writes rejected, reads treat NaN/∞ as outside, casts range-checked at ±2^30, `TryGrid` shares the rules; ≈ 1 ns/row cost |
| P0-9 | R8/R10 one-core pool, cleared outputs | B3 | P0-6, P0-3 | S | done | phase0 | pool refused on one processor; every column read writes outputs before early returns; 454 tests, receipts unchanged |
| P0-10 | Precision on main (oracle switch + skip) | B | P0-7, P0-8, P0-9 | M | done | phase0 | `Layer.Precision` (`PhaseBits` 0–8, `TurnBits` 0–16), `Layer.New(world, precision)`, `Layer.TryGetPrecision`, anchor/angle masks on all 5 `TileBake.Place` paths, snapped-key skip across all 6 write paths; 986 tests, all receipts green, packed as `1.3.0-alpha.1` |
| P0-11 | Measure the lever | A | P0-1, P0-10 | S | done | phase0 | Zen 4 (`cpu0`): at ~12.6% crossing (`C`), `Process` 72.3 → 13.5 ns/entity, `Sync` 10.8 → 7.4 ns/entity (total 120.5 → 52.7 ms); `W` 63.1 → 25.2 ms; `B1` (62.5% crossing) 320.1 → 282.7 ns/entity; `B2` 1.41 µs / 158 µs; `B4` 40 ns |
| P0-12 | Salvage + close `snapshot-bucket-index` | A | P0-1 | S | done | phase0 | profiler stale-handle fix + inspection accounting re-applied; 32 tests ported (958 total); branch closed at a53411d with the D14 note on #40; 3 findings carried to P0-14/P1-1 |
| P0-13 | #40 + README interim | A | P0-11 | S | done | phase0 | README interim reference-frame section + 8500G numbers; PLAN.md superseded header; ROADMAP board completed |
| P0-14 | Clean-up: CI inspect, doc/version echoes, carry-forward findings | A | — | S | done | phase0 | `jb inspectcode` 0 warnings; rejection outputs zeroed while preserving covered not-found `TrySenseNearest` position; exclusion fold widened to `int64` with non-uniform raster+box overflow test; non-finite `reach` rejected; ring + frame receipts in `--verify`; validation & stats READMEs updated; GC counter note in `docs/model.md` |
| P1-1 | Reference interpreter | C | P0-10 (early) | L | todo | | |
| P1-2 | Stream generator + differential + minimizer | C | P1-1 | M | todo | | |
| P1-3 | Golden corpus | C | P1-2 | M | todo | | |
| P1-4 | Laws | C | P1-2 | S | todo | | |
| — | **cut `engine-v2` from main** | — | P1-3 | — | todo | | |
| V2-1 | Delete `ResolvePool` | V | cut | M | todo | | |
| V2-2 | Directory, slabs, zeroed buffers, scratch | V | V2-1 | M | todo | | |
| V2-3 | Chunked sources + `SourceId` | V | V2-2 | L | todo | | |
| V2-4 | Kernel cache | V′ | V2-2 | M | todo | | |
| V2-5 | Planes, publish, summaries | V″ | V2-2 | M | todo | | |
| V2-6 | Change-rate `Process`, old pipeline deleted | V | V2-3, V2-4, V2-5 | L | todo | | |
| V2-7 | Turned/arc/raster/round on new path | V′ | V2-6 | M | todo | | |
| V2-8 | Overflow by promotion | V″ | V2-6 | M | todo | | |
| V2-9 | Schedule sweep | V | V2-6 | S | todo | | |
| V2-10 | One write pipeline | V | V2-9 | M | todo | | |
| V2-11 | Record / Rewind / Clear | V | V2-10 | M | todo | | |
| R-1 | Column reads | R | V2-6 | M | todo | | |
| R-2 | `World.FieldView` | R | R-1, O1 | S | todo | | |
| R-3 | Integer disk spans, row-sum areas | R′ | V2-6 | M | todo | | |
| R-4 | `Pyramid<T>` (Max, Epoch) | R″ | V2-6 | M | todo | | |
| R-5 | Max / Nearest pruning | R″ | R-4 | S | todo | | |
| R-6 | Exclusion from the planes | R′ | R-3 | M | todo | | |
| R-7 | Region copy, rectangle sums | R | R-1 | S | todo | | |
| R-8 | Derived layers, adaptive | R | R-1 | M | todo | | |
| T-1 | Transient layers + `Splat` | T | V2-11, R-1 | M | todo | | |
| T-2 | Histogram path at `PhaseBits` 0 | T | T-1 | S | todo | | |
| T-3 | Convolution ≡ direct ≡ oracle | T | T-2 | L | todo | | |
| T-4 | Per-tile cost model | T | T-3 | S | todo | | |
| T-5 | Transient at `PhaseBits` > 0 | T′ | T-1 | S | todo | | |
| T-6 | Transient self-exclusion | T | T-3, R-6 | M | todo | | |
| T-7 | Spatial-order helpers + sample | T′ | T-1, O1 | S | todo | | |
| C-1 | Statuses | K | T-6 | M | todo | | |
| C-2 | Validation caps | K | C-1 | S | todo | | |
| C-3 | Docs, README, CHANGELOG | K | C-2 | M | todo | | |
| C-4 | Gates + determinism matrix | K | C-3 | M | todo | | |
| C-5 | Fuzzer | C | P1-2 | S | todo | | |
| C-6 | Cutover: 2.0.0-alpha.1 | K | C-4, C-5 | S | todo | | |
| C-7 | netstandard / Unity numbers | K | C-6 | S | todo | | |

Lanes with primes (V′, V″, R′, R″, T′) can run in parallel with their unprimed lane once their "Needs" are done.

**Assign first, in parallel:** ~~P0-1, P0-2, P0-3, P0-4 and P0-6~~ done (2026-10-16, merged into `phase0`). **Next:** P0-7 (unblocks P0-10), then P0-10 + P0-11, and P0-14 any time.

## 4 · Cards

Every card also has to meet the common "done" bar in Appendix A: the five validation commands pass, warm paths allocate 0 B, receipts are green, and the report is filled in.

### Phase 0: truth (on `main`)

#### P0-1 · Frame harness
`main` · M · needs nothing
- Extend the existing budget harness in `benchmarks/`. Look for `--budget` on main and on `snapshot-bucket-index`, and port it if it exists only on the branch.
- Add the mode `--frames <B1|B2|B4|C|W|all> [--precision exact|cell]`. `cell` only works once P0-10 has landed.
- How each frame is measured:
  - single thread (`ResolvePool.SerialOnly = true` while the pool still exists);
  - warmup, then 300 measured frames;
  - report p50/p99, ns per entity, the stage split (Sync / Process / reads), allocated bytes per frame, the crossing rate, and a checksum of the output.
- Main has no transient layers. Run C as 1M persistent sources that are synced every frame, and say so in the output header.
- Output one JSON line per frame, culture-invariant, with keys in a stable order. Print the CPU model.
- Document pinning: find the Zen 4 cores with `lscpu -e=CPU,CORE,MAXMHZ` (they have the highest MAXMHZ), then run under `taskset -c <cpu>`.
- **Done when:**
  - B1 reproduces ≈ 315 ns/entity ±10 % on main, single-threaded;
  - W and C have first numbers;
  - every frame allocates 0 B.

#### P0-2 · Floor probes
`main` · S · needs nothing
- Add a `--floor` mode to the benchmarks project. It makes no Gi calls. It runs eight probes:
  - **P1** quantize 1M positions (Vector256);
  - **P2** snapped histogram read-modify-writes, in random order and in Morton order;
  - **P3** de-aliased histogram, two ways: two interleaved histograms, and coalescing runs that hit the same cell;
  - **P4** convolution over a 1024² grid: box (impulses plus a 2D prefix, int32), and separable tent and bell (int64);
  - **P5** publish epilogue over 1,024 tiles: int32 + 2 × RoundQ40 → saturate to int16 → compare → store, plus sum, max and row sums;
  - **P6** 1M value + gradient reads, random and coherent, over a 32 MB field and over a 2 MB field;
  - **P7** disk sums at r = 16 and r = 32, using integer spans plus row sums;
  - **P8** anchor-change sweep over 1M slots.
- For each probe, print ns/op, total ms and p50/p99, and consume a checksum.
- **Done when:** the report contains the floor table, measured once on Zen 4 and once on Zen 4c, with intrinsics both on and off.

#### P0-3 · R1: column-mask race
`main` · S · needs nothing
- **Bug.** `ResolvePool.ColumnShare` claims chunks downward from `count`. Chunk boundaries therefore fall at `count − 1024k`, so two workers can OR bits into the same `ulong` without atomics.
- **Fix.** Claim chunk indices upward instead: `k = Interlocked.Increment(ref cursor) − 1; lo = k × 1024; hi = min(lo + 1024, count)`.
- **Test.** `SenseColumn` and `ChangedMask` with counts of 8192 + {1, 37, 63, 65, 1000}, pooled versus serial, repeated 200 times. Masks and values must be identical.
- **Done when:** the test fails on the old code and passes on the new.

#### P0-4 · R2 (interim): FIFO free ring
`main` · S · needs nothing
- **Fix.** Replace the LIFO free list (`FreeHead` and the `Free` chain) with a FIFO ring, so a freed slot is the last to be reused.
- Generations stay 7-bit, because the int id has no room for more. Record the remaining hazard in `docs/model.md` (ABA after 128 reuses of one slot) and point to 2.0's `SourceId`.
- **Every path that frees slots:**
  - `Remove`;
  - expiry in `RunTimers`;
  - batch remove;
  - `Rewind`, which rebuilds the free list (rebuild it in ascending slot order);
  - `Clear`.
- **Receipts.** Receipts may change only in source ids and slot numbers. List each changed receipt with the reason. Any change to a field value is a bug.
- **Test.** 10,000 frames of kill-one/spawn-one with 1,000 free slots. A stale id must never address a live source within 128 × 1,000 reuses.

#### P0-5 · R3: int64 total before saturation
`main` · S · needs P0-3
- **Bug.** In `TileBake.Resolve` (scalar, Vector128 and AVX2 paths), the sum `boxes + dense + tent + bell` is computed in int32. It wraps before `Clamp` / `PackSignedSaturate`.
- **Fix.** Compute the sum in int64, or an exact widening equivalent, and saturate once.
- **Document** the 1.x plane limits in `docs/model.md`. 2.0 removes both (V2-8):
  - the int32 box/dense planes wrap beyond ≈ 1.06M max-amplitude sources stacked on one cell;
  - the int64 Q40 tent/bell planes wrap beyond ≈ 4,096 max-amplitude sources stacked at one peak in a tile (2^63 / (2048 × 2^40); an earlier draft said ~2,000, which was wrong).
- **Test.** A box term and a dense term that each fit in int32 but whose sum does not. The result must saturate with the correct sign.
- **Done when:** receipts are unchanged, and B1 Process is at most 3 % slower.

#### P0-6 · R4 + R5: finite and range validation
`main` · M · needs nothing
- **Writes.** `Sync`, `SyncColumns` and `SyncTransforms`, on both the pooled and the small paths, reject non-finite positions. `SyncTransforms` also sets the rejected row's `Invalid` bit.
- **Reads.** Every path that converts a world position treats a non-finite position as outside: `CellQ8`, `QueryAt`, `QueryGradient`, `Covers`, `TrySense*`, and the column reads.
- **Casts.** Range-check in float before every float→int cast, so .NET 8, .NET 9 and ARM64 agree. Clamp deposit footprints to |q| ≤ 2^30.
- **Grids.** `AddGrid`:
  - uses `ValidationRules.IsGridSize`;
  - rejects non-finite origins;
  - requires `ScaleQ8 ∈ [1, 2^20]`.

  `GiValidation.TryGrid` must check the same rules, so there is one source of truth.
- **Tests.** Every entry point, with NaN, ±∞, 3e9, and tiny and huge grid sizes.

#### P0-7 · R6: one meaning of `Changed`
`main` · M · needs P0-5
- **Rule.** On both deposit paths, a tile is changed if and only if its published int16 bytes differ from what they were before this `Process`. A removed tile is changed if and only if its old page had a non-zero cell.
- **Scope.** Epochs, `Changed`, `ChangedMask`, `ChangedTiles()` and `ChangedTiles(since)` all follow this rule, and so do derived layers.
- **Delta path.** Compare old and new values per cell inside `Resolve`, instead of copying the page.
- **Receipts.** Receipts that include changed outputs may change. List them with the reason "delta path no longer reports cancelled deposits".
- **Tests:**
  - +g then −g in the same frame → not changed;
  - move a source, then move it back in a later frame → changed both times;
  - the same scenario through the crowd path and the delta path gives identical results.

#### P0-8 · R7: `LayerLive` without recounts
`main` · S · needs P0-4
- **Fix.** Keep `LayerLive` exact on every change of gain or liveness: immediate `Fade`, fade steps, expiry, the batch fold, `Remove` and `Place`.
- Delete the per-frame `LiveStale` recount. Keep a single recount, only after `Rewind`.
- **Test.** Random streams; after every `Process`, `LayerLive` equals a brute-force count.
- **Expected effect.** W with 10k timers no longer pays an O(N) pass: ≈ 1 ms less per frame at 1M sources.

#### P0-9 · R8 + R10: one core, cleared outputs
`main` · S · needs P0-3, P0-6
- **R8.** `ResolvePool.TryAcquire` returns false when `Environment.ProcessorCount == 1`.
- **R10.** Every column read clears its masks and zeroes its values before any early return on an invalid world, layer or pointer. Return values keep their current meaning.
- **Tests** for both.

#### P0-10 · Precision on main: the oracle switch, plus skipping
`main` · M · needs P0-7, P0-8, P0-9 · **package change**: run `dotnet pack src/Gi` and propose a version.
- **API.** `Layer.Precision` is a readonly struct:
  - fields `PhaseBits` (0–8) and `TurnBits` (0–16);
  - statics `Exact` = (8, 16), `Half` = (1, 16), `Quarter` = (2, 16), `Cell` = (0, 16);
  - `TryCreate(phaseBits, turnBits, out precision)`.

  Also add `Layer.New(world, Layer.Precision)` and `Layer.TryGetPrecision`. Plain `Layer.New(world)` stays Exact.
- **Mask.** In `TileBake.Span`, mask the floor of the Q8 position before adding the stamp origin: `((int)MathF.Floor(position) & anchorMask)`. Mask the angle before calling `Place`.
- **Every `TileBake.Place` call site** must receive the layer's precision: `Deposit`, `BuildDeposits`, `ScatterDeposit`, `ScatterSource` and `ShapeOf`. `ShapeOf` is the exclusion path, and it is the one most easily missed. Derived layers have no precision.
- **Skip.** These write paths compare the snapped key of the old and the new state: `Move`, `Turn`, `Sync`, `SyncColumns`, `SyncTransforms` and `ApplyBatch`.
  - The key is computed per grid: anchor x, anchor y and the masked angle.
  - When no grid's key changed, and stamp, gain, scale, layer and liveness are also unchanged, store the new floats and do not enqueue a mutation.
- **Receipts.** Receipts at Exact must be byte-identical; this proves the mask is a no-op at (8, 16). Add new receipts for Cell mode.
- **Tests:**
  - a move smaller than the precision leaves pages and epochs byte-identical;
  - at Cell, exclusion equals the field without that source.

#### P0-11 · Measure the lever
`main` · S · needs P0-1, P0-10
- Run B1, B2, C and W at Exact and at Cell, pinned to a Zen 4 core. Put the table in the report; the owner posts it on #40.
- This is the first measured number for the plan's main lever. Compare it with §2.

#### P0-12 · Salvage and close `snapshot-bucket-index`
`main` · S · needs P0-1
- Bring over, by cherry-picking or re-applying:
  - the stats-profiler fix for stale source handles;
  - the inspection-accounting fix;
  - every test from the branch that holds under main's semantics.

  Bring no engine code.
- Leave the branch in place. Note on #40 that it was "measured, rejected — ROADMAP D14". Never move a published tag.

#### P0-13 · Issue #40 and README, interim
owner, or an agent with `gh` · S · needs P0-11
- **Issue #40.** Retitle it "1 ms frame at 1M entities on one core". Replace the goal in the body with §1, and keep the measurement history.
- **README.** Replace "The 100 ns budget" with today's measured numbers, plus one line pointing to the 2.0 contract. The final text lands in C-3.
- **PLAN.md.** Add a header line saying it is superseded by this roadmap's decisions, in particular D3, D4, D6, D10 and D11.

#### P0-14 · Clean-up and carry-forward findings
`main` · S · needs nothing
- The CI `inspect` job has been red since 8e01d9f (before Phase 0); make it green or explicitly silenced per file.
- `TrySenseMax` and `TrySenseNearest` echo the input x and y on rejection; the docs say outputs are zeroed (from P0-6).
- `src/Gi.Validation/README.md` still quotes an old package version; align it and decide whether `GiValidationVersion` bumps with Gi.
- Add one `--verify` receipt for the FIFO free ring's id-reuse guarantee (from P0-4); decide whether the `--frames verify` receipts also join `--verify` (from P0-1).
- `Sense.cs` `ExcludeCell`/`Absent` fold the box prefix in int32 and can disagree with the now-exact page value on overflowing tiles (from P0-5); fix or document.
- Investigate the steady ≈ 8,200 B/frame in the 1M-mover scenario that exists on phase0 both before and after P0-5 (suspect: steady-state growth in the crowded-rebuild or changed-list buffers); a warm frame must allocate 0 B.

### Phase 1: the oracle (tests on `main`)

#### P1-1 · Reference interpreter
`main`, test project only · L · needs P0-10, but can start earlier
- **What it is.** About 300 lines, with no caches and no tiles. For every grid cell and every live source of a base layer, it adds that source's exact contribution to the cell, with the layer's precision mask passed to `Place`.
- **Which Gi code it may call.** Only the per-cell functions: `TileBake.Place`, `BoxAt`, `TurnedDenseAt`, `TurnedSmoothAt`, `SmoothWeights`, and per-cell raster sampling. Add `InternalsVisibleTo` if needed.
- **Arithmetic.** Accumulate each plane in `BigInteger`. Round tent and bell once per cell, then saturate once.
- **Derived layers.** Re-implement `CombineCell` independently; it is small.
- **What it implements:**
  - `Place`, `Move`, `Turn`, `Scale`, `SetGain`, `Remove`, `Fade` and `Expire`, with gain and liveness as pure functions of the tick;
  - `Record`, `Rewind` and `Clear`;
  - `Changed`, meaning a tile's bytes differ from the previous frame;
  - exclusion by definition: the field of the snapshot without source s;
  - every read kind, computed from the dense field.
- **Done when:** it matches main on every receipt scenario.

#### P1-2 · Stream generator, differential harness, minimizer
`main` · M · needs P1-1
- **Generator.** Seeded command streams. They cover:
  - every stamp kind, including turned and arc stamps;
  - a range of gains and scales;
  - 1–3 grids, with base and derived layers;
  - all precisions;
  - timers, and `Record` / `Rewind` / `Clear`;
  - every read kind, with and without exclusion.

  Keep stack depth below the 1.x overflow limits from P0-5. Write the generator by hand; add no new packages.
- **Differential run.** Run each stream through Gi and through the interpreter. Compare pages after every `Process`, and compare every read result.
- **Minimizer.** On a mismatch, shrink the stream with delta debugging and print the result as a C# test.
- **Done when:**
  - 10,000 streams pass on main at Exact, Cell and Quarter;
  - a seeded bug (one rounding flipped) is caught and shrunk to fewer than 10 commands.

#### P1-3 · Golden corpus
`main` · M · needs P1-2
- **Contents.** About 300 streams × 60 frames per precision. After each frame, record a 64-bit hash of every published page and of each read-result array.
- **Hash.** FNV-1a or a hand-written xxHash64; no package.
- **Format.** Culture-invariant text in a stable order, committed with the tests. A test regenerates the corpus and compares it with the committed copy.
- Once main's engine is deleted, this corpus is the oracle.
- **Done when:** the output is identical across two runs, and across intrinsics on and off.

#### P1-4 · Laws
`main` · S · needs P1-2
- Property tests over generated streams:
  - superposition, for unsaturated box and dense planes;
  - translation, away from the grid edges;
  - an idle `Process` is the identity;
  - commands on distinct sources commute;
  - `Rewind ∘ Record = id`;
  - excluding s gives the field without s;
  - a move smaller than the precision changes no bytes.

#### Cut `engine-v2`
Once P1-3 is merged, run `git switch -c engine-v2 main`. Every card in Phases 2–5 is a PR into `engine-v2`. Until C-6, main receives only fixes.

### Phase 2: v2 core (on `engine-v2`)

#### V2-1 · Single thread
M · needs the cut
- **Delete:** every partial of `ResolvePool`, `SerialOnly`, every pooled branch, and the per-worker buffers. Keep the serial code paths.
- Remove `ProcessStages` if only the pool used it. Stage timing stays in the harness.
- **Done when:** B1 is within ±5 % of its P0-11 single-thread number.

#### V2-2 · Directory, slabs, zeroed buffers, scratch
M · needs V2-1
- **Directory.** Replace `PageMap` with a dense `int` directory per (grid, layer), where −1 means absent. Allocate it with the layer's first page.
- **Slab.** Tile blocks come from a slab of fixed-size blocks with a free list, replacing the per-tile `AlignedAlloc` and `FreeBlock`. Keep today's block layout for now.
- **Zeroing.** `NativeBuffer.Resize` zero-fills new memory. `ResizeUninitialized` is the named exception; audit every caller.
- **Scratch.** A per-world bump scratch, reset at the end of `Process`.
- **Proof.** Write the lifetime, aliasing, alignment and concurrency proof for the slab and the directory in `docs/model.md`.
- **Done when:**
  - B1 reads are ≤ 135 ns;
  - warm paths make no `NativeHeap` calls (counted by a test hook).

#### V2-3 · Sources: chunked SoA and `SourceId`
L · needs V2-2
- **Storage.** Hot columns live in 16,384-slot chunks; growing adds a chunk and never copies. Cold schedule columns are kept separately.
- **Public API.** `World.SourceId(uint Slot, uint Generation)` replaces int ids everywhere:
  - scalar calls;
  - column pointers, which become `SourceId*`;
  - batch commands and results;
  - `TransformRange.Sources`;
  - `TryGetSourceSpec`.
- **Generations.** 32-bit. A slot whose generation reaches `uint.MaxValue` is retired. Free slots go in a FIFO ring.
- **Migration.** Update tests, samples and benchmarks mechanically.
- **Corpus.** Pages and read results must stay identical; ids are not part of the page hashes.
- **Done when:** a churn test of 10^6 kill/spawn cycles on 10 slots never aliases an id.

#### V2-4 · Kernel cache
M · needs V2-2 · can run in parallel with V2-3 and V2-5
- **Key.** Per world: (stamp, sampling, grid, phaseX, phaseY, maskedAngle).
- **Contents.**
  - Integer taps for `I`, rounded per source, at gain 1.
  - Q40 taps for `T` and `B`.
  - The bounding box, the extent of each row, max |tap|, and a structure tag: Box, Separable, Spans or Dense.
- **How taps are built.** Call today's emitters at gain 1 into scratch: `EmitBox` (through its bands), `EmitRaster`, `EmitTurnedDense`, `EmitSmooth` and `EmitTurnedSmooth`.
- **Bounds.** The cache has a fixed size. On a miss, or for a kernel larger than 64×64, compute the taps on the fly into scratch; the result is still exact.
- **Test.** For every kind, width class, scale and angle below, `gain × taps` must equal today's emission into a tile cell for cell, in int32 and in wrapping int64:
  - every kind;
  - widths 1 and 2, plus other odd and even widths;
  - a sample of phases, and all 256 × 256 phases for boxes from 1×1 to 8×8;
  - scales {0.5, 1, 3.7};
  - a sample of angles.

#### V2-5 · Planes, publish, summaries
M · needs V2-2 · can run in parallel with V2-3 and V2-4
- **Tile planes,** allocated from slabs:
  - `I`, int32, always present on base layers;
  - `T` and `B`, int64, allocated on first use;
  - the int16 page;
  - a summary block: sum (int64), max with its first argmax, 32 row sums (int32), 32 row maxima, and the epoch.
- **`Publish(tile, rect)`,** for every cell in the rectangle:
  1. Compute `Sat16(I + RoundQ40(T) + RoundQ40(B))` at exact width.
  2. Compare the result with the old page, then store it.
  3. Update the summaries exactly. Rescan the tile for its max only when the old max cell decreased.
  4. Set the changed bit and the epoch.
- A rectangle covering ≥ 50 % of the tile is published as the whole tile.
- **Test.** For random `I`/`T`/`B` contents, `Publish` must equal today's `Resolve` on the equivalent planes, for every cell and every summary. Scalar and SIMD paths must be identical.

#### V2-6 · Change-rate `Process`
L · needs V2-3, V2-4, V2-5
- **Writes.** A write sets the slot's hot columns and appends the slot to the changed list, deduplicated by a per-slot tick stamp. Nothing else happens until `Process`.
- **`Process`:**
  1. For each changed slot on each grid, compute the target state (masked anchor, kernel, gain, liveness) and compare it with the deposited record.
  2. Where they differ, emit −old and +new taps directly into `I`/`T`/`B`, grow the tile's dirty rectangle, and overwrite the deposited record.
  3. Publish the dirty rectangles (V2-5).
  4. Run derived layers, with the current algorithm for now.
  5. Write epochs and `ChangedTiles()`.
- **Delete, in the same PR:**
  - the crowd heuristic: `ScanCrowding`, `Crowded`, `LayerLive` and `LiveStale`;
  - the rebuild machinery: `RebuildLayer`, `ScatterSource`, `MergeLayer`, `RebuildTile`, and their chunks, chains and arenas;
  - the op pipeline: `DepositOp`, `Ops`, `Pending`, `Fragments` and `Placements`;
  - the difference and dense planes, `TileBake.Resolve`, and the remaining users of `IntegrateBoxes` and `Quadrant`.
- Until R-6 lands, exclusion reads the deposited record and calls `Place`.
- **Done when:**
  - corpus, interpreter and receipts are green at Exact and at Cell;
  - B1 Exact `Process` ≤ 80 ns;
  - B1 Cell `Process` + `Sync` ≤ 10 ns;
  - B2 ≤ 2 µs / ≤ 20 µs;
  - RSS ≤ 180 MB.

#### V2-7 · Turned, arc, raster and round stamps
M · needs V2-6
- **Keys.** Turned and arc kernels are keyed by the masked angle. Rasters with mips work at any sampling. Full-arc round kinds do not depend on angle.
- **Exact precision with free angles** takes the miss path. Cap cache inserts per frame so the cache cannot thrash.
- **Done when:**
  - corpus and interpreter are green on streams biased toward these kinds;
  - the price per footprint cell is measured.

#### V2-8 · Overflow by promotion
M · needs V2-6 · can run in parallel with V2-7
- **Bound.** Each tile tracks, per plane, Σ |gain × maxTap| over its current deposits, using saturating int64. Persistent layers add on deposit and subtract on removal.
- **Promotion.**
  - When `I`'s bound would exceed 2^31 − 1, promote that tile's `I` to int64.
  - When the bound of `T` or `B` would exceed 2^62, promote that plane to Int128 (two longs on netstandard).
  - Demoting once the bound falls below half is optional.
- Publish and exclusion read either width.
- **Law tests,** checked against the interpreter:
  - stacks of 1, 2^12, 2^16, 2^20 and 2^24 max-amplitude sources of each kind on one cell saturate with the correct sign;
  - mixed-sign stacks that cancel give the exact small value.

#### V2-9 · Schedule sweep
S · needs V2-6
- **Data.** A dense list of timed slots, with swap-remove.
- **Each `Process`:**
  - effective gain = `FadeGain(tick)`;
  - alive = `tick < ExpireAt`;
  - any difference from the deposited state appends the slot to the changed list;
  - expired slots are freed in ascending slot order.
- **Delete:** the heap, `Alarm`, `Due`, `NextStep`, `NextDue`, `RunTimers`, `RebuildTimers`, `Push` and `Pop`.
- **Done when:**
  - the corpus is green, so fades step on exactly the same ticks;
  - W's timer stage costs ≤ 2 ns per timed slot.

#### V2-10 · One write pipeline
M · needs V2-9
- `ApplyBatch`, `SyncTransforms`, `Sync` and `SyncColumns` become loops over the same column writes the scalar mutators use.
- **Delete:** `SourceBatchEntry`, `SourceBatchLink`, `SourceBatchState`, and the planning, folding and lookup tables.
- **Keep exactly:** the semantics of `BatchStatus`, `SourceResult` and `SourceTarget.CreatedBy`, including ordered application within a batch.
- **Done when:**
  - batch tests and the corpus are green;
  - B1 `Sync` is ≤ 3 ns per row with slot-ordered input.

#### V2-11 · Record, Rewind, Clear
M · needs V2-10
- **`Record`** bumps a record epoch, costing O(1). The first write to a slot after that journals the slot's hot state and schedule state.
- **`Rewind`:**
  1. Restores the journaled slots.
  2. Applies today's `ShiftSchedules`.
  3. Rebuilds the FIFO ring deterministically.
  4. Marks the restored slots changed.
- **`Clear`:** sets directories to −1, reclaims slabs, and resets counts and journals. Generations keep counting.
- **Delete:** `Journal`, `ScheduleJournal`, `EnqueueRewind` and `RestoreSlot`.
- **Done when:** `Rewind ∘ Record = id` holds on generated streams, and the corpus is green.

### Phase 3: reads (on `engine-v2`)

#### R-1 · Column reads
M · needs V2-6
- **Validate once per call:** world, layer, pointers and lengths.
- **Grid choice.**
  - Pre-sort the grids at `AddGrid`, so picking a grid per element is a short loop.
  - Keep `Pick`'s preference order exactly: complete coverage first, then the higher `ScaleQ8`.
  - Add a fast path for worlds with a single grid.
- **Per element.**
  - Quantize 8 positions per step.
  - Prefetch the directory entry and the page line k = 8–16 elements ahead.
  - Fused gradient: when local x and y are both within 1..30, all four neighbours are read from one page.
- **API.** Add an overload of `SenseGradientColumn` that also writes values, in one pass. Masks are always overwritten; bits never accumulate across calls.
- **Done when:**
  - the corpus is green;
  - B1 reads are ≤ 25 ns in random order and ≤ 6 ns in coherent order.

#### R-2 · `World.FieldView` (needs O1)
S · needs R-1
- **API.** `World.View(world, grid, layer)` returns a `ref struct` over the directory and the pages.
  - The indexer is O(1) and copies nothing.
  - Outside the grid, or in an absent tile, it returns 0.
  - It has a `Gradient(x, y)` method.
- **Lifetime.** A view is valid until the next `Process`. Document this in `docs/model.md`.
- **Done when:**
  - view reads equal `Query` for every cell of the corpus frames;
  - a coherent cell read costs ≤ 1.5 ns.

#### R-3 · Disks: integer spans and row sums
M · needs V2-6
- **Spans.** Compute row spans with a two-pointer integer walk; no `Math.Sqrt`. Membership must equal today's `Span()`, including the centre-cell rule.
- **Sums:**
  - a tile entirely inside the disk adds its tile sum;
  - a tile row entirely inside adds its row sum;
  - partial segments use masked SIMD.
- **Test.** Exhaustive: radii 0–64 × 4,096 random sub-cell centres, with identical spans.
- **Done when:** an area sum costs ≤ 0.3 µs at r = 64 and ≤ 80 ns at r = 16.

#### R-4 · `Pyramid<T>` (Max, Epoch)
M · needs V2-6
- **Generalize `MaxPyramid`** with a struct combiner type parameter. Do not use static abstract members, because the library also targets netstandard.
- **Uses:**
  - the Max pyramid answers `QueryMax`;
  - the Epoch pyramid answers `Changed(disk)` in O(log T) and `ChangedTiles(since)` in O(output + log T).
- **Updates.** At publish, update along the dirty paths only.

#### R-5 · Max and Nearest
S · needs R-4
- **Max:** tile maxima prune tiles, row maxima prune rows, and `SegmentMax` finds the value. Tie-breaking is unchanged: higher value first, then lower y, then lower x.
- **Nearest:** the ring walk uses the pyramid to skip tiles whose max is below the threshold.
- **Done when:** both cost ≤ 0.4 µs at r = 64, with identical results.

#### R-6 · Exclusion from the planes
M · needs R-3
- **Excluded value at a cell:** `Sat16(I − ownI + RoundQ40(T − ownT) + RoundQ40(B − ownB))`. The source's own taps come from its deposited record and the kernel cache.
  - There is no special case for saturated cells, and no `Place` call per query.
- **Read kinds:**
  - point and gradient;
  - area, which touches only the cells where the disk and the footprint overlap;
  - max and nearest;
  - reads through derived recipes, which keep the `ExcludedValue` recursion.
- **Done when:**
  - the exclusion laws hold for every read kind;
  - an excluded area at r = 64 costs at most the plain area + 0.15 µs.

#### R-7 · Region copy and rectangle sums
S · needs R-1
- **`QueryRegion`** walks tile by tile: one directory load per tile, 16-cell SIMD copies, and zero-fill for absent tiles.
- **`Query(rect)`** uses tile sums and row sums.

#### R-8 · Derived layers, adaptive
M · needs R-1
- **When a derived tile is computed.**
  - If the derived layer was read during the previous frame, its tiles are recomputed at publish time, over the union of the inputs' dirty rectangles.
  - Otherwise a tile is computed on its first read, guarded by an epoch check.

  Results are identical either way.
- **Point reads** of a tile that has not been materialized evaluate the recipe pointwise.
- **Done when:**
  - W's derived stage costs ≤ 0.1 ms;
  - a derived layer that is never read costs 0.

### Phase 4: transient layers (on `engine-v2`)

#### T-1 · Transient layers and `Splat`
M · needs V2-11, R-1
- **Create:** `Layer.New(world, Layer.Residency.Transient, precision)`.
- **Write:**
  - `World.Splat(world, layer, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, byte stamp, sbyte gain)`, plus overloads that take a stamp and a gain per element;
  - a `World.SplatWriter` ref struct for adding splats inline from game loops.
- **Lifetime.** Splats accumulate between `Process` calls. `Process` publishes them, and the next frame starts empty.
- **Wrong use.** Persistent mutators on a transient layer return `WrongResidency`.
- **Unchanged:** reads, `Changed` (bytes compared with the previous frame) and derived recipes.

#### T-2 · Histogram path at `PhaseBits` 0
S · needs T-1
- **Histogram.** One read-modify-write per splat. It writes into per-tile int32 histogram pages, one set per stamp class, allocated sparsely from the slab.
- **De-aliasing.** Consecutive adds to the same cell must not serialize. Use either interleaved sub-histograms or run coalescing, whichever P0-2's P3 probe found faster.
- **Done when:** 1M splats cost ≤ 0.6 ms in coherent order.

#### T-3 · Convolution ≡ direct ≡ oracle
L · needs T-2
- **What it computes.** For each tile and stamp class, `I`/`T`/`B` from the histogram window. The halo equals the kernel radius, and neighbouring tiles are reached through the directory.
- **Forms:**
  - **Box:** the impulse form plus a 2D prefix sum;
  - **Separable:** tent and bell, rows first, then columns, in int64;
  - **Spans:** full-arc round kinds;
  - **Dense:** generic taps, at its published price.
- **Fusing.** Convolution may be fused with publish into an L1 scratch, unless T-6 needs the planes kept.
- **Properties.**
  - `Direct(tile) ≡ Convolve(tile) ≡ interpreter` on random tiles, for every kind.
  - Corpus streams that include transient layers are green.
- **Done when:** the first build runs frame C in ≤ 1.8 ms.

#### T-4 · Per-tile cost model
S · needs T-3
- **Rule.** Use direct emission when `changedFootprintCells × directCost ≤ classes × convolveCost(kernel)`, and convolution otherwise.
- **Constants** are integers taken from the P0-2 floors and fixed in source, so the choice is deterministic. Results are identical on either path.

#### T-5 · Transient at `PhaseBits` > 0
S · needs T-1
- Each splat is emitted directly; there is no histogram.
- Price: ≈ 3–8 ns per splat for 4×4–6×6 stamps.

#### T-6 · Transient self-exclusion
M · needs T-3, R-6
- **API.** `TrySense…` overloads for transient layers that take the caller's own `(x, y, stamp, gain)`.
- **Exactness.** The result must be exact, computed either from the kept planes or from recomputed taps × histogram.
- **Laws.** The same laws as R-6.

#### T-7 · Spatial-order helpers (needs O1)
S · needs T-1
- **API:**
  - `Grid.SpatialKeys(world, grid, xs, ys, Span<uint> keys)`: the key is the Morton code of the tile, then the Morton code of the cell;
  - `Grid.SortByKeys(keys, Span<int> permutation)`: LSD radix sort, 0 B when the caller supplies the scratch;
  - `Grid.Disorder(keys)`.
- **Sample.** A sample in `samples/` re-sorts a SoA every N frames.

### Phase 5: contract and cutover

#### C-1 · Statuses
M · needs T-6
- **Mutators** return `World.WriteStatus`: Applied, StaleSource, NonFinite, InvalidLayer, InvalidStamp, WrongResidency, or CapacityReached.
- **Column and batch calls** check lengths once and take a status mask, which they always overwrite.
- **Reads.** Column reads return `World.ReadStatus`: Complete, Partial, Outside, InvalidLayer, or LengthMismatch. Scalar reads keep returning `bool`.

#### C-2 · Validation caps
S · needs C-1
- **Caps:**
  - a stamp's footprint after scaling ≤ 512 × 512 cells;
  - reach ≤ 4,096 cells;
  - `ScaleQ8 ∈ [1, 2^20]`.
- `GiValidation` mirrors `ValidationRules`.

#### C-3 · Docs
M · needs C-2
- **`docs/model.md`:**
  - the axioms: D3's semantics, precision, residency, time and recipes;
  - every unsafe proof: lifetime, aliasing, alignment and concurrency.
- **README:** "The 1 ms frame" and the measured price list.
- **CHANGELOG:** the id change, precision, residency, the new meaning of `Changed`, and every removed API.
- **PLAN.md:** fold it into the docs, or delete it.

#### C-4 · Gates and determinism
M · needs C-3
- **Performance gate (O5).** `benchmarks --gate` compares B1, B2, B4, C, W and every price row against a committed baseline JSON, measured on the 8500G and pinned. It fails on a regression above 5 % or on any allocation.
- **Determinism matrix.** Corpus hashes must be identical on:
  - x64 with AVX-512;
  - x64 with AVX2 (`DOTNET_EnableAVX512F=0`);
  - scalar (`DOTNET_EnableHWIntrinsic=0`);
  - ARM64, on GitHub's `ubuntu-24.04-arm` runner.

#### C-5 · Fuzzer
S · needs P1-2; can run at any time after that
- `--fuzz <minutes>` runs long random streams against the interpreter. Each failure is minimized into a test.

#### C-6 · Cutover
S · needs C-4, C-5
- Merge `engine-v2` into `main` as version `2.0.0-alpha.1`.
- Run `dotnet pack src/Gi` and review the version.
- Tag only the new version; never move an existing tag.

#### C-7 · Other runtimes (O4)
S · needs C-6
- On the netstandard build, the corpus is green and warm paths allocate 0 B. Its numbers go in the README.

### Phase 6: later, not scheduled
- **X-1 · Deterministic tile executor.** Deposit, convolve and publish are partitioned by tile owner. Target: C8 and W8 ≤ 1.5 ms on the 8500G's 6 cores, with hashes identical to a single core.
- **X-2 · Frent adapter sample,** in `samples/` only: chunk spans feed `Sync` / `Splat`.
- **X-3 · Impulse planes for big footprints** (more than 16×16 per tile): difference or polynomial impulses, integrated at publish. Only if a measured case needs it.
- **X-4 · `TransformRange.Keys` skip hints.** Only if `Sync` dominates a real game's profile.
- **Not planned:** a GPU backend, lazy gather from bins, rebuilding from bins, and a sum pyramid for `QueryRegion`.

## 5 · Stop rules

An agent stops and reports, without working around the problem, when any of these happens:

- A corpus, interpreter or receipt mismatch it cannot explain. Report the minimized stream.
- A number worse than the stop column in §2. Profile it first (stage split, plus `perf` or `DisassemblyDiagnoser`), then report before continuing.
- The work would need any of these that the card does not list:
  - a public API change;
  - a new package;
  - a new top-level public type.
- Warm paths allocate. A card is never done while they do.
- The work would touch any of these:
  - published tags;
  - main's engine after P1-3, other than fixes;
  - the engine code on `snapshot-bucket-index`.

## 6 · Risks

| Risk | Signal | Response |
|---|---|---|
| Real games cross cells far more than 10 % of the time | the crossing rate measured in P0-11 | Cost is linear in the rate. Use a coarser grid, or a transient layer. |
| Many scales or angles thrash the kernel cache | the miss rate in V2-7 | `TurnBits`, plus a cap on inserts. The miss path stays exact. |
| Publish dominates W | the stage split in V2-6 | Merge dirty rectangles into larger ones, and tune the whole-tile threshold. |
| Senses through the derived layer dominate W | R-8 | Materialize the derived layer over dirty rectangles. |
| Scheduling noise between Zen 4 and Zen 4c cores | wide p99 spread | Always pin, and report the core type. |
| netstandard lacks Vector256, static abstract members and Int128 | C-7 | Struct combiners, a two-long Int128, and scalar paths. |

## Appendix A · Agent brief

Paste this together with the card. ROADMAP.md is a local file, so a cloud agent such as Devin cannot see it. For those agents, also paste the card itself plus §0, §2 and §5, or commit ROADMAP.md first.

```text
You are implementing card <ID> from ROADMAP.md in the Gi repository.
Read first: AGENTS.md (binding), then ROADMAP.md §0 (decisions), §2 (prediction ledger),
§5 (stop rules) and card <ID>. Do only this card.

Branch: cut <short-name> from <main | engine-v2>. Open one PR back into it.

Rules:
- Production code has no explanatory comments.
- The warm path stays unmanaged: no managed arrays, LINQ, delegates, boxing, reflection,
  locks, or exceptions on the place, process or query paths.
- Unsafe code you add or change needs its lifetime, aliasing, alignment and concurrency
  proof in docs/model.md.
- Any file you write or generate must be deterministic and culture-invariant.
- Add no new packages, and no public API beyond what the card lists.
- If you change public API or behaviour, run `dotnet pack src/Gi` and propose a version
  in your report.
- Correctness comes first: tests and receipts must be green before you time anything.

Validate. All five must pass:
  dotnet build Gi.slnx -c Release -m:1
  dotnet test Gi.slnx -c Release --no-build
  dotnet run --project samples/world -c Release --no-build
  dotnet run --project benchmarks -c Release --no-build -- --verify
  DOTNET_EnableHWIntrinsic=0 dotnet run --project benchmarks -c Release --no-build -- --verify
From P1-3 onward, the corpus and differential tests run as part of `dotnet test`.

Measure the frames the card names, pinned to a Zen 4 core (`taskset -c <cpu>`). Report
p50/p99, ns per entity, the stage split, and bytes per frame.

If a stop rule fires, stop and report. Do not work around it.
Finish with the report block from ROADMAP.md Appendix B, with every field filled in.
```

## Appendix B · Report

The agent fills this in; the owner pastes it to Claude.

```text
CARD: <ID> — <title>
PR: <url or branch@sha>    BASE: <main|engine-v2>@<sha>
STATUS: done | partial | blocked
CHANGED: <3–8 bullets: files or areas>
DELETED: <types or methods removed, or "none">
API: <public additions or removals, or "none">    VERSION: <proposed, or "n/a">
VALIDATION: build ✓/✗ · test <passed>/<total> · sample ✓/✗ · verify ✓/✗ · verify-scalar ✓/✗ · corpus ✓/✗/n.a.
RECEIPTS: unchanged | changed: <each receipt with its reason>
ALLOC: warm paths <B>/frame, intrinsics on / off
NUMBERS (pinned Zen 4, p50 / p99):
  <frame>: <before> → <after>    (ledger: <predicted>)
DEVIATIONS: <anything that went outside the card or the ledger>
QUESTIONS: <for the owner or for Claude>
```

## Appendix C · Where each decision came from

**Kept:**

| From | Items |
|---|---|
| The issue #40 plan comment | the reference interpreter, differential tests, the dense directory, 32-bit generations, integer disk spans, monoid pyramids, statuses, the measurement protocol |
| PLAN.md (main) | snapping, skipping unchanged keys, proving the lever on main first, the slab and directory, the FIFO ring, the interpreter plus a golden corpus, single-threading, no GPU, Frent as a sample, the list of unsafe proofs |
| The pasted "1 ms verdict" | precision as phase bits, the oracle theorem (checked below), two residencies, `I`/`T`/`B` with publish, `Changed` = bytes, `FieldView`, spatial order, the schedule sweep, frames C and W, the gate rule, R1–R10, the delete list |

**Dropped:**

| Dropped | Why |
|---|---|
| 100 ns per entity, worst case, any call pattern | unsatisfiable, and it amounts to 10 FPS |
| T1–T16, D2 and D8 from the issue comment | they are not present in the code |
| lazy resolve, gather, impulses for every stamp, ski rental | when every tile is read, lazy equals eager; and the branch measured rebuilding from bins as slower |
| per-source smooth rounding (PLAN.md D3) | it loses the mass of weak emitters and breaks the oracle |
| `Changed` = touched (PLAN.md D4) | it depends on the code path |
| int32-only accumulators read through Sat16 (PLAN.md) | it needs per-source rounding, and reads touch twice the bytes |
| the timing wheel | the sweep is simpler; the wheel comes back only if a measurement needs it |
| `TransformRange.Keys` skip hints | comparing against the internal anchor is always correct; hints are deferred to X-4 |
| per-chunk copy-on-write for Rewind | under netcode rollback it costs O(chunks); the per-slot journal costs O(changed) |
| "fix R3 by saturating" | not exact; promotion is |
| a per-world stamp registry | an API break with no measured gain |

**Theorem check: why masked main is the oracle at every precision.**

Mask the anchor to p phase bits. Then every source of one stamp, at one sampling, grid and angle, lands at the same sub-cell phase. Today's kernels depend only on `cell − anchor`:

- **box:** through its bands;
- **raster:** through `(gx − px) × step`;
- **smooth axes:** because `2^shift` divides `256 × anchorCell`;
- **turned kernels:** through the centre.

Each kernel is also linear in gain. Clipping to the grid only drops cells that are outside the grid.

So v2 at precision p equals main run with the same mask, applied in `TileBake.Span` together with the angle mask before `Place`, bit for bit.
