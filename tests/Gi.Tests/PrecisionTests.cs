using Xunit;

namespace Gi.Tests;

public sealed unsafe class PrecisionTests
{
    [Fact]
    public void Precision_PresetsAndTryCreateValidateBounds()
    {
        Assert.Equal((8, 16), (Layer.Precision.Exact.PhaseBits, Layer.Precision.Exact.TurnBits));
        Assert.Equal((1, 16), (Layer.Precision.Half.PhaseBits, Layer.Precision.Half.TurnBits));
        Assert.Equal((2, 16), (Layer.Precision.Quarter.PhaseBits, Layer.Precision.Quarter.TurnBits));
        Assert.Equal((0, 16), (Layer.Precision.Cell.PhaseBits, Layer.Precision.Cell.TurnBits));

        for (var phase = 0; phase <= 8; phase++)
        for (var turn = 0; turn <= 16; turn++)
        {
            Assert.True(Layer.Precision.TryCreate(phase, turn, out var created));
            Assert.Equal((phase, turn), (created.PhaseBits, created.TurnBits));
        }

        foreach (var (phase, turn) in new[] { (-1, 16), (9, 16), (0, -1), (0, 17), (-5, 99) })
        {
            Assert.False(Layer.Precision.TryCreate(phase, turn, out var fallback));
            Assert.Equal((8, 16), (fallback.PhaseBits, fallback.TurnBits));
        }
    }

