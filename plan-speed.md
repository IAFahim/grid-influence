# Gi speed plan: vector speed inside Unity now, Burst-ready core next

Status: plan, written 2026-11-03 against `main` @ 9814f8d. Card IDs are `Jnn` so they never collide with the IDs in `ROADMAP.md` (P0, P1, V2, R, T, C), `unity/ROADMAP.md` (F, U, A, V, P, E, N, S, D, G, L1–L11) or `unity/plan.md`. Facts are `Yn` (verified, evidence given) and hypotheses are `Hn` (a card settles each one). No PR may cite an `H` as a fact.

This file adds work; it does not replace the existing documents. `AGENTS.md` rules apply to every card unchanged: one API (`World`, `Grid`, `Layer`, `Stamp`), no explanatory comments in production code, 0 B on warm paths with and without intrinsics, correctness receipts before timing, deterministic and culture-independent written files. `unity/HANDOFF.md` §6 (traps) and §9 (do not) apply to every Unity card.

## 0. TL;DR

- **Ready, with one fix.** On this host the dotnet side is green on `main` @ 9814f8d (Y1). The Unity side could not build at all (Y2–Y4): the project pins `7000.0.0a9`, only `7000.0.0a12` is installed, and on a12 the pinned BovineLabs Core fails with 120 errors because Unity moved the Entities internals into the engine. **J00** bumps the editor to a12, BovineLabs Core to the upstream a12 commit, and adds the Audio module that a12 Entities no longer pulls in. With J00, `unity/compile.sh` prints `errors: 0`, EditMode passes 80/0, PlayMode 91/0 and perf 6/0 (Y5, Y17). J00 sits uncommitted in the working tree on the local branch `chore/unity-a12`.
- **The biggest lever is a build step, not a code port (J01).** Unity still cannot reference a net10 assembly: the net10 API level is disabled in a12 too (Y6), and the net10 `Gi.dll` fails with CS1705. The reason is narrow: the net10 build references five framework assemblies at version 10.0.0.0, and Unity compiles against netstandard shims at version 4.1.2.0 (Y7, Y8). Rewriting those references fixes it. Each type reference that exists in netstandard 2.1 moves there (53 of them), and the 18 that don't (`Vector128/256`, `Avx2`, `Sse41`, `AdvSimd`, `BitOperations`, `NativeMemory`, compiler attributes) keep their assembly at a lower version. The rewritten DLL then compiles the whole engine-neutral adapter at C# 9 against Unity's exact reference set with 0 errors (Y9). On .NET 10 it runs the vector paths with the same checksums as the scalar build on B1, C and W, and is 2.3x (B1), 8.3x (C) and 2.4x (W) faster in a rough unpinned run at 20k entities (Y10).
  - **The real a12 editor accepts it too** (Y18). With the rewritten DLL dropped in place of the plugin, `compile.sh` gives `errors: 0`, `burst: 0`; EditMode passes 80/0 and perf 6/0. The editor's own perf suite at 200k entities runs B1 178 → 112 ms (1.6x), C 9.9 → 2.0 ms (5.0x) and W 17.8 → 7.2 ms (2.5x). Each is a single run on a busy host; J0a and J03 take medians.
  - **One open issue** (Y19). PlayMode was 90/1: the field-draw "100 steady frames allocate nothing" test fails 4 of 4 times with the vector DLL. It also fails 1 of 3 times with the scalar DLL on a12, so it is flaky either way, but the vector DLL makes it much worse. J01 must find the allocation before the variant ships.
  - This recovers the B1 gap that `unity/perf.md` says "only V-0/G-0 recovers", without waiting for Unity to enable net10.
- **After J01, every remaining Unity cost is ECS-side or player-side:**
  - **ECS-side.** The three ledger misses are L3 (ECS time is 78% of Gi time), L7 (14.14 ms for 10k+10k commands at 1M sources) and L10 (710 ms profile edit at 1M). Wave 2b (J30–J35) attacks them with the owner's own queued ideas: sense revision-gating, an entity-level touched list, incremental deposit and incremental profile rebuild.
  - **Player-side.** IL2CPP has no usable intrinsics (Y16), so phones and consoles run the scalar netstandard build. The only acceleration there is Burst.
- **Burst needs the core to be reachable, and the gap is small.** All five Gi-calling jobs run as managed (non-Burst) jobs today (Y11). The known blocker is that Gi keeps its state in 11 mutable static fields (Y12), and Burst cannot load mutable statics. J20 looks for any others (H4). Wave 3 (J20–J24) moves them into one `GiRoot` reached by a single pointer, adds root-taking internal entry points for the six calls the adapter makes, and Burst-compiles the jobs. The public API does not change. Whether Burst can also compile the cross-platform `Vector128/256` code is H3, a day-one spike shared with needle-cs.
- **What does not move:** Gi's `Process` stays single-threaded (ROADMAP D7). Parallel `Process` is an owner-gated spike (J50), not a card.
- **Order:**
  1. J00 lands.
  2. J01 → J02 → J03: the vector build inside Unity, with receipts.
  3. In parallel: J30–J35 (ECS costs), J10–J12 (core quick wins), and the J20 Burst spike.
  4. J21 → J22 → J23: Burst-reachable core, then Burst jobs.
  5. J24: portable vector path, depending on J20.

## 1. Facts and hypotheses

