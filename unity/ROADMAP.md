# Gi for Unity Entities: roadmap

Started 2026-10-18. Day 0 is Unity's CoreCLR release, expected about 2026-11-14.

- `unity/plan.md` is the design: axioms, architecture and decisions. This file is the execution: the contract the design stands on, the ledger, the rules, the board and the cards.
- Where the two disagree, this file wins. Appendix A lists every change it makes to the plan.
- The file lives in `unity/` because the root `ROADMAP.md` belongs to issue #40, and a `Roadmap.md` beside it would collide on case-insensitive file systems.

## 0. Running the roadmap

### Roles

| Role | Does |
|---|---|
| Owner | Picks cards from the board, hands each one to an agent, merges PRs, runs Unity receipts, decides open questions. |
| Agent | Executes one card on its own branch, opens one PR, and reports in the Appendix C format. |
| Coordinator | Claude, working in this repository. Reviews each pasted report and answers **accept**, **fix** (with the exact changes) or **re-plan**; updates the board, the contract status and the ledger; names the next cards. |

### Loop

0. Once: the owner creates `unity/main` from `main` and commits `unity/plan.md` and this file to it, so agents can read both.
1. Pick cards whose **Needs** are `done` or `U-pending`. R-13 limits how many cards can be `U-pending` at once.
2. Give each agent the prompt in Appendix D. An agent without repository access also needs §0, §1, §4, its card and Appendix C pasted in.
3. The agent opens a PR into `unity/main` and ends with its report.
4. The owner pastes the report to the coordinator, who answers and updates this file.
5. The owner merges accepted PRs, runs `unity/validate.sh` for cards in `U-pending`, and pastes the summary block it prints. The coordinator moves each of those cards to `done` or `fix`.
6. At each milestone (§3):
   - no card is in `U-pending`;
   - the milestone's ledger rows are filled;
   - the coordinator writes the package `CHANGELOG.md`;
   - the owner merges `unity/main` into `main`.

### Receipt tiers

| Tier | Where it runs | What it runs |
|---|---|---|
| **D** | anywhere, including CI and cloud agents | `dotnet build Gi.slnx -c Release -m:1`, `dotnet test Gi.slnx -c Release --no-build`, `unity/lint.sh`, `unity/build-plugins.sh --check` |
| **U** | this host: the `unity` CLI (1.0.0-beta.13) with installed editors and an active Personal license — EditMode proven end to end, PlayMode is the same command, the `linux-il2cpp` module is installed for a8 (Appendix F). CI needs serial or floating licensing (O-U3) | `unity/validate.sh editmode`, `playmode`, `il2cpp` or `perf`; the script maps the CLI exits: 0 pass, 8 tests failed (terminal), anything else infrastructure (retry once) |
| **G** | anywhere | the full `AGENTS.md` validation for `src/Gi` changes: `--verify` with and without intrinsics, plus the pinned `--gate` when a timing can move |

Every **Done when** item carries its tier.
- D items are accepted on the agent's report.
- U items are accepted on a `validate.sh` summary.

### States

`todo` → `active` → `review` → `fix` (repeatable) → `U-pending` → `done`

| State | Meaning |
|---|---|
| `U-pending` | D receipts accepted and merged; U receipts still outstanding |
| `blocked` | waiting on something outside the board; the reason is given |
| `cut` | moved past its milestone by the cut line (§3) |

## 1. Contract

The design rests on facts about Gi and Entities that nobody checked while planning, because the plan was written from pasted sources only.
- A named test pins each fact below before any code relies on it.
- If a fact turns out false, its card stops (R-14). The coordinator changes the design, and the test stays as the pin.
- The coordinator fills in **Status**:
  - `confirmed`: the fact holds as written;
  - `refined`: it holds in a different form, recorded with the measured behaviour;
  - `refuted`: it is false.

### Gi facts

F-1 pins these in `GiContractTests` (Tier D).

