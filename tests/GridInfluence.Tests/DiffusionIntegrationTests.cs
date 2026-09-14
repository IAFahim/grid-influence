using Xunit;

namespace GridInfluence.Tests;

public sealed class DiffusionIntegrationTests
{
    private static int[,] StepOracle(int[,] front, Stamp[] stamps, Int2 boxMin, int decay, int spreadDenom)
    {
        var w = front.GetLength(0);
        var h = front.GetLength(1);
        var back = new int[w, h];

        for (var x = 0; x < w; x++)
        for (var y = 0; y < h; y++)
        {
            var self = front[x, y];
            var incoming = Spread(front, x - 1, y, decay, spreadDenom)
                           + Spread(front, x + 1, y, decay, spreadDenom)
                           + Spread(front, x, y - 1, decay, spreadDenom)
                           + Spread(front, x, y + 1, decay, spreadDenom);

            back[x, y] = IntegerMath.DecayKeep(self, decay)
                         - 4 * IntegerMath.Outflow(self, decay, spreadDenom)
                         + incoming;
        }

        foreach (var stamp in stamps)
        {
            var paint = RasterOraclePaint(stamp, boxMin, boxSize(w, h));
            for (var x = 0; x < w; x++)
            for (var y = 0; y < h; y++)
                back[x, y] += paint[x, y];
        }

        return back;

        static Int2 boxSize(int w, int h) => new(w, h);
    }

    private static int Spread(int[,] grid, int x, int y, int decay, int denom)
    {
        if (x < 0 || y < 0 || x >= grid.GetLength(0) || y >= grid.GetLength(1)) return 0;

        return IntegerMath.Outflow(grid[x, y], decay, denom);
    }

    private static int[,] RasterOraclePaint(Stamp stamp, Int2 boxMin, Int2 boxSize)
    {
        var estimate = Rasterizer.EstimateSpanCount(stamp.Shape);
        var buffer = new WeightedRect[Math.Max(estimate, 1)];
        int count;
        unsafe
        {
            fixed (WeightedRect* pointer = buffer)
            {
                var sink = new SpanSink(pointer, estimate);
                Rasterizer.Emit(stamp, ref sink);
                count = sink.Count;
            }
        }

        var grid = new int[boxSize.X, boxSize.Y];
        for (var i = 0; i < count; i++)
        {
            var b = buffer[i].Bounds;
            for (var y = b.Min.Y; y < b.Max.Y; y++)
            for (var x = b.Min.X; x < b.Max.X; x++)
            {
                var lx = x - boxMin.X;
                var ly = y - boxMin.Y;
                if ((uint)lx < (uint)boxSize.X && (uint)ly < (uint)boxSize.Y) grid[lx, ly] += buffer[i].Weight;
            }
        }

        return grid;
    }

    private static int[,] ReadBox(InfluenceField field, Int2 boxMin, Int2 boxSize)
    {
        var box = new int[boxSize.X, boxSize.Y];
        var flat = new int[boxSize.X * boxSize.Y];
        field.ReadRegion(boxMin, boxSize, flat);
        for (var y = 0; y < boxSize.Y; y++)
        for (var x = 0; x < boxSize.X; x++)
            box[x, y] = flat[y * boxSize.X + x];

        return box;
    }

    [Fact]
    public void Stencil_SpreadsAcrossChunkBoundaries_MatchesPerCellOracle()
    {
        const int chunkPower = 4;
        const int decay = 200;
        const int spread = 4;
        var spec = GridSpec.FromPowerOfTwo(chunkPower, uint.MaxValue);

        var front = new InfluenceField(spec);
        var back = new InfluenceField(spec);
        try
        {

        var stamps = new[] { new Stamp(InfluenceShape.SolidRect(Int2.Zero, new Int2(2, 2), 1000), new Int2(15, 15)) };
        var boxMin = new Int2(-16, -16);
        var boxSize = new Int2(64, 64);

        var oracle = new int[boxSize.X, boxSize.Y];
        var tick = 1u;
        for (var step = 0; step < 3; step++)
        {
            back.Tick(step == 0 ? stamps : [], tick, Stencil.Create(front, decay, spread));
            (front, back) = (back, front);
            tick++;

            oracle = StepOracle(oracle, step == 0 ? stamps : [], boxMin, decay, spread);
        }

        var actual = ReadBox(front, boxMin, boxSize);
        for (var y = 0; y < boxSize.Y; y++)
        for (var x = 0; x < boxSize.X; x++)
            Assert.Equal(oracle[x, y], actual[x, y]);
        }
        finally
        {
            front.Dispose();
            back.Dispose();
        }
    }

