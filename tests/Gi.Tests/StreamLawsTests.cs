using Xunit;

namespace Gi.Tests;

public sealed unsafe class StreamLawsTests
{
    private const int Side = 64;
    private const int Cells = Side * Side;

    private static byte Field(byte world, Layer.Precision precision)
    {
        Grid.New(world, 6, 0f, 0f, Side);
        return Layer.New(world, precision);
    }

    private static void Publish(byte world, byte layer, short* page)
    {
        World.QueryRegion(world, 0, layer, 0, 0, Side, Side, page);
    }

    [Fact]
    public void Superposition_UnsaturatedBoxAndTurnedDepositsAreAdditive()
    {
        var box = Stamp.Box(5, 4, 7);
        var rng = new SplitMix64(41);
        var sum = new short[Cells];
        var pageA = new short[Cells];
        var pageB = new short[Cells];
        for (var seed = 0; seed < 24; seed++)
        {
            var angle = rng.NextUnit() * 6.28f;
            var turned = seed % 2 == 1;
            var both = World.New();
            var onlyA = World.New();
            var onlyB = World.New();
            var bothLayer = Field(both, Layer.Precision.Exact);
            var layerA = Field(onlyA, Layer.Precision.Exact);
            var layerB = Field(onlyB, Layer.Precision.Exact);
            var ax = 8f + rng.NextUnit() * 40;
            var ay = 8f + rng.NextUnit() * 40;
            var bx = 8f + rng.NextUnit() * 40;
            var by = 8f + rng.NextUnit() * 40;
            var gainA = (sbyte)rng.NextInt(1, 9);
            var gainB = (sbyte)rng.NextInt(-8, 9);
            var a = World.Place(both, bothLayer, ax, ay, box, gainA);
            var b = World.Place(both, bothLayer, bx, by, box, gainB);
            var aOnly = World.Place(onlyA, layerA, ax, ay, box, gainA);
            var bOnly = World.Place(onlyB, layerB, bx, by, box, gainB);
            if (turned)
            {
                World.Turn(both, a, angle);
                World.Turn(both, b, angle);
                World.Turn(onlyA, aOnly, angle);
                World.Turn(onlyB, bOnly, angle);
            }

            World.Process(both);
            World.Process(onlyA);
            World.Process(onlyB);

            fixed (short* sumPointer = sum, aPointer = pageA, bPointer = pageB)
            {
                Publish(both, bothLayer, sumPointer);
                Publish(onlyA, layerA, aPointer);
                Publish(onlyB, layerB, bPointer);
                for (var i = 0; i < Cells; i++) Assert.Equal((short)(pageA[i] + pageB[i]), sum[i]);
            }

            World.Free(both);
            World.Free(onlyA);
            World.Free(onlyB);
        }

        Assert.True(Stamp.Free(box));
    }

    [Fact]
    public void Translation_ByWholeCellsShiftsPagesExactly()
    {
        var stamps = new[] { Stamp.Box(5, 4, 9), Stamp.Tent(6, 5, -7) };
        var rng = new SplitMix64(97);
        var nearPage = new short[Cells];
        var farPage = new short[Cells];
        foreach (var precision in new[] { Layer.Precision.Exact, Layer.Precision.Quarter })
        foreach (var stamp in stamps)
        {
            var dx = rng.NextInt(4, 13);
            var dy = rng.NextInt(4, 13);
            var x = 6f + rng.NextUnit() * 20;
            var y = 6f + rng.NextUnit() * 20;
            var near = World.New();
            var far = World.New();
            var nearLayer = Field(near, precision);
            var farLayer = Field(far, precision);
            World.Place(near, nearLayer, x, y, stamp, 5);
            World.Place(far, farLayer, x + dx, y + dy, stamp, 5);
            World.Process(near);
            World.Process(far);

            fixed (short* nearPointer = nearPage, farPointer = farPage)
            {
                Publish(near, nearLayer, nearPointer);
                Publish(far, farLayer, farPointer);
            }

            for (var cy = 0; cy < Side - 14 - dy; cy++)
            for (var cx = 0; cx < Side - 14 - dx; cx++)
                Assert.Equal(nearPage[cy * Side + cx], farPage[cy * Side + cx + dy * Side + dx]);

            World.Free(near);
            World.Free(far);
        }

        foreach (var stamp in stamps) Stamp.Free(stamp);
    }

