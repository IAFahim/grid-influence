# Handoff: Gi for Unity Entities

Rewritten 2026-10-18 at the end of the fifth session. Read this first, then `AGENTS.md` and `unity/ROADMAP.md` (§0, §1, §4, the board in §5, Appendix F). Everything is in the repository; nothing lives only in a chat.

## 1. What this project is

`src/Gi` is a standalone .NET library for sparse tiled integer influence fields. `unity/com.gi.entities` is a Unity Entities package that runs Gi inside ECS: entities emit influence, splat into transient layers, sense the field, and editor tools show it. The design is `unity/plan.md`; the execution board is `unity/ROADMAP.md` (it wins where the two disagree). Tracking issue: #77.

Rules that never change (`AGENTS.md`): Gi stays standalone and gains no Unity dependency; the public surface is `World`, `Grid`, `Layer`, `Stamp` only; production code has no explanatory comments; the warm path allocates 0 B; written files are deterministic and culture-independent. **Never merge a PR, create or move a tag, or push to `main` or `unity/main`.**

## 2. The owner's tooling decisions (O-U9 and after)

- Fast loop: `unity/compile.sh` (the editor compiles the whole host project, about 25 s, must print `errors: 0`), `unity/lint.sh`, and the dotnet tests.
- **`unity test` runs are allowed** (the owner said so): `unity/validate.sh editmode`, `playmode` and `perf` use it. Run them freely.
- **Player builds are dropped.** The owner said "we don't care of build" (2026-10-18): do not run `unity/validate.sh coreclr` or `il2cpp`. The rows that need a player (L5, the player perf columns, "samples on IL2CPP") are closed as dropped.
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
| #95 G-6 · #98 G-7 · #100 G-4 | Gi core, into `main` | separate lane |

