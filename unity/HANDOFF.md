# Handoff: Gi for Unity Entities

Written 2026-10-18 when the previous agent's session limit ran out. Read this first, then do the work in §6. Everything you need is in the repository; nothing lives only in a chat.

## 1. What this project is

`src/Gi` is a standalone .NET library for sparse tiled integer influence fields. `unity/com.gi.entities` is a Unity Entities package that runs Gi inside ECS: entities emit influence, splat into transient layers, and sense the field. The design is `unity/plan.md`; the execution board is `unity/ROADMAP.md` (it wins where the two disagree). Tracking issue: #77.

Rules that never change (`AGENTS.md`): Gi stays standalone and gains no Unity dependency; the public surface is `World`, `Grid`, `Layer`, `Stamp` only; production code has no explanatory comments; the warm path allocates 0 B; written files are deterministic and culture-independent.

## 2. Where things stand

Unity target: editor **7000.0.0a9**, BovineLabs Core pinned by git commit (pre.6). Unity 7 runs on **CoreCLR** (.NET 10.0.10-dev); the other runtime is IL2CPP. There is no Mono.

### The pull request stack (all CI green, owner merges in this order)

| PR | Branch | Card |
|---|---|---|
| #86 | `unity/u-0-tier-u` → `unity/main` | U-0 Tier U unblock, plugin provenance fix |
| #87 | `unity/f-2-scenarios` | F-2 scenarios, oracle, lint, CI |
| #88 | `unity/v-0-coreclr-plugin` | V-0 preview: CoreCLR facts |
| #89 | `unity/u-1-frame` | U-1 frame, bootstrap, process |
| #90 | `unity/u-2-reconcile` | U-2 reconcile, emitters, pulses |
| #91 | `unity/u-3-sync` | U-3 sync |
| #92 | `unity/u-6-splat` | U-6 splat |
| none yet | `unity/u-5-bindings` | **U-5 read bindings, checkpoint** (see §4) |

Each PR is based on the previous branch. Never merge them yourself.

### Board

Done: F-1, F-2, U-0, U-1, U-2, U-3, U-6, V-0 (preview), G-0a, G-1, G-2, G-3, G-5.
In progress: **U-5** (code written and green in the editor; verification and PR still to do).
Open: U-4, U-5b, V-1, V-2, V-3, A-1, A-2, P-1, E-1 to E-7, N-1, N-2, S-1, D-1, G-0 (blocked on Unity), G-4, G-6, G-7, G-8.

## 3. How to check your work

Run from the repository root.

```sh
unity/lint.sh                                                   # must print "lint: ok"
dotnet test unity/tests/Gi.Entities.Columns.Tests -c Release    # Unity-free tests (88 pass now)
dotnet build unity/tests/Gi.Entities.Columns.Compat -c Release  # C# 9 / netstandard2.1 compile check
unity/build-plugins.sh --check                                  # plugin DLLs and Gi.Corpus.dll match their sources
unity/compile.sh                                                # editor compile of the host project, ~25 s, must print errors: 0
unity/validate.sh editmode                                      # final pass only (owner decision O-U9): then playmode, coreclr, il2cpp
jb inspectcode Gi.slnx --severity=WARNING --build --output=/tmp/inspect.json   # must find 0 problems
```

`validate.sh` prints a fixed summary block (`passed:` / `failed:`); it exits 2 if no test ran. `playmode` is the editor on CoreCLR; `coreclr` and `il2cpp` build a Linux player (about 30 to 100 seconds each once cached). A task is not done until all four Unity suites pass, `jb` reports 0 findings and the dotnet tests pass.

## 4. U-5 in detail (finish this first)

Branch `unity/u-5-bindings` (based on `unity/u-6-splat`). State: 88 dotnet tests and 49 PlayMode tests pass in the CoreCLR editor.

