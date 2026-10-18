# Authoring

All authoring is assets plus MonoBehaviours in SubScenes. Baking turns them into the profile blob
and entity components through one construction path; runtime spawners use the same builders.

## Assets (Assets ▸ Create ▸ Gi)

### Profile

The root asset: plane axes and origin, the grid list, the layer stack, the stamp list, and the
bindings.

- A **grid** entry is `Power` (cells per side are `2^power`), `Origin` (min corner) and `Size`
  (world units per side). One grid is enough; several mix resolutions over different rects.
- The **layer stack** is recipe order: a derived layer may only pick layers above it, so the order
  cannot cycle. Thirty-two layers is the cap.
- The profile's hash changes when anything in it changes; baking again and a live edit both rebuild
  the Gi world from it.

### Layers

| Kind | Cells are | Fields |
|---|---|---|
| Persistent | deposited by emitters, kept until the source changes | precision |
| Transient | cleared by every Process; filled by splats each tick | precision |
| Sum | `clamp(round(wA·A + wB·B) / 2^shift)` | inputs, weights, shift |
| Min / Max | cellwise minimum / maximum of the inputs | inputs |
| Mask | `A` where `min ≤ B ≤ max`, else 0 | inputs, band |

Precision sets how positions quantize: `Exact` (8 phase bits), `Half`, `Quarter`, `Cell`, or
custom bit counts.

### Stamps

Shapes: `Box`, `Tent`, `Bell` (width/height), `Disk`, `Cone`, `Dome` (radius, arc), and `Raster`
(imported cells). All sizes are world units, not grid cells; rasters carry a one-unit interpolation
apron. `Value` is the stamp's sample scale; the deposited cell value is gain times sample,
saturating at ±32767.

Stamp Studio (menu **Gi ▸ Stamp Studio**) previews any phase, angle and scale against a real Gi
world and imports textures to raster cells.

## Inspectors

The profile, layer and stamp inspectors show exactly the text `Gi.Validation` produces — nothing
is re-worded — and the profile inspector adds a coverage map, a memory estimate (checked against
the runtime read-outs), and one thumbnail per layer. Saving an asset twice writes byte-identical
YAML; every edit is undoable.

## SubScene authoring

- **Gi Profile Authoring** — holds the profile. One per SubScene; baking checks the whole profile
  and logs every issue naming the authoring object.
- **Gi Emitter Authoring** — profile, layer, stamp, gain (−16..16), and three switches:
  `Moves` (on: the entity syncs position each tick; off: it bakes without `LocalTransform` and is
  placed once from `LocalToWorld`), `Oriented` (also sync heading), `Scaled` (also sync scale).
  `RampTicks` makes gain changes — and a new source's fade-in — a ramp over that many ticks.
  Emitters belong on persistent layers.
- **Gi Splat Authoring** — the same fields for transient layers; a splat deposits every tick it is
  enabled.
- **Gi Sensor Authoring** — one component for read bindings: a profile and the layer it senses, and
  per-kind toggles (value, gradient, area, max, nearest) with the reach, threshold and exclude-self
  fields each kind supports. Baking adds the matching `GiSensor*` output components — reach and
  threshold sit beside the outputs, so a binding can read them per entity through `ReachField` and
  `ThresholdField` — and the nearest found tag `GiSensorFound`, parked disabled until the sense pass
  finds a hit; a miss disables it again on the next tick.

## Bindings

A binding is a profile row: a kind, a layer, and `GiFieldRef` targets — the component type name
and field the result writes into. Coverage tags are the same reference with no field name, on an
enableable component.

| Kind | Writes | Needs |
|---|---|---|
| Value | `short` value | Value ref |
| Gradient | `float2` gradient | Gradient ref |
| ValueGradient | both | both refs |
| Area | `long` sum over reach | Total ref, reach |
| Max | `short` best cell in reach | Value ref, reach |
| Nearest | `short` value + `float2` position of the nearest cell at or above threshold | Value, Position refs; reach and threshold |
| Changed | `bool` — the neighbourhood changed within `Since` ticks | Coverage ref |

`ExcludeSelf` subtracts the entity's own source (persistent layers only). `Period` n senses the
entity once every n ticks and keeps its last outputs between. Reach and threshold can come per
entity from a `float`/`short` field (`ReachField`, `ThresholdField`) instead of constants; the
`GiSensor*` components carry both, so **Gi Sensor Authoring** and the binding rows meet on them.

Binding-size errors surface in the inspector before any bake.

## Baking and live edits

Baking twice gives identical blob bytes and an identical hash. In play mode, editing a layer (or
anything the profile hash covers) rebuilds the Gi world and re-places every source; world and stamp
counts show nothing leaks. Broken authoring logs each issue with the object named.
