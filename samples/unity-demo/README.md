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
- Hold RMB on the ground: paint persistent food, sheep graze toward it.
- Watch the top-right minimap: red = threat layer, green = food layer.

## What it demonstrates

- Wolves are moving sources (`World.Place` + `World.Move` each frame, `World.SetGain`
  when hunting), resolved once per frame by a single `World.Process`.
- Every sheep reads the field with ten `World.Query` calls per frame — no sheep knows
  anything about the wolves directly.
- The HUD shows live field queries/s and managed alloc/s (the engine's warm path
  allocates 0 B; what you see is IMGUI and demo UI).
- `food 1x == 4 × half` recomputes every second: the 256² and 128² grids over the same
  256-unit world conserve the world integral exactly, live, while sources move.

## The Gi assembly

`Assets/GiDemo/Plugins/Gi.dll` is a stock `net10.0` build (0.3.0-alpha.1). Refresh it with:

```sh
dotnet build src/Gi/Gi.csproj -c Release
cp src/Gi/bin/Release/net10.0/Gi.dll samples/unity-demo/Assets/GiDemo/Plugins/
```

The demo uses only the public surface: `World`, `Grid`, `Layer`, `Stamp`. No unsafe code,
no wrappers — the same calls the console samples and benchmarks make.