Still to do:
1. Run `validate.sh coreclr` and `il2cpp`; run `jb`.
2. Fill the `### U-5` heading in `docs/model.md` (it still says "Pending."). Follow the style of the filled U-1, U-2, U-3 and U-6 headings: lifetime, aliasing, alignment, concurrency of the pointers used in `Runtime/Sense/GiSenseJobs.cs`.
3. In `unity/ROADMAP.md` set the U-5 board row to `done` with the PR number, and add one line to Appendix A for any deviation. Known facts to record: the coverage tag for a `ValueGradient` binding means "the gradient is computable" (it needs the neighbouring cells), so at the grid edge a value can exist while the bit is clear; the per-binding queries use `Present` for coverage tags so a disabled tag is still matched and can be switched on.
4. Commit, push, open a PR with base `unity/u-6-splat` (copy the style of #92: receipts first, deviations, what was not run).

How U-5 works: `Runtime/GiSenseSystem.cs` (a `SystemBase`) turns each valid binding in the profile blob into an `EntityQuery` over the game's own output components, found by stable type hash. Per binding it schedules: count, prefix sum, write (positions), a parallel read in batches of whole 64-row words (`IJobParallelForDefer`), and one scatter job per output component type. Unity-free logic is in `Runtime/Columns/GiBindingTable.cs` (validation) and `GiSensePass.cs` (batched reads).

## 5. Environment and traps (each cost an hour once)

- Never edit files under `unity/com.gi.entities` while a `validate.sh` player run is compiling; it reads the sources.
- Unity wipes `unity/project/Temp` on launch. Keep helper files in `Logs/`.
- `unity test --mode` only takes EditMode and PlayMode. Player runs use the editor's own `-testPlatform` (done by `validate.sh`). IL2CPP needs two symlinked libraries on this machine; `validate.sh` makes them.
- `[OnCodeLoaded]` and `[OnCodeUnloading]` methods need a `partial` class.
- Arrays from `state.WorldUpdateAllocator` need `CollectionHelper.CreateNativeArray`, not `new NativeArray`.
- Add `using Unity.Jobs;` or `Schedule` resolves to the wrong overload.
- `IJobParallelForDefer` requires the `NativeList` it iterates to also be a field of the job struct.
- Zero-sized components (empty tags): `TypeInfo.IsZeroSized` is true; do not request a data array for them.
- A heading recovered from a quaternion is a ulp off, which flips cells that sit on Gi's 16-bit angle boundary. Test headings are biased a quarter-quantum for this reason (`GiScenarioOp.HeadingBiasRadians`).
- A change-version filter says "changed since a system last ran"; a main-thread write at the current version does not count until a system has advanced the version.
- Plugin DLLs must not embed anything that depends on the git commit or checkout path. `build-plugins.sh` already handles this; do not add a PDB or SourceLink.
- After a power cut: run `git fsck` and look for 0-byte files under `.git/refs`.
- Do not use `sleep` for 60 seconds or more in a shell command; poll with an `until` loop.

## 6. What to do, in order

Work one card per branch and per pull request. Method for every card:

1. Write the engine-neutral logic in `unity/com.gi.entities/Runtime/Columns/` first (no `Unity` namespaces; `lint.sh` enforces this) and prove it with dotnet tests against the reference oracle (`Tests/Support/GiReferenceExecutor.cs`, scenarios from `GiScenarioGenerator`).
2. Then the Unity system, with PlayMode tests in `Tests/Runtime/<Card>/` that compare the ECS result with the oracle using `GiEcsExecutor`.
3. Run all checks in §3, fill the `docs/model.md` heading, update the board and Appendix A in `unity/ROADMAP.md`.
4. Commit with the trailer the repository uses (`Co-Authored-By: ...`), push, open the PR with receipts.

Order:
1. Finish **U-5** (§4).
2. **U-4**: add `GiGuard` (development builds only; detects overlapping Gi readers and writers per world); test that a deliberately misordered system trips it; run two ECS worlds in one process for 1000 ticks; check one frame of three fixed ticks equals three frames of one tick; pin E1 and E6 (does the fixed-step group update in the Editor world?). The fences themselves already exist in `GiRuntime`.
3. **U-5b**: Area, Max, Nearest, Changed kinds, exclude-self (Gi's exclusion reads are pure since G-1, so this can run in parallel), `Period` and `Since`. The validator already knows the rules; the system currently passes `GiBindingSupport.Basic`.
4. **V-2**, then **V-1**, then **V-3**: determinism and allocation tests across backends, parity with Gi's golden corpus inside Unity, and performance numbers (ledger rows L1 to L11 in the roadmap).
5. **A-1**, **A-2**: authoring assets, bakers, live baking. These feed `GiProfileSpec`, which already exists.
6. **P-1** and the editor tools **E-1**, **E-2**, **E-3**, **E-4**, **E-6**; then **D-1** (docs and samples).
7. Last and only if the owner still wants them: E-5, E-7, N-1, N-2, S-1, G-8.

Two ideas from the owner to keep in mind, not to implement without asking: (a) the roughly 1,700 engine-neutral lines in `Runtime/Columns` could move into a reusable Gi adapter package; (b) a thinner "quick start" layer on top, so using Gi in Unity takes a few lines. Ask the owner which entity count is really needed before adding more machinery.

## 7. Cards a second agent can take in parallel (Gi core, branch from `main`)

These touch only `src/Gi`, `tests/Gi.Tests` and `docs/model.md`, so they cannot collide with the Unity work. Give them in this order: **G-6** (allocator hook), **G-7** (incremental layer hash), **G-4** (cross-world `Process`, riskiest). Specs are in `unity/plan.md` §9 and the "Gi core" table of `unity/ROADMAP.md`.

```text
You are executing card G-6 (then G-7, then G-4, each as its own PR) of unity/ROADMAP.md in the grid-influence repository.
Read first: AGENTS.md; unity/ROADMAP.md §0, §4 and the "Gi core" table; unity/plan.md §9.
Branch from main as gi/g-6-<slug>. Open one PR into main. Do not merge, tag, or push to main.
Change only src/Gi, tests/Gi.Tests and docs/model.md. No explanatory comments in production code.
Run the AGENTS.md validation commands, including --verify with and without DOTNET_EnableHWIntrinsic=0.
Run the --gate on one pinned core (taskset -c 0, from the built DLL) if anything on a warm path could move.
End with a report in the unity/ROADMAP.md Appendix C format.
```

## 8. Do not

- merge a PR, create or move a tag, or push to `main` or `unity/main`;
- change `src/Gi` for a Unity card (only G-cards change it, from `main`);
- leave debug code, probe tests or commented-out code in a commit;
- mark a card done while any of the four Unity suites, the dotnet tests, `lint.sh` or `jb` fails;
- reopen decisions D-U1 to D-U10 (locked) or the numbered deviations in Appendix A without asking the owner.

Open owner decisions O-U1 to O-U8 each have a default in the roadmap; use the default and mention it in the PR.
