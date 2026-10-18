using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class SyncTests
{
    [Fact]
    public void ColumnsPipelineWithSyncMatchesTheReferenceOverTwoHundredSeeds()
    {
        var options = GiScenarioOptions.Standard.WithoutSplats();
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, options);
            var expected = GiReferenceExecutor.Run(scenario);
            var actual = GiColumnsExecutor.Run(scenario);
            Assert.True(expected.Compare(actual).Length == 0, $"seed {seed}: {expected.Compare(actual)}");
        }
    }

    [Fact]
    public void SplatsAndTransformsTogetherStillMatch()
    {
        for (ulong seed = 400; seed < 440; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, GiScenarioOptions.Standard);
            Assert.Equal(string.Empty, GiReferenceExecutor.Run(scenario).Compare(GiColumnsExecutor.Run(scenario)));
        }
    }

    [Fact]
    public void CategoriesSeparateOrientedAndScaledRows()
    {
        Assert.Equal(0, GiSyncPass.Category(false, false));
        Assert.Equal(1, GiSyncPass.Category(true, false));
        Assert.Equal(2, GiSyncPass.Category(false, true));
        Assert.Equal(3, GiSyncPass.Category(true, true));
        Assert.Equal(0, GiSyncPass.Words(0));
        Assert.Equal(1, GiSyncPass.Words(64));
        Assert.Equal(2, GiSyncPass.Words(65));
    }

    [Fact]
    public void RejectedRowsAreReportedPerRowAndOnlyAcceptedRowsMove()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var world = setup.World;
        var alive = GiWorld.Place(world, setup.Layers[0], 20f, 20f, setup.Stamps[0], 4);
        var stale = GiWorld.Place(world, setup.Layers[0], 30f, 30f, setup.Stamps[0], 4);
        GiWorld.Remove(world, stale);
        var held = GiWorld.Place(world, setup.Layers[0], 40f, 40f, setup.Stamps[0], 4);
        GiWorld.Process(world);

        var sources = new[] { stale, held, alive };
        var xs = new[] { 1f, float.NaN, 50f };
        var ys = new[] { 1f, 2f, 50f };
        var headings = new float[3];
        var scales = new[] { 1f, 1f, 1f };
        var invalid = new ulong[1];
        var token = GiWorld.Capture(world);
        var starts = new[] { 0, 0, 0, 0 };
        var counts = new[] { 3, 0, 0, 0 };
        var wordStarts = new[] { 0, 1, 1, 1 };

        fixed (GiWorld.SourceId* s = sources)
        fixed (float* x = xs, y = ys, h = headings, sc = scales)
        fixed (ulong* inv = invalid)
        fixed (int* st = starts, ct = counts, ws = wordStarts)
        {
            Assert.Equal(GiApplyStatus.Applied, GiSyncPass.Apply(ref token, world, s, x, y, h, sc, inv, st, ct, ws));
            Assert.True(GiSyncPass.Rejected(inv, 0, 0));
            Assert.True(GiSyncPass.Rejected(inv, 0, 1));
            Assert.False(GiSyncPass.Rejected(inv, 0, 2));
        }

        Assert.True(GiWorld.TryGetSourceSpec(world, alive, out var spec));
        Assert.Equal(50f, spec.X);
        Assert.True(GiWorld.TryGetSourceSpec(world, held, out var heldSpec));
        Assert.Equal(40f, heldSpec.X);
        Assert.Equal(GiFaultCode.NonFinite, GiSyncPass.Reason(float.NaN, 2f));
        Assert.Equal(GiFaultCode.NonFinite, GiSyncPass.Reason(1f, float.PositiveInfinity));
        Assert.Equal(GiFaultCode.StaleSource, GiSyncPass.Reason(1f, 1f));
    }

    [Fact]
    public void AStaleTokenIsRecapturedOnceAndAppliedAfterwards()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var world = setup.World;
        var id = GiWorld.Place(world, setup.Layers[0], 20f, 20f, setup.Stamps[0], 4);
        GiWorld.Process(world);
        var token = GiWorld.Capture(world);
        GiWorld.Clear(world);
        var id2 = GiWorld.Place(world, setup.Layers[0], 20f, 20f, setup.Stamps[0], 4);

        var sources = new[] { id2 };
        var xs = new[] { 33f };
        var ys = new[] { 34f };
        var scratch = new float[1];
        var invalid = new ulong[1];
        var starts = new[] { 0, 0, 0, 0 };
        var counts = new[] { 1, 0, 0, 0 };
        var wordStarts = new[] { 0, 1, 1, 1 };
        fixed (GiWorld.SourceId* s = sources)
        fixed (float* x = xs, y = ys, h = scratch, sc = scratch)
        fixed (ulong* inv = invalid)
        fixed (int* st = starts, ct = counts, ws = wordStarts)
        {
            Assert.Equal(GiApplyStatus.Applied, GiSyncPass.Apply(ref token, world, s, x, y, h, sc, inv, st, ct, ws));
            Assert.False(GiSyncPass.Rejected(inv, 0, 0));
        }

        Assert.True(GiWorld.TryGetSourceSpec(world, id2, out var spec));
        Assert.Equal(33f, spec.X);
        Assert.Equal(34f, spec.Y);
        Assert.NotEqual(default, id);
    }
}
