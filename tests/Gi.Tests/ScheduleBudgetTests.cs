using Xunit;

namespace Gi.Tests;

public sealed unsafe class ScheduleBudgetTests
{
    private static readonly byte UnitBox = Stamp.Box(1, 1, 1);

    [Fact]
    public void Sweep_matches_integer_fade_rounding_for_every_gain_pair()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0, 0, 64);
        var layer = Layer.New(world);
        try
        {
            foreach (var duration in new[] { 1, 2, 3, 7, 17, 300 })
            {
                World.Clear(world);
                var ids = new int[33 * 33];
                for (var from = -16; from <= 16; from++)
                for (var to = -16; to <= 16; to++)
                {
                    var index = (from + 16) * 33 + to + 16;
                    ids[index] = World.Place(world, layer, (index % 64) + .5f, (index / 64) + .5f, UnitBox, (sbyte)from);
                }
                World.Process(world);
                for (var from = -16; from <= 16; from++)
                for (var to = -16; to <= 16; to++)
                {
                    var index = (from + 16) * 33 + to + 16;
                    World.Fade(world, ids[index], (sbyte)to, duration);
                }
                AssertTimerMap(world);
                for (var elapsed = 1; elapsed <= duration + 1; elapsed++)
                {
                    World.Process(world);
                    for (var from = -16; from <= 16; from++)
                    for (var to = -16; to <= 16; to++)
                    {
                        var index = (from + 16) * 33 + to + 16;
                        var difference = to - from;
                        var step = (Math.Abs(difference) * Math.Min(elapsed, duration) + duration / 2) / duration;
                        var expected = from + (difference < 0 ? -step : step);
                        Assert.Equal(expected, World.Query(world, grid, layer, index % 64, index / 64));
                    }
                    AssertTimerMap(world);
                }
                Assert.Equal(0, World.GetContext(world)->Timers.Length);
            }
        }
        finally { Assert.True(World.Free(world)); }
    }

    [Fact]
    public void Rescheduling_keeps_one_dense_member_and_cancellation_removes_it_immediately()
    {
        var world = World.New();
        var layer = Layer.New(world);
        var source = World.Place(world, layer, 5.5f, 5.5f, UnitBox, 8);
        try
        {
            for (var round = 0; round < 20000; round++)
            {
                World.Fade(world, source, 0, 10 + round);
                World.Expire(world, source, 20 + round);
                Assert.Equal(1, World.GetContext(world)->Timers.Length);
            }
            Assert.InRange(World.GetContext(world)->Timers.Capacity, 1, 16);
            World.SetGain(world, source, 8);
            Assert.Equal(1, World.GetContext(world)->Timers.Length);
            World.Expire(world, source, 0);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            AssertTimerMap(world);
            World.Fade(world, source, 0, 10);
            World.SetGain(world, source, 8);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            World.Expire(world, source, 10);
            World.Remove(world, source);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            AssertTimerMap(world);
        }
        finally { Assert.True(World.Free(world)); }
    }

    [Fact]
    public void Batch_schedule_cancellation_reuse_and_expiry_keep_the_dense_map_consistent()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0, 0, 64);
        var layer = Layer.New(world);
        var sources = new int[1200];
        var commands = new World.SourceCommand[2400];
        var results = new World.SourceResult[2400];
        try
        {
            for (var i = 0; i < sources.Length; i++)
            {
                sources[i] = World.Place(world, layer, i % 64 + .5f, i / 64 + .5f, UnitBox, 8);
                commands[i * 2] = World.SourceCommand.Fade(sources[i], -8, 4);
                commands[i * 2 + 1] = World.SourceCommand.Expire(sources[i], 5);
            }
            World.ApplyBatch(world, commands, results);
            Assert.Equal(sources.Length, World.GetContext(world)->Timers.Length);
            for (var i = 0; i < sources.Length; i++)
            {
                commands[i * 2] = World.SourceCommand.SetGain(sources[i], 8);
                commands[i * 2 + 1] = World.SourceCommand.Expire(sources[i], i % 3 == 0 ? 0 : 2);
            }
            World.ApplyBatch(world, commands, results);
            Assert.Equal(800, World.GetContext(world)->Timers.Length);
            AssertTimerMap(world);
            World.Process(world);
            World.Process(world);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            for (var i = 0; i < sources.Length; i++)
                Assert.Equal(i % 3 == 0 ? 8 : 0, World.Query(world, grid, layer, i % 64, i / 64));
            for (var i = 0; i < 800; i++)
            {
                var next = World.Place(world, layer, 63.5f, 63.5f, UnitBox, 1);
                Assert.NotEqual(-1, next);
                World.Fade(world, next, 0, 1);
            }
            Assert.Equal(800, World.GetContext(world)->Timers.Length);
            AssertTimerMap(world);
            World.Process(world);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            Assert.Equal(0, World.Query(world, grid, layer, 63, 63));
        }
        finally { Assert.True(World.Free(world)); }
    }

    [Fact]
    public void Rewind_restores_sweep_membership_and_fade_expiry_timing_after_recorded_churn()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0, 0, 64);
        var layer = Layer.New(world);
        var source = World.Place(world, layer, 5.5f, 5.5f, UnitBox, 16);
        World.Fade(world, source, 0, 8);
        World.Expire(world, source, 10);
        World.Process(world);
        Assert.Equal(14, World.Query(world, grid, layer, 5, 5));
        try
        {
            World.Record(world);
            World.SetGain(world, source, 5);
            World.Expire(world, source, 1);
            World.Process(world);
            var reused = World.Place(world, layer, 20.5f, 20.5f, UnitBox, -16);
            World.Fade(world, reused, 16, 2);
            World.Process(world);
            World.Process(world);
            AssertTimerMap(world);
            World.Rewind(world);
            AssertTimerMap(world);
            for (var elapsed = 2; elapsed <= 10; elapsed++)
            {
                World.Process(world);
                var expected = elapsed >= 8 ? 0 : 16 - elapsed * 2;
                Assert.Equal(expected, World.Query(world, grid, layer, 5, 5));
                Assert.Equal(0, World.Query(world, grid, layer, 20, 20));
                AssertTimerMap(world);
            }
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            Assert.False(World.TryGetSourceSpec(world, source, out _));
        }
        finally { Assert.True(World.Free(world)); }
    }

    [Fact]
    public void Warm_sweep_over_future_events_allocates_zero_and_clear_reuses_its_storage()
    {
        var world = World.New();
        var layer = Layer.New(world);
        const int count = 20000;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var source = World.Place(world, layer, 0, 0, UnitBox, 1);
                World.Expire(world, source, 1000000);
            }
            World.Process(world);
            World.Process(world);
            var capacity = World.GetContext(world)->Timers.Capacity;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < 100; frame++) World.Process(world);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(count, World.GetContext(world)->Timers.Length);
            AssertTimerMap(world);
            World.Clear(world);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            Assert.Equal(capacity, World.GetContext(world)->Timers.Capacity);
            AssertTimerMap(world);
            var reused = World.Place(world, layer, 0, 0, UnitBox, 1);
            World.Expire(world, reused, 1);
            World.Process(world);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            AssertTimerMap(world);
        }
        finally { Assert.True(World.Free(world)); }
    }

    [Fact]
    public void Expiry_returns_fifo_slots_in_ascending_order_after_different_schedule_histories()
    {
        var first = World.New();
        var second = World.New();
        var firstLayer = Layer.New(first);
        var secondLayer = Layer.New(second);
        var ids = new int[257];
        try
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = World.Place(first, firstLayer, 0, 0, UnitBox, 1);
                Assert.Equal(ids[i], World.Place(second, secondLayer, 0, 0, UnitBox, 1));
                World.Expire(first, ids[i], 1);
            }
            for (var i = ids.Length - 1; i >= 0; i--) World.Expire(second, ids[i], 1);
            World.Process(first);
            World.Process(second);
            for (var i = 0; i < ids.Length; i++)
            {
                var source = World.Place(first, firstLayer, 0, 0, UnitBox, 1);
                Assert.Equal(i, source & World.SourceIndexMask);
                Assert.Equal(source, World.Place(second, secondLayer, 0, 0, UnitBox, 1));
            }
        }
        finally
        {
            Assert.True(World.Free(first));
            Assert.True(World.Free(second));
        }
    }

    [Fact]
    public void Warm_full_expiry_and_fifo_reuse_allocate_zero_managed_bytes()
    {
        var world = World.New();
        var layer = Layer.New(world);
        const int count = 4096;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var source = World.Place(world, layer, 0, 0, UnitBox, 1);
                World.Expire(world, source, 1);
            }
            World.Process(world);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < 8; frame++)
            {
                for (var i = 0; i < count; i++)
                {
                    var source = World.Place(world, layer, 0, 0, UnitBox, 1);
                    World.Expire(world, source, 1);
                }
                World.Process(world);
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(0, World.GetContext(world)->Timers.Length);
            Assert.Equal(count, World.GetContext(world)->Sources.Count);
            AssertTimerMap(world);
        }
        finally { Assert.True(World.Free(world)); }
    }

    private static void AssertTimerMap(byte world)
    {
        var w = World.GetContext(world);
        var seen = new bool[w->TimerPositions.Length];
        for (var index = 0; index < w->Timers.Length; index++)
        {
            var slot = w->Timers.Pointer[index].Slot;
            Assert.InRange(slot, 0, w->Sources.Count - 1);
            Assert.False(seen[slot]);
            seen[slot] = true;
            Assert.Equal(index + 1, w->TimerPositions.Pointer[slot]);
            Assert.NotEqual(0, w->Sources.Alive.Pointer[slot]);
            Assert.NotEqual(0, w->Sources.Timed.Pointer[slot]);
        }
        for (var slot = 0; slot < seen.Length; slot++)
            if (!seen[slot]) Assert.Equal(0, w->TimerPositions.Pointer[slot]);
    }
}
