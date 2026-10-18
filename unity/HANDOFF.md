# Handoff: Gi for Unity Entities

Rewritten 2026-10-18 during the fourth session's wrap-up. Read this first, then `AGENTS.md` and `unity/ROADMAP.md` (§0, §1, §4, the board in §5, Appendix F). Everything is in the repository; nothing lives only in a chat.

## 1. What this project is

`src/Gi` is a standalone .NET library for sparse tiled integer influence fields. `unity/com.gi.entities` is a Unity Entities package that runs Gi inside ECS: entities emit influence, splat into transient layers, sense the field, and editor tools show it. The design is `unity/plan.md`; the execution board is `unity/ROADMAP.md` (it wins where the two disagree). Tracking issue: #77.

Rules that never change (`AGENTS.md`): Gi stays standalone and gains no Unity dependency; the public surface is `World`, `Grid`, `Layer`, `Stamp` only; production code has no explanatory comments; the warm path allocates 0 B; written files are deterministic and culture-independent. **Never merge a PR, create or move a tag, or push to `main` or `unity/main`.**

## 2. The owner's tooling decisions (O-U9 and after)

- Fast loop: `unity/compile.sh` (the editor compiles the whole host project, about 25 s, must print `errors: 0`), `unity/lint.sh`, and the dotnet tests.
- **`unity test` runs are allowed** (the owner said so): `unity/validate.sh editmode`, `playmode` and `perf` use it. Run them freely.
- **Player builds are for the very end**: `unity/validate.sh coreclr` and `il2cpp` build Linux players and are slow. The owner wants them last, once everything else is done.
- The `unity` CLI has no connected editor (the host project has no Pipeline package), so `unity recompile` and `unity status` do not work; `compile.sh` launches the editor itself.

## 3. State of the repository

### The pull-request stack (all unmerged; the owner merges in this order)

| PR | Card | Base |
|---|---|---|
| #86 U-0 · #87 F-2 · #88 V-0 (preview) · #89 U-1 · #90 U-2 · #91 U-3 · #92 U-6 | earlier sessions | stacked |
| #93 U-5 · #94 U-5b (Columns half) · #96 V-1 (Tier D half) · #97 P-1 (page table) | session 2 | stacked |
| #99 O-U9 compile tier · #101 U-4 · #102 U-5b (Unity half) · #103 V-2 · #104 V-1 (Unity half) | session 3 | stacked |
| #105 A-1 · #106 A-2 · #107 P-1 (GPU half) · #108 E-6 · #109 E-3 · #110 E-4 · #111 E-1 · #112 E-2 · #113 V-3 | session 3 | stacked |
| #114 D-1 (docs, samples) | session 4 | stacked |
| #117 V-3b (idle-tick quick wins) | session 4, after #114 | stacked |
| #115 jb sweep (12 test-only fixes) | session 4, into `unity/v-3-performance` | hygiene |
| #116 CI SDK pin (`unity.yml`) | session 4, into `unity/main` | separate lane |
| #95 G-6 · #98 G-7 · #100 G-4 | Gi core, into `main` | separate lane |

Nothing is merged. Merge order: #86 → #113 as numbered, #114, #117, then #115 (or fold it in when its base merges), #116, plus the Gi lane #95/#98/#100. When the branch line and #116 both land in `unity/main`, `unity.yml` hits an add/add conflict — keep #116's version (it carries the SDK isolation and guards) or the runner-image shadowing returns.

Fix commits found by the first real Unity run were merged **upward** (non-destructively, no force pushes). If you add a fix to a lower branch, merge that branch into the ones above it (`git merge --no-edit <lower>`), do not rebase.

### What is verified, and where

