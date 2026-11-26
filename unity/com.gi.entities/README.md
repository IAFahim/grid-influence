# Gi Entities

Sparse tiled integer influence fields for Unity Entities.

Gi is a standalone .NET library for influence fields: grids of `short` cells that entities deposit
into and read back. This package runs it inside ECS — entities emit influence, splat into transient
layers, sense the field through bindings, and editor windows show what the field looks like.

The public surface is four handles — `World`, `Grid`, `Layer`, `Stamp` — and this package keeps to it.

## Requirements

- Unity **7000.0.0a21** (CoreCLR editor or IL2CPP; there is no Mono in Unity 7).
- The builtin Entities, Collections, Burst and Mathematics modules of that editor.
- Zero external package dependencies. Optional integrations activate automatically when installed or explicitly defined: `GI_QUILL` (debug field draw), `GI_SAVANNA` (save tags), `GI_BOVINELABS_CORE`.

## Install

Add `com.gi.entities` to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.gi.entities": "https://github.com/IAFahim/grid-influence.git?path=/unity/com.gi.entities"
  }
}
```

For a local checkout, use `"file:../grid-influence/unity/com.gi.entities"` instead of the git URL.
For deterministic unmanaged capture/restore (including ECS rollback/rewind without Savanna), call `GiWorldSnapshot.Capture(EntityManager, NativeList<byte>)` and `GiWorldSnapshot.Restore(EntityManager, ...)`.

The package ships its Gi plugin DLLs committed, so a git or file install works without building
anything first.

## Quick start

1. Create the assets: **Gi ▸ Profile**, **Gi ▸ Layer**, **Gi ▸ Stamp** (menu: Assets ▸ Create ▸ Gi).
   Give the profile one grid, one persistent layer, one stamp, and add the layer and stamp to the
   profile's lists.
2. Create a SubScene and put a GameObject with **Gi Profile Authoring** in it, the profile assigned.
3. In the same SubScene, add GameObjects with **Gi Emitter Authoring**: assign the profile, layer and
   stamp, set a gain. `Moves` off means the emitter never needs a transform sync.
4. Press Play. The field exists once per fixed tick; open **Gi ▸ Field Window** to look at it.

The [manual](Documentation~/manual.md) continues from here; [authoring](Documentation~/authoring.md)
covers every asset field, [runtime](Documentation~/runtime.md) the components and systems,
[tools](Documentation~/tools.md) the windows and ConfigVars.

## Samples

Import them from the Package Manager's Samples list, open the scene, press Play.

| Sample | Shows |
|---|---|
| **Crowd** | thousands of agents that splat noise into a transient layer and steer up the gradient of a derived `Sum` layer toward goals |
| **Territory** | two teams of persistent emitters on one presence layer, a `Mask` layer that keeps the contested border band, and banner probes reading a Value binding |
| **Stealth** | guards with vision-cone emitters, a player whose footsteps splat noise, run-key pulses, and a `Nearest` binding that wakes the guards |
| **Read** | a game job that reads the field directly through `GiFrame` behind the read fence, no binding involved |

## Performance

Measured numbers, method and known misses live in [`unity/perf.md`](../perf.md) in the repository.
On Unity 7 (CoreCLR), `com.gi.entities` loads the vector CoreCLR `Gi` plugin and runs `Process`, `Apply`, `Sync`, `Splat`, and `Sense` jobs under Burst.

## License

MIT, see `LICENSE.md`.
