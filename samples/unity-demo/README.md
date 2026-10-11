# Gi Unity demo

Real-time influence-field ecology in Unity 7 (CoreCLR, .NET 10). Eight wolves hunt 120
sheep; sheep steer entirely by field queries; you paint fear and food with the mouse.

## Open

1. Unity Hub → Add project from disk → this folder (first import builds `Library/`).
2. Requires a 7000.x editor with the .NET (CoreCLR) scripting profile. The bundled SDK
   in 7000.0.0a7 is `10.0.303`; if Player Settings still shows .NET Standard 2.1,
   switch Api Compatibility Level to .NET.
3. Menu `Tools > Gi > Create Demo Scene`, then press Play.

## Controls

- Hold LMB on the ground: a fear brush chases the cursor, sheep flee it.
- Hold RMB on the ground: paint food that fades out over ten seconds
  (`World.Fade` + `World.Expire`), sheep graze toward it while it lasts.
- Watch the top-right minimap: red = threat cones, green = food layer.

## What it demonstrates

- Wolves are moving turned sources (`World.Place` + `World.Move` each frame,
  `World.Turn` to their heading, `World.SetGain` when hunting): their threat aura is a
  `Stamp.Cone` vision cone that sweeps as they run, resolved once per frame by a single
  `World.Process`.
- Sheep never touch a grid handle: `World.TrySense` reads threat and food at their own
  position, `World.TrySenseGradient` steers them (per world unit, so speed does not jump
  between the 256² and 128² grids), and personal space comes from
  `World.TrySenseArea(..., exclude: self)` on the herd layer — each sheep stamps its own
  tent there, and exclusion removes exactly its own contribution, so a lone sheep reads 0
  instead of its own aura. No sheep knows anything about the wolves directly.
- Wolves hunt with `World.TrySenseNearest`: the nearest herd cell above a threshold
  within 48 units, in world coordinates — one query, no scan.
- The calmest-cell HUD line reads a derived layer: comfort = `Layer.Sum(food, 1,
  threat, -2)`, maintained per changed tile by `Process`, and `World.QueryMax` lands on
  its peak in one pyramid read.
- The HUD shows live field queries/s and managed alloc/s (the engine's warm path
  allocates 0 B; what you see is IMGUI and demo UI).
- `food 1x == 4 × half` recomputes every second: the 256² and 128² grids over the same
  256-unit world conserve the world integral exactly, live, while sources move.

## The Gi assembly

`Assets/GiDemo/Plugins/Gi.dll` is the `netstandard2.1` build of Gi (the csproj
multi-targets `net10.0;netstandard2.1`). WebGL is IL2CPP in this editor cycle and
compiles scripts against the netstandard profile; a net10.0 dll cannot unify there
(CS1705). Editor/standalone CoreCLR targets can swap in the net10.0 build. Refresh with:

```sh
dotnet build src/Gi/Gi.csproj -c Release
cp src/Gi/bin/Release/netstandard2.1/Gi.dll samples/unity-demo/Assets/GiDemo/Plugins/
```

## Web build from the CLI

Requires the `webgl` module for the editor (`unity install-modules -e 7000.0.0a8 -m webgl`):

```sh
cd samples/unity-demo
unity build . --target WebGL --execute-method GiDemo.Editor.GiDemoBuild.BuildWeb -o Build/WebGL
cd Build/WebGL && python3 -m http.server 8735
```

The build method sets compression to Disabled so a plain static server works; wasm
players do not run from `file://`. The demo uses only the public surface: `World`,
`Grid`, `Layer`, `Stamp`. No wrappers — the same calls the console samples and
benchmarks make.

## Editor version notes

- **7000.0.0a8** is the build editor (project pin). Verified: the WebGL player survives
  long soak plus heavy fear-brush stress with zero console errors and live conservation
  receipts; the Linux CoreCLR player renders under URP.
- **7000.0.0a7 had two player bugs**, kept here for the record: the WebGL player
  trapped mid-run with `RuntimeError: function signature mismatch` from
  `StaticContentLoadUpdate` invoking `Unity.Loading.ContentLoadingSystem.ProcessResults`
  (symbolicated via `debugSymbolMode: External`; the method is compiled in, the
  engine's lookup misses, a fallback invoke traps — disabling engine code stripping
  only trades the trap for a hang at 90% load); and the Linux CoreCLR player presented
  solid black for Built-in Render Pipeline scenes, which is why this demo ships URP.
- The Hub CLI occasionally installs the WebGL module double-nested
  (`WebGLSupport/Editor/Data/PlaybackEngines/WebGLSupport/*`); the build then fails with
  `Build target 'WebGL' not supported` — move the inner directory's contents up to
  `PlaybackEngines/WebGLSupport/` and delete the scaffolding.
- 6000.6.5f1 cannot build this project as-is: the URP 17.7 pipeline assets are newer
  than the URP that editor resolves (17.6), and its preprocessor rejects them.

## Linux build from the CLI

```sh
unity build samples/unity-demo --target StandaloneLinux64 \
  --execute-method GiDemo.Editor.GiDemoBuild.BuildLinux -o samples/unity-demo/Build/Linux64
samples/unity-demo/Build/Linux64/unity-demo -screen-fullscreen 0 -screen-width 1280 -screen-height 720
```
