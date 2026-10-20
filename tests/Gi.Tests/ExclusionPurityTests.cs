using Xunit;
using Xunit.Sdk;

namespace Gi.Tests;

public sealed unsafe class ExclusionPurityTests
{
    private const int GridPower = 9;
    private const float WorldSize = 512f;

    private static readonly Layer.Precision[] Precisions =
    {
        Layer.Precision.Exact, Layer.Precision.Half, Layer.Precision.Quarter, Layer.Precision.Cell
    };

    private static byte NewWorld()
    {
        var world = World.New();
        Grid.New(world, GridPower, 0f, 0f, WorldSize);
        return world;
    }

    private static byte Raster(int width, int height, int salt)
    {
        var data = new sbyte[width * height];
        for (var i = 0; i < data.Length; i++) data[i] = (sbyte)((i * 37 + salt * 11) % 231 - 115);
        return Stamp.New(data, (ushort)width, (ushort)height);
    }

    private static ulong HashExcluded(byte world, byte layer, World.SourceId exclude, float x, float y, float reach,
        ulong seed)
    {
        var hash = new CorpusHash(seed);
        hash.Mix(World.TrySense(world, layer, x, y, exclude, out var value));
        hash.Mix(value);
        hash.Mix(World.TrySenseGradient(world, layer, x, y, exclude, out var gx, out var gy));
        hash.Mix(gx);
        hash.Mix(gy);
        hash.Mix(World.TrySenseArea(world, layer, x, y, reach, exclude, out var area));
        hash.Mix(area);
        return hash.Value;
    }

    private static ulong HashSplatExcluded(byte world, byte layer, byte stamp, sbyte gain, float x, float y,
        float reach, ulong seed)
    {
        var hash = new CorpusHash(seed);
        hash.Mix(World.TrySense(world, layer, x, y, stamp, gain, out var value));
        hash.Mix(value);
        hash.Mix(World.TrySenseGradient(world, layer, x, y, stamp, gain, out var gx, out var gy));
        hash.Mix(gx);
        hash.Mix(gy);
        hash.Mix(World.TrySenseArea(world, layer, x, y, reach, stamp, gain, out var area));
        hash.Mix(area);
        return hash.Value;
    }

    private static ulong HashFieldReads(byte world, FieldState field, FieldPlan plan, ulong seed)
    {
        var hash = new CorpusHash(seed);
        var xs = stackalloc float[8];
        var ys = stackalloc float[8];
        var reaches = stackalloc float[8];
        for (var i = 0; i < field.Ids.Count; i++)
        {
            const int count = 7;
            xs[0] = plan.Xs[i];
            ys[0] = plan.Ys[i];
            xs[1] = plan.Xs[i] + 1.75f;
            ys[1] = plan.Ys[i] - 0.5f;
            xs[2] = plan.Xs[i] - 2.5f;
            ys[2] = plan.Ys[i] + 2.5f;
            xs[3] = plan.Xs[i] + 6f;
            ys[3] = plan.Ys[i] + 6f;
            xs[4] = plan.Xs[i] - 9f;
            ys[4] = plan.Ys[i] + 3f;
            xs[5] = plan.Xs[i] + 0.25f;
            ys[5] = plan.Ys[i] + 0.25f;
            xs[6] = plan.Xs[i] - 4f;
            ys[6] = plan.Ys[i] - 7f;
            reaches[0] = 0.5f;
            reaches[1] = 2f;
            reaches[2] = 4f;
            reaches[3] = 9f;
            reaches[4] = 16f;
            reaches[5] = 1f;
            reaches[6] = 32f;
            var id = field.Ids[i];
            for (var p = 0; p < count; p++)
            {
                hash.Mix(HashExcluded(world, field.Layer1, id, xs[p], ys[p], reaches[p], hash.Value));
                hash.Mix(HashExcluded(world, field.Layer2, id, xs[p], ys[p], reaches[p], hash.Value));
                hash.Mix(HashExcluded(world, field.Derived, id, xs[p], ys[p], reaches[p], hash.Value));
            }

            hash.Mix(HashSplatExcluded(world, field.Transient, plan.StampOf[i], 5, plan.Xs[i], plan.Ys[i], 4f,
                hash.Value));
            hash.Mix(HashSplatExcluded(world, field.Transient, plan.Ghost, -3, plan.Xs[i] + 1.5f, plan.Ys[i] - 1.5f,
                12f, hash.Value));
        }

        return hash.Value;
    }