    [Fact]
    public void Layer_TryGetPrecisionReportsBaseLayersAndRejectsDerivedAndInvalid()
    {
        var world = World.New();
        try
        {
            var exact = Layer.New(world);
            var cell = Layer.New(world, Layer.Precision.Cell);
            var quarter = Layer.New(world, Layer.Precision.Quarter);
            Assert.True(Layer.Precision.TryCreate(3, 6, out var custom));
            var customLayer = Layer.New(world, custom);
            var derived = Layer.Sum(world, exact, 1, cell, 1);

            Assert.True(Layer.TryGetPrecision(world, exact, out var pExact));
            Assert.Equal((8, 16), (pExact.PhaseBits, pExact.TurnBits));
            Assert.True(Layer.TryGetPrecision(world, cell, out var pCell));
            Assert.Equal((0, 16), (pCell.PhaseBits, pCell.TurnBits));
            Assert.True(Layer.TryGetPrecision(world, quarter, out var pQuarter));
            Assert.Equal((2, 16), (pQuarter.PhaseBits, pQuarter.TurnBits));
            Assert.True(Layer.TryGetPrecision(world, customLayer, out var pCustom));
            Assert.Equal((3, 6), (pCustom.PhaseBits, pCustom.TurnBits));

            Assert.False(Layer.TryGetPrecision(world, derived, out var pDerived));
            Assert.Equal((0, 0), (pDerived.PhaseBits, pDerived.TurnBits));
            Assert.False(Layer.TryGetPrecision(world, 99, out _));
            Assert.False(Layer.TryGetPrecision(250, exact, out _));

            World.Clear(world);
            Assert.True(Layer.TryGetPrecision(world, customLayer, out pCustom));
            Assert.Equal((3, 6), (pCustom.PhaseBits, pCustom.TurnBits));
            Assert.False(Layer.TryGetPrecision(world, derived, out _));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void SubPrecisionMovesAndTurns_SkipEnqueuingAndLeavePagesAndEpochsIdentical(int phaseBits)
    {
        Assert.True(Layer.Precision.TryCreate(phaseBits, 4, out var precision));
        var world = World.New();
        var box = Stamp.Box(5, 5, 60);
        var cone = Stamp.Cone(6, 80, 120);
        try
        {
            var grid = Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, precision);
            var step = 1f / (1 << phaseBits);
            var smallDelta = step * 0.25f;
            var a = World.Place(world, layer, 20f + smallDelta, 30f + smallDelta, box, 4);
            var b = World.Place(world, layer, 60f + smallDelta, 70f + smallDelta, cone, 5);
            World.Turn(world, b, 0.5f);
            World.Process(world);

            var baseline = Snapshot(world, grid, layer, 128);
            var tick = World.Tick(world);
            var ctx = World.GetContext(world);

            var nextX = 20f + step * 0.75f;
            var nextY = 30f + step * 0.75f;
            if (phaseBits == 8)
            {
                nextX = 20f + smallDelta + 0.0005f;
                nextY = 30f + smallDelta + 0.0005f;
            }

            World.Move(world, a, nextX, nextY);
            Assert.Equal(0, ctx->Changed.Length);
            Assert.True(World.TryGetSourceSpec(world, a, out var specA));
            Assert.Equal((nextX, nextY), (specA.X, specA.Y));

            World.Turn(world, b, 0.5f + 0.002f);
            Assert.Equal(0, ctx->Changed.Length);

            var syncId = a;
            var syncX = phaseBits == 8 ? nextX + 0.0002f : 20f + step * 0.5f;
            var syncY = phaseBits == 8 ? nextY + 0.0002f : 30f + step * 0.5f;
            World.Sync(world, &syncId, &syncX, &syncY, 1);
            Assert.Equal(0, ctx->Changed.Length);
            Assert.True(World.TryGetSourceSpec(world, a, out specA));
            Assert.Equal((syncX, syncY), (specA.X, specA.Y));

            var heading = 0.5f + 0.003f;
            var range = new World.TransformRange
            {
                Sources = &b,
                Xs = &syncX,
                Ys = &syncY,
                Headings = &heading,
                Count = 1
            };
            var bSpecBefore = World.TryGetSourceSpec(world, b, out var specB);
            Assert.True(bSpecBefore);
            var bX = specB.X;
            var bY = specB.Y;
            range.Xs = &bX;
            range.Ys = &bY;
            Assert.Equal(
                World.BatchStatus.Applied,
                World.SyncTransforms(World.Capture(world), new ReadOnlySpan<World.TransformRange>(&range, 1)));
            Assert.Equal(0, ctx->Changed.Length);

            var results = new World.SourceResult[2];
            World.SourceCommand[] commands =
            [
                World.SourceCommand.Move(a, nextX, nextY),
                World.SourceCommand.Turn(b, 0.5f + 0.001f)
            ];
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
            Assert.Equal(0, ctx->Changed.Length);

            World.Process(world);
            Assert.Equal(0, World.ChangedTiles(world, grid, layer, null));
            Assert.Equal(0, World.ChangedTiles(world, grid, layer, tick, null));
            Assert.Equal(baseline, Snapshot(world, grid, layer, 128));
        }
        finally
        {
            World.Free(world);
            Assert.True(Stamp.Free(box));
            Assert.True(Stamp.Free(cone));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Exclusion_MatchesFieldWithoutSource_AtSnappedPrecisions(int phaseBits)
    {
        Assert.True(Layer.Precision.TryCreate(phaseBits, 4, out var precision));
        var world = World.New();
        var stamps = new[]
        {
            Stamp.Box(7, 5, 90),
            Stamp.Tent(9, 7, 110),
            Stamp.Bell(11, 9, 100),
            Stamp.New(new sbyte[] { 40, -20, 80, 10, 90, -50, 30, 70, 120 }, 3, 3),
            Stamp.Disk(5, 85),
            Stamp.Cone(6, 95, 150)
        };
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            Grid.New(world, 6, 0f, 0f, 128f);
            var baseLayer = Layer.New(world, precision);
            var otherLayer = Layer.New(world, Layer.Precision.Cell);
            var derived = Layer.Sum(world, baseLayer, 2, otherLayer, -1);

            var rng = new Random(707 + phaseBits);
            var ids = new World.SourceId[36];
            var xs = new float[ids.Length];
            var ys = new float[ids.Length];
            for (var i = 0; i < ids.Length; i++)
            {
                xs[i] = 16f + (float)(rng.NextDouble() * 96);
                ys[i] = 16f + (float)(rng.NextDouble() * 96);
                var layer = i % 5 == 0 ? otherLayer : baseLayer;
                var stamp = stamps[i % stamps.Length];
                var gain = (sbyte)rng.Next(-12, 13);
                if (gain == 0) gain = 5;
                ids[i] = World.Place(world, layer, xs[i], ys[i], stamp, gain);
                World.Turn(world, ids[i], (float)(rng.NextDouble() * 6.2));
                World.Scale(world, ids[i], 0.75f + i % 3 * 0.5f);
            }

            World.Process(world);

            for (var i = 0; i < 18; i++)
            {
                var id = ids[i];
                var px = xs[i] + 0.3f;
                var py = ys[i] - 0.2f;
                foreach (var layer in new[] { baseLayer, derived })
                {
                    Assert.True(World.TrySense(world, layer, px, py, id, out var exPoint));
                    Assert.True(World.TrySenseArea(world, layer, px, py, 12f, id, out var exArea));
                    Assert.True(World.TrySenseGradient(world, layer, px, py, id, out var exGx, out var exGy));

                    World.Record(world);
                    World.Remove(world, id);
                    World.Process(world);

                    Assert.True(World.TrySense(world, layer, px, py, out var remPoint));
                    Assert.True(World.TrySenseArea(world, layer, px, py, 12f, out var remArea));
                    Assert.True(World.TrySenseGradient(world, layer, px, py, out var remGx, out var remGy));

                    World.Rewind(world);
                    World.Process(world);

                    Assert.Equal(remPoint, exPoint);
                    Assert.Equal(remArea, exArea);
                    Assert.Equal((remGx, remGy), (exGx, exGy));
                }
            }
        }
        finally
        {
            World.Free(world);
            foreach (var stamp in stamps) Assert.True(Stamp.Free(stamp));
        }
    }

    private static short[] Snapshot(byte world, byte grid, byte layer, int size)
    {
        var cells = new short[size * size];
        fixed (short* p = cells)
        {
            World.QueryRegion(world, grid, layer, 0, 0, size, size, p);
        }

        return cells;
    }
}
