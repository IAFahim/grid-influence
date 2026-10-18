# Gi Entities

Sparse tiled integer influence fields for Unity Entities.

Gi is a standalone .NET library for influence fields: grids of `short` cells that entities deposit
into and read back. This package runs it inside ECS — entities emit influence, splat into transient
layers, sense the field through bindings, and editor windows show what the field looks like.

The public surface is four handles — `World`, `Grid`, `Layer`, `Stamp` — and this package keeps to it.

## Requirements

- Unity **7000.0.0a9** (CoreCLR editor or IL2CPP; there is no Mono in Unity 7).
- The builtin Entities, Collections, Burst and Mathematics modules of that editor.
- [BovineLabs Core](https://gitlab.com/tertle/com.bovinelabs.core) **2.0.0-pre.6**, pinned by commit.
- [BovineLabs Quill](https://github.com/vex-studio/tertle-monorepo) **2.0.0-pre.5** with BovineLabs Anchor
  **2.0.0-pre.6**, both pinned by commit from the same mirror (Quill itself is subscriber software;
  the mirror commit `eb83b43db8845cf28b14bec4d3170a4e5ace2a51` is the sync that pairs with Core
  2.0.0-pre.6 on Unity 7000.0.0a9).

## Install

Add the dependency pins to `Packages/manifest.json`. The BovineLabs pin that matches this editor is
served by the upstream GitLab repository, not by OpenUPM (its copy is stale):

```json
{
  "dependencies": {
    "com.bovinelabs.anchor": "https://github.com/vex-studio/tertle-monorepo.git?path=/com.bovinelabs.anchor#eb83b43db8845cf28b14bec4d3170a4e5ace2a51",
    "com.bovinelabs.core": "https://gitlab.com/tertle/com.bovinelabs.core.git#82be2794cf7535f6fb598bb704527f41629a8f64",
    "com.bovinelabs.quill": "https://github.com/vex-studio/tertle-monorepo.git?path=/com.bovinelabs.quill#eb83b43db8845cf28b14bec4d3170a4e5ace2a51",
    "com.gi.entities": "https://github.com/IAFahim/grid-influence.git?path=/unity/com.gi.entities"
  }
}
```

For a local checkout, use `"file:../grid-influence/unity/com.gi.entities"` instead of the git URL.

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
The plugin Unity loads today is the scalar netstandard2.1 build of Gi; the vector build waits on
Unity enabling the net10 API level for script assemblies.

## License

MIT, see `LICENSE.md`.
