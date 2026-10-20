using Xunit;

namespace Gi.Tests;

public sealed class BatchSemanticsTests : IDisposable
{
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp;
    private readonly byte _world = World.New();

    public BatchSemanticsTests()
    {
        _grid = Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
        _stamp = Stamp.Box(1, 1, 1);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    [Fact]
    public void PendingChanges_PreserveResolvedCellsAndTickUntilProcessing()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);

        World.Move(_world, source, 40.5f, 40.5f);
        World.SetGain(_world, source, 7);

        Assert.Equal(1, World.Tick(_world));
        Assert.Equal(4, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));

        World.Process(_world);

        Assert.Equal(2, World.Tick(_world));
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        Assert.Equal(7, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));
    }

    [Fact]
    public void RepeatedStateChanges_RetractOnceAndApplyTheFinalState()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);

        for (var index = 0; index < 100; index++)
        {
            World.Move(_world, source, 20.5f + index % 10, 20.5f);
            World.SetGain(_world, source, (sbyte)(index % 8));
        }

        World.Move(_world, source, 40.5f, 40.5f);
        World.Turn(_world, source, MathF.PI / 2f);
        World.Scale(_world, source, 2f);
        World.Scale(_world, source, 1f);
        World.SetGain(_world, source, 7);
        World.Process(_world);

        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        Assert.Equal(7, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));
        Assert.Equal(7, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
    }

    [Fact]
    public void CreationRemovedBeforeProcessing_LeavesNoDeposit()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Move(_world, source, 40.5f, 40.5f);
        World.Remove(_world, source);
        World.Process(_world);

        Assert.Equal(0, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
    }

    [Fact]
    public void SlotReusedBeforeRemovalIsProcessed_RejectsTheOldSourceId()
    {
        var previousSource = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Process(_world);
        World.Remove(_world, previousSource);
        var nextSource = World.Place(_world, _layer, 40.5f, 40.5f, _stamp, 7);

        Assert.NotEqual(previousSource, nextSource);
        World.Move(_world, previousSource, 20.5f, 20.5f);
        World.SetGain(_world, previousSource, 16);
        World.Fade(_world, previousSource, 0, 1);
        World.Expire(_world, previousSource, 1);
        World.Remove(_world, previousSource);
        World.Process(_world);

        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 20.5f, 20.5f));
        Assert.Equal(7, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));
        Assert.Equal(7, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
    }

    [Fact]
    public void SettingTheCurrentGain_CancelsAnEarlierFade()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 8);
        World.Process(_world);
        World.Fade(_world, source, 0, 4);
        World.SetGain(_world, source, 8);
        World.Process(_world);

        Assert.Equal(8, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        World.Process(_world);
        Assert.Equal(8, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
    }

    [Fact]
    public void FadeAfterGainChange_StartsFromTheNewGain()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 8);
        World.Process(_world);
        World.SetGain(_world, source, 4);
        World.Fade(_world, source, 0, 4);
        World.Process(_world);

        Assert.Equal(3, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
    }

    [Fact]
    public void SelfExclusion_UsesTheAppliedPlacementDuringPendingChanges()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 2);
        World.Process(_world);
        World.Move(_world, source, 40.5f, 40.5f);
        World.SetGain(_world, source, 8);

        Assert.True(World.TrySense(_world, _layer, 10.5f, 10.5f, source, out var previousValue));
        Assert.Equal(2, previousValue);
        Assert.True(World.TrySense(_world, _layer, 40.5f, 40.5f, source, out var nextValue));
        Assert.Equal(0, nextValue);

        World.Process(_world);

        Assert.Equal(8, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));
        Assert.True(World.TrySense(_world, _layer, 40.5f, 40.5f, source, out nextValue));
        Assert.Equal(0, nextValue);
    }

    [Fact]
    public void ProcessAll_AdvancesExpiryWithoutNewSubmissions()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 4);
        World.Expire(_world, source, 2);
        World.Process(_world);
        Assert.Equal(4, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));

        World.ProcessAll();

        Assert.Equal(2, World.Tick(_world));
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
    }

    [Fact]
    public void Rewind_RestoresPlacementAndFadeAfterMixedChanges()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 8);
        World.Fade(_world, source, 0, 8);
        World.Process(_world);
        World.Record(_world);
        World.Move(_world, source, 40.5f, 40.5f);
        World.SetGain(_world, source, 2);
        World.Expire(_world, source, 2);
        World.Process(_world);
        Assert.Equal(2, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));

        World.Rewind(_world);
        World.Process(_world);

        Assert.Equal(6, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, 40.5f, 40.5f));
        World.Process(_world);
        Assert.Equal(5, World.QueryAt(_world, _grid, _layer, 10.5f, 10.5f));
    }

    [Fact]
    public void Clear_DiscardsPendingChangesAndSchedulesWithoutResurrectingSources()
    {
        var source = World.Place(_world, _layer, 10.5f, 10.5f, _stamp, 8);
        World.Process(_world);
        World.Move(_world, source, 40.5f, 40.5f);
        World.Fade(_world, source, 0, 4);
        World.Expire(_world, source, 2);
        World.Clear(_world);
        World.Move(_world, source, 20.5f, 20.5f);
        World.Process(_world);
        World.Process(_world);

        Assert.Equal(0, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
    }
}
