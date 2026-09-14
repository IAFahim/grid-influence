# Scene sample

A complete consumer walkthrough of `GridInfluence` + `GridInfluence.Io`: a JSON scene drives two
fields — a decaying `threat` field stamped by three moving clips (line, orbit, negative annulus)
and a `terrain` field painted every frame from a 64×64 P5 weight image (`assets/bases.pgm`,
signed 16-bit). The run captures PPM frames of the world at frames 0/40/80/119 and prints query
receipts.

```sh
cd samples/scene
dotnet run -c Release                # writes ./frames/*.ppm and prints the transcript
dotnet run -c Release -- --scene assets/scene.json --out /tmp/frames
```

Expected transcript ends with reads over the stamped positions (patrol end ≈ 468, orbit ≈ 398,
negative wall ≈ −138), a terrain controller of +1 on base alpha, a descent gradient away from the
threat ridge, and `safe to place at (22, 18)? = False`.

Open `frames/threat-0080.ppm` in any image viewer that reads PNM (green = positive influence,
red = negative). The scene JSON documents every knob: shapes, motion kinds, image layers, captures.