| # | Fact | Relied on by | Status |
|---|---|---|---|
| C1 | When `Place` reports `Applied`, it never returns `default(SourceId)`. | `GiSource.Id == default` meaning "not placed" (U-2) | confirmed (#78) |
| C2 | What `TransformRange.Invalid` means (an input mask of entries to skip, or an output listing the entries Gi rejected) and its bit layout. | sync fault scatter (U-3) | confirmed (#78) |
| C3 | The largest number of commands one `ApplyBatch` call accepts, and what `TooManyCommands` reports. | batch splitting (U-2) | confirmed (#78) |
| C4 | Field values do not depend on any of these:<br>• command order within a batch<br>• source creation order<br>• splat order<br>• how one tick's commands are split across batches | plan §5.8 | confirmed (#78) |
| C5 | The single-stamp `Splat` overload gives the same result, bit for bit, as the per-entry overload called with one stamp and one gain for every entry. | uniform fast path (U-6) | confirmed (#78) |
| C6 | `Process` advances `Tick` by exactly 1, and nothing else changes it. | tick equality (U-4) | confirmed (#78) |
| C7 | Exactly which calls bump `Revision`, and that a token captured before a bump gets `StaleRevision`. | token recapture (plan §5.10) | confirmed (#78) |
| C8 | Pure reads called from 8 threads with no writer running return the same bits as serial calls. The pure reads are:<br>• `Sense*Column` with null excludes<br>• `ChangedMask` and `ChangedTiles`<br>• `Query*` and `Covers`<br>• `TrySense*` without exclusion | parallel bindings (U-5) | confirmed (#78) |
| C9 | Exclusion reads give the same results whatever state the kernel cache is in: cold, warm, or with this frame's inserts used up. | exclusion bindings (U-5b) | confirmed (#78) |
| C10 | What exclusion does on a derived layer: subtracts the source with the recipe's weights, does nothing, or returns `InvalidLayer`. | binding validation (A-1, U-5b) | confirmed (#78) |
| C11 | In the `whole` and `found` outputs, entry *i* is bit `i & 63` of word `i >> 6`. Also, what happens to the bits past `count` in the last word. | bit scatter (U-5) | confirmed (#78) |
| C12 | When nothing is in reach, `SenseNearestColumn` writes `short.MinValue` and clears the found bit. Invalid inputs zero the outputs. | U-5b | confirmed (#78) |
| C13 | For `ChangedTiles(world, grid, layer, since, …)`:<br>• which tiles it lists (expected: those changed after tick `since`)<br>• whether each tile appears only once<br>• how a tile is encoded<br>• the largest count a caller must make room for | GPU mirror (P-1) | confirmed (#78) |
| C14 | `QueryRegion` writes `width × height` values in row-major order. Also, what it writes for cells outside the grid. | mirror, hashes | confirmed (#78) |
| C15 | A grid added after sources were placed does not contain them. The adapter never does this; the pin records why D-U9 exists. | D-U9 | confirmed (#78) |
| C16 | `Stamp.Free` returns false while any world uses the stamp, and true once every world that used it is freed. | stamp registry (U-1) | refined (#78: a pending snapshot refuses only when its recorded state names the stamp; a fresh-slot place+remove before Process does not refuse) |
| C17 | `Place`, `Fade(CreatedBy)` and `Expire(CreatedBy)` sent in one batch fade the source as asked, and remove it after exactly `Ticks` calls to `Process`. | pulses (U-2) | confirmed (#78) |
| C18 | Whether world ids and `SourceId`s are reused after `World.Free` and `World.New`. | rebuild resets every `GiSource` (U-1) | confirmed (#78) |
| C19 | None of the Gi calls the adapter makes on the warm path allocates managed memory. Measure: zero `GC.GetAllocatedBytesForCurrentThread` delta over 1000 scenario ticks. | axiom 8 | confirmed (#78) |
| C20 | Gi throws only at construction, never on the warm path:<br>• `World.New` throws when 255 worlds exist.<br>• `Grid.New`, `Layer.New` and the `Stamp` factories throw on full catalogs or bad arguments.<br>• No warm-path call throws for any input the adapter can produce. | bootstrap boundary (U-1) | confirmed (#78) |
| C21 | Gi's heading convention: which way a cone points at heading 0, and which way positive headings turn. | `GiPlane` (U-1) | confirmed (#78) |

### Entities facts

The card named in each row pins its fact (Tier U).

| # | Fact | Pinned by | Status |
|---|---|---|---|
| E1 | Registering a component type in `OnCreate` (through a type handle, lookup or query) makes `state.Dependency` include earlier jobs on that type. A writer waits for earlier readers and writers; a reader waits for earlier writers. | U-4 | |
| E2 | `WithDisabled<T>` matches entities whose `T` is present but disabled; `WithAbsent<T>` matches only entities without `T`; `WithNone<T>` matches both. | U-2 | confirmed (U-2). `WithAll<T>` matches only entities where `T` is enabled; `WithNone<T>` matches the disabled and the absent. Queries on a world also match the `GiFrame` entity, so scope them with another component |
| E3 | Destroying an entity that has an `ICleanupComponentData` leaves it alive with only its cleanup components until they are removed. | U-2 | confirmed (U-2) |
| E4 | `CalculateBaseEntityIndexArrayAsync` counts only entities that pass the query's enabled bits. Also, how change filters affect it. | U-2 | confirmed (U-2) for enabled bits: the base of chunk `c` is the number of enabled matches in earlier chunks. Change filters are not exercised: reconcile counts per chunk itself, so U-3 and U-5 pin the filter case when they use it |
| E5 | Change filters work per chunk: a chunk counts as changed when any system took write access to the type since this system last ran. | U-3 | confirmed (U-3): a main-thread write marks its whole chunk changed at the current system version, so "changed since" only means something once a system has advanced the version; chunks of other archetypes are unaffected |
| E6 | Whether `FixedStepSimulationSystemGroup` updates in the Editor world. | U-4 | |
| E7 | An entity baked without `TransformUsageFlags.Dynamic` has `LocalToWorld` but no `LocalTransform`. | U-2 | open: needs a baker, so A-2 pins it. The reconcile view already falls back to `LocalToWorld` when a chunk has no `LocalTransform` |
| E8 | `FixedStepSimulationSystemGroup` runs before `TransformSystemGroup`, so `LocalToWorld` read during a fixed tick is from the previous frame. | U-3 | confirmed (U-3) in a world with `ParentSystem` and `LocalToWorldSystem`: a parented emitter reaches Gi one frame after its parent moved |
| E9 | A non-Burst job scheduled from a non-Burst `ISystem.OnUpdate` runs on a worker thread under IL2CPP and CoreCLR (Mono no longer exists in Unity 7). | U-1 | confirmed (U-1): a managed `IJob` runs on a worker while the main thread polls `IsCompleted`. Scheduled and then `Complete()`d at once, the main thread runs it inline, so a test that needs a worker must poll. Same on CoreCLR editor, CoreCLR player and IL2CPP player |
| E10 | Gi's `SourceCommand` factories compile under Burst. If they do not, the diff job writes an adapter struct and the apply job converts it. | U-2 | not needed (U-2): Burst writes the adapter `GiCommand`, and the managed apply pass converts to `SourceCommand`, because slicing a batch must re-index `CreatedBy` anyway |

## 2. Prediction ledger

- Rows L1–L8 come from plan §12; L9–L11 are new.
- **Measured** is the median from the method the card states.
- A miss is reported, not explained away.

| # | Quantity | Prediction | Basis | Card | Measured |
|---|---|---|---|---|---|
| L1 | CoreCLR: Gi time for frame C on one core, against `dotnet` on the same host | ≤ 1.15× | same RyuJIT; Unity's tiering and GC settings unknown | V-0, V-3 | |
| L2 | CoreCLR: frame C wall time with sense on 8 workers, i9 | 3.6–4.6 ms | serial writes about 3.2 ms; sense 3.3 ms ÷ 8; plus ECS passes | V-3 | |
| L3 | ECS share of frame C: projection, gather, scatter | ≤ 12% of Gi time | Burst moves about 16 MB per tick | V-3 | |
| L4 | Mono: Gi time for frame C, against `dotnet` running the `net` build | 4–10× slower | Mono's JIT running the scalar build | V-3 | |
| L5 | IL2CPP: Gi time for frame C, against `dotnet` running the `net` build | 1.5–4× slower | AOT C++ of the scalar build | V-3 | |
| L6 | Managed allocation per steady tick, on every backend | 0 B | axiom 8 | V-2 | |
| L7 | Reconcile at 1% churn of 1M sources: 10k places and 10k removes | ≤ 1.5 ms | per-command cost of `ApplyBatch` | V-3 | |
| L8 | Mirror upload of 512 changed tiles | ≤ 0.5 ms on the main thread | about 1 MB uploaded | P-1 | |
| L9 | Tier D suite for the Unity projects | ≤ 60 s wall time | agents run it on every change | F-2 | 5.4 s warm (lint, two builds, 35 tests, plugin provenance check; i9-14900K, 2026-10-18) |
| L10 | Profile edit in play mode with 1M sources: free, rebuild, re-place | ≤ 250 ms | must stay interactive | V-3 | |
| L11 | Idle cost: a profile and no entities | ≤ 20 µs per tick | seven systems, each a few µs to schedule | V-3 | |

## 3. Milestones and cut line

| Milestone | Target | Exit |
|---|---|---|
| **M1** Field in ECS | 2026-11-05 | On Mono and IL2CPP, these cards are `done`: F-1, F-2, U-0 to U-6, A-1, A-2, V-1, V-2. L6 is filled. |
| **M2** See the field | 2026-11-09 | P-1, E-1, E-2, E-3, E-4, E-6 and U-5b are `done`. |
| **M3** Day 0 | about 2026-11-14 | V-0, V-3, G-0 and D-1 are `done`. L1–L11 are filled. The owner has reviewed the package version. |
| **M4** Deep tools | after day 0 | E-5, E-7, N-1, N-2 and S-1, plus G-4 to G-8 as needed. |

| Priority | Meaning |
|---|---|
| **P0** | blocks M1 or M3 |
| **P1** | belongs to M2 |
| **P2** | belongs to M4 |
| **P3** | runs only when a measurement calls for it |

Cut line:
- When a week ends behind schedule, the lowest-priority cards that are not yet `active` move to the next milestone, in board order. They are marked `cut`.
- P0 cards never move past M3.
- When P0 is at risk, the coordinator proposes a scope change and the owner decides. An example change: IL2CPP receipts move from M1 to M3.

## 4. Rules for every card

### Branches and files

- **R-1** Branch names and PR targets:
  - Unity cards branch `unity/<card>-<slug>` from `unity/main` and open their PR into `unity/main`.
  - G-cards branch `gi/<card>-<slug>` from `main` and target `main`.
  - Agents never merge, never push to `unity/main` or `main`, and never create or move a tag.
- **R-2** Edit only the files the card lists under **Owns**, and create files only inside the folders it lists. If another file has to change, say so under Deviations in the report; do not change it.
- **R-3** Each shared file has one owner:

  | File | Owner |
  |---|---|
  | `Gi.slnx` | F-1 |
  | `package.json`, every `.asmdef`, the plugin `.meta` files | U-0 (V-0 swaps the define constraint) |
  | `.github/workflows/unity.yml` | F-2 |
  | `docs/model.md` § Unity boundary | U-0 creates one heading per card; each card replaces only the "Pending." line under its own heading |
  | package `CHANGELOG.md` | the coordinator, at milestones |
  | package `README.md`, `Documentation~/`, `Samples~/` | D-1 |

### Code

- **R-4** The `AGENTS.md` code rules apply to everything under `unity/`:
  - no explanatory comments, XML doc comments included;
  - written files are deterministic and culture-independent;
  - the warm path is unmanaged;
  - a steady tick allocates 0 B.
- **R-5** Names inside `namespace Gi.Entities`:
  - Gi's handles are used only through the aliases `GiWorld`, `GiGrid`, `GiLayer` and `GiStamp`.
  - Unity's world is always written `Unity.Entities.World`.
  - Adapter types carry the `Gi` prefix.
- **R-6** `Runtime/Columns/` and `Tests/Support/` are Unity-free:
  - no `Unity` namespace;
  - C# 9;
  - netstandard2.1 APIs only.
  
  The two dotnet projects from F-1 compile these folders, so Tier D enforces this.
- **R-7** Functions that Burst calls take only pointers, counts and unmanaged structs. Gi is called only from managed jobs and from the main-thread bootstrap and inspector code (D-U3).
- **R-8** A warm `OnUpdate` never calls `GetSingleton<GiFrame>`, because that call completes jobs. Instead it takes the singleton entity and reads `GiFrame` inside its jobs. Bootstrap and the inspector system are the only exceptions.
- **R-9** Every system that touches Gi does three things:
  - registers `GiFrame` in `OnCreate`, with the access its jobs need;
  - schedules every job through `state.Dependency`;
  - wraps its Gi jobs with the fence API from U-1.
- **R-10** A card that adds unsafe code fills its heading under `docs/model.md` § Unity boundary. The proof covers lifetime, aliasing, alignment and concurrency, using plan Appendix C as the template.
- **R-11** Each system wraps every Gi call in a `ProfilerMarker` and updates its own counters in `GiCounters` (U-1).

### Receipts

- **R-12** Every Done-when item names its test or command and its tier. Receipts come before numbers. No test is skipped, muted or made inconclusive to get a pass.
- **R-13** At most three cards can be in `U-pending` at once. A card cannot be `done` while a card it needs is still `U-pending`.
- **R-14** A refuted contract row (C or E) stops the card. Report it with the failing test; the coordinator re-plans.

### Stop rules

Plan §12's S1–S5 apply, plus:
- **S6** A card that misses the same Done-when item in two reviews goes back to the coordinator for a re-plan, not a third attempt.
- **S7** While L6 is above 0 B on any backend, no new feature card starts in the lane that caused it.

## 5. Board

| Card | Title | Lane | Pri | Needs | Tier | Wave | State | PR |
|---|---|---|---|---|---|---|---|---|
| F-1 | Contract pins and dotnet harness | foundation | P0 | — | D | 1 | done | #78 |
| U-0 | Package skeleton, plugins, host project | runtime | P0 | — | D, U | 1 | done | #79 (D) and the Tier U unblock PR (U): editor 7000.0.0a9 + BovineLabs Core pre.6 at GitLab 82be2794. `validate.sh` editmode, playmode, coreclr (player) and il2cpp (player) each pass the smoke test. See A.15 and Appendix F |
| G-0a | TFM dry run | core | P0 | — | G | 1 | done | scratch, not merged |
| G-1 | Pure exclusion reads | core | P1 | — | G | 1 | done | #81. Exclusion-row gate deltas (+8–16%) are the accepted price of pure reads; baseline untouched, quiet-host re-run pending |
| F-2 | Scenarios, reference executor, lint, CI | foundation | P0 | F-1, U-0 | D | 2 | done | stacked on #86 |
| U-1 | Frame, bootstrap, group, process | runtime | P0 | U-0, F-1 | D, U | 2 | done | stacked after #88 |
| A-1 | Assets and inspectors | authoring | P0 | U-0 | U | 2 | todo | |
| V-1 | Gi parity on Unity backends | verification | P0 | U-0, F-1 | D, U | 2 | active | #96 carries the Tier D receipts (corpus DLL deterministic, dotnet tests unchanged); the Unity replay waits out the owner's pause of build runs. Gi.Corpus needs InternalsVisibleTo (see #96) — a public live-count read-out would remove it |
| G-3 | Tooling read-outs | core | P1 | — | G | 2 | done | #82. `World.Memory(world, out storeBytes, out liveTiles, out pages, out kernelEntries, out liveSources)`; E-1/E-4/E-6 consume it |
| U-2 | Reconcile, emitters, pulses | runtime | P0 | U-1, F-2 | D, U | 3 | done | stacked after #89 |
| U-4 | Fences, guard, cross-world writers | runtime | P0 | U-1 | U | 3 | todo | |
| U-6 | Splat | runtime | P0 | U-1, F-2 | D, U | 3 | done | stacked after #91 |
| A-2 | Bakers, builders, live baking | authoring | P0 | A-1, U-1 | U | 3 | todo | |
| P-1 | GPU mirror and heatmap | presentation | P1 | U-1, F-2 | D, U | 3 | todo | |
| E-3 | Stamp Studio | editor | P1 | A-1, U-1 | U | 3 | todo | |
| E-4 | Profile editor | editor | P1 | A-1 | U | 3 | todo | |
| E-6 | Profiler module and ConfigVars | editor | P1 | U-1 | U | 3 | todo | |
| G-2 | Runtime teardown | core | P1 | — | G | 3 | done | #84. `World.Teardown()`; revision floor keeps pre-teardown tokens refused; RSS-flat over 1000 cycles |
| G-7 | Incremental layer hash | core | P1 | — | G | 3 | todo | |
| U-3 | Sync | runtime | P0 | U-2 | D, U | 4 | done | stacked after #90 |
| U-5 | Read bindings: value and gradient | runtime | P0 | U-2 | D, U | 4 | done | stacked after #93. CoreCLR and IL2CPP players 49/49 at c6723dc; editmode and a fresh PlayMode editor run deferred by the owner |
| V-2 | ECS determinism and allocation | verification | P0 | U-3, U-4, U-5, U-6 | U | 5 | todo | |
| U-5b | Read bindings: area, max, nearest, changed | runtime | P1 | U-3, U-5 | D, U | 5 | active | #94 carries the Columns half (Tier D receipts); the Unity half and Tier U receipts wait out the owner's pause of build runs |
| E-1 | Field window | editor | P1 | P-1 | U | 5 | todo | |
| E-2 | Scene overlay and live edit | editor | P1 | P-1, A-2, U-4 | U | 5 | todo | |
| V-3 | Performance | verification | P0 | V-2 | D, U | 6 | todo | |
| D-1 | Docs and samples | docs | P0 | U-5, U-6, A-2 | D, U | 6 | todo | |
| E-5 | Explain cell and entity inspector | editor | P2 | E-1, U-2 | U | 6 | todo | |
| E-7 | Flight recorder | editor | P2 | U-6, E-1 | D, U | 6 | todo | |
| N-1 | Server-authoritative fields | netcode | P2 | V-2 | U | 6 | todo | |
| S-1 | Save | save | P2 | V-2 | U | 6 | todo | |
| G-4 | Cross-world `Process` | core | P2 | — | G | 6 | todo | |
| G-5 | Schedule read-back | core | P2 | — | G | 6 | done | #83. `World.TryGetSchedule` (gain, fade ends/duration/ticks-left, expiry ticks-left); save/continue equals uninterrupted, proven |
| G-6 | Allocator hook | core | P2 | — | G | 6 | review | #95, accepted in review (8/8 CI green; allocator==frees over a full lifecycle; warm frames make 0 hook calls, no gate needed). Owner merges |
| N-2 | Lockstep checks | netcode | P2 | N-1 | U | 7 | todo | |
| G-8 | Parallel splat | core | P3 | V-3 | G | 7 | todo | |
| V-0 | CoreCLR day 0 | verification | P0 | V-1, V-2, a CoreCLR build | U | day 0 | active | Preview answers in Appendix B (items 1-5, 7); the flip (item 11) waits for Unity's net10 API level |
| G-0 | Build for Unity's CoreCLR TFM | core | P0 | G-0a, V-0 step 1 | G | day 0 | blocked | The TFM is net10.0 (already built); it cannot be referenced until Unity enables the net10 API level (Appendix B item 3) |

### Waves

- Cards in one wave can run in parallel. Each card starts when its Needs are met.
- G-cards need nothing from the Unity side, so they are placed in a wave by priority.
- Dates are targets.

| Wave | Target | Cards |
|---|---|---|
| 1 | Oct 19–20 | F-1, U-0, G-0a, G-1 |
| 2 | Oct 20–23 | F-2, U-1, A-1, V-1, G-3 |
| 3 | Oct 23–28 | U-2, U-4, U-6, A-2, P-1, E-3, E-4, E-6, G-2, G-7 |
| 4 | Oct 27–30 | U-3, U-5 |
| 5 | Oct 30–Nov 5 | V-2, U-5b, E-1, E-2 |
| 6 | Nov 3–13 | V-3, D-1, E-5, E-7, N-1, S-1, G-4, G-5, G-6 |
| 7 | after day 0 | N-2, G-8 |
| day 0 | about Nov 14 | V-0, G-0 |

**Critical path:** F-1 → U-1 → U-2 → U-3 and U-5 → V-2 → V-3 → V-0.
- A slip on this path moves M1 and M3.
- Every other card has slack.

V-0 starts early, on any CoreCLR preview that exists, by running the plan Appendix A checklist. Its final pass is on the release build.

## 6. Cards

Each card states:
- its goal;
- what to read;
- the files it **Owns**;
- what to **Do**;
- when it is **Done**.

Design detail lives in the plan sections each card cites; this file does not repeat it.

Terms used in the cards:
- **Equivalence**: a scenario run through the adapter produces the same per-tick `GiFieldHash`es as the same scenario run on a raw Gi world by `GiReferenceExecutor` (F-2).
- **Seeds**: seeds for `GiScenario`.
- **Short paths**: paths such as `Runtime/…` are relative to `unity/com.gi.entities/`.

### Foundation

#### F-1 · Contract pins and dotnet harness

P0 · Tier D · Needs: —

Goal:
- every Gi fact in §1 is either a passing test or a reported refutation;
- Unity-free adapter code has a home that `dotnet test Gi.slnx` builds and runs.

Read:
- plan §1 and §5;
- the public API of `src/Gi` and `src/Gi.Validation`;
- `tests/Gi.Tests`, for its test framework and the pins that already exist;
- `docs/model.md`.

Owns:
- `unity/tests/Gi.Entities.Columns.Tests/`: a net10.0 test project using the same framework as `tests/Gi.Tests`. It compiles `Runtime/Columns/**` and `Tests/Support/**` as linked sources, and references `src/Gi` and `src/Gi.Validation`.
- `unity/tests/Gi.Entities.Columns.Compat/`: a netstandard2.1 library with `LangVersion` 9 that compiles the same linked sources. It has no tests; it exists to fail the build when Unity-incompatible code appears.
- `Tests/Support/GiFieldHash.cs`
- `unity/tests/Gi.Entities.Columns.Tests/GiContractTests.cs`
- `Gi.slnx`: add the two projects.

Do:
1. Create both projects under the repository's existing build properties, with warnings as errors, as in Gi.
2. Write `GiFieldHash`: FNV-1a 64 over every cell of every grid of one layer, read with `QueryRegion`, in row-major order, grids in id order. Test grids are small, so this is a test tool, not a runtime feature.
3. Write one test per row C1–C21, named `C01_…` to `C21_…`.
   - C4 and C8 are seeded property tests over at least 200 seeds.
   - Where `tests/Gi.Tests` already pins a fact, cite that test instead of duplicating it.
4. In the report, give each row as confirmed, refined (with the exact behaviour) or refuted.

Done when:
- [D] `dotnet build Gi.slnx -c Release -m:1` and `dotnet test Gi.slnx -c Release --no-build` pass, with both projects in the solution.
- [D] Every C row has a test or a cited existing test, and a status in the report.
- [D] The compat project fails on a C# 10 feature and on a non-netstandard2.1 API in a linked file. Show both in the report; commit neither.
- [D] `src/Gi` is unchanged.

Note: if a fact can only be reached through Gi internals, do not add `InternalsVisibleTo`. Pin what the public API shows, and report the rest.

#### F-2 · Scenarios, reference executor, lint, CI

P0 · Tier D · Needs: F-1, U-0

Goal:
- one seeded scenario drives every equivalence and determinism test, in dotnet and in Unity;
- Tier D runs in CI.

Read: plan §5.3, §5.8; F-1's report.

Owns:
- `Tests/Support/`: `GiRandom.cs` (PCG32), `GiScenario.cs`, `GiReferenceExecutor.cs`
- `unity/tests/Gi.Entities.Columns.Tests/ScenarioTests.cs`
- `unity/lint.sh`
- `.github/workflows/unity.yml`

Do:
1. Write `GiScenario`.
   - It holds a profile shape (grids, layers, stamps) and a list of ticks.
   - Each tick holds operations: spawn (layer, stamp, gain, position, heading, scale, ramp ticks), despawn, disable, enable, move, turn, scale, set gain, change layer or stamp, pulse, plus the tick's splats.
   - `GiRandom` generates it from a seed and counts.
   - It prints to a stable text form for reports.
2. Write `GiReferenceExecutor`. It runs a scenario on a raw Gi world through the scalar API (`Place`, `Move`, `Remove`, `Fade`, `Expire`, `Splat`, …) and returns every layer's hash for every tick. It is the oracle for every equivalence test.
3. Tests:
   - one seed gives identical text and hashes on every run;
   - shuffling each tick's operations leaves the hashes unchanged (C4 at scenario level).
4. Write `lint.sh`. With stable output, it fails on:
   - any `//` or `/*` comment in `Runtime`, `Presentation`, `Authoring`, `Editor` or `Netcode`;
   - `System.Linq` or `UnityEngine.Debug` in `Runtime`;
   - any `Unity` namespace in `Runtime/Columns` or `Tests/Support`.
5. Write `unity.yml`. On pushes and PRs that touch `unity/**` or `src/**`, it:
   - installs the SDK that `Plugins/Gi/manifest.txt` names;
   - runs the Tier D commands.

Done when:
- [D] The scenario tests pass, and one seed prints byte-identical text on two runs.
- [D] `lint.sh` passes on the tree, and fails on one planted violation of each rule. Show the failures in the report; do not commit them.
- [D] `unity.yml` is green on the PR.
- [D] L9 is measured.

#### U-0 · Package skeleton, plugins, host project

P0 · Tier D and U · Needs: —

Goal:
- the package imports into an empty project and loads the right Gi build;
- every later card finds its assembly, folder and test suite already in place.

Read: plan §3; §4 (D-U1, D-U2, O-U1, O-U2); §5.1.

Owns:
- `unity/build-plugins.sh`, `unity/validate.sh`
- `unity/com.gi.entities/`: `package.json`, `LICENSE.md`, an empty `CHANGELOG.md`, `Plugins/**`, every `.asmdef`, and one smoke test
- `unity/project/**`: the host Unity project
- `.gitignore`: the host project's `Library`, `Temp`, `Logs`, `UserSettings` and `obj` folders
- `docs/model.md`: a new section "Unity boundary" with one heading each for U-1, U-2, U-3, U-5, U-6 and P-1, each followed by the line "Pending."

Do:
1. Write `build-plugins.sh`.
   - It builds `src/Gi` and `src/Gi.Validation` for each plugin TFM, in Release, with `Deterministic` and `ContinuousIntegrationBuild`.
   - It copies the DLLs into `Plugins/Gi/<variant>/`.
   - It writes `Plugins/Gi/manifest.txt`, listing each file's sha256 and TFM, plus the SDK version.
   - `--check` rebuilds with that SDK and fails on any difference.
   - The DLLs are committed (O-U8), so the repository always ships binaries that CI proves were built from its own source.
2. Give the plugin `.meta` files fixed GUIDs and define constraints:

   | Variant | Define constraint |
   |---|---|
   | `netstandard2.1` | `!GI_CORECLR` |
   | `coreclr` | `GI_CORECLR`, which V-0 replaces with Unity's own symbol |

3. Create these assemblies, each referencing only what plan §5.1 allows:
   - `Gi.Entities` (unsafe code allowed)
   - `Gi.Entities.Presentation`
   - `Gi.Entities.Authoring`
   - `Gi.Entities.Editor`
   - `Gi.Entities.Netcode` (constrained to `UNITY_NETCODE`)
   - `Gi.Entities.Tests.Support`
   - `Gi.Entities.Tests`
   - `Gi.Entities.Editor.Tests`
   - `Gi.Entities.Performance`
4. Set up the host project's manifest.
   - It references the package as `file:../../com.gi.entities` and lists it under `testables`.
   - It pins these per O-U2: Entities, Collections, Burst, Mathematics, the Test Framework, Performance Testing, and BovineLabs.Core (by commit).
5. Write `validate.sh <editmode|playmode|il2cpp|perf>`.
   - It runs `build-plugins.sh --check`, then the batchmode test run. `il2cpp` builds the tests into a Linux IL2CPP player and runs them there.
   - It prints a fixed-format summary: suite, passed, failed, skipped, duration, Unity version, scripting backend and HEAD commit.
6. Add a smoke test that creates a Gi world, reads its revision and frees it.

Done when:
- [D] Two runs of `build-plugins.sh` produce identical manifests, and `--check` passes.
- [D] `dotnet build Gi.slnx` and `dotnet test Gi.slnx` are unaffected.
- [U] The host project opens with no errors or warnings from the package, and `validate.sh editmode` passes the smoke test on Mono.
- [U] `validate.sh il2cpp` passes the smoke test in a Linux IL2CPP player.

### Runtime

#### U-1 · Frame, bootstrap, group, process

P0 · Tier D and U · Needs: U-0, F-1

Goal: an ECS world with a profile owns exactly one Gi world, processes it once per fixed tick, and survives rebuilds, code reloads and quits without leaking.

Read:
- plan §5.2 and §5.3;
- plan §5.4, rows 1 and 5;
- plan §5.7 and §5.10;
- plan §6, for what a profile contains;
- contract rows C16, C18, C20 and C21.

Owns:
- `Runtime/Components/`: every component in plan §5.3 (`GiEmitter`, `GiGainRamp`, `GiOriented`, `GiScaled`, `GiSource`, `GiFault`, `GiSplat`) plus `GiPulse`. Later cards add systems that use these components; they do not add components.
- `Runtime/`: `GiFrame.cs`, `GiTables.cs`, `GiFaults.cs`, `GiRuntime.cs`, `GiRuntimeConfig.cs`, `GiCounters.cs`, `GiSystemGroup.cs`, `GiBootstrapSystem.cs`, `GiProcessSystem.cs`
- `Runtime/Profile/GiProfileBlob.cs`: the full blob layout, bindings included. A-2 fills it from assets.
- `Runtime/Columns/GiPlane.cs`: projection onto the XZ or XY plane, with an origin, and heading from the entity's axes.
- Tests in `Tests/Runtime/Frame/` and `unity/tests/Gi.Entities.Columns.Tests/PlaneTests.cs`.
- The U-1 heading in `docs/model.md`.

Do:
1. `GiTables` maps profile indices to runtime ids for grids, layers and stamps.
2. `GiRuntime` holds the stamp registry. It maps a content hash to a stamp id and keeps a refcount per Gi world. A stamp is freed only when its refcount reaches 0 (C16).
3. `GiRuntime` defines the fence API: `Writer(ref SystemState, JobHandle)` and `Reader(ref SystemState, JobHandle)`. In this card they only pass the handle through; U-4 fills them in. Later cards use the API from the start.
4. Bootstrap, on a profile hash change:
   1. Validate the blob with `Gi.Validation`.
   2. Free the old Gi world.
   3. Create the new world: grids, then layers in recipe order, then stamps through the registry.
   4. Capture the token.
   5. Increment `Generation` and reset every `GiSource.Id` (C18).

   Exceptions are caught at this boundary, become the `Profile` fault, and are logged once.
5. Lifetimes:
   - `OnDestroy` frees the Gi world.
   - `[OnCodeUnloading]` frees every world and stamp the adapter owns.
   - `[OnCodeLoaded]` reclaims orphans, unless the ConfigVar `gi.runtime.reclaim` is off.
6. `GiProcessSystem` runs `Process(Token)` in a managed job. On `StaleRevision` it recaptures the token once; if that fails, it raises `StaleToken`.
7. `GiPlane` takes heading from the entity's forward axis on XZ, and from its right axis (local +X) on XY, since forward points out of that plane. C21 fixes how the angle is measured.
8. `GiCounters` declares every counter listed in plan §7. Each system card updates its own.

Done when:
- [D] `PlaneTests` pass. For every axis-aligned direction on both planes, a cone placed at the projected heading has its `QueryMax` ahead along that direction. NaN positions pass through unchanged, so Gi reports `NonFinite`.
- [U] After 1000 build-and-free cycles, the live world count (from `CopyWorldIds`) and the stamps in use are back at their starting values.
- [U] Two ECS worlds with the same profile share stamps, and closing both frees those stamps.
- [U] A 33-layer blob raises `Profile`. No exception escapes, and no Gi world exists afterwards.
- [U] Entering play mode twice with domain reload disabled leaves no orphan Gi worlds.
- [U] `Tick(world)` advances by exactly one per fixed step.
- [U] Steady ticks allocate 0 B.
- [U] E9 is pinned.

#### U-2 · Reconcile, emitters, pulses

P0 · Tier D and U · Needs: U-1, F-2

Goal: each tick, the difference between the desired state in ECS and the applied state in Gi becomes one batch, in a deterministic order.

Read: plan §1 (axioms 3, 6 and 9), §5.3, §5.4 row 2, §5.7, §5.10.

Owns:
- `Runtime/GiReconcileSystem.cs`, `Runtime/GiPulses.cs`
- `Runtime/Columns/`: `GiDiff.cs`, `GiPulseExpand.cs`, `GiApplyPass.cs`
- Tests in `Tests/Runtime/Reconcile/` and `unity/tests/Gi.Entities.Columns.Tests/ReconcileTests.cs`
- The U-2 heading in `docs/model.md`

Semantics, amending plan §5.3:
- `GiEmitter` is enableable, and disabled means absent.
  - Disabling removes the source and resets `GiSource.Id`, without a structural change.
  - Enabling places the source again.
- `Disabled` and `Prefab` entities are never placed.
- A layer or stamp change is `Remove`, then `Place`, in the same batch.
- A gain change:
  - is a `Fade` over `GiGainRamp.Ticks` when the entity has a ramp, and `SetGain` otherwise;
  - a new source with a ramp is placed at gain 0 and faded in through `CreatedBy`.
- `Place` takes its position through `GiPlane`: from `LocalTransform` on dynamic roots, and from `LocalToWorld` otherwise (E7).
- Pulses are drained, sorted by their bit pattern, and appended after the entity commands. This makes the batch, not just the field, identical across runs.

Do:
1. Build four queries:

   | Query | Matches | Result |
   |---|---|---|
   | added | `GiEmitter` enabled, no `GiSource` | one query-level structural add |
   | gone | `GiSource` with `WithAbsent<GiEmitter>` | `Remove`, then one query-level structural remove |
   | disabled | placed `GiSource` with `WithDisabled<GiEmitter>` | `Remove`, then reset `Id` |
   | live | both present and enabled, change-filtered on `GiEmitter` | diff |

2. Build the commands in Burst passes: count, prefix sum, then write (E4). Commands are ordered by query, then chunk, then entity.
3. A managed job applies the batch, split at C3's cap. A Burst pass then writes the results back. Any result other than `Applied` enables `GiFault` with that status and leaves `GiSource` unchanged.
4. A Columns-level executor drives `GiDiff`, `GiPulseExpand` and `GiApplyPass` without ECS, so that dotnet can test them against the reference.

Done when:
- [D] The Columns executor matches the reference over 200 seeds. The seeds cover spawn, despawn, disable, enable, gain changes, layer and stamp changes, and pulses.
- [U] The ECS executor matches the reference over 50 seeds, both as generated and with spawn order shuffled.
- [U] An emitter on a transient or derived layer gets `GiFault`, and nothing throws.
- [U] A tick without adds or removes makes no structural change, shown by a counter.
- [U] E2, E3, E4, E7 and E10 are pinned.
- [U] Steady ticks allocate 0 B.

#### U-3 · Sync

P0 · Tier D and U · Needs: U-2

Goal: moved, turned and scaled emitters reach Gi through `Sync` and `SyncTransforms`, with work proportional to the chunks that changed.

Read: plan §5.4 row 3, §5.8; C2.

Owns:
- `Runtime/GiSyncSystem.cs`
- `Runtime/Columns/GiSyncPass.cs`
- Tests in `Tests/Runtime/Sync/` and `unity/tests/Gi.Entities.Columns.Tests/SyncTests.cs`
- The U-3 heading in `docs/model.md`

Do:
1. Change-filter on `LocalTransform`, and on `LocalToWorld` for parented emitters.
2. Gather `SourceId`, x and y. For `GiOriented` and `GiScaled` emitters, also gather heading and scale.
3. Call `Sync` for emitters that only have a position, and `SyncTransforms` for the rest.
4. Scatter `Invalid` into `GiFault`, as C2 defines it.

Done when:
- [D] Columns-level scenarios with moves, turns and scales match the reference over 200 seeds.
- [U] The ECS executor matches the reference over 50 seeds.
- [U] Unchanged chunks pass zero entries to Gi, shown by a counter.
- [U] On both planes, the `QueryMax` of a cone emitter's field lies ahead of the entity along its forward axis.
- [U] A test documents the one-frame lag of parented emitters (E8).
- [U] E5 is pinned.
- [U] Steady ticks allocate 0 B.

#### U-4 · Fences, guard, cross-world writers

P0 · Tier U · Needs: U-1

Goal: prove the one-writer-then-readers rule, both within one ECS world and across several.

Read: plan §5.6.

Owns:
- `Runtime/GiRuntime.Fences.cs`, a partial of `GiRuntime`
- `Runtime/GiGuard.cs`
- Tests in `Tests/Runtime/Concurrency/`

Do:
1. Back the fence API with two process-wide `SharedStatic<JobHandle>`s, `Writers` and `All` (plan §5.6). Bootstrap completes `All` before any `World.New` or `World.Free`.
2. Write `GiGuard`.
   - In builds with collection checks, one atomic word per Gi world counts readers and marks the writer.
   - An overlap raises a fault naming both systems.
   - Other builds compile the guard away.
3. Pin E6. If the fixed-step group does not update in the Editor world, run the group there under `SimulationSystemGroup`, with a fixed-rate manager.

Done when:
- [U] Two ECS worlds with profiles, in one process, run 1000 ticks with the guard on and raise no fault. Their hashes equal those of the two worlds run one after the other.
- [U] A deliberately misordered test system trips the guard, and the fault names it.
- [U] One frame of three fixed ticks equals three frames of one tick, in hashes and in `Tick`.
- [U] E1 and E6 are pinned.

#### U-5 · Read bindings: value and gradient

P0 · Tier D and U · Needs: U-2

Goal: turn profile rows into parallel column reads whose results land in the game's own components.

Read: plan §5.5; C8, C11.

Owns:
- `Runtime/GiSenseSystem.cs`
- `Runtime/GiSenseConfig.cs`, which holds `gi.sense.batch`
- `Runtime/Columns/`: `GiBindingTable.cs`, `GiSensePass.cs`
- Tests in `Tests/Runtime/Sense/` and `unity/tests/Gi.Entities.Columns.Tests/BindingTests.cs`
- The U-5 heading in `docs/model.md`

Do:

Scope: the kinds Value, Gradient and ValueGradient, without exclusion.

1. Build one query per binding from the blob, resolving output components by stable type hash.
2. Gather the positions of enabled entities.
3. Read in managed batches whose sizes are multiples of 64. The default is 4096.
4. Scatter the outputs into fields by offset, through dynamic type handles, so change versions are bumped.
5. Write coverage tags from the `whole` bits, using `GetEnabledBitsRW` and `UpdateChunkDisabledCount`.

Done when:
- [D] `GiBindingTable` rejects wrong field sizes and a second writer to the same field, with stable messages.
- [D] C8 holds through `GiSensePass` over 200 seeds.
- [U] Every entity's outputs equal `TrySense` and `TrySenseGradient` at its position.
- [U] Parallel output equals serial output, bit for bit.
- [U] In partly enabled chunks, only enabled entities are written, and the hashes of unrelated components do not change.
- [U] Steady ticks allocate 0 B.

#### U-5b · Read bindings: area, max, nearest, changed

P1 · Tier D and U · Needs: U-3, U-5

Goal: the remaining binding kinds, plus self-exclusion, `Period` and `Since`.

Read: plan §5.5; C9–C12.

Owns:
- `Runtime/GiSenseSystem.Kinds.cs`, a partial
- `Runtime/Columns/GiSensePass.Kinds.cs`
- Tests in `Tests/Runtime/SenseKinds/` and `unity/tests/Gi.Entities.Columns.Tests/BindingKindTests.cs`

Done when:
- [D] Each new kind's pass matches the scalar Gi call at Columns level over 200 seeds.
- [U] For every entity, Area, Max, Nearest and Changed match `TrySenseArea`, `TrySenseMax`, `TrySenseNearest` and `Changed`. The found and coverage tags match too.
- [U] Exclude-self bindings match `TrySense` with exclusion.
  - They run single-threaded under `Writers` until G-1 is accepted.
  - After that, a follow-up makes them parallel and reruns this test. G-1 landed (#81): reads insert nothing, but the parallel window must still exclude concurrent writers — a `TryFindTaps` hit can race a write-path insert (`EmitDelta`/`FlushHistogram`; the 32-entry ring overwrites taps in place).
- [U] Exclude-self on a transient layer is rejected both at bake and at bootstrap, because a splat has no `SourceId`.
- [U] With `Period` set to n, each entity is sensed exactly once every n ticks.
- [U] Steady ticks allocate 0 B.

#### U-6 · Splat

P0 · Tier D and U · Needs: U-1, F-2

Goal: enabled `GiSplat`s reach their transient layers as one `Splat` call per layer.

Read: plan §5.4 row 4; C5.

Owns:
- `Runtime/GiSplatSystem.cs`
- `Runtime/Columns/`: `GiSplatSort.cs`, `GiSplatPass.cs`
- Tests in `Tests/Runtime/Splat/` and `unity/tests/Gi.Entities.Columns.Tests/SplatTests.cs`
- The U-6 heading in `docs/model.md`

Done when:
- [D] The counting sort is stable, uniform layers are detected, and Columns-level splats match the reference over 200 seeds.
- [U] ECS splats match the reference over 50 seeds.
- [U] The uniform overload equals the per-entry overload (C5).
- [U] A disabled `GiSplat` contributes nothing.
- [U] A `GiSplat` on a persistent layer gets `GiFault`.
- [U] Steady ticks allocate 0 B.

### Verification

#### V-1 · Gi parity on Unity backends

P0 · Tier D and U · Needs: U-0, F-1

Goal: Gi computes the same bits under Unity's Mono and IL2CPP as it does under `dotnet`.

Read: `tests/Gi.Tests`, for the golden corpus, its interpreter and its hashes.

Owns:
- `unity/tests/Gi.Corpus/`: a netstandard2.1 project that links the corpus interpreter's sources from `tests/Gi.Tests`.
- `Tests/Corpus/`
- An exception to R-3: a `--corpus` option in `build-plugins.sh` that builds `Gi.Corpus.dll` and copies the golden streams.

Do:
1. Replay every golden stream inside a Unity test, and compare each hash with the committed corpus.
2. Never re-implement the interpreter. If it is tangled with test-framework types, extract the framework-free part into a file that both projects link, leaving `dotnet test Gi.slnx` unchanged.

Done when:
- [D] `Gi.Corpus.dll` builds deterministically.
- [D] `dotnet test Gi.slnx` is unchanged.
- [U] Every golden hash matches on Mono, in EditMode.
- [U] Every golden hash matches in a Linux IL2CPP player.

This roadmap tests IL2CPP on Linux only. Other IL2CPP targets (consoles, mobile, WebGL) are for the owner's game to test.

#### V-2 · ECS determinism and allocation

P0 · Tier U · Needs: U-3, U-4, U-5, U-6

Goal: the whole adapter meets plan §5.8 and axiom 8.

Owns: `Tests/Runtime/Determinism/`, `Tests/Runtime/Allocation/`.

Done when, on Mono and in a Linux IL2CPP player:
- [U] **Permutation.** Shuffling the spawn order gives the same per-tick hashes.
- [U] **Rebuild.** After a rebuild, the hashes equal those of a world started fresh with the same profile and entities. Pulses and fades in flight are dropped by design, and the test documents this.
- [U] **Catch-up.** Frames that run several fixed ticks give the same hashes as frames that run one.
- [U] **Allocation.** 100 steady ticks with every system active allocate 0 B. "Every system" means emitters, sync, splat and every binding kind that has landed. L6 is filled.

#### V-3 · Performance

P0 · Tier D and U · Needs: V-2

Goal: Unity numbers for the frames issue #40 measured under `dotnet`.

Read: `benchmarks/`, for frames B1, C and W and their parameters.

Owns: `Tests/Performance/**`, `unity/perf.md`, `unity/perf-baseline.json`.

Do:
1. Build Performance Testing scenes for frames B1, C and W. Take their parameters from `benchmarks/` by linking or generating them, never by retyping them.
2. Measure Gi-only time with zero job workers, and wall time with the default workers.
3. Run on Mono and IL2CPP now, and on CoreCLR at V-0.

Done when:
- [U] L3–L5, L7, L10 and L11 are filled now; L1 and L2 at V-0.
- [D] `perf-baseline.json` is deterministic: sorted keys, invariant culture, fixed decimals.
- [D] `perf.md` has one table per backend and host, and states the method.

#### V-0 · CoreCLR day 0

P0 · Tier U · Needs: V-1, V-2, a CoreCLR build of Unity

Goal: the CoreCLR backend runs the fast Gi build, with every receipt that Mono and IL2CPP have.

Read: plan Appendix A.

Owns:
- the define constraint on both plugin `.meta` files;
- the answers to plan Appendix A. The agent records them in the report, and the coordinator copies them into plan §5.1.

Do:
1. As soon as any preview exists, answer plan Appendix A items 1–8 on it. Item 1, the TFM, starts G-0.
2. On the release build, after G-0 lands:
   1. Enable `Plugins/Gi/coreclr` behind Unity's own CoreCLR define.
   2. Run V-1, V-2 and V-3.

Done when:
- [U] V-1 and V-2 pass on CoreCLR, both with intrinsics on and with `DOTNET_EnableHWIntrinsic=0`.
- [U] L1–L3 are filled.
- [U] Exactly one `Gi.dll` is active on each backend, shown by the Field window's runtime panel or by a test.

### Authoring

#### A-1 · Assets and inspectors

P0 · Tier U · Needs: U-0

Goal: designers can author profiles, layers and stamps. Anything invalid shows its error in the inspector, in the same words `Gi.Validation` uses.

Read: plan §6; O-U4; C10.

Owns:
- `Authoring/`: `GiProfile.cs`, `GiLayerAsset.cs`, `GiStampAsset.cs`
- `Editor/Inspectors/**`
- Tests in `Tests/Editor/Authoring/`

Do:
1. Create the assets described in plan §6.
2. Build inspectors that show `Gi.Validation` help boxes.
3. Build the layer stack described in O-U4. In it, a derived layer can only pick layers above it.

Done when:
- [U] Each `Gi.Validation` rule in plan §6 has an inspector test, and the message the inspector shows is exactly `Gi.Validation`'s text.
- [U] Saving an asset twice gives byte-identical YAML.
- [U] No inspector action creates a forward layer reference. Tests drive the inspector through `SerializedObject`.
- [U] Every edit can be undone and redone.

#### A-2 · Bakers, builders, live baking

P0 · Tier U · Needs: A-1, U-1

Goal: authoring becomes data through one construction path, shared by bakers, runtime spawners and tests.

Read: plan §6; E7.

Owns:
- `Authoring/`: `GiProfileAuthoring.cs`, `GiEmitterAuthoring.cs`, `GiSplatAuthoring.cs`, `GiProfileBlobBuilder.cs`, `Bakers/**`
- `Runtime/Builders/`: `GiEmitterBuilder.cs`, `GiSplatBuilder.cs`
- Tests in `Tests/Editor/Baking/`

Do:
1. `GiProfileBlobBuilder` turns the assets into the blob defined in U-1, with an xxHash3 content hash.
2. The builders apply components through BovineLabs `IEntityCommands`.
3. The emitter baker asks for `TransformUsageFlags.Dynamic` only when the authoring says the emitter moves.
4. A live-baked profile edit rebuilds the Gi world.

Done when:
- [U] Baking twice gives identical blob bytes and an identical hash.
- [U] Editing a layer in play mode rebuilds the world and re-places every source. World and stamp counts show that nothing leaked.
- [U] Every baker error names its authoring object as context.
- [U] Static emitters bake without `LocalTransform` and are still placed (E7).

### Presentation

#### P-1 · GPU mirror and heatmap

P1 · Tier D and U · Needs: U-1, F-2

Goal:
- chosen layers are mirrored on the GPU, at a cost proportional to how much changed;
- they draw the same way in the game, the Scene view and the Field window.

Read: plan §7, "GPU mirror"; C13, C14.

Owns:
- `Presentation/**`, including `GiMirrorConfig.cs` (which holds `gi.mirror.budget`) and the shaders
- `Runtime/Columns/GiPageTable.cs`
- Tests in `Tests/Runtime/Mirror/` and `unity/tests/Gi.Entities.Columns.Tests/PageTableTests.cs`
- The P-1 heading in `docs/model.md`

Done when:
- [D] Under random change sets, `GiPageTable` allocates, frees and reuses tile slots deterministically.
- [U] After random ticks, over 50 seeds, the mirror decodes to exactly what `QueryRegion` returns for the whole layer.
  - A compute pass decodes the GPU mirror into a buffer for the comparison.
  - Tests drive a raw Gi world through `GiReferenceExecutor`.
- [U] Uploaded bytes scale with the number of changed tiles.
- [U] Once changes stop, the budget carry-over drains completely.
- [U] The heatmap renders in Built-in, URP and HDRP; the owner confirms with screenshots.
- [U] Steady frames allocate 0 B.
- [U] L8 is filled.

### Editor

Every editor card follows plan §7:
- windows never call Gi directly; `GiInspectorSystem` serves their requests;
- UI Toolkit, with the BovineLabs theme, draws them.

#### E-1 · Field window

P1 · Tier U · Needs: P-1

Owns:
- `Editor/Field/**`
- `Presentation/GiInspectorSystem.cs`
- Tests in `Tests/Editor/Field/`

Do: build the Field window described in plan §7, with these specifics:
- The default palette is a diverging map that is safe for colour blindness, with a legend in field units.
- The runtime panel shows G-3's read-outs once G-3 lands, and "n/a" until then.

Done when:
- [U] Probe values equal `TrySense*` calls made on the main thread with the same inputs.
- [U] With the window open, the game's steady tick still allocates 0 B.
- [U] Closing the window frees its mirror.
- [U] The ECS world, grid and layer pickers update as worlds are created and destroyed.

#### E-2 · Scene overlay and live edit

P1 · Tier U · Needs: P-1, A-2, U-4

Owns: `Editor/Overlay/**`; tests in `Tests/Editor/Overlay/`.

Done when:
- [U] Dragging an emitter in edit mode changes the overlay on the next editor tick.
- [U] Each drawn footprint matches the nonzero bounds that `QueryRegion` reports for that emitter alone.
- [U] Overlay toggles persist across sessions, in a deterministic settings file.

#### E-3 · Stamp Studio

P1 · Tier U · Needs: A-1, U-1

Owns: `Editor/StampStudio/**`; tests in `Tests/Editor/StampStudio/`.

Done when:
- [U] For any phase, angle and scale, the preview's hash equals that of a runtime world placing the same stamp, over 50 seeds.
- [U] At most one temporary stamp exists at any time.
- [U] Closing the Studio frees its Gi world and its temporary stamp.
- [U] Importing the same texture twice gives identical raster cells.

#### E-4 · Profile editor

P1 · Tier U · Needs: A-1

Owns: `Editor/Profile/**`; tests in `Tests/Editor/Profile/`.

Done when:
- [U] The coverage map agrees with `Covers` at every point of a probe lattice.
- [U] Binding size errors appear in the editor before any bake.
- [U] The memory estimate is a tested formula. Once G-3 lands, it is checked against G-3's read-outs.

#### E-5 · Explain cell and entity inspector

P2 · Tier U · Needs: E-1, U-2

Owns: `Editor/Explain/**`, `Editor/Inspectors/GiSourceInspector.cs`; tests in `Tests/Editor/Explain/`.

Done when:
- [U] In random scenes without saturation, the contributions sum to the cell's value. In saturated scenes, the `saturated` flag shows.
- [U] Each contribution links to its entity, and following the link selects that entity.
- [U] For a derived layer, the tool shows the recipe evaluated at the cell, with intermediate values.

#### E-6 · Profiler module and ConfigVars

P1 · Tier U · Needs: U-1

Owns: `Editor/Profiler/**`, `Runtime/GiDebugConfig.cs`; tests in `Tests/Editor/Profiler/`.

Done when:
- [U] Every counter in `GiCounters` shows in a Profiler module, alongside the markers each system adds (R-11).
- [U] Every `gi.*` ConfigVar name matches BovineLabs' naming pattern.
- [U] A test sets a ConfigVar from the command line and sees the override take effect.

#### E-7 · Flight recorder

P2 · Tier D and U · Needs: U-6, E-1

Owns:
- `Runtime/Recorder/**`, `Editor/Recorder/**`
- Tests in `Tests/Runtime/Recorder/` and `unity/tests/Gi.Entities.Columns.Tests/RecorderTests.cs`

Done when:
- [D] Encoding the recorder's stream and decoding it again returns the original, over 200 seeds.
- [U] Replaying 10k ticks of the Crowd sample reproduces every live hash.
  - Keyframes taken in the middle of a fade are exact only once G-5 lands.
  - Until then, the recorder takes keyframes only on ticks with no fade in flight.
- [U] An exported range runs as a test in `tests/Gi.Tests` without edits.
- [U] A diff view highlights the tiles where the live run and the replay differ.

### Netcode, save, docs

#### N-1 · Server-authoritative fields

P2 · Tier U · Needs: V-2

Read: plan §8.

Owns: `Netcode/**`; tests in `Tests/Runtime/Netcode/`.

Done when:
- [U] In a host-mode test, the server and client fields are equal for static sources.
- [U] When the profile hashes differ, the client keeps Gi off and raises a fault.

#### N-2 · Lockstep checks

P2 · Tier U · Needs: N-1

Read: plan §8, N-2.

Owns: `Netcode/Lockstep/**`.

Done when:
- [U] A desync injected on purpose is reported within *k* ticks, where *k* is the check interval.

#### S-1 · Save

P2 · Tier U · Needs: V-2

Read: plan §8, S-1.

Owns: `Runtime/Save/**`; tests in `Tests/Runtime/Save/`.

Done when:
- [U] For emitters and splats, save, load and continue gives the same result as an uninterrupted run.
- [U] Pulses come back with their remaining ticks.
- [U] Once G-5 lands, a restore taken in the middle of a fade is also exact. G-5 landed (#83): the rounding phase is provably not derivable from (gain, target, ticksLeft), so the restore re-drives the staircase per tick with `Fade(target, 0)` from the read-back's from/ticks/ticksLeft.

#### D-1 · Docs and samples

P0 · Tier D and U · Needs: U-5, U-6, A-2

Owns:
- the package `README.md`
- `Documentation~/`
- `Samples~/`, with four samples:
  - Crowd
  - Territory
  - Stealth
  - a game job calling a pure Gi read through `GiFrame`
- the introduction to `docs/model.md` § Unity boundary

Done when:
- [U] The README quick start works from an empty project.
- [U] Every sample runs on Mono and IL2CPP.
- [D] Every heading under § Unity boundary has its proof, and none still says "Pending."

### Gi core

Plan §9 gives each card's change, reason and Done-when.
- Every G-card branches from `main` (R-1) and passes Tier G.
- Every G-card has a fallback, so the Unity side never waits on it (S5).

| Card | Pri | Wave | What the card does, and what the Unity side does until it lands |
|---|---|---|---|
| G-0a | P0 | 1 | A dry run, not merged. Build Gi for `net8.0` and `net9.0` on a scratch branch, list every `#if` that needs, and run the corpus on each. Its report reduces G-0 to a routine step on day 0. |
| G-0 | P0 | day 0 | Gi builds for the TFM V-0 names, and the `coreclr` plugin variant uses that build. Fallback: S2. |
| G-1 | P1 | 1 | Until it lands, exclude-self bindings run single-threaded (U-5b). |
| G-2 | P1 | 3 | Until it lands, each code reload leaks Gi's process tables once. |
| G-3 | P1 | 2 | Until it lands, the Field window shows "n/a" for the memory and live-tile read-outs. |
| G-4 | P2 | 6 | Until it lands, `Writers` serializes Gi writes across ECS worlds. |
| G-5 | P2 | 6 | Until it lands, save and recorder keyframes are exact only when no fade is in flight. |
| G-6 | P2 | 6 | Until it lands, Unity's memory profiler cannot see Gi's memory. |
| G-7 | P1 | 3 | Until it lands, hashes scan whole grids. That is fine in tests but too slow for live desync checks. |
| G-8 | P3 | 7 | Until it lands, splat runs on one core. The card starts only if V-3 shows splat dominating the frame. |

## Appendix A: Changes to plan.md

This roadmap changes the plan in the ways listed here. The coordinator folds them into the plan when the owner asks.

1. **Unity-free code.**
   - Unity and two dotnet projects in `Gi.slnx` both compile `Runtime/Columns/` and `Tests/Support/` (R-6).
   - Every runtime card therefore has Tier D receipts.
   - A Gi change that breaks the adapter's assumptions fails `dotnet test Gi.slnx`.
2. **Contract first.** §1 lists the facts the plan assumed. F-1 pins each one with a test before anything uses it.
3. **One scenario, three executors.** `GiScenario` runs through `GiReferenceExecutor`, the Columns executor and the ECS executor. Their per-tick hashes must agree (F-2).
4. **U-1 grows; U-4 shrinks.**
   - U-1 defines every component, the group, `GiProcessSystem`, `GiPlane`, the full blob layout, `GiCounters` and the fence API.
   - Plan U-4 shrinks to fences, the guard and cross-world serialization.
   - U-4, U-6, A-2 and P-1 then need only U-1.
5. **Enableable emitters.** `GiEmitter` is enableable, and disabled means absent. `Disabled` and `Prefab` entities are never placed.
6. **Position source** (amends D-U4).
   - `LocalTransform` on dynamic roots, and `LocalToWorld` otherwise.
   - Parented movers are therefore one frame stale.
   - On XY, heading comes from local +X.
7. **Pulses are sorted** by bit pattern, so that batches, not only fields, are identical across runs.
8. **U-5 splits in two.**
   - U-5 covers value and gradient (P0).
   - U-5b covers the other kinds, exclusion, `Period` and `Since` (P1).
9. **Exclude-self is rejected on transient layers,** because splats have no `SourceId`.
10. **Plugins are committed** with a manifest, and CI proves they match the source (O-U8).
11. **Proofs land with the code.** Each card fills its own heading under `docs/model.md` § Unity boundary; D-1 writes only the introduction.
12. **ConfigVars are split by area** into `GiRuntimeConfig`, `GiSenseConfig`, `GiMirrorConfig` and `GiDebugConfig`, so no two cards share a file.
13. **Performance results** go to `unity/perf.md`, not to the package README.
14. **Card ids.** This file's cards replace plan §10 and §11. Plan §12's ledger continues here as §2.
15. **The editor baseline follows O-U2** (minimum Unity = whatever the supplied BovineLabs.Core targets) — **attempted and refuted on 2026-10-18**: the 6000.7 repin hits the same wall as the Unity 7 alphas, because on 6000.7 the builtin Collections is a type-forwarding shim into the engine's `UnityEngine.ManagedKernelModule.dll`, where `HashMapHelper`, `Pair`, `UnsafeParallelHashMapData` and `UntypedUnsafeList` are internal with a Unity-only friend list, and the public registry serves no standalone 6.x Collections to pin instead. BovineLabs 2.0.0-pre.1 is the newest published build (its main branch is an unpublished pre.2 evidently built against a 6000.7 newer than b5). **The host project keeps the 7000.0.0a8 pin.** U-0 is `blocked` (Tier D accepted); the exits are a published BovineLabs build against a 6000.7.0f1+/Unity 7 installable editor, or the owner revisiting O-U6. Editors 6000.7.0b4 and b5 stay installed for the retry.

    **Superseded 2026-10-18 (later the same day): the refutation tested the wrong BovineLabs build.** It repinned to `com.bovinelabs.core` 2.0.0-pre.1, the only version on OpenUPM, which targets the 6000.x registry line. BovineLabs' own registry (`upm.bovinelabs.com`) serves up to 2.0.0-pre.5 (built for 7000.0.0a7), and the public GitLab `main` is 2.0.0-pre.6 with `"unity": "7000.0", "unityRelease": "0a9"`. The host project is now pinned to editor 7000.0.0a9 and to `com.bovinelabs.core` at GitLab commit `82be2794cf7535f6fb598bb704527f41629a8f64` (pre.6). It compiles, and the EditMode, PlayMode, CoreCLR-player and IL2CPP-player suites run (Appendix F). U-0's Tier U items are unblocked.

16. **Content hashes are FNV-1a 64 with a finalizer, not xxHash3** (`GiContentHash`). It is Unity-free, so `dotnet test` pins it and the profile hash, the stamp registry key and the rebuild check use one implementation. A collision needs 2^-64 luck; the profile blob also stores the hash and bootstrap recomputes it, so a corrupted blob is rejected.
17. **The ConfigVar is `gi.runtime.keep-orphans` (default false), not `gi.runtime.reclaim`.** A `SharedStatic<bool>` is false until ConfigVarManager applies its default, and `[OnCodeLoaded]` callbacks have no defined order, so the safe polarity is the one whose zero value means "reclaim".
18. **Fences are implemented in U-1** (`GiRuntime.BeginWrite/EndWrite/BeginRead/EndRead/CompleteAll`), because U-1's process job already needs them. U-4 keeps the guard, the cross-world proof and the Editor-world check.
19. **`GiSystemGroup` and every system use `Worlds.SimulationEditor`.** Whether the group actually updates in the Editor world is still U-4's pin (E6).

20. **Reconcile skips unchanged chunks** by change version: a chunk is scanned when `GiEmitter`, `GiSource` or the chunk order changed since the system last ran, or when it has no `GiEmitter` at all (destroyed or removed emitters). The 50-seed ECS equivalence test covers every operation kind, including enable-bit toggles, so a miss would fail it.
21. **Batch commands are an adapter type** (`GiCommand`), not Gi's `SourceCommand`. The managed apply pass converts and slices at command-group boundaries (never between a `Place` and the `CreatedBy` commands that follow it), re-indexing `CreatedBy` per slice, and retries once after a stale token.
22. **Cleanup components are removed through a command buffer played back at the start of the next update** and only when the retire query is non-empty, so a quiet tick has no structural change and no sync point.

23. **Sync is one `SyncTransforms` call over up to four row categories** (none, oriented, scaled, both), because the call accepts null heading and scale columns and reports rejected rows through the `Invalid` mask. The plan's separate `Sync` call for position-only rows is not needed. Gi clears and fills the mask itself.
24. **A parented emitter's pose comes from `LocalToWorld`** (a chunk with `Parent`, or without `LocalTransform`), and a root's from `LocalTransform`; both reconcile and sync use `GiPose`.
25. **`GiStats` is a component on the frame entity** (commands, pulses, sync rows, splats, sensed, changed tiles). Jobs write it; the profiler module (E-6) and tests read it.

26. **Layer residency is part of `GiTables`** (`TransientMask`), because `World.Splat` returns 0 for a non-transient layer without saying why. The splat pass maps an unknown layer key to `InvalidLayer`, a persistent or derived layer to `WrongResidency`, and an unknown stamp key to `InvalidStamp`, and skips the entity.
27. **Splats are not change-filtered**: a transient layer is cleared by every `Process`, so every enabled `GiSplat` is read and splatted every tick. The sort is a stable counting sort by layer (chunk order, then entity order) and a layer whose stamps and gains all agree uses the single-stamp overload.

28. **The coverage tag of a `ValueGradient` binding means "the gradient is computable"** (Gi needs the neighbouring cells), so at a grid edge an entity can carry a value while its tag is clear; a `Value` binding's tag is that value's own coverage. Tests pin both meanings.
29. **Per-binding queries ask for a coverage-tag component with `Present`, not `All`,** so an entity whose tag is disabled still matches the query and the scatter can switch the tag on.
30. **Coverage tags are written through `ArchetypeChunk.SetComponentEnabled`,** which performs the card's `GetEnabledBitsRW` + `UpdateChunkDisabledCount` work for the entity's own chunk.

New open decisions:

| ID | Question | Default |
|---|---|---|
| O-U7 | Should the Unity-free adapter tests live in `Gi.slnx` or in a separate solution? | `Gi.slnx`, so Gi's own CI guards the contract. |
| O-U8 | Should the plugin DLLs be committed with a CI provenance check, or built locally and shipped only in a release tarball? | Committed. Git-URL installs then work, and CI proves the binary matches the source. |

## Appendix B: CoreCLR checklist

The questions are plan Appendix A. V-0 runs them and records the answers here. Results below are from the **7000.0.0a9** alpha (2026-10-18, i9-14900K, Linux); the release build still has to be run again.

| # | Question | Answer on 7000.0.0a9 |
|---|---|---|
| 1 | Runtime | CoreCLR, **.NET 10.0.10-dev** (`System.Private.CoreLib`, no `Mono.Runtime`) in the editor and in the Linux player. `ScriptingImplementation` is `Mono2x, IL2CPP, WinRTDotNET, CoreCLR`. Script assemblies still compile against netstandard / .NET 4.8 reference assemblies: `ApiCompatibilityLevel.NET` (net10) exists but is marked "still a work in progress and is disabled for now", and `EditorAssembliesCompatibilityLevel` has only Default, NET_Unity_4_8 and NET_Standard. |
| 2 | Define | `ENABLE_CORECLR` (editor and CoreCLR player, set per assembly by Bee). IL2CPP player: `ENABLE_IL2CPP`. `ENABLE_MONO` is never defined. Plugin define constraints do evaluate `ENABLE_CORECLR`. |
| 3 | Load | The netstandard2.1 `Gi.dll` loads and runs in all three: editor, CoreCLR player, IL2CPP player. A `net10.0` `Gi.dll` is selected correctly by a define-constrained importer but **cannot be referenced by script assemblies**: CS1705 (`Gi` uses `System.Runtime 10.0.0.0`, higher than the referenced assembly). It stays unusable until the net10 API level is enabled. 7000.0.0a10 (installed and checked 2026-10-18) is unchanged: `ApiCompatibilityLevel.NET` is still "disabled for now" and `EditorAssembliesCompatibilityLevel` still has only Default, NET_Unity_4_8 and NET_Standard. |
| 4 | Intrinsics | CoreCLR editor and player: `Vector128`/`Vector256` accelerated; SSE4.1, AVX2, BMI2, POPCNT supported; AVX-512 absent on this CPU. IL2CPP player: nothing usable (`Avx2` type missing, `Vector128.IsHardwareAccelerated` false, `Sse41` and `Popcnt` report false). So IL2CPP must keep the scalar build. |
| 5 | JIT | `DOTNET_TieredCompilation`, `DOTNET_TieredPGO`, `DOTNET_EnableHWIntrinsic` unset by default; GC is workstation, latency Interactive (CoreCLR) or Batch (IL2CPP). Warm-up and environment-variable behaviour: V-3. |
| 6 | Reload | Not yet tested (U-1 covers code unload and orphan reclaim). |
| 7 | Jobs | 31 job workers on this host. Managed job from a managed `ISystem`: E9, U-1. |
| 8 | Burst | Burst 2.0.0 is a builtin module and the package compiles with it. `BurstTrampoline`: not yet exercised. |
| 9 | Parity | V-1. |
| 10 | Numbers | V-3. |
| 11 | Flip | **Blocked by item 3**, not by the plugin machinery. When Unity enables the net10 API level: build the `coreclr:net10.0` variant (the variant→TFM mapping already exists in `build-plugins.sh`; add it to `variants`), write both importers' define constraints with `GiPluginSetup` (`GI_PLUGIN_VARIANT`, `GI_PLUGIN_CONSTRAINT`), and rerun V-1, V-2, V-3. |

Importer metas: Unity 7 writes `platformData` as a mapping (`Any:`, `Editor:`). A hand-written meta using the older list form (`- first:` / `second:`) is silently ignored, which is what lesson 8 saw. Always let `GiPluginSetup` (the `PluginImporter` API) write the meta.

## Appendix C: Report format

```text
Card:        <id>
Branch / PR: <name / number>
Host:        <cpu, os; for Tier U also the Unity version and scripting backend>
Owns check:  <files changed outside Owns, or "none">
Contract:    <rows touched: confirmed / refined (behaviour) / refuted (failing test)>
Receipts:    <one line per Done-when item: tier, test or command, result>
Not run:     <Tier U items left for validate.sh>
Numbers:     <only after receipts: median of N, and the method>
Ledger:      <row: predicted, measured>
Deviations:  <what differs from the card, and why>
Follow-ups:  <risks, new cards>
```

## Appendix D: Agent prompt

```text
You are executing card <ID> of unity/ROADMAP.md in the grid-influence repository.

Read first: AGENTS.md; unity/ROADMAP.md §0, §1, §4 and card <ID>; the unity/plan.md sections the card cites.
Branch from unity/main as unity/<id>-<slug>. For a G-card, branch from main as gi/<id>-<slug>.
Change only the files the card lists under Owns.
Produce every Done-when receipt you can run here; list the rest under "Not run".
If a contract row in §1 is refuted, stop and report it with the failing test.
Open one PR into unity/main (into main for a G-card). Do not merge, tag, or push to a shared branch.
End with a report in the Appendix C format.
```

## Appendix E: Review checklist

The coordinator checks, in this order:

1. **Owns.** The diff touches only the card's files, or the report explains why it does not.
2. **Contract.** Every row the card touches has a status. A refuted row means re-plan.
3. **Receipts.** Every Done-when item has a named receipt with its tier.
   - D items must pass.
   - U items must pass, or the card goes to `U-pending`.
4. **Rules.**
   - `lint.sh` passes.
   - Allocation is 0 B wherever a card requires it.
   - Written files are deterministic.
   - Proofs are filled in for unsafe code.
5. **Ledger.** The rows the card owns are filled in with their method, and misses are stated plainly.
6. **Verdict.** One of:
   - accept, moving the card to `U-pending` or `done`;
   - fix, with the exact changes;
   - re-plan, naming the section that moves.

   Then update the board and name the next cards.

## Appendix F: Host environment and session notes

Recorded 2026-10-18 so later sessions start from verified facts, not assumptions. Amend this appendix, not the plan, as the environment changes.

### Environment (verified on this host)

- `unity` CLI 1.0.0-beta.13 at `~/.local/bin/unity`. Official documentation: github.com/Unity-Technologies/skills, skill `unity-cli` (with references for editors-install, build-run-test, auth-license-cloud, integration-advanced). Clone it fresh if `/tmp/unity-skills` is gone.
- License: Unity Personal (Assigned), active via OAuth; batchmode entitlement resolved on every launch. Personal licenses are machine-and-account bound: a service account **cannot** activate one, so a CI lane (O-U3) needs serial or floating licensing.
- The pinned editor is **7000.0.0a9** (`~/Unity/Hub/Editor`). Editors 7000.0.0a8, 6000.7.0b4 and 6000.7.0b5 stay installed from the earlier attempts; 7000.0.0a10 exists and is not installed. Modules installed for a9: `linux-il2cpp` (`unity install-modules -e 7000.0.0a9 -m linux-il2cpp --yes --accept-eula`).
- **Unity 7 scripting runtimes.** a9 has two: **CoreCLR** (the default; the editor itself runs on it, its assemblies are compiled with `ENABLE_CORECLR`; the bundled runtime is .NET 10.0.10, `System.Private.CoreLib` 10.0.10-dev, under `Data/PlaybackEngines/LinuxStandaloneSupport/Variations/CoreCLRShared`) and **IL2CPP**. There is no Mono backend left. `ScriptingImplementation` is `Mono2x, IL2CPP, WinRTDotNET, CoreCLR`. Modules `windows-coreclr`, `mac-coreclr` and `mac-server-coreclr` exist, so the CoreCLR transition is already testable on this host.
- **Backend selection.** `scriptingBackend:` in `ProjectSettings.asset` is keyed by build-target name and the existing `Linux64` key is ignored. `unity/project/Assets/Editor/GiHostBackend.cs` reads `GI_SCRIPTING_BACKEND` (`il2cpp` or `coreclr`) and calls `PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, …)`. `validate.sh` sets it and restores `ProjectSettings.asset` afterwards.
- **Suites** (all through `unity/validate.sh <suite>`, each prints the fixed summary block and exits `1` on failures, `2` on infrastructure errors or **zero tests**):

  | Suite | Runs | Command underneath |
  |---|---|---|
  | `editmode` | `Tests/Editor` in the editor (CoreCLR) | `unity test --mode EditMode` |
  | `playmode` | `Tests/Runtime` in the editor (CoreCLR) | `unity test --mode PlayMode` |
  | `coreclr` | `Tests/Runtime` in a Linux CoreCLR player | editor `-runTests -testPlatform StandaloneLinux64` |
  | `il2cpp` | `Tests/Runtime` in a Linux IL2CPP player | the same, with IL2CPP selected |
  | `perf` | `Tests/Performance` in the editor | `unity test --mode PlayMode --filter Gi.Entities.Performance` |

  `unity test --mode` only accepts `EditMode` and `PlayMode`; the player platforms need the editor's own `-testPlatform`, which `unity test` reserves.
- **IL2CPP on this host.** The host project depends on `com.unity.toolchain.linux-x86_64` 2.0.11 (sysroot and clang; the library package does not). Unity's bundled `clang++` and `ld.lld` need `libncurses.so.6` and `libxml2.so.2`, which this distro (Arch-based) ships as `libncursesw.so.6` and `libxml2.so.16`. `validate.sh il2cpp` creates symlinks in `unity/project/Logs/lib-shim` and prepends it to `LD_LIBRARY_PATH`. A cold IL2CPP build is about two minutes wall and 38 minutes of CPU; warm reruns relink only (about 30 s).
- Receipt exit contract for `unity test`: `0` pass, `8` tests ran and failed (terminal, never retry), anything else is infrastructure and safe to retry once. For the direct editor runs: `0` pass, `2` tests failed, `3` run error.
- A running editor is controllable headlessly: `unity pipeline install` (com.unity.pipeline, Unity 6.0+), launch the editor with `-batchmode -nographics` and no `-quit`, then `unity command eval '<C#>'` (about 40 ms round trip) and `unity close`. The editor-tooling cards (E-*) should drive editors this way instead of hand-authored UI harnesses. Safe Mode caveat: a project that boots with compile errors never loads the pipeline — fix the code and restart; `unity pipeline list` confirms.
- BovineLabs sources: the public upstream is `https://gitlab.com/tertle/com.bovinelabs.core.git` (the host project pins commit `82be2794`, 2026-10-15, version 2.0.0-pre.6). The registry `upm.bovinelabs.com` serves up to pre.5; OpenUPM serves only pre.1 (stale; do not use). A local mirror of the BovineLabs monorepo exists at `~/GitHub/tertle-monorepo` (private; paid-tier content, never copy from it into this repository).

### Lessons — mistakes not to repeat

1. A direct push to `main` (the engine-v2 merge, cfc38a5) bypassed CI and carried 134 latent inspection findings; every PR then failed the `inspect` gate until `hygiene/inspect-sweep` cleared them. Land every change through a PR so the gates run.
2. "No Unity on this host" was asserted after checking only the Hub folder and `unity-editor`. Before declaring an environment limit, inventory with `which unity && unity doctor --format json`.
3. UPM git-URL dependencies are availability risks: the pinned `github.com/terrtle/com.bovinelabs.core.git` 404s (repository gone). Depend on registry versions through the OpenUPM scoped registry, or vendor; the swap is recorded on PR #79.
4. Unity Test Framework treats an asmdef with `"includePlatforms": []` as a PlayMode assembly: EditMode discovery finds zero tests. EditMode test assemblies need `"includePlatforms": ["Editor"]`.
5. ReSharper findings are enforced by CI (`jb inspectcode --severity=WARNING`, zero tolerance) but not by `TreatWarningsAsErrors`; run `jb inspectcode Gi.slnx --build` locally before opening any PR, in `unity/` and `src/` alike.
6. On Unity 7 alphas, Entities/Collections/Burst are builtin editor modules that override any manifest pin (packages-lock records "builtin"). A package built for the 6000.x registry line then fails with package-internal compile errors (com.bovinelabs.core 2.0.0-pre.1: 222 errors against builtin Collections 6.7.0, no newer version published). Treat builtin-module drift as a first-class risk of any alpha pin; O-U2's rule is the guard, and the host project's repin is Appendix A.15.
7. `registry.openupm.com` was NXDOMAIN worldwide on 2026-10-18 (confirmed through two DoH resolvers and OpenUPM's own nameservers). The registry is served live at `package.openupm.com`, which the manifests use; a consumer-side scoped registry is therefore an install requirement, recorded in the README.
8. On the a8 alpha, a precompiled DLL whose `.meta` carries a PluginImporter block (defineConstraints, platformData) is silently excluded from the precompiled set; bare `.meta` files (same GUIDs) register fine. Keep plugin `.metas` bare until the coreclr variant needs real per-platform settings.
9. The gate harness mis-spawns children under `dotnet run` (Environment.ProcessPath is the apphost, so children fall through to the verify suite and the host string parses empty). Run gates against the built DLL — `taskset -c 0 dotnet benchmarks/bin/Release/net10.0/GiBenchmarks.dll --gate` — which is how the baseline was recorded. A harness fix (spawn via the dotnet host explicitly) is a queued chore.
10. The 6000.7 registry line has the same builtin wall as the Unity 7 alphas (A.15): builtin Collections is a shim into engine internals with a Unity-only InternalsVisibleTo list, and no standalone 6.x Collections exists on the public registry. Verify a dependency's internals usage against the *builtin* modules of the exact editor before pinning anything to a beta line.
11. Coordinator merge discipline: after resolving a conflicted merge locally, `git add` every file the fix touched before committing — an unstaged fix rides the working tree while the push goes out without it (caught by CI on #83; cost one extra CI cycle).
12. A dependency "wall" was declared after testing only the newest build on one registry. The BovineLabs build that matched the editor (pre.6, 2026-10-15) lived on the upstream GitLab `main` and its own registry, not on OpenUPM. Before refuting a pin, list every source of the dependency (registries, the upstream repository, local mirrors on the host) and read the target Unity version in its `package.json`. The refutation in A.15 was wrong for this reason.
13. A green exit with zero tests proves nothing: the first `validate.sh il2cpp` printed `passed: 0` with exit 0 because it only entered PlayMode in the editor and found no PlayMode tests. `validate.sh` now exits `2` when no tests ran, and the player suites use the editor's own `-testPlatform`.
14. The editor deletes `Temp/` at every launch. Keep launch-time shims in `Logs/`.
15. `set -o pipefail` plus an early-exiting `awk` in a pipeline turns the upstream command into SIGPIPE (exit 141) and aborts the script under `set -e`. Capture the producer's output in a variable first.
16. Unity 7 alphas: pin one exact alpha for the host project and the package (`unityRelease`), and bump them together. The package's `package.json` now says `"unity": "7000.0", "unityRelease": "0a9"` and depends on the builtin module versions (entities and collections 6.7.0, burst 2.0.0, mathematics 1.4.0).
17. Hand-written plugin `.meta` files with the old list-form `platformData` are ignored by Unity 7 without a message, so the DLL silently vanishes from the precompiled set (this was lesson 8). Let the `PluginImporter` API write them (`unity/project/Assets/Editor/GiPluginSetup.cs`).
18. A heading recovered from a quaternion (`atan2`) is a ulp off the angle it came from. Gi quantizes angles to 16 bits, so an angle that sits exactly on a quantization boundary can fall into the neighbouring cell. Scenario headings are therefore offset a quarter of a quantum (`GiScenarioOp.HeadingBiasRadians`); production lockstep code must supply headings from deterministic state instead of deriving them from rotations (plan §5.8).

### Session state (2026-10-18, handed off)

The previous agent's session ended; **`unity/HANDOFF.md` is the current state and the instructions for what to do next.** It lists the pull request stack (#86 to #92, all green), the checkpoint branch `unity/u-5-bindings`, the validation commands, the traps, and the order of the remaining cards.
