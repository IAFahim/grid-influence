using Xunit;

namespace Gi.Tests;

public sealed unsafe class CrossWorldProcessTests
{
    private const int GridPower = 8;
    private const int Side = 256;
    private const int Ticks = 10_000;
    private const int HashEvery = 50;
    private const int Layers = 2;
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private sealed class Pair
    {
        public byte First;
        public byte Second;
    }

    private static byte[] NewStamps()
    {
        return
        [
            Stamp.Box(4, 4, 90),
            Stamp.Box(6, 3, -70),
            Stamp.Tent(7, 7, 100),
            Stamp.Bell(9, 9, -128),
            Stamp.Disk(4, 60),
            Stamp.Cone(5, 45, 90),
            Stamp.Dome(6, -50, 270),
        ];
    }

    private static byte NewWorld()
    {
        var world = World.New();
        Grid.New(world, GridPower, 0f, 0f, Side);
        for (var l = 0; l < Layers; l++) Layer.New(world);
        return world;
    }

    private static Pair NewPair()
    {
        return new Pair { First = NewWorld(), Second = NewWorld() };
    }

    private static void FreePair(Pair pair)
    {
        World.Free(pair.First);
        World.Free(pair.Second);
    }

    private static ulong Fold(ulong hash, byte world, short[] window)
    {
        for (var layer = 0; layer < Layers; layer++)
        {
            fixed (short* destination = window)
                World.QueryRegion(world, 0, (byte)layer, 0, 0, Side, Side, destination);
            foreach (var cell in window) hash = (hash ^ (ushort)cell) * Prime;
        }

        return hash;
    }

    private static void Expect(World.WriteStatus status)
    {
        if (status != World.WriteStatus.Applied) throw new InvalidOperationException("write status " + status);
    }

    private static ulong Drive(byte world, byte[] stamps, ulong seed, int ticks)
    {
        var rng = new SplitMix64(seed);
        var live = new List<World.SourceId>();
        var window = new short[Side * Side];
        var hash = Offset;
        for (var tick = 0; tick < ticks; tick++)
        {
            if (live.Count < 48 && rng.Chance(40))
            {
                var gain = (sbyte)rng.NextInt(1, 100);
                var id = World.Place(world, (byte)rng.NextInt(0, Layers), rng.NextInt(16, 240), rng.NextInt(16, 240),
                    stamps[rng.NextInt(0, stamps.Length)], rng.Chance(50) ? gain : (sbyte)-gain, out var status);
                Expect(status);
                live.Add(id);
            }

            if (live.Count > 0 && rng.Chance(30))
                Expect(World.Move(world, live[rng.NextInt(0, live.Count)], rng.NextInt(16, 240), rng.NextInt(16, 240)));
            if (live.Count > 0 && rng.Chance(15))
                Expect(World.SetGain(world, live[rng.NextInt(0, live.Count)], (sbyte)rng.NextInt(1, 100)));
            if (live.Count > 24 && rng.Chance(20))
            {
                var index = rng.NextInt(0, live.Count);
                Expect(World.Remove(world, live[index]));
                live[index] = live[^1];
                live.RemoveAt(live.Count - 1);
            }

            World.Process(world);
            if (tick % HashEvery == 0) hash = Fold(hash, world, window);
        }

        return Fold(hash, world, window);
    }

    private static (ulong First, ulong Second) RunSequential(Pair pair, byte[] stamps, int ticks)
    {
        return (Drive(pair.First, stamps, 11, ticks), Drive(pair.Second, stamps, 23, ticks));
    }

    private sealed class Worker(Barrier start, byte world, byte[] stamps, ulong seed, int ticks)
    {
        public ulong Hash;
        public Exception? Failure;

        public void Run()
        {
            try
            {
                start.SignalAndWait();
                Hash = Drive(world, stamps, seed, ticks);
            }
            catch (Exception e)
            {
                Failure = e;
            }
        }
    }

