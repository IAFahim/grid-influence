# Tools

## Field window (Gi ▸ Field Window)

The field, live: world, grid and layer pickers that follow worlds being created and destroyed; the
heatmap with tile-occupancy and changed-tile overlays; a reach slider; a hover readout of value,
gradient, area, max and nearest; pinned probes with sparklines; and a runtime panel (backend, Gi
plugin and code path, CPU features, live worlds, stamps of 255, and the memory read-outs or n/a).

The window never calls Gi itself — the inspector system serves it, and closing it frees the GPU
mirror. Clicking a cell opens the Explain window at that cell.

## Explain cell and entity inspector (Gi ▸ Explain Cell)

Pick a world, a layer and a cell (or click one in the Field window) to see where the value comes
from: each source's contribution, computed as the cell's value minus its value with that source
excluded, sorted by size, with a button that selects the source's entity. For stamps whose cells are
integers (box, raster, disk, cone, dome) the contributions add up to the value exactly. Tent and bell
stamps round their summed kernel once per cell, so their leave-one-out shares can differ from the
value by up to one per contributing source; the window shows the difference as `unattributed`. A
cell whose value sits at the 32,767 limit shows `saturated`, and then the shares understate what the
sources add.

A derived layer (`Sum`, `Min`, `Max`, `Mask`) shows its recipe evaluated at the cell, one line per
step with the input values, the weighted sum before the shift where there is one, and the result
beside the value the world holds. Select any entity with a `GiEmitter` or `GiSource` — in the
Entities Hierarchy or from a contribution — and the window lists its desired state, its applied
state, the live Gi source (position, heading, scale, gain, layer, stamp), its fault, and the field
value and gradient under it in every layer.

Requests are answered by the inspector system on the main thread after it completes the Gi jobs,
so a click costs one pass over the sources of the world and nothing while the window is idle.

## Scene overlay (Gi ▸ Toggle Scene Overlay Heatmap)

Draws grid bounds, the heatmap lattice, and stamp footprints in the Scene view. Footprints belong to
the selection: select a GameObject that bakes to an emitter (the overlay finds its entity through the
`EntityGuid` the baker leaves), or an entity in the Entities Hierarchy, and its footprint is drawn
rotated and scaled; `Gi ▸ Toggle Scene Overlay All Footprints` draws every emitter instead. `Gi ▸
Toggle Scene Overlay Reach Discs` draws, for each selected entity, one disc per Area, Max, Nearest or
Changed binding that writes to a component the entity has, with the radius from the binding or from
the entity's reach field. Settings persist in a deterministic file under `UserSettings/Gi/`. Dragging
an emitter in edit mode changes the field on the next editor tick.

## Flight recorder (Gi ▸ Flight Recorder)

Set the `gi.record` ConfigVar (or the toggle in the window) and the adapter records every input Gi
receives each tick — the reconcile commands and their results, the sync rows, the splat rows — plus
the per-layer field hashes after `Process`, into a ring of `gi.record.ticks` ticks (default 600),
compressed with LZ4 through BovineLabs' `CodecService` (stored uncompressed if the native library is
missing). A keyframe of every live source, with its fade and expiry state in flight and the tick's
splats, is taken every `gi.record.keyframe` ticks (default 120), so any tick in the window can be
rebuilt.

The window scrubs to a tick (a scratch Gi world restores the nearest earlier keyframe and replays the
ticks after it, checking every hash against the recorded one), compares the replay with the live
field and highlights the tiles that differ, exports a range as a C# test for `tests/Gi.Tests`, and
saves the whole ring to a `.girec` file. The exported file builds a `StreamSetup` from the profile and
runs it through the same differential runner as the fuzz regressions, so a bug seen in play becomes a
Gi regression test without edits: `unity/export-check.sh` records a crowd, exports a range, drops it
into `tests/Gi.Tests`, runs it and removes it again. The stream format has no raster stamps and no
single-input `Sum` layer; a profile that uses one is refused with a named reason.

Start recording before the first tick if you can. A recorder started mid-run adopts the entity sources
that exist but cannot know pulses already in flight or the transient layers' current contents; its
first keyframe then reports that it does not rebuild the field it was taken from.

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
