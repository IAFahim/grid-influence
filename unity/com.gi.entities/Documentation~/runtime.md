# Runtime

Everything under `Gi.Entities` runs in the fixed-step `GiSystemGroup` (see the
[manual](manual.md)). Game code touches it through components, builders, and reads.

## Components

| Component | Meaning |
|---|---|
| `GiEmitter` | the desired source: layer key, stamp key, gain. Enableable — disabled means removed (no structural change) |
| `GiGainRamp` | gain changes and new sources fade over `Ticks` |
| `GiOriented` / `GiScaled` | also sync heading / scale from the transform |
| `GiSource` | the applied source (`ICleanupComponentData`); `Id.Placed` says whether it is in Gi |
| `GiSplat` | a transient deposit: layer key, stamp key, gain. Enableable |
| `GiFault` | per-entity fault code, raised as an enabled flag |
| `GiStats` | per-tick counters on the frame entity: commands, pulses, sync rows, splats, sensed, changed tiles |

Layer and stamp references are keys — the profile index of the asset — resolved through the
profile (`profile.LayerIndex(layerAsset)`), never through Gi ids.

## Spawning at runtime

The builders are the same path bakers use:

```csharp
var entity = manager.CreateEntity(typeof(LocalTransform));
var commands = new EntityManagerCommands(manager, entity);
GiEmitterBuilder.Apply(ref commands, new GiEmitterSettings
{
    Layer = new GiLayerKey(0),
    Stamp = new GiStampKey(1),
    Gain = 5,
    RampTicks = 4,
    Oriented = true,
});
```

`GiSplatBuilder.Apply` is the splat form. Both add `GiFault` disabled and nothing else. In a baker,
wrap `BakerCommands` around `GetEntity(...)` — the Stealth sample's player baker shows it.

## Pulses

A pulse is a one-shot deposit that lives `Ticks` fixed ticks, optionally fading out. Write it into
the pulse stream from a system that runs before the Gi group:

```csharp
var query = state.GetEntityQuery(new EntityQueryDesc
{
    All = new[] { ComponentType.ReadWrite<GiPulseSingleton>() },
    Options = EntityQueryOptions.IncludeSystems,
});
var writer = query.GetSingleton<GiPulseSingleton>().CreateThreadStream();
writer.Write(new GiPulse { Layer = layer, Stamp = stamp, X = x, Y = y, Gain = 8, Ticks = 2 });
```

## Reading through bindings

Add a binding row to the profile pointing at your component's field and the sense system writes it
every tick ([authoring](authoring.md) lists the kinds). That is the cheap path: Burst gathers,
parallel column reads, per-type scatter, coverage tags — zero allocation.

## Reading directly through GiFrame

For anything a binding cannot express, read the field from your own job. The `Read` sample is the
whole pattern; the load-bearing parts:

```csharp
public void OnCreate(ref SystemState state)
{
    state.RequireForUpdate<GiFrame>();
    _frames = state.GetComponentLookup<GiFrame>(true);
}

public void OnUpdate(ref SystemState state)
{
    _frames.Update(ref state);
    var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
    var prepare = new ReadPrepareJob { Frame = frameEntity, Frames = _frames, ... }.Schedule(state.Dependency);
    var read = GiRuntime.BeginRead(state.Dependency);
    read = JobHandle.CombineDependencies(read, prepare);
    read = new ReadSenseJob { ... }.Schedule(count, 64, read);
    GiRuntime.EndRead(read);
}
```

Three rules:

1. Register `GiFrame` in `OnCreate` and take the singleton **entity** with
   `SystemAPI.GetSingletonEntity<GiFrame>()`. Never `GetSingleton<GiFrame>()` in a warm update —
   it completes jobs.
2. Read the frame data inside a job through a `ComponentLookup<GiFrame>`, like the prepare job
   above; the world id, layer table and plane come from it.
3. Wrap the reading job in `GiRuntime.BeginRead` / `EndRead`. Pure reads (`TrySense*`, the
   `Sense*Column` family without excludes) run in parallel; anything that writes to Gi belongs to
   the adapter's own systems.