    [Fact]
    public void ExcludedReads_MatchBetweenInsertableWritesAndADisabledCache()
    {
        for (var scenario = 0; scenario < 10; scenario++)
        {
            var secondGrid = scenario == 8;
            var plan = new FieldPlan(scenario, new SplitMix64((ulong)(400 + scenario)));
            try
            {
                foreach (var precision in Precisions)
                {
                    var warm = NewWorld();
                    var cold = NewWorld();
                    try
                    {
                        var warmField = new FieldState(warm, secondGrid, plan, precision);
                        var coldField = new FieldState(cold, secondGrid, plan, precision);
                        World.DisableKernelInserts(cold);
                        World.Process(warm);
                        World.Process(cold);

                        var warmFirst = HashFieldReads(warm, warmField, plan, CorpusHash.Offset);
                        var coldReads = HashFieldReads(cold, coldField, plan, CorpusHash.Offset);
                        var warmSecond = HashFieldReads(warm, warmField, plan, CorpusHash.Offset);
                        Assert.True(warmFirst == coldReads && warmSecond == coldReads,
                            $"scenario {scenario} precision {precision.PhaseBits}/{precision.TurnBits}: " +
                            $"warm {warmFirst:x16}/{warmSecond:x16} vs cold {coldReads:x16}");
                    }
                    finally
                    {
                        World.Free(warm);
                        World.Free(cold);
                    }
                }
            }
            finally
            {
                foreach (var stamp in plan.Stamps) Stamp.Free(stamp);
                Stamp.Free(plan.Ghost);
            }
        }
    }

    private static (ulong Main, ulong Turn) KeyOf(byte world, int grid, byte layer, byte stamp, float x, float y,
        Layer.Precision precision)
    {
        var w = World.GetContext(world);
        var g = w->Grids + grid;
        var mask = w->Recipes[layer].AnchorMask;
        var ax = TileBake.AnchorQ8(x, g->OriginX, g->ScaleQ8, mask);
        var ay = TileBake.AnchorQ8(y, g->OriginY, g->ScaleQ8, mask);
        TileBake.PlaceAnchored(ax, ay, g->ScaleQ8, g->Size, StampCatalog.Get(stamp), 0, 256, precision, out var p);
        var key = new KernelKey(stamp, p.Sampling, grid, p.Fx, p.Fy, 0);
        return (key.Main, key.Turn);
    }

    [Fact]
    public void ExcludedReads_InsertNoKernelEntriesAndLeaveTheBudgetUnspent()
    {
        var precision = Layer.Precision.Half;
        var placed = Stamp.Tent(6, 6, 80);
        var ghost = Raster(4, 4, 7);
        var warm = NewWorld();
        var cold = NewWorld();
        try
        {
            var warmLayer = Layer.New(warm, Layer.Residency.Persistent, precision);
            var warmTransient = Layer.New(warm, Layer.Residency.Transient, precision);
            var coldLayer = Layer.New(cold, Layer.Residency.Persistent, precision);
            var coldTransient = Layer.New(cold, Layer.Residency.Transient, precision);
            World.SourceId a = default;
            World.SourceId b = default;
            for (var i = 0; i < 3; i++)
            {
                a = World.Place(warm, warmLayer, 60f + i * 3.25f, 70f + i * 2.5f, placed, 6);
                b = World.Place(cold, coldLayer, 60f + i * 3.25f, 70f + i * 2.5f, placed, 6);
            }

            World.Process(warm);
            World.Process(cold);
            World.DisableKernelInserts(cold);
            var warmContext = World.GetContext(warm);
            Assert.True(warmContext->Kernels.CanInsert);

            var (main, turn) = KeyOf(warm, 0, warmTransient, ghost, 130.5f, 140.5f, precision);
            Assert.True(warmContext->Kernels.Find(main, turn) == null);

            var warmHash = new CorpusHash(CorpusHash.Offset);
            var coldHash = new CorpusHash(CorpusHash.Offset);
            for (var i = 0; i < 6; i++)
            {
                var x = 130.5f + i * 0.75f;
                var y = 140.5f - i * 0.5f;
                warmHash.Mix(HashSplatExcluded(warm, warmTransient, ghost, 4, x, y, 8f, warmHash.Value));
                warmHash.Mix(HashExcluded(warm, warmLayer, a, x, y, 6f, warmHash.Value));
                coldHash.Mix(HashSplatExcluded(cold, coldTransient, ghost, 4, x, y, 8f, coldHash.Value));
                coldHash.Mix(HashExcluded(cold, coldLayer, b, x, y, 6f, coldHash.Value));
            }

            Assert.True(warmContext->Kernels.Find(main, turn) == null);
            Assert.True(warmContext->Kernels.CanInsert);
            Assert.Equal(coldHash.Value, warmHash.Value);
        }
        finally
        {
            World.Free(warm);
            World.Free(cold);
            Stamp.Free(placed);
            Stamp.Free(ghost);
        }
    }

