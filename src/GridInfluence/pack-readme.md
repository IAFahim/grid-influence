# GridInfluence

Chunked, sparse, integer influence fields for .NET: difference-array stamp rasterization,
prefix-sum resolve, per-tick decay/spread across chunk seams, deterministic budgets, retention and
compaction. Zero dependencies, unmanaged data path, 0 B allocation on warm ticks and queries.

Derived from BovineLabs Timeline Grid Influence (MIT, © 2026 BovineLabs).

```csharp
using GridInfluence;

using var front = new InfluenceField(GridSpec.FromPowerOfTwo(5, 256));
using var back = new InfluenceField(GridSpec.FromPowerOfTwo(5, 256));
back.Tick([new Stamp(InfluenceShape.Disc(Int2.Zero, 8, 100), new Int2(10, 10))], tick: 1,
    Stencil.Create(front, decayPerMille: 300, spreadDenominator: 4));
(front, back) = (back, front);
int value = front.AsReader().ReadCell(new Int2(10, 10));
```

Bulk data crosses as unmanaged spans: `WriteRegion`/`ReadRegion`. For JSON scenes and PPM weight
maps see `GridInfluence.Io`. Full semantics, receipts, and the unsafe proof:
[docs/model.md](https://github.com/IAFahim/grid-influence/blob/main/docs/model.md).