    [Fact]
    public void DoubleBufferedPair_AccumulatesAndDecays()
    {
        var pair = new FieldPair(new FieldConfig(7, 4, uint.MaxValue, decayPerMille: 300, spreadDenominator: 4));
        try
        {
            var stamp = new Stamp(InfluenceShape.Disc(Int2.Zero, 6, 100), new Int2(8, 8));
            for (var frame = 0; frame < 12; frame++) pair.Step([stamp]);

            var reader = pair.Front.AsReader();
            var peak = reader.ReadCell(new Int2(8, 8));
            Assert.True(peak > 0, "continuously stamped decaying field should hold a positive peak");
            Assert.True(peak < 12 * 100, "decay must cap the equilibrium below the un-decayed sum");

            var edge = reader.ReadCell(new Int2(2, 8));
            Assert.True(edge > 0, "spread must reach neighbouring cells");
        }
        finally
        {
            pair.Dispose();
        }
    }

    [Fact]
    public void Decay_DrainsToExactZero()
    {
        var pair = new FieldPair(new FieldConfig(9, 3, uint.MaxValue, decayPerMille: 500, spreadDenominator: 100));
        try
        {
            pair.Step([new Stamp(InfluenceShape.Disc(Int2.Zero, 5, 64), new Int2(4, 4))]);
            var previous = 0L;
            for (var frame = 0; frame < 400; frame++)
            {
                pair.Step([]);
                var value = pair.Front.AsReader().ReadCell(new Int2(4, 4));
                Assert.True(Math.Abs(value) <= Math.Max(previous, 64), "decay must never increase the magnitude");
                previous = Math.Abs(value);
            }

            Assert.Equal(0, previous);
            Assert.Equal(0, pair.Front.ActiveSlotCount);
        }
        finally
        {
            pair.Dispose();
        }
    }

    [Fact]
    public void Spread_CrossesChunkEdgesBothWays()
    {
        var pair = new FieldPair(new FieldConfig(3, 2, uint.MaxValue, decayPerMille: 1, spreadDenominator: 4));
        try
        {
            pair.Step([new Stamp(InfluenceShape.SolidRect(Int2.Zero, new Int2(1, 1), 1000), new Int2(3, 3))]);
            Assert.Equal(1000, pair.Front.AsReader().ReadCell(new Int2(3, 3)));

            pair.Step([]);

            var reader = pair.Front.AsReader();
            Assert.Equal(3, reader.ReadCell(new Int2(3, 3)));
            Assert.Equal(249, reader.ReadCell(new Int2(4, 3)));
            Assert.Equal(249, reader.ReadCell(new Int2(2, 3)));
            Assert.Equal(249, reader.ReadCell(new Int2(3, 4)));
            Assert.Equal(249, reader.ReadCell(new Int2(3, 2)));
            Assert.Equal(0, reader.ReadCell(new Int2(4, 4)));

            var total = reader.ReadCell(new Int2(3, 3))
                        + reader.ReadCell(new Int2(4, 3))
                        + reader.ReadCell(new Int2(2, 3))
                        + reader.ReadCell(new Int2(3, 4))
                        + reader.ReadCell(new Int2(3, 2));
            Assert.Equal(999, total);
        }
        finally
        {
            pair.Dispose();
        }
    }
}