    private static void RunThreadReads(byte world, ThreadField field, ReadOutcomes outcomes)
    {
        for (var i = 0; i < field.Ids.Count; i++)
        {
            var sourceLayer = (byte)(i % 2);
            var layer = i % 4 == 3 ? field.Derived : sourceLayer;
            World.TrySense(world, layer, field.Xs[i], field.Ys[i], field.Ids[i], out outcomes.Points[2 * i]);
            World.TrySenseArea(world, layer, field.Xs[i], field.Ys[i], 3f + i % 5 * 4f, field.Ids[i],
                out outcomes.Areas[2 * i]);
            World.TrySenseGradient(world, layer, field.Xs[i], field.Ys[i], field.Ids[i], out var gx, out var gy);
            outcomes.GradientBits[4 * i] = BitConverter.SingleToInt32Bits(gx);
            outcomes.GradientBits[4 * i + 1] = BitConverter.SingleToInt32Bits(gy);

            World.TrySense(world, field.Transient, field.Xs[i], field.Ys[i], field.Ghost, 6,
                out outcomes.Points[2 * i + 1]);
            World.TrySenseArea(world, field.Transient, field.Xs[i], field.Ys[i], 7f, field.Ghost, -5,
                out outcomes.Areas[2 * i + 1]);
            World.TrySenseGradient(world, field.Transient, field.Xs[i], field.Ys[i], field.Ghost, 3, out gx, out gy);
            outcomes.GradientBits[4 * i + 2] = BitConverter.SingleToInt32Bits(gx);
            outcomes.GradientBits[4 * i + 3] = BitConverter.SingleToInt32Bits(gy);
        }
    }

    private static void RunEightThreads(Action<int> read)
    {
        const int threads = 8;
        var ready = new ManualResetEventSlim(false);
        var go = new ManualResetEventSlim(false);
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            var index = t;
            workers[index] = new Thread(() =>
            {
                ready.Set();
                go.Wait();
                read(index);
            })
            {
                IsBackground = true
            };
            workers[index].Start();
        }

