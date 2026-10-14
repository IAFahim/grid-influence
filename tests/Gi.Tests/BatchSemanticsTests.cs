using Xunit;

namespace Gi.Tests;

public sealed class BatchSemanticsTests : IDisposable
{
    private readonly byte world = World.New();
    private readonly byte grid;
    private readonly byte layer;
    private readonly byte stamp;

    public BatchSemanticsTests()
    {
        grid = Grid.New(world, 6, 0f, 0f, 64f);
        layer = Layer.New(world);
        stamp = Stamp.Box(1, 1, 1);
    }

    public void Dispose()
    {
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void PendingChanges_PreserveResolvedCellsAndTickUntilProcessing()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 4);
        World.Process(world);

        World.Move(world, source, 40.5f, 40.5f);
        World.SetGain(world, source, 7);

        Assert.Equal(1, World.Tick(world));
        Assert.Equal(4, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(world, grid, layer, 40.5f, 40.5f));

        World.Process(world);

        Assert.Equal(2, World.Tick(world));
        Assert.Equal(0, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.Equal(7, World.QueryAt(world, grid, layer, 40.5f, 40.5f));
    }

    [Fact]
    public void RepeatedStateChanges_RetractOnceAndApplyTheFinalState()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 4);
        World.Process(world);

        for (var index = 0; index < 100; index++)
        {
            World.Move(world, source, 20.5f + index % 10, 20.5f);
            World.SetGain(world, source, (sbyte)(index % 8));
        }

        World.Move(world, source, 40.5f, 40.5f);
        World.Turn(world, source, MathF.PI / 2f);
        World.Scale(world, source, 2f);
        World.Scale(world, source, 1f);
        World.SetGain(world, source, 7);
        World.Process(world);

        Assert.Equal(0, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.Equal(7, World.QueryAt(world, grid, layer, 40.5f, 40.5f));
        Assert.Equal(7, World.Query(world, grid, layer, 0, 0, 64, 64));
    }

    [Fact]
    public void CreationRemovedBeforeProcessing_LeavesNoDeposit()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 4);
        World.Move(world, source, 40.5f, 40.5f);
        World.Remove(world, source);
        World.Process(world);

        Assert.Equal(0, World.Query(world, grid, layer, 0, 0, 64, 64));
    }

    [Fact]
    public void SlotReusedBeforeRemovalIsProcessed_RejectsTheOldSourceId()
    {
        var previousSource = World.Place(world, layer, 10.5f, 10.5f, stamp, 4);
        World.Process(world);
        World.Remove(world, previousSource);
        var nextSource = World.Place(world, layer, 40.5f, 40.5f, stamp, 7);

        Assert.NotEqual(previousSource, nextSource);
        World.Move(world, previousSource, 20.5f, 20.5f);
        World.SetGain(world, previousSource, 16);
        World.Fade(world, previousSource, 0, 1);
        World.Expire(world, previousSource, 1);
        World.Remove(world, previousSource);
        World.Process(world);

        Assert.Equal(0, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(world, grid, layer, 20.5f, 20.5f));
        Assert.Equal(7, World.QueryAt(world, grid, layer, 40.5f, 40.5f));
        Assert.Equal(7, World.Query(world, grid, layer, 0, 0, 64, 64));
    }

    [Fact]
    public void SettingTheCurrentGain_CancelsAnEarlierFade()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 8);
        World.Process(world);
        World.Fade(world, source, 0, 4);
        World.SetGain(world, source, 8);
        World.Process(world);

        Assert.Equal(8, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        World.Process(world);
        Assert.Equal(8, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
    }

    [Fact]
    public void FadeAfterGainChange_StartsFromTheNewGain()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 8);
        World.Process(world);
        World.SetGain(world, source, 4);
        World.Fade(world, source, 0, 4);
        World.Process(world);

        Assert.Equal(3, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
    }

    [Fact]
    public void SelfExclusion_UsesTheAppliedPlacementDuringPendingChanges()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 4);
        World.Place(world, layer, 10.5f, 10.5f, stamp, 2);
        World.Process(world);
        World.Move(world, source, 40.5f, 40.5f);
        World.SetGain(world, source, 8);

        Assert.True(World.TrySense(world, layer, 10.5f, 10.5f, source, out var previousValue));
        Assert.Equal(2, previousValue);
        Assert.True(World.TrySense(world, layer, 40.5f, 40.5f, source, out var nextValue));
        Assert.Equal(0, nextValue);

        World.Process(world);

        Assert.Equal(8, World.QueryAt(world, grid, layer, 40.5f, 40.5f));
        Assert.True(World.TrySense(world, layer, 40.5f, 40.5f, source, out nextValue));
        Assert.Equal(0, nextValue);
    }

    [Fact]
    public void ProcessAll_AdvancesExpiryWithoutNewSubmissions()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 4);
        World.Expire(world, source, 2);
        World.Process(world);
        Assert.Equal(4, World.QueryAt(world, grid, layer, 10.5f, 10.5f));

        World.ProcessAll();

        Assert.Equal(2, World.Tick(world));
        Assert.Equal(0, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
    }

    [Fact]
    public void Rewind_RestoresPlacementAndFadeAfterMixedChanges()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 8);
        World.Fade(world, source, 0, 8);
        World.Process(world);
        World.Record(world);
        World.Move(world, source, 40.5f, 40.5f);
        World.SetGain(world, source, 2);
        World.Expire(world, source, 2);
        World.Process(world);
        Assert.Equal(2, World.QueryAt(world, grid, layer, 40.5f, 40.5f));

        World.Rewind(world);
        World.Process(world);

        Assert.Equal(6, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(world, grid, layer, 40.5f, 40.5f));
        World.Process(world);
        Assert.Equal(5, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
    }

    [Fact]
    public void Clear_DiscardsPendingChangesAndSchedulesWithoutResurrectingSources()
    {
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 8);
        World.Process(world);
        World.Move(world, source, 40.5f, 40.5f);
        World.Fade(world, source, 0, 4);
        World.Expire(world, source, 2);
        World.Clear(world);
        World.Move(world, source, 20.5f, 20.5f);
        World.Process(world);
        World.Process(world);

        Assert.Equal(0, World.Query(world, grid, layer, 0, 0, 64, 64));
    }
}
