# GridInfluence.Io

PNM/PPM weight-map codec and JSON scene runner for `GridInfluence`.

- **Weights as images**: load binary P5 gray maps (8 or 16 bit, big-endian per the PNM spec) with a
  signed bias (`value - (maxval+1)/2`), or unsigned with `signed: false`. Decode writes straight
  into unmanaged memory.
- **World as images**: export any field region at any frame as exact signed 16-bit P5 (`SaveGraySigned`)
  or as a color P6 preview (green = positive, red = negative).
- **Scenes**: a JSON document declares fields (chunk power, decay, spread), stamp clips with
  static/line/circle motion, PPM image layers, and per-frame captures. `SceneRunner` ticks the
  registry and writes the captures — the file-driven equivalent of timeline tracks.

```json
{
  "frames": 120,
  "fields": [ { "key": "threat", "chunkPower": 4, "decayPerMille": 300, "spreadDenominator": 4 } ],
  "clips": [ { "field": "threat", "fromFrame": 0, "toFrame": 119, "weight": 220,
               "shape": { "kind": "disc", "radius": 6 },
               "motion": { "kind": "line", "from": [-20, -20], "to": [24, 20] } } ],
  "images": [ { "field": "terrain", "path": "bases.pgm", "origin": [-32, -32], "everyFrame": true } ],
  "captures": [ { "frame": 119, "field": "threat", "origin": [-32, -32], "size": [64, 64], "path": "threat.ppm", "color": true } ]
}
```

Run `dotnet run --project samples/Influence` for a working scene. IO stays out of the engine: the
core package has no file or JSON dependency.