        for (var t = 0; t < threads; t++) ready.Wait();
        go.Set();
        for (var t = 0; t < threads; t++) workers[t].Join();
    }

    [Fact]
    public void ExcludedReads_OnEightThreads_MatchSerialReadsAndInsertNothing()
    {
        var world = NewWorld();
        var field = new ThreadField(world, new SplitMix64(61_000));
        World.Process(world);
        World.Process(world);

        var transient = field.Transient;
        var (ghostMain, ghostTurn) = KeyOf(world, 0, transient, field.Ghost, 64.5f, 96.5f, Layer.Precision.Quarter);
        var context = World.GetContext(world);
        Assert.True(context->Kernels.CanInsert);
        Assert.True(context->Kernels.Find(ghostMain, ghostTurn) == null);

        var serial = new ReadOutcomes(field.Ids.Count);
        try
        {
            RunThreadReads(world, field, serial);

            var parallel = new ReadOutcomes[8];
            for (var t = 0; t < 8; t++) parallel[t] = new ReadOutcomes(field.Ids.Count);
            RunEightThreads(t => RunThreadReads(world, field, parallel[t]));

            for (var t = 0; t < 8; t++)
            {
                Assert.True(parallel[t].Points.AsSpan().SequenceEqual(serial.Points.AsSpan()), $"thread {t} points");
                Assert.True(parallel[t].Areas.AsSpan().SequenceEqual(serial.Areas.AsSpan()), $"thread {t} areas");
                Assert.True(parallel[t].GradientBits.AsSpan().SequenceEqual(serial.GradientBits.AsSpan()),
                    $"thread {t} gradients");
            }

            HashSplatExcluded(world, transient, field.Ghost, 2, 64.5f, 96.5f, 9f, CorpusHash.Offset);
            Assert.True(context->Kernels.Find(ghostMain, ghostTurn) == null);
            Assert.True(context->Kernels.CanInsert);

            var repeat = new ReadOutcomes(field.Ids.Count);
            RunThreadReads(world, field, repeat);
            Assert.True(repeat.Points.AsSpan().SequenceEqual(serial.Points.AsSpan()));
            Assert.True(repeat.Areas.AsSpan().SequenceEqual(serial.Areas.AsSpan()));
            Assert.True(repeat.GradientBits.AsSpan().SequenceEqual(serial.GradientBits.AsSpan()));
        }
        finally
        {
            World.Free(world);
            foreach (var stamp in field.Stamps) Stamp.Free(stamp);
            Stamp.Free(field.Ghost);
        }
    }

    [Fact]
    public void ExcludedColumnReads_OnEightThreads_MatchSerialReads()
    {
        const int count = 24;
        var world = NewWorld();
        var field = new ThreadField(world, new SplitMix64(71_000));
        World.Process(world);

        var xs = new float[count];
        var ys = new float[count];
        var excludes = new World.SourceId[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = field.Xs[i % field.Ids.Count];
            ys[i] = field.Ys[i % field.Ids.Count];
            excludes[i] = field.Ids[i * 7 % field.Ids.Count];
        }

        try
        {
            var serialValues = new short[count];
            var serialGradients = new int[2 * count];
            var serialWords = new ulong[(count + 63) >> 6];
            ReadColumns(world, field, xs, ys, excludes, count, serialValues, serialGradients, serialWords);

            var parallel = new (short[] Values, int[] Gradients, ulong[] Words)[8];
            for (var t = 0; t < 8; t++)
                parallel[t] = (new short[count], new int[2 * count], new ulong[(count + 63) >> 6]);
            RunEightThreads(t => ReadColumns(world, field, xs, ys, excludes, count, parallel[t].Values,
                parallel[t].Gradients, parallel[t].Words));

            for (var t = 0; t < 8; t++)
            {
                Assert.True(parallel[t].Values.AsSpan().SequenceEqual(serialValues.AsSpan()), $"thread {t} values");
                Assert.True(parallel[t].Gradients.AsSpan().SequenceEqual(serialGradients.AsSpan()),
                    $"thread {t} gradients");
                Assert.True(parallel[t].Words.AsSpan().SequenceEqual(serialWords.AsSpan()), $"thread {t} words");
            }
        }
        finally
        {
            World.Free(world);
            foreach (var stamp in field.Stamps) Stamp.Free(stamp);
            Stamp.Free(field.Ghost);
        }
    }

    private static void ReadColumns(
        byte world, ThreadField field, float[] xs, float[] ys, World.SourceId[] excludes, int count,
        short[] values, int[] gradients, ulong[] words)
    {
        Array.Clear(words);
        fixed (float* pXs = xs, pYs = ys)
        fixed (World.SourceId* pExcludes = excludes)
        fixed (short* pValues = values)
        fixed (int* pGradients = gradients)
        fixed (ulong* pWords = words)
        {
            World.SenseColumn(world, field.Layer1, pXs, pYs, pExcludes, count, pValues, pWords);
            for (var i = 0; i < count; i++)
            {
                World.TrySenseGradient(world, field.Derived, pXs[i], pYs[i], pExcludes[i], out var gx, out var gy);
                pGradients[2 * i] = BitConverter.SingleToInt32Bits(gx);
                pGradients[2 * i + 1] = BitConverter.SingleToInt32Bits(gy);
            }
        }
    }

    [Fact]
    public void ExcludedFuzzStreamReads_AgreeBetweenTheInsertableAndTheDisabledCache()
    {
        const int streams = 150;
        const int seedBase = 2_600_000;
        var excludedReads = 0;
        for (var i = 0; i < streams; i++)
        {
            var profile = i % 3 == 0 ? StreamProfile.Kinds : i % 3 == 1 ? StreamProfile.Gate : StreamProfile.Transient;
            var setup = StreamGenerator.Generate(seedBase + i, (PrecisionMode)(i % 5), profile);
            foreach (var command in setup.Commands)
            {
                if (command.Op != StreamOp.Read) continue;
                if (command.Read is ReadKind.SenseEx or ReadKind.SenseAreaEx or ReadKind.SenseGradientEx
                    or ReadKind.SenseColumnEx)
                    excludedReads++;
            }

            var insertable = StreamRunner.TryRun(setup, setup.Commands);
            if (insertable != null) throw new XunitException($"seed {seedBase + i} insertable: {insertable.Message}");

            var disabled = StreamRunner.TryRun(setup, setup.Commands, uncached: true);
            if (disabled != null) throw new XunitException($"seed {seedBase + i} disabled: {disabled.Message}");
        }

        Assert.True(excludedReads >= streams, $"only {excludedReads} excluded reads across {streams} streams");
    }

    private sealed class FieldPlan
    {
        public readonly int Count;
        public readonly sbyte[] Gains = new sbyte[40];
        public readonly byte Ghost;
        public readonly float[] Scales = new float[40];
        public readonly byte[] StampOf = new byte[40];
        public readonly List<byte> Stamps = [];
        public readonly float[] Turns = new float[40];
        public readonly float[] Xs = new float[40];
        public readonly float[] Ys = new float[40];

        public FieldPlan(int scenario, SplitMix64 rng)
        {
            Stamps.Add(Stamp.Box(4, 4, 90));
            Stamps.Add(Stamp.Box(6, 3, -70));
            Stamps.Add(Raster(5, 4, 3));
            Stamps.Add(Stamp.Tent(7, 7, 100));
            Stamps.Add(Stamp.Bell(9, 9, -128));
            Stamps.Add(Stamp.Disk(4, 60));
            Stamps.Add(Stamp.Cone(5, 45, 90));
            Stamps.Add(Stamp.Dome(6, -50, 270));
            Stamps.Add(Stamp.Tent(8, 5, 127));
            Stamps.Add(Stamp.Bell(70, 70, 90));
            Ghost = scenario == 0 ? Stamp.Tent(5, 5, 40) : Raster(3, 3, 11);

            Count = scenario == 6 ? 40 : 14;
            for (var i = 0; i < Count; i++)
            {
                StampOf[i] = scenario == 9 ? Stamps[9] : Stamps[(scenario + i) % 9];

                float x;
                float y;
                if (scenario == 6)
                {
                    x = WorldSize / 2 + 0.5f;
                    y = WorldSize / 2 + 0.5f;
                }
                else if (scenario == 7)
                {
                    var row = i / 3;
                    x = 1.5f + i % 3 * 0.75f;
                    y = 0.5f + row * 0.75f;
                }
                else if (scenario == 9)
                {
                    x = WorldSize / 2 + (i - Count / 2f) * 1.5f;
                    y = WorldSize / 2 + i % 5 * 2.5f;
                }
                else
                {
                    x = 12f + rng.NextUnit() * (WorldSize - 24f);
                    y = 12f + rng.NextUnit() * (WorldSize - 24f);
                }

                var gain = scenario == 6 ? (sbyte)16 : (sbyte)rng.NextInt(-9, 10);
                Xs[i] = x;
                Ys[i] = y;
                Gains[i] = gain == 0 ? (sbyte)4 : gain;
                Turns[i] = i % 7 == 3 ? 0.25f + i * 0.37f : 0f;
                Scales[i] = i % 11 == 5 ? 1.5f : 1f;
            }
        }
    }

    private sealed class FieldState
    {
        public readonly byte Derived;
        public readonly List<World.SourceId> Ids = [];
        public readonly byte Layer1;
        public readonly byte Layer2;
        public readonly byte Transient;

        public FieldState(byte world, bool secondGrid, FieldPlan plan, Layer.Precision precision)
        {
            if (secondGrid) Grid.New(world, GridPower - 1, 0f, 0f, WorldSize);
            Layer1 = Layer.New(world, Layer.Residency.Persistent, precision);
            Layer2 = Layer.New(world, Layer.Residency.Persistent, precision);
            Derived = Layer.Sum(world, Layer1, 3, Layer2, -2);
            Transient = Layer.New(world, Layer.Residency.Transient, precision);
            for (var i = 0; i < plan.Count; i++)
            {
                var id = World.Place(world, (byte)(i % 2), plan.Xs[i], plan.Ys[i], plan.StampOf[i], plan.Gains[i]);
                if (!plan.Turns[i].Equals(0f)) World.Turn(world, id, plan.Turns[i]);
                if (!plan.Scales[i].Equals(1f)) World.Scale(world, id, plan.Scales[i]);
                Ids.Add(id);
                if (i % 2 == 0)
                    World.Splat(world, Transient, new[] { plan.Xs[i] }, new[] { plan.Ys[i] }, plan.StampOf[i],
                        plan.Gains[i]);
            }
        }
    }

    private sealed class ThreadField
    {
        public readonly byte Derived;
        public readonly byte Ghost;
        public readonly List<World.SourceId> Ids = [];
        public readonly byte Layer1;
        public readonly byte Layer2;
        public readonly List<byte> Stamps = [];
        public readonly byte Transient;
        public readonly List<float> Xs = [];
        public readonly List<float> Ys = [];

        public ThreadField(byte world, SplitMix64 rng)
        {
            Grid.New(world, GridPower - 1, 0f, 0f, WorldSize);
            var precision = Layer.Precision.Quarter;
            Layer1 = Layer.New(world, Layer.Residency.Persistent, precision);
            Layer2 = Layer.New(world, Layer.Residency.Persistent, precision);
            Derived = Layer.Sum(world, Layer1, 1, Layer2, 1);
            Transient = Layer.New(world, Layer.Residency.Transient, precision);
            Stamps.Add(Stamp.Box(5, 5, 90));
            Stamps.Add(Stamp.Tent(8, 8, 110));
            Stamps.Add(Stamp.Bell(10, 6, -95));
            Stamps.Add(Raster(6, 5, 5));
            Stamps.Add(Stamp.Cone(6, 70, 120));
            Ghost = Stamp.Tent(4, 4, 55);

            for (var i = 0; i < 96; i++)
            {
                var x = 14f + rng.NextUnit() * (WorldSize - 28f);
                var y = 14f + rng.NextUnit() * (WorldSize - 28f);
                var id = World.Place(world, (byte)(i % 2), x, y, Stamps[i % Stamps.Count], (sbyte)rng.NextInt(-8, 9));
                if (i % 3 == 0) World.Turn(world, id, rng.NextUnit() * 3f);
                Ids.Add(id);
                Xs.Add(x);
                Ys.Add(y);
                if (i % 2 == 0) World.Splat(world, Transient, new[] { x }, new[] { y }, Stamps[i % Stamps.Count], 5);
            }
        }
    }

    private sealed class ReadOutcomes
    {
        public readonly long[] Areas;
        public readonly int[] GradientBits;
        public readonly short[] Points;

        public ReadOutcomes(int count)
        {
            Points = new short[2 * count];
            Areas = new long[2 * count];
            GradientBits = new int[4 * count];
        }
    }
}
