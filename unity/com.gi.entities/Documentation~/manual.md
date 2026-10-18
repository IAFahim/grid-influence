# Manual

Gi computes influence fields: sparse grids of `short` cells over a plane. Sources deposit stamps
into layers, derived layers combine other layers per cell, and queries read values, gradients,
areas, maxima and nearest cells back. This package maps that onto ECS.

## The mapping

| Gi | In this package |
|---|---|
| `World` | one Gi world per ECS world that has a profile, owned by the bootstrap system |
| `Grid` | declared in the profile asset; a grid is `2^power` cells per side covering `size` world units from its origin corner |
| `Layer` | declared in the profile asset; persistent, transient, or derived |
| `Stamp` | declared in the profile asset; content-hashed and shared between worlds |

`GiFrame` is the bridge: a singleton on the bootstrap system's entity holding the Gi world id, the
token, the tick, the generation, the id tables and the fault word. Reading it is the capability to
read the field.

## One fixed tick

The adapter lives in `GiSystemGroup`, inside `FixedStepSimulationSystemGroup`, ordered last. Each
fixed tick it runs, in order:

1. **Bootstrap** — builds or rebuilds the Gi world when the baked profile hash changes.
2. **Reconcile** — turns the difference between the desired state (`GiEmitter` components) and the
   applied state (`GiSource`) into one deterministic command batch: place, remove, fade, set-gain.
3. **Sync** — moves, turns and rescales placed sources whose transforms changed, reading only the
   chunks a change filter marked.
4. **Splat** — every enabled `GiSplat` deposits into its transient layer, one Gi call per layer.
5. **Process** — advances the Gi world one tick: sweeps changed sources, publishes dirty tiles.
6. **Sense** — the profile's bindings read the field in parallel batches and write the results into
   your components.

`Tick` advances by exactly one per fixed step. Sensing reads resolved state, so a source placed or
moved this tick is seen by senses after this tick's process; parented emitters read their
`LocalToWorld`, which updates one frame late (E8).

## Planes

A profile picks the XZ or XY plane and an origin. Positions project onto that plane; on XZ the
plane's y is world z. Heading is measured in radians from +X toward +Z (on XZ), taken from the
entity's rotation — oriented emitters sync it, cones point along it. The samples show the
round-trip: an object that should face plane heading `h` carries rotation `RotateY(pi/2 - h)`.

## Determinism

Field values do not depend on command order, source creation order, splat order, or how a tick's
commands were split. ECS ordering (query, then chunk, then entity) fixes the batch. What you must
keep deterministic yourself: spawn order does not matter, but pulses and headings read from live
input do not replay — derive them from stored state, not from animations or input history.

A profile edit rebuilds the Gi world: every source is re-placed from its `GiEmitter`, and fades in
flight are dropped by design.

## Faults

Failures never throw on the warm path. They raise flags instead: `GiFaultKind` on the frame
(`Profile`, `StaleToken`, `Adapter`, `Capacity`, `Reclaimed`, `Overlap`) and `GiFault` components
on entities (`GiFaultCode`: `InvalidSource`, `InvalidLayer`, `InvalidStamp`, `SourceLimit`,
`NonFinite`, `WrongResidency`, `StaleSource`, `Unknown`). The Field window and the profiler module
show both.

## Where to go next

- [Authoring](authoring.md) — every field of every asset, and what the inspectors check.
- [Runtime](runtime.md) — components, builders, pulses, bindings, and reading the field from a job.
- [Tools](tools.md) — the Field window, the Scene overlay, Stamp Studio, the profiler module, ConfigVars.
