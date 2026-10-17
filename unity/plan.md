# Gi for Unity Entities: runtime and tooling plan

Written 2026-10-17. The horizon is Unity's CoreCLR release, expected around 2026-11-14 (the owner's estimate, four weeks out).

Scope:
- a Unity package under `unity/` that runs Gi inside Entities;
- editor tooling built on that package;
- the small Gi core cards in §9.

Gi stays a standalone .NET library. Nothing in `src/Gi` learns about Unity.

This file is `unity/plan.md` because the repository root already has `PLAN.md`, and the two names collide on case-insensitive file systems.

## 0. Working this plan

- **One card, one agent.** Each card (§9, §10) is for a single agent, who reads §0–§5, the card, and the files the card names. An agent without repository access needs those sections and Appendix B pasted into its prompt.
- **Reporting.** Agents report in the Appendix B format. The owner accepts a card against its **Done when** list and the ledger (§12).
- **Receipts before timings.** A card that moves numbers shows parity, equivalence and allocation tests first.
- **Gi changes.** `src/Gi` changes only through G-cards, and those follow `AGENTS.md` in full: the validation commands, `--verify` with and without intrinsics, the pinned gate, and the `docs/model.md` proofs.
- **Code rules.** Unity code follows the same rules as Gi:
  - no explanatory comments;
  - an unmanaged warm path;
  - deterministic, culture-independent written files;
  - 0 B of allocation in steady state.
- **CoreCLR unknowns.** Nothing about Unity's CoreCLR is guessed in code. Appendix A lists the open questions, and card V-0 settles them on a real build.

## 1. Axioms

Everything else in this plan derives from these. A design that breaks one is wrong even if it is faster.

1. **Gi is the field.**
   - Unity adds no field type, no second engine and no query surface.
   - Every read and write is a call on Gi's existing `World`, `Grid`, `Layer` and `Stamp` handles.
   - Unity decides only when calls happen and where their data lives.
2. **Columns at the boundary.**
   - Gi only ever sees dense columns: `float*`, `short*`, `SourceId*` and bit words.
   - ECS chunks never cross into Gi. Burst gathers chunks into columns and scatters results back.
   - The managed side is a straight line of Gi calls with no per-entity loop.
3. **Desired state in ECS, applied state in Gi.**
   - Gameplay writes components.
   - Once per tick, a reconcile step turns the difference into one `ApplyBatch` call.
   - Gameplay never calls a Gi write.
4. **One writer, then many readers.**
   - Per Gi world, the writes (reconcile, sync, splat, `Process`) run strictly one after another.
   - Pure reads run in parallel, and only between `Process` and the next write.
   - That window is enforced by ECS dependencies on the `GiFrame` singleton, not by a lock.
5. **Quantisation lives in Gi.**
   - Unity passes floats. It never rounds, snaps or caches field values.
   - The GPU mirror is write-only presentation, not a field.
6. **Every lifetime has exactly one owner.**

   | Thing | Owner |
   |---|---|
   | Entity-bound source | ECS, through a cleanup component |
   | Detached pulse | Gi, through `Expire` |
   | Gi world | the `GiFrame` singleton |
   | Stamp | a refcounted, process-wide registry |

   Each acquire has one release, and every release is idempotent.
7. **Total at runtime.**
   - The warm path consumes typed statuses and produces typed faults.
   - Gi exceptions are reachable only during bootstrap, which validates first with `Gi.Validation`.
