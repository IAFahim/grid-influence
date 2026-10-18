using System;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class ProbeTests
{
    private static GiProbeRequest Request(GiProbeKind kind, GiScenarioWorld setup, int layer, float x, float y, float reach, short threshold)
    {
        return new GiProbeRequest { Id = 7, Kind = kind, World = setup.World, Layer = setup.Layers[layer], X = x, Y = y, Reach = reach, Threshold = threshold };
    }

    [Fact]
    public void EveryProbeKindEqualsTheScalarCallOverOneHundredSeeds()
    {
        for (ulong seed = 1; seed <= 100; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var rng = new GiRandom(seed);
            for (var i = 0; i < 25; i++)
                GiWorld.Place(setup.World, setup.Layers[i % 2], 6 + rng.Next(52), 6 + rng.Next(52), setup.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(12)));
            GiWorld.Process(setup.World);
            GiWorld.Process(setup.World);

            for (var probe = 0; probe < 12; probe++)
            {
                var x = -4 + rng.Next(72) + rng.Next(8) * 0.125f;
                var y = -4 + rng.Next(72) + rng.Next(8) * 0.125f;
                var reach = 3 + rng.Next(30);
                var threshold = (short)rng.Next(9);
                var layer = rng.Next(5);

                var value = GiProbe.Run(Request(GiProbeKind.Value, setup, layer, x, y, reach, threshold));
                Assert.Equal(GiWorld.TrySense(setup.World, setup.Layers[layer], x, y, out var v), value.Ok);
                Assert.Equal(v, value.Value);
                Assert.Equal(GiWorld.Tick(setup.World), value.Tick);
                Assert.Equal(7, value.Id);

                var gradient = GiProbe.Run(Request(GiProbeKind.Gradient, setup, layer, x, y, reach, threshold));
                Assert.Equal(GiWorld.TrySenseGradient(setup.World, setup.Layers[layer], x, y, out var gx, out var gy), gradient.Ok);
                Assert.Equal((gx, gy), (gradient.GradientX, gradient.GradientY));

                var area = GiProbe.Run(Request(GiProbeKind.Area, setup, layer, x, y, reach, threshold));
                Assert.Equal(GiWorld.TrySenseArea(setup.World, setup.Layers[layer], x, y, reach, out var total), area.Ok);
                Assert.Equal(total, area.Total);

                var max = GiProbe.Run(Request(GiProbeKind.Max, setup, layer, x, y, reach, threshold));
                var maxOk = GiWorld.TrySenseMax(setup.World, setup.Layers[layer], x, y, reach, out var best, out var bx, out var by);
                Assert.Equal((best, bx, by), (max.Value, max.PositionX, max.PositionY));
                Assert.Equal(maxOk, max.Ok);

                var nearest = GiProbe.Run(Request(GiProbeKind.Nearest, setup, layer, x, y, reach, threshold));
                GiWorld.TrySenseNearest(setup.World, setup.Layers[layer], x, y, reach, threshold, out var near, out var nx, out var ny);
                Assert.Equal((near, nx, ny), (nearest.Value, nearest.PositionX, nearest.PositionY));
                Assert.Equal(near != short.MinValue, nearest.Ok);
            }
        }
    }

    [Fact]
    public void AProbeOfAMissingWorldOrLayerIsNotOkAndNeverThrows()
    {
        var request = new GiProbeRequest { Kind = GiProbeKind.Value, World = 200, Layer = 3, X = 1, Y = 1 };
        Assert.False(GiProbe.Run(request).Ok);
        foreach (var kind in Enum.GetValues<GiProbeKind>())
        {
            request.Kind = kind;
            request.Reach = 5;
            Assert.False(GiProbe.Run(request).Ok);
        }
    }

    [Fact]
    public void TheHistoryKeepsTheNewestValuesAndRejectsStaleTicks()
    {
        var history = new GiHistory(4);
        Assert.Equal(-1, history.LastTick);
        Assert.True(history.Push(1, 10));
        Assert.True(history.Push(2, -5));
        Assert.False(history.Push(2, 99));
        Assert.False(history.Push(1, 99));
        Assert.Equal(2, history.Count);
        Assert.Equal(-5, history[1]);

        for (var tick = 3; tick <= 9; tick++) Assert.True(history.Push(tick, (short)(tick * 3)));
        Assert.Equal(4, history.Count);
        Assert.Equal(new short[] { 18, 21, 24, 27 }, new[] { history[0], history[1], history[2], history[3] });
        Assert.Equal(6, history.TickAt(0));
        Assert.Equal(9, history.LastTick);

        history.Range(out var low, out var high);
        Assert.Equal((18, 27), (low, high));
        Assert.Throws<ArgumentOutOfRangeException>(() => history[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GiHistory(0));

        history.Clear();
        Assert.Equal(0, history.Count);
        history.Range(out low, out high);
        Assert.Equal((0, 0), (low, high));
        Assert.True(history.Push(1, 4));
    }
}
