using Xunit;

namespace Gi.Tests;

public sealed class DepositPrepTests
{
    [Fact]
    public void RasterBatches_ParallelPreparationMatchesSerialDepositsCellForCell()
    {
        var serial = World.New();
        var parallel = World.New();
        Grid.New(serial, 8, 0f, 0f, 256f);
        Grid.New(parallel, 8, 0f, 0f, 256f);
        Layer.New(serial);
        Layer.New(parallel);
        var samples = new sbyte[7 * 9];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)((i * 37 % 21) - 10);
        var stamp = Stamp.New(samples, 7, 9);
        var rng = new Random(7);
        for (var i = 0; i < 60; i++)
        {
            var x = (float)(rng.NextDouble() * 248);
            var y = (float)(rng.NextDouble() * 248);
            var gain = (sbyte)(1 + rng.Next(16));
            World.Place(serial, 0, x, y, stamp, gain);
            World.Place(parallel, 0, x, y, stamp, gain);
        }

        ResolvePool.SerialOnly = true;
        World.Process(serial);
        ResolvePool.SerialOnly = false;
        World.Process(parallel);
        AssertCellsEqual(serial, parallel);

        World.Free(serial);
        World.Free(parallel);
        Stamp.Free(stamp);
    }

    [Fact]
    public unsafe void MixedShapesAcrossGridsAndLayers_ParallelPreparationMatchesSerialEveryFrame()
    {
        var serial = World.New();
        var parallel = World.New();
        foreach (var world in new[] { serial, parallel })
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            Grid.New(world, 5, 0f, 0f, 128f);
            Layer.New(world);
            Layer.New(world);
            Layer.Sum(world, 0, 2, 1, -1);
        }

        var stamps = new[]
        {
            Stamp.Box(9, 9, 20), Stamp.Tent(12, 12, 40), Stamp.Bell(12, 12, 40),
            Stamp.Disk(8, 30), Stamp.Cone(8, 30, 90), Stamp.Dome(8, 30),
        };
        var rng = new Random(11);
        var count = 700;
        var ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            var layer = (byte)(i % 2);
            var x = (float)(rng.NextDouble() * 124);
            var y = (float)(rng.NextDouble() * 124);
            var gain = (sbyte)(rng.Next(33) - 16);
            ids[i] = World.Place(serial, layer, x, y, stamps[i % stamps.Length], gain);
            World.Place(parallel, layer, x, y, stamps[i % stamps.Length], gain);
        }

        ResolvePool.SerialOnly = true;
        World.Process(serial);
        ResolvePool.SerialOnly = false;
        World.Process(parallel);

        var serialTiles = stackalloc int[16];
        var parallelTiles = stackalloc int[16];
        var previousSerial = ResolvePool.SerialOnly;
        try
        {
            for (var frame = 0; frame < 4; frame++)
            {
                for (var i = 0; i < count; i++)
                {
                    var x = (float)(rng.NextDouble() * 124);
                    var y = (float)(rng.NextDouble() * 124);
                    var angle = (float)(rng.NextDouble() * Math.PI * 2);
                    World.Move(serial, ids[i], x, y);
                    World.Turn(serial, ids[i], angle);
                    World.Move(parallel, ids[i], x, y);
                    World.Turn(parallel, ids[i], angle);
                }

                if (frame == 1)
                    for (var i = 0; i < count; i += 7)
                    {
                        World.Remove(serial, ids[i]);
                        World.Remove(parallel, ids[i]);
                    }

                if (frame == 2)
                    for (var i = 0; i < count; i += 5)
                    {
                        World.SetGain(serial, ids[i], (sbyte)(i % 17 - 8));
                        World.SetGain(parallel, ids[i], (sbyte)(i % 17 - 8));
                    }

                ResolvePool.SerialOnly = frame % 2 == 0;
                World.Process(serial);
                ResolvePool.SerialOnly = frame % 2 == 1;
                World.Process(parallel);
                AssertCellsEqual(serial, parallel);
                Assert.Equal(
                    World.ChangedTiles(serial, 0, 0, World.Tick(serial) - 1, serialTiles),
                    World.ChangedTiles(parallel, 0, 0, World.Tick(parallel) - 1, parallelTiles));
                Assert.Equal(
                    World.QueryMax(serial, 0, 2, out var sx, out var sy),
                    World.QueryMax(parallel, 0, 2, out var px, out var py));
                Assert.Equal(sx, px);
                Assert.Equal(sy, py);
            }
        }
        finally
        {
            ResolvePool.SerialOnly = previousSerial;
        }

        World.Free(serial);
        World.Free(parallel);
        foreach (var stamp in stamps) Stamp.Free(stamp);
    }

    private static unsafe void AssertCellsEqual(byte serial, byte parallel)
    {
        var serialCells = new short[256 * 256];
        var parallelCells = new short[256 * 256];
        fixed (short* sp = serialCells)
        fixed (short* pp = parallelCells)
        {
            World.QueryRegion(serial, 0, 0, 0, 0, 256, 256, sp);
            World.QueryRegion(parallel, 0, 0, 0, 0, 256, 256, pp);
        }

        Assert.Equal(serialCells, parallelCells);
        Assert.Equal(World.Query(serial, 0, 0, 0, 0, 128, 128), World.Query(parallel, 0, 0, 0, 0, 128, 128));
        Assert.Equal(World.Query(serial, 1, 1, 0, 0, 32, 32), World.Query(parallel, 1, 1, 0, 0, 32, 32));
    }
}
