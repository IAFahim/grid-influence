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
