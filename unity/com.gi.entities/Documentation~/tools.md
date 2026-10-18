# Tools

## Field window (Gi ▸ Field Window)

The field, live: world, grid and layer pickers that follow worlds being created and destroyed; the
heatmap with tile-occupancy and changed-tile overlays; a reach slider; a hover readout of value,
gradient, area, max and nearest; pinned probes with sparklines; and a runtime panel (backend, Gi
plugin and code path, CPU features, live worlds, stamps of 255, and the memory read-outs or n/a).

The window never calls Gi itself — the inspector system serves it, and closing it frees the GPU
mirror.

## Scene overlay (Gi ▸ Toggle Scene Overlay Heatmap)

Draws grid bounds, the heatmap lattice and every emitter's footprint in the Scene view. Settings
persist in a deterministic file under `UserSettings/Gi/`. Dragging an emitter in edit mode changes
the field on the next editor tick. The reach-disc toggle is stored but draws nothing yet, and the
footprints are of every emitter in the Gi world, not only the selection.

## Stamp Studio (Gi ▸ Stamp Studio)

Scratch-world previews of any phase, angle and scale — exact and cell layers — plus texture import
to raster cells. At most one temporary stamp exists at any time; closing the window frees its
world and stamp.

## Profile inspector

Coverage map, memory estimate, one thumbnail per layer, and a field picker for binding targets.

## Profiler module

`Gi Impure Reads` is always 0 (exclusion reads are pure). The module charts:

| Counter | |
|---|---|
| Gi Sources | placed sources |
| Gi Pulses | pulses this tick |
| Gi Splats | splat rows this tick |
| Gi Changed Tiles | tiles the last process published |
| Gi Commands | reconcile commands this tick |
| Gi Faults | fault flags raised |
| Gi Impure Reads | always 0 |
| Gi Bytes | Gi's memory, from `World.Memory` |

Markers: `Gi.Process`, `Gi.Reconcile.Apply`, `Gi.Sync.Apply`, `Gi.Splat.Apply`, `Gi.Sense.Read`.

## ConfigVars

| Name | Default | |
|---|---|---|
| `gi.runtime.keep-orphans` | false | keep Gi worlds and stamps no adapter owns when code reloads |
| `gi.sense.batch` | 4096 | rows per sense batch, rounded down to a multiple of 64 |
| `gi.mirror.budget` | 128 | tiles uploaded to a GPU mirror per frame; the rest carries over |
| `gi.guard` | true | report overlapping Gi readers and writers in checked builds |
| `gi.draw.heatmap` | true | draw the heatmap in the overlay and the Field window |

## Troubleshooting

- **A fault named Profile** — the baked blob failed validation; the bake log names the authoring
  object. No Gi world exists for that profile.
- **InvalidLayer / WrongResidency / InvalidStamp on an entity** — its emitter or splat points at a
  key the profile does not carry, an emitter on a transient layer, or a splat on a persistent one.
- **NonFinite** — an emitter's transform held NaN; it is skipped until it does not.
- **Overlap** — two systems read and wrote one Gi world at once; the fault names both. That is a
  missing `BeginRead`/`EndWrite` in whichever system is yours.
- **The field is one tick late** — senses read resolved state; see the manual's tick order.
- **The heatmap is blank in batch mode** — the mirror needs a compute-capable GPU.

## Performance

Numbers, method and known misses live in `unity/perf.md` in the repository. Two facts worth
knowing up front: the plugin Unity loads is the scalar netstandard2.1 build (about 2x the vector
build on stamp-heavy frames, equal on cell-resolution frames), and the adapter allocates zero
managed bytes on a steady tick.