`hygiene/jb-sweep-2026-10-18` (session 4's below-WARNING jb sweep, 12 test-only fixes) is pushed with no PR; the owner merges it or drops it.

Fix commits found by the first real Unity run were merged **upward** (non-destructively, no force pushes). If you add a fix to a lower branch, merge that branch into the ones above it (`git merge --no-edit <lower>`), do not rebase.

### What is verified, and where

On `unity/fold-prototype-cards` (my stack plus the parallel line, all four merges conflict-resolved): Columns 193, EditMode 79 (84 with netcode embedded), PlayMode 91, perf 6, `lint.sh` ok, `compile.sh` `errors: 0`, `quickstart.sh` ok. On the head of my own stack alone (`unity/v-3c-million`), in the editor (CoreCLR 7000.0.0a9, a real GPU): Columns 192, EditMode 72 (77 with `unity/netcode-setup.sh install`, which adds the netcode tests), PlayMode 85, perf 6, `jb` 0, `lint.sh` ok, Compat build ok, `build-plugins.sh --check` ok, `unity/quickstart.sh` ok (the README quick start from a clean project with only the two pins), `unity/export-check.sh` ok (an exported recorder range runs unedited in `tests/Gi.Tests`).

**Not run, and not wanted:** the Linux player suites (`coreclr`, `il2cpp`). The owner dropped them, so L5 and the player perf columns are closed as dropped.

### Board

Done or in review: every card (including A-3 #122, E-9 #123 and E-8 #124 from the owner's prototype, built by the parallel session) except V-0's flip and G-0 (blocked: Unity must enable the net10 API level; rechecked on 2026-10-19, 7000.0.0a10 is the newest alpha and unchanged, Appendix B). G-8 is closed without code because its gate is not met. G-9 (column batch entry points) is deferred until the L3 measurement rework (owner decision 2026-10-18), and the parallel session recorded small cards: a sensor gizmo follow-up, validating that `Coverage`/`Changed` fields name enableable components (the Burst scatter is UB otherwise), and a `Coverage` ref on a value-carrying component that parks entities out of the sense query. Each row in `unity/ROADMAP.md` says which receipts ran.

## 4. What to do next, in order

1. **Merge order.** #86 to #113, #114, #117, #118 as before. After that there are two lines that both touch ROADMAP, HANDOFF, `docs/model.md`, `.gitignore`, `GiDebugConfig` and the Presentation asmdef: the parallel session's (#119, then #122/#123/#124) and mine (#120, #121, #125, #126, V-3c). `unity/fold-prototype-cards` has the conflicts already resolved and the combined result verified; merge my line, then the fold PR, and the parallel PRs (#119, #122-124) become redundant (their commits arrive through the fold). Merging the parallel line first also works, but then my line conflicts in those files.
2. **Wait for the owner to merge** the stack in the order of the table above (the Gi lane #95, #98, #100 into `main` too), then merge `unity/main` upward only if asked. Do not start new cards on top of an unmerged stack unless the owner says so.
3. **Re-check V-0/G-0** whenever a newer Unity 7 alpha appears (`https://services.api.unity.com/unity/editor/release/v1/releases?version=7000`): if `ApiCompatibilityLevel.NET` is enabled, follow Appendix B item 11 and rerun V-1, V-2, V-3.
4. The owner's **ISystem directive**: new systems are ISystem (all of this session's are). Converting the adapter's SystemBase systems (`GiSenseSystem` holds managed binding tables, `GiCountersSystem`, `GiInspectorSystem`) is the owner's decision under D-U3; it is a card of its own.
5. Optional perf cards that need the owner's pick, none on the board: sense revision-gating (L3's real cause), an entity-level touched list for the reconcile diff (L7), a Gi-core incremental deposit (L7's re-bake half, from `main` with the full gate), and L2 on frame C with workers. Two ideas from the owner are on record: the roughly 1,700 engine-neutral lines in `Runtime/Columns` could become a reusable adapter package, and a thinner quick-start layer.
6. Owner decisions O-U1 to O-U8 each have a default in the roadmap; use the default and say so in the PR.

## 5. Known gaps and honest caveats (do not hide these)

- Misses in the prediction ledger (`unity/perf.md`): L3 (ECS time is 77-78% of Gi time on the probe scene, which is not frame C), L7 (14.14 ms for 10k+10k at 1M sources, measured, predicted 1.5 ms) and L10 (710 ms at 1M, predicted 250 ms). L11 was met by V-3b (15.4 us). L2 was not measured on frame C.
- P-1: the heatmap is a compute kernel into a `RenderTexture`; screenshots in Built-in, URP and HDRP, and ledger row L8 in a real scene, were not taken. No by-eye look at the new windows (Field, Explain, Flight Recorder, Stamp Studio, Profile editor) or at the reach discs.
- Netcode: the package is not in the committed manifest. The 7000.0.0a9 alpha cannot compile one file of `com.unity.netcode` (6.7.0, 7.0.0, 7.1.0 alike), so `unity/netcode-setup.sh install` embeds 6.7.0 with that class removed; without it the Gi netcode assemblies and tests are simply absent. `unity/netcode-setup.sh remove` restores `packages-lock.json`; never commit the lock while the package is embedded. Appendix A 37 lists the other netcode deviations.
- E-7: a recorder started mid-run cannot know pulses in flight or the transient layers' contents, and says so; the export cannot hold raster stamps or single-input `Sum` layers.
- S-1: the Savanna save system is not in the pinned BovineLabs Core (only its attributes); the tests copy the saved components by hand. `GiSave.Prepare` and `Release` bracket the game's save.
- E-8 (#124): `com.bovinelabs.quill`/`anchor` resolve through the public vex-studio tertle-monorepo mirror (`eb83b43`) — the only anonymously resolvable source and the same one the owner's own test project uses, but the published manifest now depends on a third-party mirror of subscriber software; supply-chain, availability and licensing are the owner's to bless before this package ships publicly. Quill's `QuillSettings` asset must be included in players or Quill crashes `Object.Instantiate(null)` — add it to the final player pass. One allocation test was flaky once under an interactive graphics session (background GC); green in all batch runs.
- The Gi plugin Unity loads is the scalar netstandard2.1 build, about 2.06x slower than the net10 vector build on B1. Recovering it needs Unity to enable the net10 API level (V-0/G-0).
- The samples' `.meta` files ship inside `Samples~` so scene-to-script references survive import; regenerate scenes only through an editor.

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
- Unity 7000.0.0a9 deprecates `Object.GetInstanceID()` and `Selection.Contains(int)` **as errors**: identity goes through `UnityEngine.EntityId` (`Object.GetEntityId`, `Selection.Contains(EntityId)`), and `ObjectChangeEventStream` uses the new getter names with `entityId`/`EntityId` args (`GetChangeGameObjectOrComponentPropertiesEvent`, `UpdatePrefabInstances` exposing `entityIds` as `NativeArray<EntityId>.ReadOnly`). Every editor card on this build hits this (found by E-9, #123).
- A lambda that captures a local makes C# allocate its closure at method entry, so a per-tick `OnUpdate` that merely contains one allocates every tick. Keep such code in a separate method taken only when needed.
- `NetCodeTestWorld.Bootstrap` takes an explicit system list: include `GiSystemGroup`, every Gi system and the generated `*RpcCommandRequestSystem`, or the Gi code does not run and a test comparing two empty worlds passes. Assert `GiFrame.Alive` and live-source counts, not only hashes. Create the ghost collection before `CreateWorlds`.
- `Assert.Fail` in `NetCodeTestWorld.Dispose` hides the original exception; wrap the test body and log it.
- `Turn` truncates its angle, and `TryGetSourceSpec` returns the quantum's lower edge, so a restored heading needs `+ pi / 65536`.
- A restore that re-drives a fade must drop its entry when the owner's applied gain changes: a new `Fade` onto the current gain leaves no trace in Gi.
- Adding a `GiFaultKind` means bumping `GiFaults.KindCount`.
- `EntityManager.GetComponentDataRawRO`, `BakingUtility`, `EntitySelectionProxy` and `PropertyInspector<T>` are internal to the Entities packages; the code reaches them by reflection (`GiEntitySelection`, the test `GiBaking`) or through public handles (`GiBindingRead`).


## 7. How to check your work

```sh
unity/lint.sh                                                   # lint: ok
dotnet test unity/tests/Gi.Entities.Columns.Tests -c Release    # Unity-free tests
dotnet build unity/tests/Gi.Entities.Columns.Compat -c Release  # C# 9 / netstandard2.1 compile check
unity/build-plugins.sh --check                                  # plugin DLLs, Gi.Corpus.dll, GiBenchmarks.dll match their sources
unity/compile.sh                                                # errors: 0
unity/validate.sh editmode                                      # then playmode, perf
unity/quickstart.sh                                             # README quick start from a clean project
unity/export-check.sh                                           # an exported recorder range runs in tests/Gi.Tests
unity/netcode-setup.sh install                                  # optional: embed netcode, then validate.sh editmode runs its tests; remove when done
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
