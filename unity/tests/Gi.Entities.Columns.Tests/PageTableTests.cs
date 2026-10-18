using Gi.Entities.Tests.Support;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class PageTableTests
{
    [Fact]
    public void ReleasedSlotsAreReusedBeforeFreshOnes()
    {
        var table = new GiPageTable(16);
        Assert.Equal(0, table.Acquire(3));
        Assert.Equal(1, table.Acquire(7));
        Assert.Equal(2, table.Acquire(12));
        Assert.Equal(3, table.LiveTiles);

        Assert.True(table.Release(7));
        Assert.Equal(2, table.LiveTiles);
        Assert.Equal(1, table.Acquire(9));
        Assert.Equal(3, table.Acquire(1));
        Assert.Equal(4, table.LiveTiles);

        Assert.Equal(0, table.SlotOf(3));
        Assert.Equal(1, table.SlotOf(9));
        Assert.Equal(2, table.SlotOf(12));
        Assert.Equal(3, table.SlotOf(1));
        Assert.Equal(-1, table.SlotOf(7));
    }

    [Fact]
    public void AcquiringTheSameTileTwiceKeepsItsSlot()
    {
        var table = new GiPageTable(8);
        Assert.Equal(0, table.Acquire(5));
        Assert.Equal(0, table.Acquire(5));
        Assert.Equal(1, table.LiveTiles);

        Assert.False(table.Release(2));
        Assert.True(table.Release(5));
        Assert.False(table.Release(5));
        Assert.Equal(0, table.LiveTiles);
    }

    [Fact]
    public void OutOfRangeTilesAreRefusedWithoutStateChanges()
    {
        var table = new GiPageTable(4);
        Assert.Equal(-1, table.Acquire(-1));
        Assert.Equal(-1, table.Acquire(4));
        Assert.Equal(-1, table.SlotOf(4));
        Assert.False(table.Release(-1));
        Assert.False(table.Release(4));
        Assert.Equal(0, table.LiveTiles);
    }

    [Fact]
    public void RandomChangeSetsAllocateFreeAndReuseDeterministically()
    {
        const int ticks = 200;
        const int tileCount = 96;
        var first = new GiPageTable(tileCount);
        var second = new GiPageTable(tileCount);
        var rng = new GiRandom(0x5EED_0001ul);
        var changed = new int[tileCount];
        var zeroed = new int[tileCount];

        for (var tick = 0; tick < ticks; tick++)
        {
            var changedCount = 0;
            for (var tile = 0; tile < tileCount; tile++)
                if (rng.Next(100) < 8)
                    changed[changedCount++] = tile;

            var zeroCount = 0;
            for (var tile = 0; tile < tileCount; tile++)
                if (rng.Next(100) < 3)
                    zeroed[zeroCount++] = tile;

            foreach (var table in new[] { first, second })
            {
                for (var i = 0; i < changedCount; i++) table.Acquire(changed[i]);
                for (var i = 0; i < zeroCount; i++) table.Release(zeroed[i]);
            }

            for (var tile = 0; tile < tileCount; tile++)
                Assert.Equal(first.SlotOf(tile), second.SlotOf(tile));
            Assert.Equal(first.LiveTiles, second.LiveTiles);
        }

        Assert.True(first.LiveTiles > 0);
        for (var round = 0; round < 2; round++)
            for (var tile = 0; tile < tileCount; tile++)
            {
                first.Release(tile);
                second.Release(tile);
            }

        Assert.Equal(0, first.LiveTiles);
        Assert.Equal(0, second.LiveTiles);
        Assert.Equal(first.Acquire(0), second.Acquire(0));
        Assert.Equal(1, first.LiveTiles);
    }

    [Fact]
    public void TheBudgetCapsUploadsAndTheBacklogDrainsCompletely()
    {
        var backlog = 10;
        var uploaded = 0;
        for (var frame = 0; frame < 3; frame++)
        {
            var take = GiPageTable.Plan(backlog, 4);
            uploaded += take;
            backlog -= take;
        }

        Assert.Equal(10, uploaded);
        Assert.Equal(0, backlog);

        Assert.Equal(0, GiPageTable.Plan(10, 0));
        Assert.Equal(0, GiPageTable.Plan(0, 4));
        Assert.Equal(3, GiPageTable.Plan(3, 8));
    }
}
