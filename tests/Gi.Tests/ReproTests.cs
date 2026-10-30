using Xunit;

namespace Gi.Tests;

public sealed class ReproTests
{
    [Fact]
    public void MinimizedDifferentialFailure()
    {
        var setup = new StreamSetup { Seed = 950004,
            Grids = new[]
            {
                new GridSpec { Power = 6, X = BitConverter.Int32BitsToSingle(0x42000000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC2000000)), Size = BitConverter.Int32BitsToSingle(0x42800000) },
                new GridSpec { Power = 5, X = BitConverter.Int32BitsToSingle(0x00000000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC2000000)), Size = BitConverter.Int32BitsToSingle(0x41200000) },
            },
            Layers = new[]
            {
                new LayerSpec { Precision = Layer.Precision.Cell },
                new LayerSpec { Derived = true, Op = LayerOp.Min, A = 0, B = 0 },
                new LayerSpec { Precision = Layer.Precision.Cell, Transient = true },
            },
            Stamps = new[]
            {
                new StampSpec { Kind = StampKind.Disk, Radius = 1, Arc = 272, Value = 26 },
                new StampSpec { Kind = StampKind.Box, Width = 4, Height = 1, Value = -105 },
                new StampSpec { Kind = StampKind.Bell, Width = 4, Height = 5, Value = 95 },
                new StampSpec { Kind = StampKind.Box, Width = 3, Height = 5, Value = 48 },
                new StampSpec { Kind = StampKind.Dome, Radius = 3, Arc = 360, Value = 27 },
                new StampSpec { Kind = StampKind.Dome, Radius = 4, Arc = 360, Value = 80 },
            },
            Commands = new List<StreamCommand>
            {
                new StreamCommand { Op = StreamOp.Splat, Layer = 2, Stamp = 4, Gain = -13, X = BitConverter.Int32BitsToSingle(0x42480000), Y = BitConverter.Int32BitsToSingle(0x4316B83B), Aux = 11450791444516267386ul },
                new StreamCommand { Op = StreamOp.Process, Aux = 7412798738559066363ul },
            },
        };
        Assert.Null(StreamRunner.TryRun(setup, setup.Commands));
    }

    [Theory]
    [InlineData(60, 5)]
    [InlineData(10, 1)]
    public void DenseBoxCrowdVanishesOnTheEmptyFrameAfterFusing(int value, int gain)
    {
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
        var box = Stamp.Box(4, 4, (sbyte)value);
        var xs = new float[800];
        var ys = new float[800];
        for (var i = 0; i < 800; i++)
        {
            var row = i / 28;
            xs[i] = 1 + (i % 28);
            ys[i] = 1 + row;
        }

        Assert.Equal(800, World.Splat(world, transient, xs, ys, box, (sbyte)gain));
        World.Process(world);
        Assert.NotEqual(0, World.Query(world, 0, transient, 0, 0, 64, 64));

        World.Process(world);
        Assert.Equal(0, World.Query(world, 0, transient, 0, 0, 64, 64));
        World.Free(world);
        Stamp.Free(box);
    }
}