The plane projection is `frame.Plane.Project(x, y, z, out var px, out var py)`.

## Fences and the guard

`GiRuntime` keeps two process-wide handles: writers chain behind `BeginWrite`/`EndWrite`, readers
behind `BeginRead`/`EndRead`. In checked builds `GiGuard` reports — with both systems named — if a
reader overlaps a writer on one Gi world. `gi.guard` turns that off. You do not need the guard's
forgiveness; you need its discipline: one writer, then readers, per Gi world, per tick.

## Save and load

Mark nothing yourself: `GiEmitter`, `GiSplat`, `GiGainRamp`, `GiOriented` and `GiScaled` already carry `[Save]` (BovineLabs' attribute), and `GiSource`, which Gi owns, does not. Gi's state is rebuilt from the saved entities: on load the reconcile system places every emitter again.

What entity data cannot say is what was in flight: a gain ramp half way up, and pulses (which have no entity). Call `GiSave.Prepare(entityManager)` just before your save system runs and `GiSave.Release(entityManager)` after. `Prepare` writes a `GiSavedSource` component on every ramped emitter and a `GiSavedPulse` element per live pulse on a `GiSaveHolder` entity, both `[Save]`; the first tick of the loaded world restores them, so the field continues exactly as an uninterrupted run would — a ramp keeps its staircase, a pulse keeps its remaining ticks. Without `Prepare` a loaded ramp restarts from zero and pulses are lost.

Gi's tick counter starts again from zero in a loaded world. Nothing the adapter keeps depends on it.

## Netcode

Netcode for Entities is optional. The `Gi.Entities.Netcode` assembly compiles only when `com.unity.netcode` is installed (it defines `UNITY_NETCODE` through a version define), so a project without it never sees the code.

**What replicates.** Server and client each run their own Gi world from the replicated desired state; gameplay that matters reads the server's field and clients read theirs for presentation. `GiEmitter` and `GiSplat` are not ghost-serialised directly (their key types would need ghost variants); put `GiGhostEmitterAuthoring` (or `GiGhostSplatAuthoring`) beside `GhostAuthoringComponent` and your emitter authoring, and `GiGhostServerSystem` copies layer, stamp, gain and the enabled bit into a `[GhostField]` mirror that `GiGhostClientSystem` copies back into `GiEmitter` / `GiSplat` before the Gi group runs. Positions travel with the ghost's `LocalTransform`. Pulses are local to the world that emits them: emit them from replicated events on both sides.

**Profile check.** After a connection is up the server sends its profile hash once (`GiProfileHashRpc`). A client whose profile hash differs creates a `GiSuppress` entity: `GiBootstrapSystem` releases its Gi world, refuses to build one, and raises the `Profile` fault until the entity goes away. Add `GiSuppress` yourself to keep Gi off for any other reason.

**Lockstep check.** For games whose clients run the same inputs as the server, set the `gi.net.check` ConfigVar to a number of ticks *k* (0 is off). Every Gi world then keeps one incremental hash per layer per tick (a main-thread sync point each tick, so leave it off in production unless you want the check), the server sends `(tick, layer, hash)` every *k* ticks, and a client that holds a different hash for that tick raises the `Desync` fault, logs it once and shows it in the Field window's runtime panel. A desync that starts at tick *t* is reported against the first multiple of *k* after *t*. Start the check only after the field has settled; hashes of a field still being replicated differ legitimately.

**Building and testing here.** The Unity 7000.0.0a9 alpha cannot compile one editor file of the registry package, so `unity/netcode-setup.sh install` downloads `com.unity.netcode` 6.7.0 into `unity/project/Packages` (git-ignored), removes that file's incompatible half, drops the package's own editor tests and lets the Gi netcode tests reach its test-world helpers. `unity/netcode-setup.sh remove` takes it out again and restores `packages-lock.json`. With the package embedded, `unity/validate.sh editmode` also runs the netcode tests (two real server and client worlds over the loopback).

