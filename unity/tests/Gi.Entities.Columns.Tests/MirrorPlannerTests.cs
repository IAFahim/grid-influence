using System.Collections.Generic;
using Gi.Entities.Tests.Support;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class MirrorPlannerTests
{

    private static readonly int[] TakenPrefix = new[] { 9, 3, 40, 7 };
    [Fact]
    public void TilesAreDeduplicatedAndLeaveInArrivalOrder()
    {
        var planner = new GiMirrorPlanner(64);
        foreach (var tile in new[] { 9, 3, 9, 40, 3, 7 }) planner.Enqueue(tile);
        Assert.Equal(4, planner.Pending);

        var taken = new int[8];
        Assert.Equal(4, planner.Take(8, taken));
        Assert.Equal(TakenPrefix, taken[..4]);
        Assert.Equal(0, planner.Pending);

        planner.Enqueue(9);
        Assert.Equal(1, planner.Pending);
        Assert.Equal(0, planner.Take(0, taken));
        Assert.Equal(1, planner.Pending);
        planner.Enqueue(-1);
        planner.Enqueue(64);
        Assert.Equal(1, planner.Pending);
    }

    [Fact]
    public void TheBudgetCapsEachFrameAndTheCarryOverDrainsCompletely()
    {
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var rng = new GiRandom(seed);
            const int tiles = 256;
            var budget = 1 + rng.Next(12);
            var planner = new GiMirrorPlanner(tiles);
            var buffer = new int[budget];
            var everChanged = new HashSet<int>();
            var uploads = new HashSet<int>();
            long uploaded = 0;

            for (var frame = 0; frame < 60; frame++)
            {
                var changes = rng.Next(4) == 0 ? 0 : rng.Next(30);
                for (var i = 0; i < changes; i++)
                {
                    var tile = rng.Next(tiles);
                    everChanged.Add(tile);
                    planner.Enqueue(tile);
                }

                var before = planner.Pending;
                var taken = planner.Take(budget, buffer);
                Assert.Equal(System.Math.Min(before, budget), taken);
                for (var i = 0; i < taken; i++)
                {
                    uploads.Add(buffer[i]);
                    planner.Commit(buffer[i], false);
                }

                uploaded += taken;
            }

            var drainFrames = 0;
            while (planner.Pending > 0)
            {
                var pendingBefore = planner.Pending;
                var taken = planner.Take(budget, buffer);
                Assert.Equal(System.Math.Min(pendingBefore, budget), taken);
                for (var i = 0; i < taken; i++)
                {
                    uploads.Add(buffer[i]);
                    planner.Commit(buffer[i], false);
                }

                uploaded += taken;
                drainFrames++;
                Assert.True(drainFrames <= (tiles + budget - 1) / budget, $"seed {seed} did not drain");
            }

            Assert.Equal(0, planner.Pending);
            Assert.Equal(everChanged, uploads);
            Assert.True(uploaded >= everChanged.Count);
        }
    }

    [Fact]
    public void UploadedBytesScaleWithTheNumberOfChangedTiles()
    {
        long Cost(int changed)
        {
            var planner = new GiMirrorPlanner(1024);
            for (var i = 0; i < changed; i++) planner.Enqueue(i * 3);
            var buffer = new int[1024];
            return GiMirrorPlanner.UploadBytes(planner.Take(1024, buffer));
        }

        Assert.Equal(0, Cost(0));
        Assert.Equal(Cost(1) * 10, Cost(10));
        Assert.Equal(Cost(1) * 100, Cost(100));
        Assert.Equal(2048L + 4L, Cost(1));
    }

    [Fact]
    public void EmptyTilesGiveTheirSlotBackAndItIsReused()
    {
        var planner = new GiMirrorPlanner(32);
        Assert.Equal(0, planner.Commit(5, false));
        Assert.Equal(1, planner.Commit(6, false));
        Assert.Equal(2, planner.Commit(7, false));
        Assert.Equal(3, planner.Capacity);
        Assert.Equal(3, planner.LiveTiles);

        Assert.Equal(-1, planner.Commit(6, true));
        Assert.Equal(2, planner.LiveTiles);
        Assert.Equal(-1, planner.SlotOf(6));
        Assert.Equal(1, planner.Commit(20, false));
        Assert.Equal(3, planner.Capacity);
        Assert.Equal(3, planner.Commit(6, false));
        Assert.Equal(4, planner.Capacity);
    }

    [Fact]
    public void ThePoolGrowsByDoubling()
    {
        Assert.Equal(16, GiMirrorPlanner.GrownCapacity(0, 1));
        Assert.Equal(16, GiMirrorPlanner.GrownCapacity(16, 16));
        Assert.Equal(32, GiMirrorPlanner.GrownCapacity(16, 17));
        Assert.Equal(200, GiMirrorPlanner.GrownCapacity(100, 130));
        Assert.Equal(512, GiMirrorPlanner.GrownCapacity(256, 512));
        Assert.Equal(1024, GiMirrorPlanner.GrownCapacity(256, 513));
        Assert.Equal(1, GiMirrorPlanner.TilesPerSide(5));
        Assert.Equal(128, GiMirrorPlanner.TilesPerSide(12));
    }
}