    [Fact]
    public void IdleProcess_IsTheIdentityOnPublishedState()
    {
        var box = Stamp.Box(4, 4, 6);
        var rng = new SplitMix64(1_414);
        var world = World.New();
        var layer = Field(world, Layer.Precision.Exact);
        for (var i = 0; i < 3; i++)
        {
            World.Place(world, layer, 8f + rng.NextUnit() * 40, 8f + rng.NextUnit() * 40, box,
                (sbyte)rng.NextInt(1, 12));
            World.Process(world);
        }

        var page = stackalloc short[Cells];
        var after = stackalloc short[Cells];
        Publish(world, layer, page);
        var sum = World.Query(world, 0, layer, 0, 0, Side, Side);
        var max = World.QueryMax(world, 0, layer, out var maxX, out var maxY);
        var tick = World.Tick(world);
        var tiles = stackalloc int[8];
        Assert.True(World.ChangedTiles(world, 0, layer, tiles) > 0);

        for (var i = 0; i < 3; i++) World.Process(world);

        Publish(world, layer, after);
        for (var i = 0; i < Cells; i++) Assert.Equal(page[i], after[i]);
        Assert.Equal(sum, World.Query(world, 0, layer, 0, 0, Side, Side));
        var idleMax = World.QueryMax(world, 0, layer, out var idleX, out var idleY);
        Assert.Equal(max, idleMax);
        Assert.Equal((maxX, maxY), (idleX, idleY));
        Assert.Equal(0, World.ChangedTiles(world, 0, layer, tiles));
        Assert.Equal(0, World.ChangedTiles(world, 0, layer, tick, tiles));

        World.Free(world);
        Assert.True(Stamp.Free(box));
    }