8. **0 B per tick** on CoreCLR, Mono and IL2CPP, with and without hardware intrinsics.
9. **Deterministic by algebra.**
   - The field at tick *t* is a pure function of the profile and the inputs up to *t*.
   - Contributions are integer sums and commute, so command order can change `SourceId`s but never values.
   - Order-sensitive state (a source's gain, layer and stamp) has exactly one writer.
10. **CoreCLR first, never CoreCLR only.**
    - One adapter source serves every backend.
    - Which Gi build loads (`net` with intrinsics, or scalar `netstandard2.1`) is chosen by a plugin define, not by a code fork.
11. **Tooling is made of runtime parts.**
    - Windows and overlays use the same read window, GPU mirror and recorder that the game uses.
    - Deleting the editor assembly leaves the runtime compiling and behaving exactly the same. The same holds for the netcode and presentation assemblies.

## 2. What this buys

| Capability | Built from |
|---|---|
| A million agents steer by gradient with no per-agent Gi code | read bindings (§5.5) plus one Burst steering job |
| Event-driven AI: an agent sleeps until its neighbourhood changes | `ChangedMask` scattered into an enableable wake tag |
| Short-lived influence such as noise, scent or explosions, with no entity | pulses: `Place`, `Fade` and `Expire`, linked by `CreatedBy` in one batch |
| Stamp and field previews that match runtime to the bit | Stamp Studio and overlays rendered by Gi itself |
| "Why is this cell 87?" | explain-cell: each source's contribution is the value minus the value excluding that source |
| A bug seen in play becomes a Gi regression test | the flight recorder exports Gi's corpus stream format |
| Fog of war, minimaps and heatmaps whose cost follows change, not area | a GPU mirror fed by `ChangedTiles` |
| A CoreCLR speed-up on day 0 with no code change | the `net` Gi plugin behind a define constraint |

## 3. Targets

| Backend | Gi plugin | SIMD | Where Gi runs | Role |
|---|---|---|---|---|
| CoreCLR (editor and desktop players) | the `net` TFM that Unity's runtime loads (G-0) | AVX2, AVX-512, AdvSimd | managed jobs, RyuJIT | primary from day 0 |
| Mono (today's editor and players) | `netstandard2.1` | scalar | managed jobs | fallback, measured by V-3 |
| IL2CPP (consoles, mobile, WebGL) | `netstandard2.1` | scalar, autovectorised by the C++ compiler | managed jobs, AOT | fallback, measured by V-3 |

Burst compiles everything around Gi: projection, gather, scatter, the reconcile diff and enable bits. Gi itself is never Burst-compiled. Its process-wide statics are not legal in Burst, and making them legal would give Gi a Unity dependency, which `AGENTS.md` forbids.

Gi has never been measured under Mono or IL2CPP. The issue #40 numbers come from `dotnet` on one pinned core of an i9-14900K:

| Frame C step | Time |
|---|---|
| Splat | 1.9 ms |
| `Process` | 1.3 ms |
| Value with gradient | 3.3 ms |
| Whole frame | about 6.6 ms |

On .NET, the `netstandard2.1` build runs 1.3–4.4× slower than the `net` build.

Non-goals:
- Burst-compiling Gi, or porting it to HPC#.
- A GameObject or MonoBehaviour API. If one comes later, it is a thin layer over the same frame.
- Replicating or saving field data.
- Predicted fields on clients. That is N-3, after this plan.
- Computing the field on the GPU. The GPU only mirrors it.

## 4. Decisions

### Locked

| ID | Decision |
|---|---|
| D-U1 | The package is `unity/com.gi.entities`, in this repository. It consumes Gi as precompiled plugins that `unity/build-plugins.sh` builds from `src/Gi`. Gi references nothing from Unity. |
| D-U2 | Dependencies: Entities (with Transforms), Collections, Burst, Mathematics and BovineLabs.Core. From BovineLabs.Core the package uses singleton collections, ConfigVars, chunk extensions, logging, the sparse uploader and the theme. Netcode is optional, under `UNITY_NETCODE`. |
| D-U3 | Gi is called only from non-Burst code: managed jobs, or a non-Burst `ISystem.OnUpdate` for bootstrap. Calling Gi from Burst through `[UnmanagedCallersOnly]` entry points was considered and deferred. On CoreCLR, managed jobs already run Gi at full speed, so this is revisited only if V-3 shows overhead from system updates. |
| D-U4 | Positions come from the `LocalTransform` of root entities, projected through the profile's plane (XZ by default, XY as an option). A profile can name a `float2` component field instead. The package has no position component of its own. |
| D-U5 | Sensing is declared as read bindings in the profile. Each binding writes into the game's own components. The package ships no output component types, so outputs carry domain names such as `Threat` or `FoodDirection`. |
| D-U6 | `GiSystemGroup` runs last in `FixedStepSimulationSystemGroup`, so one Gi tick is one fixed tick. Gameplay reads tick *t*'s outputs at tick *t*+1, so system order within a tick never changes what an agent senses. |
| D-U7 | Authoring refers to layers and stamps by asset. Data refers to them by profile index (`GiLayerKey`, `GiStampKey`). Runtime ids come from per-world tables and are never assumed to equal indices. |
| D-U8 | Each ECS world that holds a profile gets one Gi world. Thin-client, service and menu worlds get none. |
| D-U9 | A profile change rebuilds the Gi world in this order: free it, create a new one, add grids, add layers in recipe order, then re-place every source. Grids are never added to a live world, because a grid added after its sources misses them. |
| D-U10 | The field is never replicated or saved. Its inputs are. |

### Open

Each has a default that agents use until the owner decides.

| ID | Question | Default |
|---|---|---|
| O-U1 | Package id and display name | `com.gi.entities`, "Gi Entities" |
| O-U2 | Minimum Unity and Entities versions | Whatever the supplied BovineLabs.Core targets. It already uses `Unity.Scripting.LifecycleManagement`, `EntityId` and `MemoryLabel`. |
| O-U3 | Unity in CI (GameCI with license secrets) or a local script | A local `unity/validate.sh` now; a CI lane after day 0. |
| O-U4 | Shape of the layer editor | An ordered stack where order is recipe order, so cycles cannot be drawn, with a read-only graph view beside it. |
| O-U5 | Shape of new Gi read-outs (G-3, G-5) | `out` parameters on `World` methods. `AGENTS.md` allows no new nested type besides `FieldView`. |
| O-U6 | BovineLabs.Core as a hard dependency | Yes. Revisit only if a second consumer needs a runtime free of BovineLabs. |

## 5. Architecture

### 5.1 Layout

```text
unity/
  plan.md
  build-plugins.sh             deterministic build of Gi and Gi.Validation per plugin TFM, copied into Plugins/
  validate.sh                  Unity batchmode: EditMode, PlayMode, IL2CPP player tests, perf smoke
  com.gi.entities/
    package.json
    Plugins/Gi/netstandard2.1/   Gi.dll, Gi.Validation.dll    define constraint: not CoreCLR
    Plugins/Gi/coreclr/          Gi.dll, Gi.Validation.dll    define constraint: the CoreCLR symbol (V-0)
    Runtime/       Gi.Entities               frame, reconcile, sync, splat, process, sense, pulses
    Presentation/  Gi.Entities.Presentation  GPU mirror, heatmap draw
    Authoring/     Gi.Entities.Authoring     assets, authoring components, bakers, builders
    Netcode/       Gi.Entities.Netcode       UNITY_NETCODE only
    Editor/        Gi.Entities.Editor        windows, overlays, inspectors, validator, recorder UI
    Tests/         Runtime, Editor, Performance
    Samples~/      Crowd, Territory, Stealth
```

How the two plugin folders work:
- Both folders carry `.meta` files with fixed GUIDs.
- Their define constraints are complementary, so exactly one `Gi.dll` is active in any configuration.
- The adapter compiles against whichever one is active. The public API is the same for every TFM, because the `#if NET` code is internal.
- Until V-0 names Unity's CoreCLR define, only the `netstandard2.1` folder is active.

### 5.2 Names

`UnityEngine.Grid` and `Unity.Entities.World` collide with Gi's handles. Inside `namespace Gi.Entities`, the bare names `World`, `Grid`, `Layer` and `Stamp` resolve to Gi's types before any `using` directive applies. Adapter code therefore never uses them bare:
- Every file aliases Gi's handles as `GiWorld`, `GiGrid`, `GiLayer` and `GiStamp`.
- Unity's world is always written fully qualified, as `Unity.Entities.World`.

Adapter types carry the `Gi` prefix.

### 5.3 Data model

```csharp
public readonly struct GiLayerKey : IEquatable<GiLayerKey> { public readonly byte Index; }
public readonly struct GiStampKey : IEquatable<GiStampKey> { public readonly byte Index; }

public struct GiTables
{
    public FixedList64Bytes<byte> Layers;
    public FixedList512Bytes<byte> Stamps;
}

public struct GiFrame : IComponentData
{
    public byte World;
    public GiWorld.Token Token;
    public int Tick;
    public uint Generation;
    public GiTables Tables;
    public GiFaults Faults;
}
```

`GiFrame` lives on the bootstrap system's entity, one per ECS world.
- `Generation == 0` means no Gi world exists, so no assumption is made about Gi's world ids.
- Access to `GiFrame` is the capability:
  - `SystemAPI.GetSingleton<GiFrame>()` grants reading Gi;
  - `GetSingletonRW<GiFrame>()` grants writing it.
- ECS dependency tracking then orders the jobs (§5.6).

Per entity:

| Component | Kind | Writer | Meaning |
|---|---|---|---|
| `GiEmitter` | `IComponentData` | gameplay, bakers | the desired source: layer, stamp, gain |
| `GiGainRamp` | `IComponentData` | gameplay, bakers | gain changes become a `Fade` over `Ticks`; a new source fades in from 0 |
| `GiOriented`, `GiScaled` | tags | bakers | also sync heading, scale, or both |
| `GiSource` | `ICleanupComponentData` | reconcile only | the applied state: `SourceId`, layer, stamp and target gain. `Id == default` means not placed. |
| `GiFault` | `IComponentData`, `IEnableableComponent` | reconcile, sync | the last write's `SourceResultStatus`; enabled while faulted |
| `GiSplat` | `IComponentData`, `IEnableableComponent` | gameplay, bakers | splat stamp × gain into a transient layer this tick |
| game output types | `IComponentData` | sense only | values written by read bindings |
| game wake tags | `IEnableableComponent` | sense only | `ChangedMask`, coverage and found bits |

U-2 pins with a test that `Place` never returns `default(SourceId)`. If it can, `GiSource` gains a placed flag and nothing else changes.

Detached pulses go through a BovineLabs singleton collection of `NativeThreadStream`s, so any job can emit them in parallel:

```csharp
public struct GiPulse
{
    public GiLayerKey Layer;
    public GiStampKey Stamp;
    public float2 Position;
    public float Heading;
    public float Scale;
    public sbyte Gain;
    public ushort Ticks;
    public bool FadeOut;
}
```

A pulse becomes three commands in the same batch:
1. `Place`;
2. `Fade(CreatedBy(i), 0, Ticks)`, only when `FadeOut` is set;
3. `Expire(CreatedBy(i), Ticks)`.

Gi owns the pulse's lifetime, and no entity exists for it.

### 5.4 The tick

`GiSystemGroup` is declared with:
- `[UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]`;
- `[WorldSystemFilter(Worlds.SimulationEditor)]`.

Its systems run in this order. Where each piece of work runs:
- **B**: a Burst job.
- **M**: a managed job that only calls Gi.
- **T**: the main thread.

| # | System | Work | Gi calls |
|---|---|---|---|
| 1 | `GiBootstrapSystem` | **T**: when the profile hash changes, build or rebuild the Gi world, refresh `GiFrame`, and on a rebuild reset every `GiSource.Id` | `World.New`, `Grid.New`, `Layer.*`, the stamp registry |
| 2 | `GiReconcileSystem` | **T**: add `GiSource` to new emitters with one query-level add.<br>**B**: diff desired against applied state in changed chunks, emitting removes first, then places and updates in chunk order (count, prefix sum, write, so the order never depends on threads); then append the drained pulses.<br>**M**: `ApplyBatch`, split to Gi's documented cap.<br>**B**: write the applied state and fault bits from the results.<br>**T**: remove `GiSource` where the emitter is gone, with one query-level remove. | `ApplyBatch(Token, …)` |
| 3 | `GiSyncSystem` | **B**: for chunks whose `LocalTransform` changed, project into `SourceId`, x and y columns, plus heading and scale for oriented or scaled sources.<br>**M**: `Sync(Token, …)` for sources that have only a position, and `SyncTransforms(Token, …)` for the rest.<br>**B**: scatter the invalid bits into `GiFault`. | `Sync`, `SyncTransforms` |
| 4 | `GiSplatSystem` | **B**: counting-sort the enabled `GiSplat`s by layer into x, y, stamp-id and gain columns, noting which layers have a uniform stamp and gain.<br>**M**: one `Splat` per layer, using the single-stamp overload when the layer is uniform. | `Splat` |
| 5 | `GiProcessSystem` | **M**: advance the field one tick. | `Process(Token)` |
| 6 | `GiSenseSystem` | Per read binding:<br>**B**: gather positions plus per-entity reach, threshold and exclude columns.<br>**M**: the column read, in parallel batches when the binding is pure.<br>**B**: scatter outputs and bits into the game's components. | `Sense*Column`, `ChangedMask` |

Structural changes happen only in step 2, and only when its add or remove query is non-empty. A tick with no spawns or despawns has no sync point.

Columns:
- Each system owns its columns. They only grow, and are reused every tick.
- Gather offsets come from `CalculateBaseEntityIndexArrayAsync`.
- Managed passes whose length is known only after a gather run as BovineLabs `IJobParallelForDeferBatch` jobs.

### 5.5 Read bindings

A binding is one row of the profile:

| Field | Values |
|---|---|
| Kind | `Value`, `Gradient`, `ValueGradient`, `Area`, `Max`, `Nearest`, `Changed` |
| Layer | a layer asset |
| Outputs | component fields, picked as BovineLabs `ComponentFieldAsset`s: a `short` value, a `float2` vector or position, or a `long` total |
| Reach, Threshold | a constant, or a `float` or `short` component field |
| Exclude self | use the entity's own `GiSource` as the exclude column |
| Coverage tag | optional enableable tag, set from Gi's `whole` bits (or `found` bits for `Nearest`) |
| Period | sense every *n*th tick, on ticks where `entity.Index % n == tick % n`; the default is 1 |
| Since | `Changed` only: how many ticks back to look; the default is 1 |

| Kind | Gi call | Outputs |
|---|---|---|
| Value | `SenseColumn` | `short` |
| Gradient | `SenseGradientColumn` | `float2` per world unit, on the plane's axes |
| ValueGradient | `SenseGradientColumn` with values | `short`, `float2` |
| Area | `SenseAreaColumn` | `long` |
| Max | `SenseMaxColumn` | `short`, plus the best position as a `float2` |
| Nearest | `SenseNearestColumn` | `short` (`short.MinValue` when nothing is found), a `float2` position, the found tag |
| Changed | `ChangedMask`, with one reach for the whole binding | the wake tag |

Rules, checked at bake time and again at bootstrap:
- Every output field has exactly the size its kind writes.
- Each output field belongs to exactly one binding, so every field has one writer.
- `Max`, `Nearest` and `Changed` cannot exclude, because Gi has no exclude form for them.
- A binding's query is its output components, plus `LocalTransform` or the profile's position field, plus `GiSource` when it excludes.

Threading and bit words:
- A binding with **Exclude self** goes through Gi's impure read path, which inserts kernels and uses the shared canvas. It runs single-threaded until G-1 lands.
- Every other binding runs in parallel batches whose sizes are multiples of 64, so no two batches ever write the same bit word.
- In partly enabled chunks, only enabled entities receive outputs.
- Bits beyond `count` in the last word are never scattered.

### 5.6 Concurrency

**Inside one ECS world, `GiFrame` access orders everything.**
- Writer systems take `GiFrame` read-write. Readers take it read-only.
- ECS then makes every writer job wait for the previous tick's readers, and every reader wait for the last writer.
- A game job that reads Gi follows the same rule: its system takes `GiFrame` read-only, and the job calls pure reads only. This is the whole extension story for custom AI: it calls Gi directly, through the world id in `GiFrame`.

**Across ECS worlds, Gi shares process-wide state.**
- Host mode runs a server world and a client world in one process, and the editor adds worlds of its own.
- The shared state is the kernel canvas buffers, `NativeHeap.Calls` and `ProcessStages`.

Until G-4 removes that sharing, `GiRuntime` keeps two process-wide handles:

| Handle | Covers | Rule |
|---|---|---|
| `Writers` | every Gi write job and every impure read job, in any world | each such job depends on it and extends it |
| `All` | `Writers` plus every pure read | completed before any Gi world is created or freed |

**`GiGuard` proves the discipline in development builds** (`ENABLE_UNITY_COLLECTIONS_CHECKS`).
- Each Gi world has one atomic word that counts readers and marks the writer.
- An overlap raises a fault naming both systems.
- In release builds it compiles to nothing.

### 5.7 Lifetimes

| Thing | Acquired by | Released by | Never released twice because |
|---|---|---|---|
| Gi world | `GiBootstrapSystem`, when a profile singleton exists | its `OnDestroy`, removal of the profile, or a rebuild | `Generation == 0` means there is none |
| Stamp | the registry, keyed by content hash, refcounted per Gi world | the registry, after the last world using it is freed | refcount |
| Entity-bound source | reconcile, through `Place` | reconcile, through `Remove`, once `GiEmitter` is gone or the entity is destroyed (`GiSource` survives as the cleanup component) | `Id == default` means none |
| Pulse | reconcile, through `Place` | Gi, through `Expire` | Gi owns it |
| Columns | each system | `OnDestroy` | one owner |

`Stamp.Free` flushes every world's kernel cache, so stamps are freed only at teardown, never mid-play.

**Rebuild.** A rebuild resets every `GiSource.Id` to `default` in the same main-thread step. A fresh Gi world reuses slots and generations, so this is what keeps a `SourceId` from a freed world from aliasing a source in the new one.

**Code reload.**
- `[OnCodeUnloading]` completes `GiRuntime.All`, frees the Gi worlds and stamps the adapter owns, then calls Gi's runtime teardown (G-2). Nothing native leaks across a reload.
- `[OnCodeLoaded]` reclaims orphans. Any Gi world still listed by `CopyWorldIds`, and any stamp still defined, belongs to a lost adapter instance and is freed. This holds whether or not Unity reloads `Gi.dll` itself.
- The ConfigVar `gi.runtime.reclaim` turns reclaim off, for projects that also use Gi outside the adapter.

**Quit.** BovineLabs `WorldSafeShutdown` disables system groups at quit. Releases happen in `OnDestroy`, so the order in which worlds shut down does not matter.

### 5.8 Determinism contract

- **Same inputs, same bits.** The same profile and inputs give the same field bits on every backend and ISA. Gi's CI already covers the ISAs and .NET; V-1 adds Unity's backends using the golden corpus.
- **Order never changes values.** Field values do not depend on entity, chunk or pulse order. V-2 spawns the same scene in shuffled orders and compares per-tick layer hashes.
- **`SourceId`s are local.** They do depend on order, so they are never serialized, never sent over the network, and never compared across worlds.
- **One writer per order-sensitive state.** Only reconcile writes a source's gain, layer and stamp.
- **Time is ticks, not frames.**
  - The Gi tick count equals the number of fixed ticks since bootstrap. V-2 checks this across catch-up frames.
  - Fade and pulse durations are counted in ticks, so frame rate never affects them.
- **Projection is exact; derived heading is not.**
  - Projection copies coordinates and does no arithmetic, so it is exact everywhere.
  - Heading derived from a quaternion uses `math.atan2`, which reproduces exactly only on one platform.
  - A lockstep game therefore supplies heading from its own deterministic state, through the profile's heading field.

### 5.9 Memory and allocation

- **Managed:** nothing is allocated per tick.
  - Gi spans are built from pointers.
  - Logging goes through BLLogger with `FixedString`, and only on fault transitions.
- **Adapter native memory:** grow-only columns and command lists per system, labelled through BovineLabs `MemoryLabelAllocator` as `Gi/Columns` and `Gi/Commands`.
- **Gi native memory:** Gi's own heap. Unity's memory profiler cannot see it until G-6 routes it through `MallocTracked` with a label.

### 5.10 Failure model

`GiFaults` is a bit set on `GiFrame` that records the tick each bit was first raised. The Field window shows it, and BLLogger logs each transition once.

| Gi result | Adapter response |
|---|---|
| `BatchStatus.StaleRevision` | Recapture the token and retry once. A second failure raises `StaleToken`. |
| `BatchStatus.InvalidWorld` | Rebuild. |
| `InsufficientResults`, `InvalidCommand`, `InvalidReference`, `OverlappingBuffers`, `TooManyCommands`, `InvalidRange` | An adapter bug: raise `Adapter`, and assert in development builds. |
| A `SourceResultStatus` other than `Applied` | Enable `GiFault` on that entity and leave `GiSource` unchanged. The write is retried only when the desired state changes again, so a permanent error cannot storm. |
| `WriteStatus.CapacityReached` from a scale | `GiFault`. |
| `ReadStatus.Partial` or `Outside` | Normal. Per-entity coverage is in the bits. |
| `ReadStatus.InvalidLayer` or `LengthMismatch` | An adapter bug: raise `Adapter`. |
| An exception during bootstrap | Caught at the bootstrap boundary and raised as `Profile`. The world stays absent. |

## 6. Authoring and baking

**Assets**

- **`GiProfile`** holds:
  - the plane: XZ or XY, plus an origin;
  - the position source: `LocalTransform`, or a `float2` field;
  - an optional heading field;
  - grids, each with a power, an origin and a size;
  - layers, as ordered sub-assets;
  - references to stamps;
  - read bindings.
- **`GiLayerAsset`** is a sub-asset of a profile. It has a name and is one of:
  - a base layer, with a residency (`Persistent` or `Transient`) and a precision (`Exact`, `Half`, `Quarter`, `Cell`, or custom phase and turn bits);
  - a derived layer (`Sum`, `Min`, `Max` or `Mask`) over layers above it in the stack, with weights, a shift, or a mask range.
- **`GiStampAsset`** is standalone and can be shared between profiles. Its shape is one of:
  - `Box`, `Tent` or `Bell`, with a width, height and value;
  - `Disk`, `Cone` or `Dome`, with a radius, value and arc;
  - `Raster`, from painted cells or an imported texture mapped to `sbyte`.

**Authoring components**

- `GiProfileAuthoring`: one per subscene that owns a field.
- `GiEmitterAuthoring`.
- `GiSplatAuthoring`.

**Bakers**

Bakers turn authoring into data and do nothing else.

| Input | Baked as |
|---|---|
| Profile | `BlobAssetReference<GiProfileBlob>` on a singleton, with an xxHash3 content hash. The hash drives rebuilds and the netcode compatibility check. |
| Emitter | `GiEmitter`, plus `GiGainRamp`, `GiOriented` or `GiScaled` when set |
| Splat | `GiSplat` |

`GiEmitterBuilder` and `GiSplatBuilder` apply the same components through BovineLabs `IEntityCommands`. Bakers, runtime spawners and tests share this one construction path.

**Validation**

One function set, `Gi.Validation`, is used in three places with the same message each time:
- inspector help boxes;
- baker errors, with the authoring object as context;
- bootstrap faults.

| Rule | Checked by |
|---|---|
| grid power 5..14, scale in range | `TryGrid` |
| at most 32 grids and 32 layers per world; at most 255 stamps across every live profile | `TryLayerCount`, `TryStampCount`, the registry |
| box, tent and bell sizes 1..256 per axis; value −128..127 | `TryRectangle` |
| round radius 1..128, arc 1..360 | `TryRound` |
| gain within ±16 | `TryGain` |
| derived weights ±32767, shift 0..15, inputs above in the stack | `TryDerivedLayer`, stack order |
| scaled footprint at most 512 cells per axis | `TryFootprint` |
| reach at most 4096 cells on the finest grid | `TryReach` |
| an emitter's layer is base and persistent; a splat's layer is base and transient | residency, statically |
| binding output sizes; one writer per output field | binding table, statically |

## 7. Editor tooling

Every tool is built from runtime parts (axiom 11):
- the `GiFrame` read window;
- the GPU mirror (P-1);
- the recorder (E-7).

How tools reach Gi and draw:
- **No direct calls while jobs run.** Windows never call Gi themselves. A `GiInspectorSystem` in `PresentationSystemGroup` completes the `GiFrame` dependency and serves queued probe requests. For impure probes such as explain-cell it also completes `GiRuntime.Writers`. It then publishes results to the window.
- **UI.** Windows are built with UI Toolkit and themed with `BovineThemeRoot` or `BovineThemeUtility.Apply`. Custom elements are `[UxmlElement]`s.

| Tool | What it does | Made of |
|---|---|---|
| **Field window** | Pickers for the ECS world, grid and layer.<br>A heatmap with a diverging colour map, isolines and gradient glyphs.<br>A hover probe for value, gradient, area, max and nearest, with a reach slider. Pinned probes show sparklines over ticks.<br>Overlays for tile occupancy and for tiles changed this tick.<br>A runtime panel showing the backend, Gi plugin, ISA paths in use (`Avx2.IsSupported` and the rest), Gi version, live worlds, and stamps used out of 255. | mirror, probes, G-3 |
| **Scene overlay** | The heatmap on the ground plane, with isolines and gradient glyphs.<br>Stamp footprints for selected emitters, rotated and scaled.<br>Reach discs for bindings.<br>Grid bounds, and which grid is finest at the cursor. | mirror, gizmos |
| **Live edit** | `GiSystemGroup` also runs in the Editor world, so dragging an emitter in an open subscene updates the field in edit mode. | live baking |
| **Stamp Studio** | Edit a stamp and see Gi render it in a scratch world.<br>Sliders for sub-cell phase, angle and scale.<br>Exact and Cell precision side by side.<br>The footprint after scaling, live validation messages, and texture import for raster stamps.<br>Editing during play flushes kernel caches, so expect a hitch. | scratch world, at most one temporary stamp |
| **Profile editor** | A grid list with a coverage map and a memory estimate.<br>A layer stack where derived layers can only take inputs from above them, with a thumbnail per layer.<br>A read-binding table with output field pickers and size checks. | `Gi.Validation` |
| **Explain cell** | Click a cell to see each source's contribution, computed as the value minus the value excluding that source. Sorted, with links to the entities.<br>A derived layer shows its recipe evaluated at the cell.<br>A `saturated` flag appears when the parts do not add up to the clamped total. | exclusion reads on the main thread |
| **Entity inspector** | For `GiEmitter` and `GiSource`: desired against applied state, the live `TryGetSourceSpec`, fault status, and the field value and gradient at the entity for each layer. | `PropertyInspector<T>` |
| **Profiler module** | Counters for sources, pulses, splats, changed tiles, commands, faults, impure reads and Gi bytes (G-3).<br>Markers for each system and Gi call. | `ProfilerCounterValue`, `ProfilerMarker` |
| **ConfigVars** | `gi.sense.batch`, `gi.mirror.budget`, `gi.draw.heatmap`, `gi.guard`, `gi.record`, `gi.record.ticks`, `gi.runtime.reclaim` | BovineLabs ConfigVars |
| **Validator** | The menu item `Gi ▸ Validate` and a build preprocessor. They check profiles, bindings, the plugin variant against the backend, and the stamp budget across profiles, and warn on Mono above 100k sensors. | `IPreprocessBuildWithReport` |
| **Flight recorder** | A ring buffer of inputs per tick (batches, sync columns, and splat columns compressed with LZ4 through `CodecService`), with periodic keyframes.<br>Scrub and replay into a scratch world, asserting per-tick hashes.<br>Export any range as a Gi test stream. | E-7 |

**GPU mirror (P-1).** It keeps a `GraphicsBuffer` copy of chosen (grid, layer) pairs, as a page table with one int per tile plus a pool of 2 KB tiles. Each frame:
1. It asks `ChangedTiles(world, grid, layer, since, …)` which tiles changed.
2. It reserves upload space with BovineLabs `BovineSparseUploader`.
3. `QueryRegion` writes each changed 32×32 tile straight into the reserved bytes.
4. A tile that reads back all zero returns its slot to the pool.

`gi.mirror.budget` caps the tiles uploaded per frame and carries the rest over to the next frame, so cost follows change rather than area. One HLSL shader draws the mirror in the game, the Scene view and the Field window.

## 8. Netcode and save

### N-1: server-authoritative fields

- **What replicates.** `GiEmitter` and `GiSplat` are ghost components, with `[GhostField]` on layer, stamp and gain. `GiSource` is client-local.
- **Who computes the field.** The server and every client each reconcile their own Gi world from the replicated desired state.
- **Who reads it.** Gameplay that matters reads on the server. Clients use their field for presentation.
- **Client tick rate.** A presentation-only client field may run under BovineLabs `LimitedRateNoCatchUpManager`, so Gi cost is capped under load. The server stays on fixed ticks.
- **Pulses** are local to the world that emits them. A game that wants pulses on clients emits them from replicated events on both sides.
- **Profile check.** The server sends its profile hash once on connection. A client whose hash differs refuses to start Gi.

### N-2: lockstep checks

For games whose clients run the same inputs as the server:
- Every *k* ticks, an RPC carries (tick, layer, hash). A mismatch raises `Desync`, which the Field window shows.
- The hash comes from G-7. Until G-7 lands, the adapter computes it from `ChangedTiles` and `QueryRegion`.

### N-3: predicted fields

This comes after this plan. Gi's `Record` and `Rewind` would align with prediction rollback. `Rewind` bumps the revision, so the adapter recaptures the token on every rollback.

### S-1: save

| | What | How |
|---|---|---|
| Saved | `GiEmitter`, `GiSplat`, `GiGainRamp` | BovineLabs `[Save]` |
| Not saved | `GiSource` | `[SaveIgnore]` |
| Saved by the adapter | live pulses: layer, stamp, position, gain and end tick | a mirror the adapter keeps |

- **Load** rebuilds the Gi world, re-places every source, restores pulses with their remaining ticks, and resets every `since`.
- **Fades** restored part-way are exact only once G-5 lands.

## 9. Gi core cards

Every G-card:
- follows `AGENTS.md` in full;
- adds no type outside `World`, `Grid`, `Layer` and `Stamp` (O-U5);
- has a fallback, so the Unity side ships without it (stop rule S5).

| Card | Change | Why Unity needs it | Done when |
|---|---|---|---|
| G-0 | Build for the TFM Unity's CoreCLR loads, if that is not `net10.0` (for example `net8.0`), putting differing APIs behind `#if`. | The intrinsics build on day 0. | Corpus hashes are equal across every TFM and ISA in CI, and `--verify` passes on each TFM. |
| G-1 | Make exclusion reads pure: a read never inserts into the kernel cache or touches the shared canvas, and its output stays bit-identical to today's. | Parallel **Exclude self** bindings. | A differential test against the cached path passes over the fuzz corpus; exclusion reads on 8 threads match serial reads; no gate regresses. |
| G-2 | Add runtime teardown: a `World` method that frees all process memory when no world is live, so `Runtime.Ensure` can start fresh. | Code reload without native leaks. | A test that alternates teardown and use 1000 times keeps RSS flat. |
| G-3 | Expose tooling read-outs as `out` parameters: bytes per store, live tiles, pages, kernel cache entries, live sources. | Field window and profiler module. | Tests show the values match the internal counters. |
| G-4 | Remove process-wide mutable state from write paths: canvas buffers per world, `NativeHeap.Calls` atomic or removed, `ProcessStages` per world. | `Process` on server and client worlds at once; retires `GiRuntime.Writers`. | Two worlds processing on two threads for 10k ticks hash the same as running them one after the other. |
| G-5 | Read back fade and expire schedules, as `TryGetSchedule(world, source, out gain, out ticksLeft, …)`. | Exact save in the middle of a fade. | A save, load and continue run equals an uninterrupted run. |
| G-6 | Add an allocator hook: two unmanaged function pointers, installed before the first allocation. | Gi memory becomes visible under a Unity `MemoryLabel`. | Allocation tests pass with a counting hook, and the default path is unchanged. |
| G-7 | Keep an incremental layer hash in `Process`: an XOR of tile hashes, updated only for changed tiles. | Desync checks and recorder asserts. | After every fuzz stream, the hash equals a full recompute. |
| G-8 | Parallel splat: per-worker histograms merged in a fixed order. Only if V-3 shows splat dominating the frame. | Splat across many cores. | Bit-identical to serial splat; the single-threaded gate is unchanged. |

## 10. Cards

Each card lists what it **Needs** (cards that must land first), what to **Do**, and when it is **Done**. "Equivalence" means that the adapter's field, after the same scenario, has the same per-tick layer hashes as issuing the same Gi calls by hand. It is the receipt for axiom 1.

### Runtime

**U-0 · Package skeleton and plugins.** Needs: nothing.

Do:
- Write `build-plugins.sh`. It builds Gi and Gi.Validation for each plugin TFM with `Deterministic=true` and `ContinuousIntegrationBuild=true`, then copies the DLLs into `Plugins/` next to fixed `.meta` GUIDs.
- Add `package.json` and the asmdefs, with unsafe code allowed only in `Gi.Entities`.
- Write `validate.sh`.

Done when:
- An empty project imports the package with no warnings.
- An EditMode test creates and frees a Gi world.
- Two plugin builds are byte-identical, by sha256.
- `dotnet build Gi.slnx` is untouched.

**U-1 · Frame and bootstrap.** Needs: U-0.

Do:
- Build `GiFrame`, `GiTables`, `GiFaults` and `GiRuntime` (the handles, stamp registry and guard).
- Write `GiBootstrapSystem`: build from a blob, rebuild on a hash change, reset `GiSource` on rebuild, reclaim on code load, release on destroy.
- Have tests construct blobs directly, so this card does not wait for A-2.

Done when:
- Building and freeing 1000 times leaves the live world count and the stamps used where they started.
- Two ECS worlds with one profile share stamps, and both closing frees them.
- A profile with 33 layers raises the `Profile` fault, and no exception escapes.
- Ticks after bootstrap allocate 0 B.

**U-2 · Reconcile and pulses.** Needs: U-1.

Do:
- Add the components.
- Write the add and remove queries, the Burst diff (count, prefix sum, write), the managed `ApplyBatch` job, and the write-back.
- Make `GiGainRamp` fade a new source in from 0 using `CreatedBy`.
- Build the pulse collection.
- Split batches to the cap that `docs/model.md` documents.
- Test that `Place` never returns `default`.

Done when:
- A random spawn, despawn, gain and pulse scenario passes equivalence.
- An emitter on a transient layer faults.
- A tick without spawns or despawns makes no structural change.
- Ticks allocate 0 B.

**U-3 · Projection and sync.** Needs: U-2.

Do:
- Project onto the XZ or XY plane, or read the profile's position field. Add optional heading and scale.
- Filter by chunk change.
- First pin, with a test, which way `TransformRange.Invalid` goes (in or out).
- Write the `Sync` and `SyncTransforms` passes, and scatter faults.

Done when:
- Moves, turns and scales pass equivalence.
- Unchanged chunks cost no Gi work, shown by a counter.
- A cone stamp points along the entity's forward axis.
- Ticks allocate 0 B.

**U-4 · Group, process, fences, guard.** Needs: U-2.

Do:
- Place the group.
- Write `GiProcessSystem`, `GiRuntime.Writers` and `All`, and `GiGuard`.
- Confirm that `FixedStepSimulationSystemGroup` ticks in the Editor world. If it does not, the editor variant sits in `SimulationSystemGroup` under a rate manager.

Done when:
- `Tick(world)` equals the number of fixed ticks across catch-up frames.
- The guard flags a deliberately misordered test system.
- A host-mode test with two worlds passes with the guard on.

**U-5 · Read bindings.** Needs: U-3, U-4.

Do:
- Build the binding blob and the queries.
- Write gather, call and scatter for all seven kinds.
- Run parallel batches in multiples of 64, and exclusion bindings on a single thread.
- Add coverage and found tags, `Period`, and `Since`.

Done when:
- Every kind matches the scalar `TrySense*` (or `Changed`) call for every entity.
- Parallel output is bit-identical to serial output.
- Partly enabled chunks write only the enabled entities, and nothing outside the query is touched.
- With `Period` *n*, each entity is sensed exactly once every *n* ticks.
- Ticks allocate 0 B.

**U-6 · Splat.** Needs: U-4.

Do:
- Write the counting sort by layer, the uniformity flag, and both `Splat` overloads.

Done when:
- Splat passes equivalence.
- The uniform and per-entity overloads give identical fields. This is a Gi invariant, pinned here.
- A disabled `GiSplat` contributes nothing.
- Ticks allocate 0 B.

### Verification

**V-1 · Gi parity on Unity backends.** Needs: U-0.

Do:
- Read the corpus format in `tests/Gi.Tests`.
- Replay every golden stream in a Unity test and compare its hashes against the committed corpus.
- Run it on Mono (EditMode) and IL2CPP (a Linux player), and on CoreCLR when available.

Done when:
- Every hash is equal on Mono and IL2CPP.
- The test is part of `validate.sh`.

**V-2 · ECS determinism and allocation.** Needs: U-5, U-6.

Do:
- Permutation invariance: shuffled spawn order gives the same per-tick hashes.
- Rebuild invariance.
- Catch-up invariance: one frame of three ticks equals three frames of one tick.
- Run the whole group for 100 steady ticks under `Is.Not.AllocatingGCMemory()`, with intrinsics on and off. On CoreCLR, off means `DOTNET_EnableHWIntrinsic=0`.

Done when:
- Everything passes on Mono and IL2CPP.
- CoreCLR passes at V-0.

**V-0 · CoreCLR day 0.** Needs: a CoreCLR build of Unity.

Do:
- Work through Appendix A on any preview as early as one exists, and again on release.
- Enable `Plugins/Gi/coreclr`.
- Record the define and the TFM in this file.

Done when:
- V-1 and V-2 pass on CoreCLR with intrinsics on and off.
- L1–L3 have values in the ledger.

**V-3 · Performance.** Needs: V-2.

Do:
- Build Unity Performance Testing scenes that mirror frames B1, C and W, using the parameters from `benchmarks/`.
- Measure Gi-only time with zero job workers, and wall time with the default workers.
- Cover Mono, IL2CPP and CoreCLR, on the i9 and on the Ryzen 8500G when it is available.

Done when:
- L1–L8 are filled in.
- `unity/perf-baseline.json` (deterministic format) and a per-backend table in the package README are committed.

### Authoring

**A-1 · Assets and inspectors.** Needs: U-0.

Do:
- Create `GiProfile`, the `GiLayerAsset` sub-assets and `GiStampAsset`.
- Build the inspectors with validation help boxes, and the layer stack (O-U4).

Done when:
- Every `Gi.Validation` rule has an inspector test whose message matches `Gi.Validation` exactly.
- Asset YAML is stable across saves.
- No UI path can create a forward layer reference.

**A-2 · Bakers and live baking.** Needs: A-1, U-1.

Do:
- Define the blob layout and its xxHash3 hash.
- Write the authoring components, the builders over `IEntityCommands`, and baker errors with context.
- Make live baking trigger a rebuild.

Done when:
- Baking twice gives identical blob bytes and hash.
- Editing a layer in play mode rebuilds and re-places everything without leaking.
- Every baker error points at its authoring object.

### Presentation and editor

**P-1 · GPU mirror and heatmap.** Needs: U-4.

Do:
- Build the page table, the tile pool, uploads driven by `ChangedTiles` through `BovineSparseUploader`, the per-frame budget, and one HLSL shader that runs in URP, HDRP and the built-in pipeline.

Done when:
- After random ticks, a GPU readback equals `QueryRegion` over the whole layer.
- Uploaded bytes scale with changed tiles.
- Ticks allocate 0 B.

**E-1 · Field window.** Needs: P-1.

Do:
- Build the window as listed in §7, including `GiInspectorSystem` and the probe queue.

Done when:
- Probe values equal `TrySense*` calls on the main thread.
- With the window open, the game's tick still allocates 0 B.
- Closing the window frees its mirror.

**E-2 · Scene overlay and live edit.** Needs: P-1, A-2.

Do:
- Draw the overlays and gizmos from §7.
- Run `GiSystemGroup` in the Editor world.

Done when:
- Dragging an emitter in edit mode updates the overlay on the next editor tick.
- Overlay toggles persist across sessions.

**E-3 · Stamp Studio.** Needs: A-1, U-1.

Do:
- Build the Studio from §7.

Done when:
- For the same phase, angle and scale, the preview hash equals the hash from a runtime world that places the same stamp.
- At most one temporary stamp exists at any time.
- Closing the Studio frees its world and stamp.

**E-4 · Profile editor.** Needs: A-1.

Do:
- Build the grid list with its coverage map and memory estimate, the layer stack with thumbnails, and the read-binding table.

Done when:
- The coverage map agrees with `Covers`.
- Binding size errors appear before any bake.

**E-5 · Explain cell and entity inspector.** Needs: E-1.

Do:
- Build both tools from §7.

Done when:
- In random unsaturated scenes, contributions sum to the value.
- Entity links select the right entity.
- Derived recipes show the correct intermediate values.

**E-6 · Profiler module and ConfigVars.** Needs: U-5.

Do:
- Add the counters, markers and ConfigVars from §7.

Done when:
- The counters appear in the Profiler.
- Every ConfigVar name passes the BovineLabs name regex.
- A command-line override test passes.

**E-7 · Flight recorder.** Needs: U-6, E-1.

Do:
- Build the ring buffer and keyframes, scratch-world replay, per-tick hash asserts, and the exporter to the Gi stream format.

Done when:
- Replaying 10k ticks of the Crowd sample reproduces every live hash.
- An exported stream runs as a test in `tests/Gi.Tests` without edits.

### Netcode, save, docs

**N-1 · Server-authoritative fields.** Needs: V-2. Netcode only.

Done when:
- In a host-mode test, the server and client fields are equal for static sources.
- A profile hash mismatch keeps Gi off on the client and raises a fault.

**N-2 · Lockstep checks.** Needs: N-1.

Done when:
- A desync injected on purpose is reported within *k* ticks.

**S-1 · Save.** Needs: V-2.

Done when:
- Save, load and continue equals an uninterrupted run for emitters and splats.
- Pulses come back with their remaining ticks.
- Mid-fade restores are exact once G-5 lands.

**D-1 · Docs and samples.** Needs: U-5, U-6.

Do:
- Write the package README quick start.
- Add a "Unity boundary" section to `docs/model.md` with the proofs from Appendix C.
- Build three samples:
  - **Crowd:** 1M splatting agents steered by gradient.
  - **Territory:** persistent emitters, a `Mask` layer, and the mirror as an overlay.
  - **Stealth:** pulses for noise, `Nearest` reads, and wake tags.
- Add a sample of a game job calling a pure Gi read through `GiFrame`.

Done when:
- The quick start works from an empty project.
- All samples run on Mono and IL2CPP.

## 11. Schedule and board

| Week | Dates | Runtime | Authoring and editor | Core | Verification |
|---|---|---|---|---|---|
| 1 | Oct 19–25 | U-0, U-1, U-2 | A-1 | G-0 dry run (an extra TFM on a branch), G-1 | V-1 |
| 2 | Oct 26–Nov 1 | U-3, U-4, U-5, U-6 | A-2, E-3 | G-1, G-2, G-3 | V-2 |
| 3 | Nov 2–8 | P-1 | E-1, E-2, E-4, E-5, E-6 | G-4 | V-0 on any CoreCLR preview |
| 4 | Nov 9–14 | N-1, S-1 if time allows | E-7, D-1 | G-5, G-6, G-7 as needed | V-3, final V-0 |
| Day 0 | about Nov 14 | | | G-0 for the real TFM | Appendix A, then flip the plugin define |

The critical path is U-0 → U-1 → U-2 → U-3 and U-4 → U-5 → V-2 → V-3. V-1 runs early because it is the receipt everything else stands on.

| Card | Lane | Needs | State |
|---|---|---|---|
| U-0 | runtime | — | todo |
| U-1 | runtime | U-0 | todo |
| U-2 | runtime | U-1 | todo |
| U-3 | runtime | U-2 | todo |
| U-4 | runtime | U-2 | todo |
| U-5 | runtime | U-3, U-4 | todo |
| U-6 | runtime | U-4 | todo |
| P-1 | presentation | U-4 | todo |
| A-1 | authoring | U-0 | todo |
| A-2 | authoring | A-1, U-1 | todo |
| E-1 | editor | P-1 | todo |
| E-2 | editor | P-1, A-2 | todo |
| E-3 | editor | A-1, U-1 | todo |
| E-4 | editor | A-1 | todo |
| E-5 | editor | E-1 | todo |
| E-6 | editor | U-5 | todo |
| E-7 | editor | U-6, E-1 | todo |
| V-0 | verification | CoreCLR build | todo |
| V-1 | verification | U-0 | todo |
| V-2 | verification | U-5, U-6 | todo |
| V-3 | verification | V-2 | todo |
| N-1 | netcode | V-2 | todo |
| N-2 | netcode | N-1 | todo |
| S-1 | save | V-2 | todo |
| D-1 | docs | U-5, U-6 | todo |
| G-0 … G-8 | core | §9 | todo |

## 12. Prediction ledger and stop rules

These are predictions, not facts. Each row is filled with a measurement by the card named, and a miss is reported, not explained away.

| # | Quantity | Prediction | Basis | Measured by |
|---|---|---|---|---|
| L1 | CoreCLR: Gi time for frame C on one core, against `dotnet` on the same host | ≤ 1.15× | Same RyuJIT; Unity's tiering and GC settings are unknown. | V-0, V-3 |
| L2 | CoreCLR: frame C wall time with sense spread over 8 workers, on the i9 | 3.6–4.6 ms | Writes stay serial (about 3.2 ms); sense is 3.3 ms divided by 8; plus ECS passes. | V-3 |
| L3 | ECS share of frame C (projection, gather, scatter) | ≤ 12% of Gi time | About 16 MB moved per tick by Burst. | V-3 |
| L4 | Mono: Gi time for frame C, against `dotnet` running the `net` build | 4–10× | Mono's JIT running the scalar build. | V-3 |
| L5 | IL2CPP: the same comparison | 1.5–4× | AOT C++ of the scalar build. | V-3 |
| L6 | Managed allocation per steady tick, every backend | 0 B | Axiom 8. | V-2 |
| L7 | Reconcile at 1% churn of 1M sources (10k places and 10k removes) | ≤ 1.5 ms | Per-command cost of `ApplyBatch`. | V-3 |
| L8 | Mirror upload of 512 changed tiles per frame | ≤ 0.5 ms on the main thread | About 1 MB uploaded. | P-1 |

Stop rules:

- **S1. Correctness first.** Any parity or equivalence mismatch stops timing work on that backend until it is fixed.
- **S2. Day-0 fallback.**
  - Trigger: three days after day 0, CoreCLR still cannot load the `net` plugin, even with G-0.
  - Response: ship `netstandard2.1` on CoreCLR too, record the numbers, and keep the define ready.
- **S3. Zero allocation.** Steady-state allocation above 0 B on any backend means the card is not done.
- **S4. Column overhead.** If L3 exceeds 25%, re-plan that pass to call Gi chunk by chunk before adding features.
- **S5. Unity never waits on core.**
  - A G-card that cannot meet the `AGENTS.md` gates does not block Unity.
  - The fallbacks ship instead: single-thread exclusion, serialized worlds, no teardown.

## Appendix A: CoreCLR day-0 checklist

V-0 answers each question on a real build and records the answer here.

1. **Runtime.** Which .NET version and TFM is it? Which C# language version? Which API compatibility levels are offered?
2. **Define.** Which scripting define marks CoreCLR? It becomes the plugin define constraint.
3. **Load.** Does a `net` TFM plugin load in the editor and in a player, and can scripts compile against it?
4. **Intrinsics.** In both the editor and a player, what do these report:
   - `Vector128.IsHardwareAccelerated`, `Vector256.IsHardwareAccelerated`, `Vector512.IsHardwareAccelerated`;
   - `Avx2.IsSupported`, `Avx512F.IsSupported`, `AdvSimd.IsSupported`?
5. **JIT.**
   - What are the tiering and PGO defaults?
   - Do `DOTNET_*` environment variables reach a player?
   - How long does frame C take to reach steady state?
   - Does ReadyToRun or AOT apply to plugins?
6. **Reload.**
   - Are plugin assemblies unloaded when scripts reload?
   - Does automatic statics cleanup touch them?
   - In what order do `[OnCodeUnloading]` and ECS world disposal run?
7. **Jobs.**
   - Do managed jobs run on the workers?
   - Which GC mode is used?
   - Does a GC suspend running managed jobs promptly while they are in Gi's loops?
8. **Burst.** Is behaviour unchanged? Does BovineLabs `BurstTrampoline` still work?
9. **Parity.** Run V-1 and V-2 on CoreCLR, including with `DOTNET_EnableHWIntrinsic=0`.
10. **Numbers.** Run V-3's frames and fill L1–L3.
11. **Flip.**
    1. Enable `Plugins/Gi/coreclr`.
    2. Rerun steps 9 and 10.
    3. Record the define and TFM in §5.1.

## Appendix B: Report format

```text
Card:        <id>
Branch / PR: <name / number>
Host:        <cpu, os>
Backends:    <CoreCLR | Mono | IL2CPP>
Changed:     <files>
Receipts:    <tests run with pass and fail counts; parity hashes; bytes allocated per tick>
Numbers:     <only after receipts; median of N, with the method>
Ledger:      <row: predicted, measured>
Deviations:  <what differs from the card, and why>
Follow-ups:  <risks, new cards>
```

## Appendix C: Unsafe boundary proofs

These go into `docs/model.md` under "Unity boundary" (D-1), as `AGENTS.md` requires for unsafe code.

- **Lifetime.**
  - A column is owned by its system and stays valid from the gather job to the scatter job of the same tick.
  - Those jobs are chained, and the next tick's gather depends on this tick's scatter.
  - Gi keeps no caller pointer past a call: `TransformRange`, splat spans and column reads are all consumed synchronously. U-3 and U-5 cite the `src/Gi` lines that show this.
- **Aliasing.**
  - Each Gi call receives distinct input and output columns.
  - Burst jobs mark columns `[NoAlias]`.
  - `ApplyBatch` rejects overlap with `OverlappingBuffers`.
- **Alignment.**
  - Columns come from Unity allocators, which align to at least 16 bytes.
  - Gi needs only natural alignment. U-5 confirms that the vector paths use unaligned loads.
- **Concurrency.**
  - Within a world, access to `GiFrame` orders the jobs. Across worlds, the `GiRuntime` handles do. `GiGuard` is the receipt (§5.6).
  - Pure reads touch only their own world's pages and pyramids, plus process tables that change only while `All` is complete.
  - Impure reads are serialized with writers until G-1 lands.