On the head of the stack (`unity/perf-idle-quickwins`, PR #117): everything below plus the V-3b receipts — dotnet 157 + 1198, `lint.sh` ok, `jb` 0, `compile.sh` `errors: 0`, EditMode 45, PlayMode 85, perf 6, `build-plugins.sh --check` ok; idle tick 29.4 → 15.4 us (19 → 7 jobs, L11 met; `perf.md` idle/L11 rows hand-marked, re-run `perf.sh` on the recording host in the final pass). On `unity/d-1-docs` (PR #114), in the editor (CoreCLR 7000.0.0a9): everything session 3 verified on `unity/v-3-performance` (EditMode 45, PlayMode 85, perf 6 — see the roadmap session state) plus the D-1 receipts: `compile.sh` `errors: 0` with the samples imported at `Assets/Samples/com.gi.entities/0.1.0`, the 4-test sample smoke green on the imported copies, `lint.sh` ok, dotnet 157 + 1198 pass, `jb` 0, `build-plugins.sh --check` ok.

**Not yet run anywhere: the Linux player suites (`coreclr`, `il2cpp`)**, and so the ledger rows that need them (L5, the player columns of `unity/perf.md`).

### Board

Done or in review: F-1, F-2, U-0, U-1, U-2, U-3, U-4, U-5, U-5b, U-6, V-1, V-2, V-3, A-1, A-2, P-1, E-1, E-2, E-3, E-4, E-6, D-1 (U-pending), G-0a, G-1 to G-7 (G-6, G-7, G-4 in review). Each row in `unity/ROADMAP.md` says which receipts ran.
Still open: the optional cards **E-5, E-7, N-1, N-2, S-1, G-8**, **V-0 flip** and **G-0** (blocked: Unity must enable the net10 API level; Appendix B), the perf follow-up cards from session 4's investigation (sense revision-gating, entity-level touched list, Gi-core incremental deposit, L3/L7 measurement rework — see the roadmap session state; the quick wins themselves landed as #117), and the final player pass.

## 4. What to do next, in order

1. **The final player pass** (the owner wants it last, and everything else on the critical path is done): `unity/validate.sh coreclr`, then `il2cpp`; then perf for players (set `GI_PERF_OUT`, run the perf suite in a player, merge with `GI_PERF_JSON=<file> unity/perf.sh none`). Clear every `U-pending` row on the board (D-1's two [U] items included: the README quick start from an empty project, and the samples running in the players — import them from the Package Manager first) and update Appendix A.
2. Session 4's perf investigation is condensed in the roadmap's fourth-session state. The three L11 quick wins **landed as #117** (idle 29.4 → 15.4 us). Still open from the investigation, each a card of its own when the owner wants it: the sense revision-gating card (L3's real cause), the entity-level touched list for the diff (L7, U-2 territory), the Gi-core incremental-deposit G-card (L7's re-bake half, from `main` with the full gate), and the measurement reworks (record L3 on an ECS frame-C scenario with workers; split L7 into apply-only vs full-tick at a stated density).
3. The owner's **ISystem directive**: new systems are ISystem, not SystemBase (the samples comply). Whether to convert the adapter's own systems — SystemBase today because Gi is called only from managed jobs, D-U3 — is the owner's decision; if they say yes, it is a card of its own touching every runtime system.
4. Optional cards if the owner still wants them, cheapest first: E-5 (explain cell and entity inspector), E-7 (flight recorder), then N-1/N-2/S-1/G-8. Ask the owner first; two ideas from them are on record: the roughly 1,700 engine-neutral lines in `Runtime/Columns` could become a reusable adapter package, and a thinner quick-start layer so using Gi in Unity takes a few lines.
5. Owner decisions O-U1 to O-U8 each have a default in the roadmap; use the default and say so in the PR.

## 5. Known gaps and honest caveats (do not hide these)

- Misses in the prediction ledger (see `unity/perf.md`): L3 (ECS time is 78% of Gi time on the probe scene) and L7 (14 ms for 10k+10k, predicted 1.5 ms) — both largely measurement-shape per the session-4 investigation, rework recorded as cards. L11 was met by #117 (15.4 us). L10 was measured at 200k sources, not 1M. L2 was not measured on frame C.
- E-2: the Scene overlay draws the footprints of every emitter in the Gi world (selected-GameObject-to-entity mapping is not wired) and the reach-disc toggle has no drawing.
- P-1: the heatmap is a compute kernel into a `RenderTexture`; the owner's screenshots in Built-in, URP and HDRP, and ledger row L8 in a real scene, are not done.
- E1 and E6 are pinned and pass (E6: the fixed-step group and the Gi group do run in an Editor world).
- The Gi plugin Unity loads is the scalar netstandard2.1 build, about 2.06x slower than the net10 vector build on B1. Recovering it needs Unity to enable the net10 API level (V-0/G-0).
- The owner directed (session 4): **new systems are ISystem, not SystemBase**. The samples comply; the adapter's systems are SystemBase by the D-U3 managed-jobs decision and were left alone.
- The samples' `.meta` files ship inside `Samples~` so scene-to-script references survive import; regenerate scenes only through an editor (a transient generator wrote them; it is not committed).

## 6. Traps (each cost time; most were found by actually running the tests)

- `PopupField<T>` rejects a null default; use the index constructor.
- `SystemBase` reading a singleton's data (`SystemAPI.GetSingleton`) does not complete the jobs that write it; read through `EntityManager.GetComponentData`, and register the types in `OnCreate`.
- A system that touches `GiFrame` (even `TryGetSingleton`) gets ordered by ECS against the Gi jobs; a "misordered system" test must use no ECS access at all.
- `ProfilerRecorder.LastValue` only updates at frame boundaries; a timing test must `yield return null` between frames. `ProfilerCounterValue` with `FlushOnEndOfFrame` is last-writer-wins across ECS worlds, so a world with no profile must not write counters.
- The editor's default ECS world also runs the Gi systems (with no profile); tests that read globals must not assume only their own world exists.
- Unity compiles Editor and package code against the .NET Standard 2.1 surface: no `System.Runtime.Intrinsics`, no interpolated-string handlers with `CultureInfo`; use reflection or `ToString("...", InvariantCulture)`.
- `Stamp` sizes are in world units, not grid cells; a raster carries a one-unit interpolation apron. `TrySenseNearest` returns "complete", not "found"; found is `value != short.MinValue`.
- The benchmark harness keeps read-mode flags in statics set after its receipts; reset them between calls in one process (`GiFrames.Run` does).
- dotnet's tiered JIT is bimodal on some frames (C ran 22 ms in one process and 9 ms in the next); compare medians of several processes, and run the gate from the built DLL: `taskset -c 0 dotnet benchmarks/bin/Release/net10.0/GiBenchmarks.dll --gate`.
- `pkill -f <pattern>` kills your own shell if the pattern is in your command line; kill by PID.
- Never edit files under `unity/com.gi.entities` while a Unity run is compiling; it reads the sources.
- Unity wipes `unity/project/Temp` on launch; keep helper files in `Logs/`.
- `[OnCodeLoaded]` needs a `partial` class; `CollectionHelper.CreateNativeArray` for `WorldUpdateAllocator`; add `using Unity.Jobs;` or `Schedule` resolves to the wrong overload; `IJobParallelForDefer` needs the list as a job-struct field; zero-sized components have `TypeInfo.IsZeroSized`.
- On this Entities build `Baker<T>` needs an `Unity.Entities.Hybrid` asmdef reference, and `SubScene` lives in `Unity.Scenes` (namespace and assembly) with its GUID wired through the editor-only `SceneAsset` property.
- `state.WorldUpdateAllocator` is already an `Allocator` (no `.ToAllocator()`), and `NativeThreadStream.Writer` is not `IDisposable` — write and drop it.
- Inside `namespace Gi.Entities.*` the bare `World`/`Grid`/`Layer`/`Stamp` are Gi's handles (R-5): write `Unity.Entities.World.DefaultGameObjectInjectionWorld`, not `World.DefaultGameObjectInjectionWorld`, in samples and tests too.
- Batch play-mode pumps frames at about 2000 fps, so waiting N frames waits almost no time: wait on `GiFrame.Tick` with a wall-clock timeout — and capture the frame's `Generation` first, because a stale frame from the previous scene satisfies any tick threshold. Scene-based tests also need `EditorBuildSettings.scenes` set (Unity 7 refuses `LoadSceneAsync` otherwise), a TearDown that destroys runtime-spawned entities and leftover baked emitters, and rebuilds of the default world break editor subscene hooks — clean entities instead.
- A `ComponentLookup` used for enabled-bit reads in a parallel `IJobEntity` must be declared `[ReadOnly]` or the job scheduled serially; the safety system calls enabled-bit access a write.

## 7. How to check your work

```sh
unity/lint.sh                                                   # lint: ok
dotnet test unity/tests/Gi.Entities.Columns.Tests -c Release    # Unity-free tests
dotnet build unity/tests/Gi.Entities.Columns.Compat -c Release  # C# 9 / netstandard2.1 compile check
unity/build-plugins.sh --check                                  # plugin DLLs, Gi.Corpus.dll, GiBenchmarks.dll match their sources
unity/compile.sh                                                # errors: 0
unity/validate.sh editmode                                      # then playmode, perf
jb inspectcode Gi.slnx --severity=WARNING --build --output=/tmp/inspect.sarif --format=Sarif   # 0 findings
```

`validate.sh` prints `passed:` / `failed:`; it exits 2 if no test ran. A card is `done` only when every receipt in its row has run and `jb` is clean.

## 8. Method for every card

Engine-neutral logic first, in `unity/com.gi.entities/Runtime/Columns/` (no `Unity` namespaces; `lint.sh` enforces this), proven by dotnet tests against real Gi calls or the reference oracle; then the Unity system, then tests that run in the editor; then the `docs/model.md` heading if the card has unsafe code; board row; PR with receipts first, what was not run, and any deviation. One card per branch and per PR, stacked on the previous one.

## 9. Do not

- merge a PR, create or move a tag, or push to `main` or `unity/main`;
- change `src/Gi` for a Unity card (only G-cards do, from `main`, with the full AGENTS.md validation including the pinned gate);
- leave debug code, probe tests or commented-out code in a commit;
- mark a card done while any receipt in its row has not run, or while `jb` or `lint.sh` fails;
- reopen decisions D-U1 to D-U10 or the numbered deviations in Appendix A without asking the owner.
