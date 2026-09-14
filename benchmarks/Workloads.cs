using GridInfluence;

namespace Benchmarks;

internal static class Fixtures
{
    public static Stamp[] BuildStamps(int count, int extent)
    {
        var stamps = new Stamp[count];
        var state = 0x9E3779B9u;
        for (var i = 0; i < count; i++)
        {
            state = state * 1664525u + 1013904223u;
            var x = (int)(state % (uint)(extent - 96)) + 48;
            state = state * 1664525u + 1013904223u;
            var y = (int)(state % (uint)(extent - 96)) + 48;
            var radius = 8;
            stamps[i] = i % 2 == 0
                ? new Stamp(InfluenceShape.Disc(Int2.Zero, radius, 100), new Int2(x, y))
                : new Stamp(InfluenceShape.SolidRect(new Int2(-6, -6), new Int2(12, 12), 60), new Int2(x, y));
        }

        return stamps;
    }

    public const int DecayPerMille = 40;
    public const int SpreadDenominator = 4;
}

internal sealed class NaiveField
{
    private int[] _cells;
    private int[] _next;
    private readonly int _extent;
    private readonly int _decay;
    private readonly int _spread;

    public NaiveField(int extent, int decay, int spread)
    {
        _extent = extent;
        _decay = decay;
        _spread = spread;
        _cells = new int[extent * extent];
        _next = new int[extent * extent];
    }

    public int Extent => _extent;

    public int this[int x, int y] => _cells[y * _extent + x];

    public void Tick(ReadOnlySpan<Stamp> stamps)
    {
        if (_decay > 0) DecaySpread();
        else Array.Clear(_cells);

        foreach (var stamp in stamps) Paint(stamp);
    }

    private void DecaySpread()
    {
        var extent = _extent;
        for (var y = 0; y < extent; y++)
        {
            var row = y * extent;
            for (var x = 0; x < extent; x++)
            {
                var self = _cells[row + x];
                var incoming = Outflow(x - 1, y) + Outflow(x + 1, y) + Outflow(x, y - 1) + Outflow(x, y + 1);
                _next[row + x] = IntegerMath.DecayKeep(self, _decay)
                                 - 4 * IntegerMath.Outflow(self, _decay, _spread)
                                 + incoming;
            }
        }

        (_cells, _next) = (_next, _cells);
    }

    private int Outflow(int x, int y)
    {
        if ((uint)x >= (uint)_extent || (uint)y >= (uint)_extent) return 0;

        return IntegerMath.Outflow(_cells[y * _extent + x], _decay, _spread);
    }

    private void Paint(in Stamp stamp)
    {
        var shape = stamp.Shape;
        var origin = stamp.Origin;
        var weight = shape.Weight;
        if (shape.Kind == ShapeKind.Disc)
        {
            var radius = shape.DiscRadius;
            var r2 = (long)radius * radius;
            for (var dy = -radius; dy <= radius; dy++)
            {
                var y = origin.Y + dy;
                if ((uint)y >= (uint)_extent) continue;

                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + (long)dy * dy > r2) continue;

                    var x = origin.X + dx;
                    if ((uint)x < (uint)_extent) _cells[y * _extent + x] += weight;
                }
            }
        }
        else
        {
            var min = origin + shape.RectMin;
            var max = min + shape.RectSize;
            for (var y = Math.Max(0, min.Y); y < Math.Min(_extent, max.Y); y++)
            for (var x = Math.Max(0, min.X); x < Math.Min(_extent, max.X); x++)
                _cells[y * _extent + x] += weight;
        }
    }
}

internal sealed class PipelineField : IDisposable
{
    private InfluenceField _front;
    private InfluenceField _back;
    private uint _tick;

    public PipelineField(int chunkPower, uint retention = uint.MaxValue)
    {
        var spec = GridSpec.FromPowerOfTwo(chunkPower, retention);
        _front = new InfluenceField(spec);
        _back = new InfluenceField(spec);
    }

    public InfluenceField Front => _front;

    public FieldStats Tick(ReadOnlySpan<Stamp> stamps)
    {
        _tick++;
        var stencil = Stencil.Create(_front, Fixtures.DecayPerMille, Fixtures.SpreadDenominator);
        var stats = _back.Tick(stamps, _tick, stencil);
        (_front, _back) = (_back, _front);
        return stats;
    }

    public FieldStats TickNoDecay(ReadOnlySpan<Stamp> stamps)
    {
        _tick++;
        return _front.Tick(stamps, _tick);
    }

    public void Dispose()
    {
        _front.Dispose();
        _back.Dispose();
    }
}