    private static (ulong First, ulong Second) RunConcurrent(Pair pair, byte[] stamps, int ticks)
    {
        var start = new Barrier(2);
        var workers = new[] { new Worker(start, pair.First, stamps, 11, ticks), new Worker(start, pair.Second, stamps, 23, ticks) };
        var threads = new[] { new Thread(workers[0].Run), new Thread(workers[1].Run) };
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        start.Dispose();
        foreach (var worker in workers)
            if (worker.Failure != null)
                throw new InvalidOperationException("worker failed", worker.Failure);

        return (workers[0].Hash, workers[1].Hash);
    }

    [Fact]
    public void TwoWorldsOnTwoThreads_HashLikeTheSameWorldsRunOneAfterTheOther()
    {
        var stamps = NewStamps();
        try
        {
            var sequentialPair = NewPair();
            var sequentialBefore = NativeHeap.Calls;
            var sequential = RunSequential(sequentialPair, stamps, Ticks);
            var sequentialCalls = NativeHeap.Calls - sequentialBefore;
            FreePair(sequentialPair);

            var concurrentPair = NewPair();
            var concurrentBefore = NativeHeap.Calls;
            var concurrent = RunConcurrent(concurrentPair, stamps, Ticks);
            var concurrentCalls = NativeHeap.Calls - concurrentBefore;
            FreePair(concurrentPair);

            Assert.NotEqual(sequential.First, sequential.Second);
            Assert.True(sequentialCalls > 0);
            Assert.Equal(sequential, concurrent);
            Assert.Equal(sequentialCalls, concurrentCalls);
        }
        finally
        {
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RepeatedConcurrentRuns_AgreeWithEachOther()
    {
        var stamps = NewStamps();
        try
        {
            var baseline = default((ulong First, ulong Second));
            for (var round = 0; round < 4; round++)
            {
                var pair = NewPair();
                var result = RunConcurrent(pair, stamps, 1_000);
                FreePair(pair);
                if (round == 0) baseline = result;
                Assert.Equal(baseline, result);
            }
        }
        finally
        {
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void StageTimers_AccumulateInTheWorldThatProcessed()
    {
        var stamps = NewStamps();
        var pair = NewPair();
        try
        {
            ProcessStages.Reset();
            ProcessStages.Measuring = true;
            Drive(pair.First, stamps, 11, 200);
            ProcessStages.Measuring = false;

            var first = Runtime.Worlds[pair.First].Stages;
            var second = Runtime.Worlds[pair.Second].Stages;
            Assert.True(first.Prepare > 0);
            Assert.True(first.Apply > 0);
            Assert.Equal(default, second);
            Assert.Equal(first.Prepare, ProcessStages.PrepareTicks);
            Assert.Equal(first.Apply, ProcessStages.ApplyTicks);

            ProcessStages.Reset();
            Assert.Equal(0, ProcessStages.PrepareTicks);
            Assert.Equal(0, ProcessStages.ApplyTicks);
        }
        finally
        {
            ProcessStages.Measuring = false;
            FreePair(pair);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void StageTimers_StayExactWhileTwoWorldsMeasureAtOnce()
    {
        var stamps = NewStamps();
        var pair = NewPair();
        try
        {
            ProcessStages.Reset();
            ProcessStages.Measuring = true;
            RunConcurrent(pair, stamps, 500);
            ProcessStages.Measuring = false;

            var first = Runtime.Worlds[pair.First].Stages;
            var second = Runtime.Worlds[pair.Second].Stages;
            Assert.True(first.Prepare > 0 && second.Prepare > 0);
            Assert.Equal(first.Prepare + second.Prepare, ProcessStages.PrepareTicks);
            Assert.Equal(first.Apply + second.Apply, ProcessStages.ApplyTicks);
        }
        finally
        {
            ProcessStages.Measuring = false;
            FreePair(pair);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }
}