| ID | Fact | Evidence (2026-11-03, i9-14900K, Linux, dotnet SDK 10.0.401, runtime 10.0.12) |
|---|---|---|
| Y1 | dotnet validation is green on `main` @ 9814f8d | `dotnet build Gi.slnx -c Release -m:1` ok; `dotnet test`: Gi.Tests 1312/0, Gi.Entities.Columns.Tests 196/0; `samples/world` ok; `--verify` 144/0; `DOTNET_EnableHWIntrinsic=0 --verify` 127/0; `Gi.Entities.Columns.Compat` (C# 9) 0 errors; `unity/build-plugins.sh --check` identical; `unity/lint.sh` ok (215 files) |
| Y2 | Only 7000.0.0a12 is installed; the project pins 7000.0.0a9 | `~/Unity/Hub/Editor/` holds `7000.0.0a12` only; `ProjectVersion.txt` says a9, so `compile.sh` stops at "editor not found" unless `UNITY_EDITOR` is set |
| Y3 | a12 breaks BovineLabs Core 82be279 | `UNITY_EDITOR=…a12 unity/compile.sh`: 120 errors, all inside `com.bovinelabs.core` (CS0122 ×117 for `EntityDataAccess`, `BufferHeader`, `ChunkIndex`…, CS8337 ×3). a12's `com.unity.entities` source no longer defines those types (`EntitiesILPostProcessor` fetches `Unity.Entities.BufferHeader` from the engine), so the `Unity.Entities.asmref` IVT injection reaches nothing. Upstream Core `dccb0b5` (2.0.0-pre.7, 2026-10-31) declares `unityRelease 0a12` and carries "Support Unity 7 a11 Entities internals" (39fc95e) and "Expose Unity 7 engine internals for dependent packages" (ebe4a14) |
| Y4 | a12 Entities no longer depends on `com.unity.modules.audio` | the a12 lock drops it; `com.bovinelabs.anchor` @ eb83b43 then fails with 54 × CS1069 (`AudioClip`, `AudioSource` "forwarded to UnityEngine.AudioModule") |
| Y5 | With J00, the host project compiles on a12 | `unity/compile.sh`: `errors: 0`, `burst: 0`, `unity: 7000.0.0a12` |
| Y6 | a12 still disables the net10 API level | `UnityEditor.CoreModule.dll` (a12) contains "The .NET (net10) API compatibility level is still a work in progress and is disabled for now." (and the same for editor assemblies); unchanged since a9/a10 (`unity/ROADMAP.md` Appendix B item 3) |
| Y7 | The net10 `Gi.dll` references exactly five assemblies, all 10.0.0.0 | `monodis --assemblyref`: System.Runtime, System.Runtime.Intrinsics, System.Memory, System.Threading, System.Runtime.InteropServices. The netstandard2.1 build references only `netstandard 2.1.0.0` |
| Y8 | Unity compiles scripts against netstandard 2.1 plus 4.x shims, with no System.Runtime.Intrinsics | `Data/NetStandard/ref/2.1.0/netstandard.dll` + `Data/NetStandard/compat/2.1.0/shims/netstandard/*.dll` (System.Runtime 4.1.2.0); no `System.Runtime.Intrinsics.dll` in either folder |
| Y9 | The retargeted net10 `Gi.dll` compiles Unity-style | Spike tool: Cecil, 53 type refs moved to `netstandard`, 18 kept (System.Runtime: `BitOperations` and five compiler attributes; System.Runtime.InteropServices: `NativeMemory`; System.Runtime.Intrinsics: `Vector64/128/256`, `X86.Sse/Sse2/Sse41/Avx/Avx2`, `Arm.AdvSimd`), versions lowered to 4.1.2.0 / 4.1.2.0 / 4.0.0.0, `TargetFramework` set to .NETStandard 2.1. `csc -langversion:9 -nostdlib` with Unity's refs over `Runtime/Columns/**` + `Tests/Support/**`: **0 errors**. The unmodified net10 DLL in the same command: CS1705 (`System.Runtime 10.0.0.0`) + 2 × CS1503 |
| Y10 | The retargeted DLL runs the vector paths on .NET 10 with identical output | `Gi.Frames.Runner` (the netstandard2.1 harness) with only `Gi.dll` swapped: receipts green; checksums equal: B1 exact `6782ae93471d0dd1`, C cell transient fused `3acb24c8a236bce8`, W cell `eda5d2665ddfdbc5`. p50 at 20,000 entities, 5 frames, **unpinned on a loaded host**: B1 98.7 → 42.8 ms, C 22.3 → 2.69 ms, W 7.86 → 3.27 ms. Indicative only; J01 owns the real numbers |
| Y11 | Every Gi call in the adapter runs in a managed job | `GiProcessSystem.ProcessJob : IJob` (`World.Process`), `GiApplyJob : IJob` (`ApplyBatch`), `GiSplatApplyJob : IJob` (`Splat`), `GiSyncApplyJob : IJob` (`SyncTransforms`), `GiSenseReadJob : IJobParallelForDefer` (`Sense*Column`, `ChangedMask`); the 21 `[BurstCompile]` jobs in `Runtime/` only gather and scatter ECS data or reset state |
| Y12 | Gi has 11 mutable static fields | `Runtime.Worlds/Stamps/ZeroDense/WorldRevisions/_revisionFloor`, `NativeHeap.Calls/HookAllocate/HookFree`, `ProcessStages.Measuring`, `StampCatalog.Count`, `World._worldCount` |
| Y13 | netstandard `NativeHeap.AlignedAlloc` is not aligned | the `#else` branch calls `Marshal.AllocHGlobal` (16-byte malloc alignment); net10 uses `NativeMemory.AlignedAlloc(bytes, 64)`. The adapter never calls `World.SetAllocator` |
| Y14 | 81 `#if NET` sites in 21 files | TileBake 15, Histogram 7, Column.Gradient 7, TilePublish 6, Column.Field 6, World 5, TileSummaries 5, Sense 5, FusedBox 5, then 12 files with 1–3 |
| Y15 | Standing ledger misses | `unity/perf.md` (a9, editor CoreCLR, scalar plugin): L3 78% ECS share; L7 14.14 ms for 10k+10k at 1M (0.707 µs per command); L10 710 ms at 1M; B1 2.06x the dotnet net10 build |
| Y16 | IL2CPP has no usable intrinsics | `unity/ROADMAP.md` Appendix B item 4 (a9): `Avx2` type missing, `Vector128.IsHardwareAccelerated` false, `Sse41`/`Popcnt` false |
| Y17 | The a12 suites pass with J00 and the scalar plugin | `unity/validate.sh editmode` 80/0 (35.5 s), `playmode` 91/0, `perf` 6/0. a12 editor perf at 200k (one run, busy host): B1 178.2 ms, C 9.90 ms, W 17.8 ms, idle 20.0 µs, reconcile 0.876 µs per command (17.5 ms for 10k+10k), profile edit 155 ms, ECS share of Gi time (serial) 87% |
| Y18 | The a12 editor compiles and runs the retargeted net10 `Gi.dll` | Y9's file copied over `Plugins/Gi/netstandard2.1/Gi.dll` (same meta), then restored: `compile.sh` `errors: 0`, `burst: 0`, no "Unable to resolve reference" lines; `unity test` EditMode 80/0, perf 6/0. Editor perf at 200k, one run each, the same session as Y17: B1 178.2 → 111.9 ms, C 9.90 → 1.99 ms, W 17.8 → 7.21 ms, mirror 512 tiles 0.023 → 0.008 ms. ECS-side rows did not move (reconcile 0.876 → 0.919 µs per command, profile edit 155 → 172 ms, idle 20.0 → 23.9 µs: noise at one run, and not Gi time) |
| Y19 | `GiFieldDrawTests.OneHundredSteadyFramesWithTheDrawActiveAllocateNothing` is flaky on a12 and worse on the vector DLL | It measures the process-wide `GC.GetTotalAllocatedBytes(true)` and needs 5 zero windows in a row out of 16. Scalar: passed in the full PlayMode run, then 2 passes and 1 failure in 3 isolated runs. Vector: failed in the full run (PlayMode 90/1) and in 3 of 3 isolated runs. The other allocation suites (V-2) pass on both |

| ID | Hypothesis | Settled by |
|---|---|---|
| H2 | Burst compiles the existing `[BurstCompile]` jobs that only *name* Gi types against the retargeted DLL | **settled TRUE.** `burst: 0` in every `compile.sh` with the coreclr variant (J01, J02, J23); J23's five Burst jobs compile, SYNC Burst-clean on both variants (H5) |
| H8 | ~~The Y19 failures on the vector DLL come from an allocation inside Gi's vector paths~~ → refuted: main-thread per-thread delta is 0 in all 16 windows; the process-total jitter (0–600 B) is background-thread noise | **settled REFUTED by J01.** Fix shipped: per-thread `SteadyThreadZero` replaces the process-total assert; no production-code change (draw path was already main-thread clean) |
| H3 | Burst 2.0 (built-in, a12) compiles IL that uses the cross-platform `System.Runtime.Intrinsics.Vector128/256` API into SIMD | **settled TRUE both ways by J20.** Portable: `vmovdqu` loads, `vpcmpeqw`, per-element `vpextrw` gather. Coreclr: `vmovups`, `vpcmpeqw`, `vpshufb`+`vpmovmskb`. (Shared with needle-cs U01) |
| H4 | ~~After J21/J22, the six adapter-called paths contain no other Burst blockers~~ → refuted as stated | **settled FALSE-as-stated by J23/H5.** 1/5 jobs Burst-clean (SYNC); PROCESS blocked on `Runtime.Root` via `PlaneFold` static `Take` + APPLY on `typeof` over `SourceCommand` (src statics, J23Follow threaded the warm closures but these two remain), SPLAT+SENSE on Burst 2.0 `Vector256._lower` compiler bug (toolchain, not src) |
| H5 | Burst-compiled scalar Gi (LLVM auto-vectorization) is ≤ 1.5x the JIT vector build on frames C and W | **settled PARTIAL by H5 re-measure.** Sync.Apply portable-Burst 26.96 vs coreclr-JIT 30.74 = **0.88x → pick Burst** (`GI_BURST_SYNC`); other four jobs BLOCKED (see H4), stay managed |
| H6 | ~~The ECS share (L3) is dominated by per-entity gather/scatter that revision-gating skips~~ → refuted as stated | **settled REFUTED-as-stated by J30/J31/J32.** Gate engages (byte-equal) but serial L3 reads 189% (J30) → 177% (J31+J32 tip): L3-as-a-share-of-5x-smaller-vector-Gi-time is structurally unhittable in an all-live probe scene; re-plan the target or the scene |
| H7 | The same retarget fixes needle-cs V11 (the identical CS1705 on `Needle.Inference.dll`) | a needle-cs card using J01's tool; recorded here only as a pointer |

## 2. Target architecture

```
com.gi.entities/Plugins/Gi/
 ├─ coreclr/Gi.dll, Gi.Validation.dll    net10.0 build, references retargeted by J01
 │                                       define constraint ENABLE_CORECLR  (editor, desktop CoreCLR players)
 └─ portable/Gi.dll, Gi.Validation.dll   netstandard2.1 build (today's file)
                                         define constraint !ENABLE_CORECLR (IL2CPP players)

src/Gi (one source tree, unchanged public surface)
 ├─ World / Grid / Layer / Stamp          public API, reads Runtime.Root once per call
 └─ internal root entries (J22)           Process(GiRoot*, …), ApplyBatch(GiRoot*, …), SyncTransforms, Splat,
                                          Sense*Column, ChangedMask: no static access, Burst-reachable
                                          visible to Gi.Entities through InternalsVisibleTo (OJ1)

com.gi.entities/Runtime (J23)
 └─ ProcessJob, GiApplyJob, GiSplatApplyJob, GiSyncApplyJob, GiSenseReadJob
        [BurstCompile] on the portable variant; the coreclr variant keeps whichever of JIT-vector and Burst
        J23 measures faster per job (a define, not a runtime switch)
```

Rules:

- **One source tree, two IL binaries, the same bytes out.** The corpus, `--verify` and the frame checksums already require the vector and scalar paths to publish identical pages (Y10 confirms this through the retargeted file). Every variant inherits that receipt.
- **The retarget is a deterministic build step**, not a hand edit. It lives in `build-plugins.sh`, so `--check` keeps proving that the committed DLLs match the sources.
- **No Unity dependency in `src/Gi`** (AGENTS.md). The Burst work only removes statics and adds internal entry points. Anything that names `Unity.*` lives in `com.gi.entities`.

## 3. Targets

| Where | Measured today | Target | Card |
|---|---|---|---|
| Editor CoreCLR, frames B1 / C / W at 200k (`unity/perf.sh`) | a9 scalar: 156.37 / 9.00 / 13.72 ms. a12, one run each: scalar 178.2 / 9.90 / 17.8; retargeted vector 111.9 / 1.99 / 7.21 (Y17, Y18) | ≤ 1.10x the dotnet net10 row on the same host and the same run | J01, J03 |
| L3: ECS share of Gi time, probe scene, workers off | 78% (a9); 87% scalar and 79% vector on a12, one run | ≤ 30% after J30, ≤ 12% after J31–J32 | J30–J32 |
| L7: reconcile 10k places + 10k removes at 1M | 14.14 ms (a9, measured at 1M); 17.5 ms extrapolated from 200k on a12 | ≤ 4 ms after J33, ≤ 1.5 ms after J34 | J33, J34 |
| L10: profile edit at 1M sources | 710 ms (a9); 155 ms at 200k on a12 | ≤ 250 ms | J35 |
| L11: idle tick | 15.4 µs (a9); 20.0 µs on a12, one run on a busy host | ≤ 17 µs, measured as a median | every Unity card |
| Warm managed allocation per tick | 0 B (V-2) | 0 B on both plugin variants and on every Burst job | every card |
| Determinism | page checksums equal across intrinsic legs | equal across coreclr, portable and Burst jobs | J02, J23 |
| IL2CPP player, frame C | unmeasured (players dropped 2026-10-18) | ≤ 1.5x the editor CoreCLR row, with Burst jobs | J25 (owner-gated, OJ2) |

## 4. Cards

Sizes: S ≤ 1 agent-day, M 2–4 days, L 1–2 weeks. "Base" is the branch to cut from (OJ6). Core cards (`src/Gi`) run the full AGENTS.md validation plus the pinned gate (`taskset -c 0 dotnet benchmarks/bin/Release/net10.0/GiBenchmarks.dll --gate`). Unity cards run `unity/compile.sh`, `unity/lint.sh`, the dotnet tests, and `unity/validate.sh editmode` and `playmode`; `perf` runs when a number moves.

### Wave 0: readiness

| ID | Work | Owns | Done when | Size |
|---|---|---|---|---|
| J00 | **a12 bump.** `ProjectVersion.txt` → 7000.0.0a12 (8751f7c83ed7); BovineLabs Core → `dccb0b5` (2.0.0-pre.7) in `manifest.json`, `packages-lock.json` and `package.json`; add `com.unity.modules.audio`; `package.json` `unityRelease` → `0a12` | `unity/project/ProjectSettings/ProjectVersion.txt`, `unity/project/Packages/*`, `unity/com.gi.entities/package.json` | `compile.sh` `errors: 0` (Y5, done); `validate.sh editmode` and `playmode` green; `quickstart.sh` ok; HANDOFF §2 and `unity/ROADMAP.md` Appendix F name a12. **Status: changes in the working tree on `chore/unity-a12`, not committed. Done: compile, editmode, playmode, perf (Y5, Y17). Left: `quickstart.sh`, the doc lines** | S |
| J0a | **a12 baselines.** `unity/perf.sh` on a12 with the scalar plugin, so J01 compares against the same editor; refresh `perf-baseline.json` and `perf.md` | `unity/perf*.json`, `unity/perf.md` | the table regenerates on a quiet host (close other editors first: Y17 was one run beside a second editor); L1–L11 rows restated for a12 | S |

### Wave 1: the vector build inside Unity (critical path)

| ID | Work | Owns | Done when | Size |
|---|---|---|---|---|
| J01 | **Retarget step.** New tool project `unity/tests/Gi.Retarget` (net10 console, Mono.Cecil from NuGet, so CI needs no Unity install), wired into `build-plugins.sh` as variant `coreclr:net10.0`. It moves every top-level type reference whose full name netstandard 2.1 defines to `netstandard 2.1.0.0`, lowers the remaining framework references to a fixed table (System.Runtime 4.1.2.0, System.Runtime.InteropServices 4.1.2.0, System.Runtime.Intrinsics 4.0.0.0, System.Memory and System.Threading only if still used), drops unused references, rewrites `TargetFrameworkAttribute`, and writes with a deterministic MVID. `GiPluginSetup` writes both importers: `coreclr` with `ENABLE_CORECLR`, `portable` with `!ENABLE_CORECLR`, and `validateReferences: 0` if the importer flags System.Runtime.Intrinsics. The spike tool (Appendix A, 63 lines) already passed Y9, Y10 and Y18; J01 turns it into a product, it does not re-prove the idea. **J01 also owns Y19/H8**: the field-draw steady-zero test must pass 5 of 5 isolated runs on the `coreclr` variant, or the allocation's source must be shown to be outside Gi and the test's counter narrowed (that test change needs the owner's OK) | `unity/tests/Gi.Retarget/**`, `unity/build-plugins.sh`, `unity/com.gi.entities/Plugins/Gi/**`, `unity/project/Assets/Editor/GiPluginSetup.cs`, `Gi.slnx` | `build-plugins.sh --check` reproducible twice in a row; `compile.sh` `errors: 0` `burst: 0`; editmode and playmode green with the `coreclr` variant (H1, H2), the in-Unity `Gi.Corpus` tests included; the editor perf suite's B1 drops into the vector band (≤ 1.2x the dotnet net10 row), which proves the vector paths run in the editor | M |
| J02 | **Parity receipts for both variants.** Golden corpus, determinism (`GiDeterminismTests`) and the frame receipts run in the editor on `coreclr` and, by forcing the `portable` importer, on `portable`. A new `Gi.Entities.Columns.Compat` configuration compiles against the retargeted DLL | `unity/tests/Gi.Entities.Columns.Compat/*`, test-only files | the same hashes on both variants; the Compat build with the retargeted DLL has 0 errors | S |
| J03 | **Numbers and ledger.** `perf.sh` gains the row "Unity editor, CoreCLR, coreclr variant (vector)"; `perf.md` regenerates; Appendix B item 11 and HANDOFF §5 ("only V-0/G-0 recovers it") rewritten to the measured outcome; V-0/G-0 board rows updated | `unity/perf.sh`, `unity/perf*.json`, `unity/perf.md`, `unity/ROADMAP.md`, `unity/HANDOFF.md` | B1, C and W within the §3 target or the miss is printed in the ledger | S |

### Wave 2a: core quick wins (`src/Gi`, base `main`)

| ID | Work | Owns | Done when | Size |
|---|---|---|---|---|
| J10 | netstandard `AlignedAlloc` returns 64-byte-aligned memory (over-allocate, store the base pointer in the header word, `AlignedFree` reads it back) | `src/Gi/NativeHeap.cs`, `docs/model.md` (alignment proof) | `NativeHeapTests` assert `((nuint)p & 63) == 0` on both TFMs; gate unchanged; 0 B | S |
| J11 | **Portable vector audit.** For each of the 81 `#if NET` sites (Y14), record whether it uses only cross-platform `Vector128/256` API or an ISA class (`Avx2`, `Sse41`, `AdvSimd`, `Sse2`, `Avx`). Output: a table in `docs/model.md` with the site, the API and its stage share from the frame stages | `docs/model.md` | the table covers all 81 sites; it names the sites J24 would rewrite | S |
| J12 | **Static inventory.** Prove Y12 is complete: a test reflects over `Gi` for non-const, non-readonly static fields and pins the list | `tests/Gi.Tests/*` | the test fails if a new mutable static appears | S |

### Wave 2b: ECS costs (`com.gi.entities`, base per OJ6)

| ID | Work | Owns | Done when | Size |
|---|---|---|---|---|
| J30 | **Sense revision-gating** (HANDOFF §4.4, L3's named cause). A binding whose layer's `ChangedTiles(since)` is empty under the probe's footprint and whose probe did not move skips gather, read and scatter. Engine-neutral planner in `Runtime/Columns` first, with dotnet tests | `Runtime/Columns/GiSensePass*.cs`, `Runtime/Sense/*`, `Runtime/GiSenseSystem*.cs`, tests | L3 ≤ 30% on the V-3 probe scene; results byte-equal to ungated on generated scenarios; 0 B | M |
| J31 | **G-9 column batch entry points** (deferred until "the L3 rework", which J30 is): one Gi call per binding batch instead of per row group | core: `src/Gi/Column*.cs` (base `main`); Unity: `Runtime/Sense/*` | gate unchanged or better; L3 ≤ 12% with J30 | M |
| J32 | **Scatter fusion.** Gather and scatter jobs per binding kind fold into one chunk job per archetype | `Runtime/Sense/*`, `Runtime/Sync/*` | job count per tick and L3 both drop; idle tick ≤ 17 µs | S |
| J33 | **Entity-level touched list for reconcile** (L7). Track changed emitters through change filters plus an enableable `GiDirty` tag instead of diffing every live source | `Runtime/Reconcile/*`, `Runtime/Columns/GiDiff.cs`, `Runtime/Columns/GiApplyPass.cs`, tests | L7 ≤ 4 ms at 1M (`GI_PERF_ENTITIES=1000000`); generated scenarios byte-equal to the full diff | M |
| J34 | **Incremental deposit in Gi** (L7's re-bake half, a core card): `ApplyBatch` of N moves costs O(N) deposit work, with no tile rebuild when N ≪ the tile's sources | `src/Gi/World.Writes.cs`, `src/Gi/World.SourceBatch.cs`, `src/Gi/TileBake.cs` (base `main`, full gate) | B2 200-movers row improves; corpus and gate green; then L7 ≤ 1.5 ms | L |
| J35 | **Incremental profile edit** (L10). Diff old and new profile blobs; free and rebuild only the changed layers; re-place in one `ApplyBatch` | `Runtime/GiBootstrapSystem.cs`, `Runtime/Profile/*`, `Runtime/Columns/GiWorldBuilder.cs`, tests | L10 ≤ 250 ms at 1M; an edit that touches one layer of three costs ≤ 40% of a full rebuild | M |

### Wave 3: Burst-reachable core

| ID | Work | Owns | Done when | Size |
|---|---|---|---|---|
| J20 | **Spike: Burst × Gi.** Throwaway asmdef in the host project: (1) a `[BurstCompile]` `IJob` calling `World.SenseColumn` on the portable DLL, recording every Burst error; (2) a `[BurstCompile]` function using `Vector128.Load`/`Equals`/`ExtractMostSignificantBits` on `short*`, reading the Burst Inspector output (H3); (3) the same on the retargeted DLL | spike only, not merged; report in the PR description and §1 | H3 settled both ways with the disassembly quoted; the full error list for (1) recorded (it should name only the Y12 statics) | S |
| J21 | **`GiRoot`.** The 11 statics move into one blittable `GiRoot` struct; `Runtime.Root` is the only static. `Runtime.Ensure`/`Release` keep their semantics, revisions included | `src/Gi/Runtime.cs`, `src/Gi/NativeHeap.cs`, `src/Gi/ProcessStages.cs`, `src/Gi/Stamp.cs`, `src/Gi/World.cs`, `docs/model.md` | 0 public API change (`ApiExamples` and the README compile unchanged); gate within ±1%; J12's test updated to "one static" | M |
| J22 | **Root entries.** Internal static methods taking `GiRoot*` for the six adapter calls (Y11). The public methods become one-line forwards; `InternalsVisibleTo("Gi.Entities")` (OJ1). Warm paths reachable from them do not touch `Runtime.Root` | `src/Gi/**` touched paths, `src/Gi/Gi.csproj` | a test proves the entries never read the static (run them with `Runtime.Root` nulled after capture); gate ±1%; corpus green | M |
| J23 | **Burst Gi jobs.** `[BurstCompile]` on ProcessJob, GiApplyJob, GiSplatApplyJob, GiSyncApplyJob and GiSenseReadJob, calling the J22 entries with the root pointer captured on the main thread at `OnUpdate`. Markers and `GiGuard` stay outside the Burst body if Burst rejects them. Measure Burst-scalar against JIT-vector per job on the editor (H5) and pick per job with a define | `Runtime/GiProcessSystem.cs`, `Runtime/Reconcile/GiReconcileJobs.cs`, `Runtime/Splat/GiSplatJobs.cs`, `Runtime/Sync/GiSyncJobs.cs`, `Runtime/Sense/GiSenseJobs.cs`, `docs/model.md` | `burst: 0`; editmode, playmode and corpus green on both variants; 0 B; per-job timing table in the PR | M |
| J24 | **Portable vector path** (only if J20 settles H3 as true). Rewrite the ISA-class sites that J11 lists to cross-platform `Vector128/256` so one IL serves the JIT and Burst, and ship that build as a third variant (`burst`) for IL2CPP. If H3 is false, J24 becomes a kernel table in Gi plus Burst kernels in the package (the needle-cs N30 pattern), re-planned before it starts | `src/Gi/**` sites from J11, `unity/build-plugins.sh` | gate ±2% on net10 (the rewrite must not cost the JIT path); corpus green on all variants | L |
| J25 | **IL2CPP player pass** (owner-gated, OJ2): Linux x64 IL2CPP with the portable variant and the J23 jobs; frame C and the ECS table | `unity/validate.sh il2cpp` only | numbers in `perf.md`, or the exact IL2CPP error | S |

### Wave 4: owner-gated research

| ID | Work | Gate |
|---|---|---|
| J50 | **Parallel `Process` spike.** A deterministic tile executor over Unity job workers (or `Parallel.For` on dotnet): tiles are independent after the deposit; publish stays ordered. Measure frame C and W at 1, 4 and 8 workers with byte-equal pages | ROADMAP D7 says single-threaded and the tile executor is "optional, after 2.0"; G-8 (parallel splat) was cut when its gate failed. A spike only; no merge without the owner (OJ3) |
| J51 | **Mirror and draw on the vector build:** P-1's screenshots and L8 in a real scene, which HANDOFF §5 lists as not taken | by-eye check, the owner's call |

## 5. Dependencies and file ownership

```
J00 ─► J0a ─► J01 ─► J02 ─► J03
               │
               ├────────────► J23 ◄── J22 ◄── J21 ◄── J12
J20 ───────────┴─► J24 (needs J11, J20)          ▲
J10, J11, J12: independent                       │
J30 ─► J31 ─► J32        J33 ─► J34        J35   │
                                                  └── J20 (error list feeds J21)
J25 after J23 (OJ2) ; J50 any time (OJ3)
```

- **Critical path:** J00 → J0a → J01 → J02 → J03, about 3 agent-days. Everything else can start after J00.
- **Parallel lanes after J00:** about 9 at once: J01, J10, J11, J12, J20, J30, J33, J35, plus J34 on `main`.
- **Single owners:**
  - `build-plugins.sh` and `Plugins/Gi/**`: J01, then J24.
  - `src/Gi/Runtime.cs` and `NativeHeap.cs`: J10, then J21.
  - The Sense files: J30 → J31 → J32, in that order.
  - `Runtime/Reconcile/*`: J33.
  - The five job files in J23: J23 only. J23 starts after J30–J33 have merged, or rebases onto them.
- **Never in parallel:** two cards that change `src/Gi` and both run the pinned gate on the same core. Gate runs are serialized per host.

## 6. Agent protocol

```text
You are executing card <Jnn> of plan-speed.md in the grid-influence repository.

Read first: AGENTS.md; plan-speed.md §0, §1, §2 and card <Jnn>; unity/HANDOFF.md §6 and §9 for any Unity card.
Branch: core cards from main as gi/<jnn>-<slug>; Unity cards from the base OJ6 names as unity/<jnn>-<slug>.
Change only the files under Owns. Engine-neutral logic goes in Runtime/Columns with dotnet tests first.
Correctness receipts before timing. 0 B on warm paths, with and without hardware intrinsics.
Unsafe code: add or extend the docs/model.md proof (lifetime, aliasing, alignment, concurrency).
If a Y fact is refuted or an H fact is settled, say so first in the report and patch §1 in your PR.
Open one PR. Do not merge, tag, push to main or unity/main, or move a published tag.
End with the unity/ROADMAP.md Appendix C report.
```

Stop rules:

- **Correctness first.** A checksum, corpus line or determinism hash differs between variants: stop, report the first differing page, and change nothing else.
- **Gate regression.** The gate regresses more than the card allows: stop and report it with three pinned runs. The tiered JIT is bimodal (HANDOFF §6), so compare medians.
- **New allocation.** A warm-path allocation appears: stop.
- **Retarget rejected.** J01 cannot get `errors: 0` on a12 after the retarget: stop. Record the exact errors as a refutation of Y9 for the real editor. Do not hand-edit metas; let `GiPluginSetup` write them (HANDOFF Appendix B, importer note).

## 7. First 48 hours

1. **Owner:** review and commit J00 from `chore/unity-a12` (OJ5), then answer OJ1, OJ2 and OJ6. Every question has a default, so work can start before the answers arrive.
2. **Agent A:** J0a, then J01, then J02 and J03.
3. **Agent B:** J20 (a spike, one day). Post H3's answer to needle-cs as well.
4. **Agents C and D:** J30 and J33 (the Unity ECS costs).
5. **Agent E:** J10, then J11 and J12 (small core cards, one gate host).
6. **Agent F:** J35.
7. **After J20 and J12:** J21 → J22 → J23.

## 8. Risks

| Risk | Effect | Mitigation |
|---|---|---|
| A later Unity alpha validates plugin references strictly or starts reading `TargetFrameworkAttribute` | the `coreclr` variant stops importing | the `portable` variant stays shipped and selected by define, so a fallback is one importer flip; J01 keeps the probe test that fails loudly |
| A later Unity alpha enables the net10 API level | J01's retarget becomes unnecessary | good outcome: delete the step and reference the plain net10 build (Appendix B item 11) |
| Cecil output not byte-reproducible | `build-plugins.sh --check` fails in CI | deterministic MVID; J01's "reproducible twice" receipt; the input is the deterministic net10 build |
| Burst rejects something beyond the statics (H4 false) | J22/J23 grow | J20's error list sizes J21/J22 before anyone starts them |
| BovineLabs pre.7 behaviour changes (settings resolution, facets) | adapter tests regress | J00's editmode and playmode receipts; any regression is a J00 fix before J01 |
| The third-party monorepo pins (quill and anchor @ eb83b43) go stale against a13+ | another break like Y3 | J00 records the working triple; HANDOFF lists it |
| Multi-agent gate noise on one host | false regressions | serialize gate runs (§5); medians of three |

## 9. Owner decisions (each has a default, so work continues)

| # | Question | Default |
|---|---|---|
| OJ1 | How does the adapter reach the root entries (J22)? | `InternalsVisibleTo("Gi.Entities")` on `Gi`: no public surface change, and the same mechanism `Gi.Tests`, `Gi.Stats` and `GiBenchmarks` already use |
| OJ2 | Are players back in scope (J25, IL2CPP numbers)? | No, as decided on 2026-10-18. J23 and J24 are measured in the editor; player rows stay "not run" |
| OJ3 | May J50 (parallel `Process`) run as a spike? | Yes as a spike, no merge |
| OJ4 | Does the package ship the `coreclr` variant by default? | Yes, for `ENABLE_CORECLR`; `portable` for everything else |
| OJ5 | Land J00: a12, BovineLabs Core pre.7 (`dccb0b5`), and the Audio module? | Yes. The third-party mirror caveat in HANDOFF §5 (E-8) still stands |
| OJ6 | Base branch for Unity cards now that `unity/main` (4ba19b1) is fully merged into `main`? | `unity/main` after a sync PR from `main` (as #138 did); core cards from `main` |

## 10. Readiness log (this session)

- dotnet suite on `main` @ 9814f8d: green (Y1).
- Unity, before J00: no a9 editor (Y2); a12 run: 120 errors in BovineLabs Core (Y3). The a12 run also rewrote `manifest.json` and `packages-lock.json` (test-framework 1.9.0, performance 7.0.0). Those edits were reverted. The `Library/` cache is now an a12 import.
- Unity, with J00: `compile.sh` `errors: 0`, `burst: 0` (Y5); EditMode 80/0, PlayMode 91/0, perf 6/0 (Y17).
- Retarget spike: Y9 and Y10 offline, then Y18 in the real editor. The plugin file was swapped only for those runs and restored with `git checkout` (git shows no diff on it). `validate.sh` refuses a swapped plugin by design, because it runs `build-plugins.sh --check` first, so the vector runs called `unity test` directly. The spike source is Appendix A; J01 rebuilds it as a proper tool.
- Open: Y19 (field-draw allocation test; flaky on scalar, failing on vector).
- Not run: `quickstart.sh`, `export-check.sh`, `jb inspectcode`, the netcode embed, and the pinned `--gate` (no `src/Gi` change was made).

## Appendix A: the retarget spike (starting point for J01)

Run on 2026-11-03 for Y9, Y10 and Y18. It is a spike: it references the editor's `Unity.Cecil.dll` (J01 switches to the Mono.Cecil NuGet package) and reads the shim versions from the Unity install (J01 replaces that with a fixed table, plus a test that checks the table against the installed shims when Unity is present).

```sh
D=~/Unity/Hub/Editor/7000.0.0a12/Editor/Data/NetStandard
dotnet bin/Release/net10.0/Retarget.dll src/Gi/bin/Release/net10.0/Gi.dll out/Gi.dll \
    $D/compat/2.1.0/shims/netstandard $D/ref/2.1.0
```

`Retarget.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Unity.Cecil"><HintPath>/home/i/Unity/Hub/Editor/7000.0.0a12/Editor/Data/Managed/Unity.Cecil.dll</HintPath></Reference>
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
using Mono.Cecil;

var input = args[0];
var output = args[1];
var shims = args[2];
var netstandardPath = Path.Combine(args[3], "netstandard.dll");

var nsModule = ModuleDefinition.ReadModule(netstandardPath);
var nsTypes = new HashSet<string>();
foreach (var t in nsModule.Types) nsTypes.Add(t.FullName);
foreach (var t in nsModule.ExportedTypes) nsTypes.Add(t.FullName);

var shimVersions = new Dictionary<string, Version>();
foreach (var file in Directory.GetFiles(shims, "*.dll"))
{
    using var m = ModuleDefinition.ReadModule(file);
    shimVersions[m.Assembly.Name.Name] = m.Assembly.Name.Version;
}

var resolver = new DefaultAssemblyResolver();
var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
var nsRef = new AssemblyNameReference("netstandard", new Version(2, 1, 0, 0))
{
    PublicKeyToken = new byte[] { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 }
};
module.AssemblyReferences.Add(nsRef);

int moved = 0, kept = 0;
var keptNames = new SortedDictionary<string, List<string>>();
foreach (var tr in module.GetTypeReferences())
{
    if (tr.DeclaringType != null) continue;
    if (tr.Scope is not AssemblyNameReference a || a.Name == "netstandard") continue;
    if (nsTypes.Contains(tr.FullName)) { tr.Scope = nsRef; moved++; }
    else
    {
        kept++;
        if (!keptNames.TryGetValue(a.Name, out var list)) keptNames[a.Name] = list = new List<string>();
        list.Add(tr.FullName);
    }
}

var used = new HashSet<string> { "netstandard" };
foreach (var tr in module.GetTypeReferences())
    if (tr.DeclaringType == null && tr.Scope is AssemblyNameReference a) used.Add(a.Name);
foreach (var a in module.AssemblyReferences.ToArray())
{
    if (!used.Contains(a.Name)) { module.AssemblyReferences.Remove(a); continue; }
    if (a.Name == "netstandard") continue;
    a.Version = shimVersions.TryGetValue(a.Name, out var v) ? v : new Version(4, 0, 0, 0);
}

foreach (var attr in module.Assembly.CustomAttributes)
    if (attr.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")
    {
        attr.ConstructorArguments[0] = new CustomAttributeArgument(attr.ConstructorArguments[0].Type, ".NETStandard,Version=v2.1");
        attr.Properties.Clear();
    }

module.Write(output);
Console.WriteLine($"moved {moved} type refs to netstandard, kept {kept}");
foreach (var (name, list) in keptNames) Console.WriteLine($"  {name}: {string.Join(", ", list.Distinct().OrderBy(x => x))}");
foreach (var a in module.AssemblyReferences) Console.WriteLine($"  ref {a.Name} {a.Version}");
```