    [Fact]
    public void CommandsOnDistinctSourcesCommute()
    {
        var box = Stamp.Box(5, 5, 8);
        var tent = Stamp.Tent(4, 6, -9);
        var rng = new SplitMix64(7_777);
        var firstPage = new short[Cells];
        var secondPage = new short[Cells];
        foreach (var precision in new[] { Layer.Precision.Exact, Layer.Precision.Quarter })
        {
            var first = World.New();
            var second = World.New();
            var firstLayer = Field(first, precision);
            var secondLayer = Field(second, precision);
            var ax = 10f + rng.NextUnit() * 20;
            var ay = 10f + rng.NextUnit() * 20;
            var bx = 10f + rng.NextUnit() * 20;
            var by = 10f + rng.NextUnit() * 20;
            var a1 = World.Place(first, firstLayer, ax, ay, box, 4);
            var b1 = World.Place(first, firstLayer, bx, by, tent, 6);
            var b2 = World.Place(second, secondLayer, bx, by, tent, 6);
            var a2 = World.Place(second, secondLayer, ax, ay, box, 4);

            var na = 14f + rng.NextUnit() * 20;
            var nb = 14f + rng.NextUnit() * 20;
            var ta = rng.NextUnit() * 6.28f;
            var tb = rng.NextUnit() * 6.28f;
            var ga = (sbyte)rng.NextInt(1, 13);
            var gb = (sbyte)rng.NextInt(1, 13);

            World.Move(first, a1, na, na);
            World.Turn(first, a1, ta);
            World.Scale(first, a1, 2f);
            World.SetGain(first, a1, ga);
            World.Move(first, b1, nb, nb);
            World.Turn(first, b1, tb);
            World.Scale(first, b1, 1.5f);
            World.SetGain(first, b1, gb);

            World.Move(second, b2, nb, nb);
            World.Turn(second, b2, tb);
            World.Scale(second, b2, 1.5f);
            World.SetGain(second, b2, gb);
            World.Move(second, a2, na, na);
            World.Turn(second, a2, ta);
            World.Scale(second, a2, 2f);
            World.SetGain(second, a2, ga);

            World.Process(first);
            World.Process(second);

            fixed (short* firstPointer = firstPage, secondPointer = secondPage)
            {
                Publish(first, firstLayer, firstPointer);
                Publish(second, secondLayer, secondPointer);
            }

            for (var i = 0; i < Cells; i++) Assert.Equal(firstPage[i], secondPage[i]);
            World.Free(first);
            World.Free(second);
        }

        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(tent));
    }

    [Fact]
    public void RewindAfterRecordRestoresTheRecordedField()
    {
        var box = Stamp.Box(5, 5, 7);
        var rng = new SplitMix64(31_337);
        var world = World.New();
        var layer = Field(world, Layer.Precision.Exact);
        var keeper = World.Place(world, layer, 12f, 12f, box, 5);
        World.Process(world);
        World.Record(world);

        var page = stackalloc short[Cells];
        var after = stackalloc short[Cells];
        Publish(world, layer, page);
        var sum = World.Query(world, 0, layer, 0, 0, Side, Side);
        World.TryGetSourceSpec(world, keeper, out var recorded);

        World.Move(world, keeper, 40f + rng.NextUnit() * 10, 40f + rng.NextUnit() * 10);
        World.Turn(world, keeper, 2.5f);
        World.Scale(world, keeper, 3f);
        World.SetGain(world, keeper, 11);
        World.Place(world, layer, 30f, 30f, box, 9);
        var doomed = World.Place(world, layer, 20f, 20f, box, 3);
        World.Remove(world, doomed);
        World.Process(world);
        Publish(world, layer, after);
        var pagesDiffer = false;
        for (var i = 0; i < Cells; i++)
            if (page[i] != after[i])
            {
                pagesDiffer = true;
                break;
            }

        Assert.True(pagesDiffer);
        Assert.False(World.TryGetSourceSpec(world, doomed, out _));

        World.Rewind(world);
        World.Process(world);

        Publish(world, layer, after);
        for (var i = 0; i < Cells; i++) Assert.Equal(page[i], after[i]);
        Assert.Equal(sum, World.Query(world, 0, layer, 0, 0, Side, Side));
        Assert.True(World.TryGetSourceSpec(world, keeper, out var rewound));
        Assert.Equal(recorded.X, rewound.X);
        Assert.Equal(recorded.Y, rewound.Y);
        Assert.Equal(recorded.HeadingRadians, rewound.HeadingRadians);
        Assert.Equal(recorded.Scale, rewound.Scale);
        Assert.Equal(recorded.Gain, rewound.Gain);

        World.Free(world);
        Assert.True(Stamp.Free(box));
    }

    [Fact]
    public void ExcludingOneSourceSensesTheFieldWithoutIt()
    {
        var box = Stamp.Box(6, 6, 9);
        var rng = new SplitMix64(55);
        for (var seed = 0; seed < 16; seed++)
        {
            var turned = seed % 4 == 3;
            var withBoth = World.New();
            var withOne = World.New();
            var bothLayer = Field(withBoth, Layer.Precision.Exact);
            var oneLayer = Field(withOne, Layer.Precision.Exact);
            var ax = 10f + rng.NextUnit() * 12;
            var ay = 10f + rng.NextUnit() * 12;
            var bx = 18f + rng.NextUnit() * 24;
            var by = 18f + rng.NextUnit() * 24;
            var a = World.Place(withBoth, bothLayer, ax, ay, box, 5);
            var lonely = World.Place(withBoth, bothLayer, bx, by, box, 7);
            var aOnly = World.Place(withOne, oneLayer, ax, ay, box, 5);
            if (turned)
            {
                World.Turn(withBoth, a, 0.6f);
                World.Turn(withBoth, lonely, 2.1f);
                World.Turn(withOne, aOnly, 0.6f);
            }

            World.Process(withBoth);
            World.Process(withOne);

            for (var cy = 2; cy < Side; cy += 3)
            for (var cx = 2; cx < Side; cx += 3)
            {
                var expected = World.TrySense(withOne, oneLayer, cx + 0.5f, cy + 0.5f, out var value);
                var actual = World.TrySense(withBoth, bothLayer, cx + 0.5f, cy + 0.5f, lonely, out var excluded);
                Assert.Equal(expected, actual);
                Assert.Equal(value, excluded);
            }

            World.Free(withBoth);
            World.Free(withOne);
        }

        Assert.True(Stamp.Free(box));
    }

    [Fact]
    public void MoveSmallerThanTheLayerQuantumChangesNoPublishedBytes()
    {
        var box = Stamp.Box(5, 4, 6);
        var tent = Stamp.Tent(6, 4, 8);
        var stamps = new[] { box, tent };
        var precisions = new[]
        {
            Layer.Precision.Exact, Layer.Precision.Half, Layer.Precision.Quarter, Layer.Precision.Cell
        };
        var rng = new SplitMix64(909_909);
        var page = new short[Cells];
        var after = new short[Cells];
        foreach (var precision in precisions)
        foreach (var stamp in stamps)
        {
            var quantum = 1f / (1 << precision.PhaseBits);
            for (var trial = 0; trial < 6; trial++)
            {
                var world = World.New();
                var layer = Field(world, precision);
                var x = 10f + rng.NextUnit() * 20;
                var y = 10f + rng.NextUnit() * 20;
                var source = World.Place(world, layer, x, y, stamp, 6);
                if (trial % 2 == 1) World.Turn(world, source, 1.25f);
                World.Process(world);

                var nx = x;
                var ny = y;
                int attempts;
                fixed (short* pagePointer = page)
                {
                    Publish(world, layer, pagePointer);
                }

                for (attempts = 0; attempts < 64; attempts++)
                {
                    nx = x + (rng.NextUnit() - 0.5f) * quantum;
                    ny = y + (rng.NextUnit() - 0.5f) * quantum;
                    if (!World.PositionChanged(World.GetContext(world), layer, x, y, nx, ny)) break;
                }

                Assert.True(attempts < 64);

                World.Move(world, source, nx, ny);
                World.Process(world);
                fixed (short* afterPointer = after)
                {
                    Publish(world, layer, afterPointer);
                }

                for (var i = 0; i < Cells; i++) Assert.Equal(page[i], after[i]);
                World.Free(world);
            }
        }

        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(tent));
    }
}
