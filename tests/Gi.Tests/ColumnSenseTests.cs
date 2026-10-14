using Xunit;

namespace Gi.Tests;

public sealed class ColumnSenseTests : IDisposable
{
    private readonly byte _world = World.New();
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _derived;
    private readonly byte _box = Stamp.Box(8, 8, 100);
    private readonly byte _dome = Stamp.Dome(6, 90);
    private readonly byte _cone = Stamp.Cone(5, -80, 270);
    private readonly int _first;
    private readonly int _second;
    private readonly int _third;

    public ColumnSenseTests()
    {
        _grid = Grid.New(_world, 7, 0f, 0f, 128f);
        _layer = Layer.New(_world);
        _derived = Layer.Sum(_world, _layer, 1, _layer, 2);
        _first = World.Place(_world, _layer, 40f, 40f, _box, 3);
        _second = World.Place(_world, _layer, 44f, 44f, _dome, 2);
        _third = World.Place(_world, _layer, 60f, 40f, _cone, -2);
        World.Turn(_world, _third, 1.1f);
        World.Scale(_world, _third, 1.5f);
        World.Process(_world);
        Assert.True(_first >= 0 && _second >= 0 && _third >= 0);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_box));
        Assert.True(Stamp.Free(_dome));
        Assert.True(Stamp.Free(_cone));
    }

    private int ExcludeAt(int index) => index % 3 == 0 ? -1 : index % 3 == 1 ? _second : _third;

    [Fact]
    public unsafe void SenseColumnWithExcludes_MatchesScalarSense()
    {
        const int count = 48;
        var random = new Random(1400);
        Span<float> xs = stackalloc float[count];
        Span<float> ys = stackalloc float[count];
        Span<int> excludes = stackalloc int[count];
        Span<short> batch = stackalloc short[count];
        Span<short> scalar = stackalloc short[count];
        Span<ulong> whole = stackalloc ulong[(count + 63) >> 6];
        FillCoordinates(random, xs, ys, count);
        for (var i = 0; i < count; i++) excludes[i] = ExcludeAt(i);

        var scalarComplete = true;
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (int* excludePointer = excludes)
        fixed (short* batchPointer = batch)
        fixed (short* scalarPointer = scalar)
        fixed (ulong* wholePointer = whole)
        {
            for (var i = 0; i < count; i++)
            {
                bool filled;
                if (excludes[i] >= 0)
                    filled = World.TrySense(_world, _layer, xs[i], ys[i], excludes[i], out scalarPointer[i]);
                else
                    filled = World.TrySense(_world, _layer, xs[i], ys[i], out scalarPointer[i]);
                scalarComplete &= filled;
            }

            var batchComplete = World.SenseColumn(_world, _layer, xPointer, yPointer, excludePointer, count, batchPointer, wholePointer);
            Assert.Equal(scalarComplete, batchComplete);
        }

        Assert.True(batch.SequenceEqual(scalar));
        AssertMaskMatchesCompleteness(whole, count, scalarComplete);
    }

    [Fact]
    public unsafe void SenseGradientColumn_MatchesScalarGradient()
    {
        const int count = 48;
        var random = new Random(1401);
        Span<float> xs = stackalloc float[count];
        Span<float> ys = stackalloc float[count];
        Span<int> excludes = stackalloc int[count];
        Span<float> batchX = stackalloc float[count];
        Span<float> batchY = stackalloc float[count];
        Span<float> scalarX = stackalloc float[count];
        Span<float> scalarY = stackalloc float[count];
        Span<ulong> whole = stackalloc ulong[(count + 63) >> 6];
        FillCoordinates(random, xs, ys, count);
        for (var i = 0; i < count; i++) excludes[i] = ExcludeAt(i);

        var scalarComplete = true;
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (int* excludePointer = excludes)
        fixed (float* batchXPointer = batchX)
        fixed (float* batchYPointer = batchY)
        fixed (float* scalarXPointer = scalarX)
        fixed (float* scalarYPointer = scalarY)
        fixed (ulong* wholePointer = whole)
        {
            for (var i = 0; i < count; i++)
            {
                bool filled;
                if (excludes[i] >= 0)
                    filled = World.TrySenseGradient(_world, _layer, xs[i], ys[i], excludes[i], out scalarXPointer[i], out scalarYPointer[i]);
                else
                    filled = World.TrySenseGradient(_world, _layer, xs[i], ys[i], out scalarXPointer[i], out scalarYPointer[i]);
                scalarComplete &= filled;
            }

            var batchComplete = World.SenseGradientColumn(
                _world, _layer, xPointer, yPointer, excludePointer, count, batchXPointer, batchYPointer, wholePointer);
            Assert.Equal(scalarComplete, batchComplete);
        }

        Assert.True(batchX.SequenceEqual(scalarX));
        Assert.True(batchY.SequenceEqual(scalarY));
        AssertMaskMatchesCompleteness(whole, count, scalarComplete);
    }

    [Fact]
    public unsafe void SenseAreaColumn_MatchesScalarArea()
    {
        const int count = 48;
        var random = new Random(1402);
        Span<float> xs = stackalloc float[count];
        Span<float> ys = stackalloc float[count];
        Span<float> reaches = stackalloc float[count];
        Span<int> excludes = stackalloc int[count];
        Span<long> batch = stackalloc long[count];
        Span<long> scalar = stackalloc long[count];
        Span<ulong> whole = stackalloc ulong[(count + 63) >> 6];
        FillCoordinates(random, xs, ys, count);
        for (var i = 0; i < count; i++)
        {
            reaches[i] = 2f + i % 12;
            excludes[i] = ExcludeAt(i);
        }

        var scalarComplete = true;
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (float* reachPointer = reaches)
        fixed (int* excludePointer = excludes)
        fixed (long* batchPointer = batch)
        fixed (long* scalarPointer = scalar)
        fixed (ulong* wholePointer = whole)
        {
            for (var i = 0; i < count; i++)
            {
                bool filled;
                if (excludes[i] >= 0)
                    filled = World.TrySenseArea(_world, _layer, xs[i], ys[i], reaches[i], excludes[i], out scalarPointer[i]);
                else
                    filled = World.TrySenseArea(_world, _layer, xs[i], ys[i], reaches[i], out scalarPointer[i]);
                scalarComplete &= filled;
            }

            var batchComplete = World.SenseAreaColumn(
                _world, _layer, xPointer, yPointer, reachPointer, excludePointer, count, batchPointer, wholePointer);
            Assert.Equal(scalarComplete, batchComplete);
        }

        Assert.True(batch.SequenceEqual(scalar));
        AssertMaskMatchesCompleteness(whole, count, scalarComplete);
    }

    [Fact]
    public unsafe void SenseMaxColumn_MatchesScalarMax()
    {
        const int count = 48;
        var random = new Random(1403);
        Span<float> xs = stackalloc float[count];
        Span<float> ys = stackalloc float[count];
        Span<float> reaches = stackalloc float[count];
        Span<short> batch = stackalloc short[count];
        Span<short> scalar = stackalloc short[count];
        Span<float> batchX = stackalloc float[count];
        Span<float> batchY = stackalloc float[count];
        Span<float> scalarX = stackalloc float[count];
        Span<float> scalarY = stackalloc float[count];
        Span<ulong> whole = stackalloc ulong[(count + 63) >> 6];
        FillCoordinates(random, xs, ys, count);
        for (var i = 0; i < count; i++) reaches[i] = 3f + i % 15;

        var scalarComplete = true;
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (float* reachPointer = reaches)
        fixed (short* batchPointer = batch)
        fixed (short* scalarPointer = scalar)
        fixed (float* batchXPointer = batchX)
        fixed (float* batchYPointer = batchY)
        fixed (float* scalarXPointer = scalarX)
        fixed (float* scalarYPointer = scalarY)
        fixed (ulong* wholePointer = whole)
        {
            for (var i = 0; i < count; i++)
                scalarComplete &= World.TrySenseMax(_world, _layer, xs[i], ys[i], reaches[i],
                    out scalarPointer[i], out scalarXPointer[i], out scalarYPointer[i]);

            var batchComplete = World.SenseMaxColumn(
                _world, _layer, xPointer, yPointer, reachPointer, count, batchPointer, batchXPointer, batchYPointer, wholePointer);
            Assert.Equal(scalarComplete, batchComplete);
        }

        Assert.True(batch.SequenceEqual(scalar));
        Assert.True(batchX.SequenceEqual(scalarX));
        Assert.True(batchY.SequenceEqual(scalarY));
        AssertMaskMatchesCompleteness(whole, count, scalarComplete);
    }

    [Fact]
    public unsafe void SenseNearestColumn_MatchesScalarNearest()
    {
        const int count = 48;
        var random = new Random(1404);
        Span<float> xs = stackalloc float[count];
        Span<float> ys = stackalloc float[count];
        Span<float> reaches = stackalloc float[count];
        Span<short> thresholds = stackalloc short[count];
        Span<short> batch = stackalloc short[count];
        Span<short> scalar = stackalloc short[count];
        Span<float> batchX = stackalloc float[count];
        Span<float> batchY = stackalloc float[count];
        Span<float> scalarX = stackalloc float[count];
        Span<float> scalarY = stackalloc float[count];
        Span<ulong> found = stackalloc ulong[(count + 63) >> 6];
        FillCoordinates(random, xs, ys, count);
        for (var i = 0; i < count; i++)
        {
            reaches[i] = 4f + i % 20;
            thresholds[i] = (short)(20 * (i % 4));
        }

        var scalarComplete = true;
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (float* reachPointer = reaches)
        fixed (short* thresholdPointer = thresholds)
        fixed (short* batchPointer = batch)
        fixed (short* scalarPointer = scalar)
        fixed (float* batchXPointer = batchX)
        fixed (float* batchYPointer = batchY)
        fixed (float* scalarXPointer = scalarX)
        fixed (float* scalarYPointer = scalarY)
        fixed (ulong* foundPointer = found)
        {
            for (var i = 0; i < count; i++)
                scalarComplete &= World.TrySenseNearest(_world, _layer, xs[i], ys[i], reaches[i], thresholds[i],
                    out scalarPointer[i], out scalarXPointer[i], out scalarYPointer[i]);

            var batchComplete = World.SenseNearestColumn(
                _world, _layer, xPointer, yPointer, reachPointer, thresholdPointer, count,
                batchPointer, batchXPointer, batchYPointer, foundPointer);
            Assert.Equal(scalarComplete, batchComplete);
        }

        Assert.True(batch.SequenceEqual(scalar));
        Assert.True(batchX.SequenceEqual(scalarX));
        Assert.True(batchY.SequenceEqual(scalarY));
        for (var i = 0; i < count; i++)
        {
            var bit = (found[i >> 6] & (1ul << (i & 63))) != 0;
            Assert.Equal(scalar[i] != short.MinValue, bit);
        }
    }

    [Fact]
    public unsafe void BatchSensing_RejectsInvalidWorldLayerAndNullBuffers()
    {
        Span<float> xs = stackalloc float[2];
        Span<float> ys = stackalloc float[2];
        Span<short> values = stackalloc short[2];
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (short* valuePointer = values)
        {
            Assert.False(World.SenseColumn(255, _layer, xPointer, yPointer, null, 2, valuePointer, null));
            Assert.False(World.SenseColumn(_world, 31, xPointer, yPointer, null, 2, valuePointer, null));
            Assert.True(World.SenseColumn(_world, _layer, xPointer, yPointer, null, 0, valuePointer, null));
            Assert.False(World.SenseColumn(_world, _layer, null, yPointer, null, 2, valuePointer, null));
        }
    }

    [Fact]
    public void SourceSpec_ReflectsPlacementTurnAndScale()
    {
        var source = World.Place(_world, _layer, 33.5f, 21.5f, _box, 2);
        World.Turn(_world, source, 1.25f);
        World.Scale(_world, source, 2f);

        Assert.True(World.TryGetSourceSpec(_world, source, out var spec));
        Assert.Equal(33.5f, spec.X);
        Assert.Equal(21.5f, spec.Y);
        Assert.Equal(1.25f, spec.HeadingRadians, 3);
        Assert.Equal(2f, spec.Scale, 3);
        Assert.Equal(2, spec.Gain);
        Assert.Equal(_layer, spec.Layer);
        Assert.Equal(_box, spec.Stamp);

        Assert.False(World.TryGetSourceSpec(_world, -1, out _));
        World.Remove(_world, source);
        Assert.False(World.TryGetSourceSpec(_world, source, out _));
    }

    [Fact]
    public void LayerRecipe_And_StampSpec_ExposeDistilledState()
    {
        Assert.True(Layer.TryGetRecipe(_world, _layer, out var baseRecipe));
        Assert.Equal(Layer.Derivation.Base, baseRecipe.Op);

        Assert.True(Layer.TryGetRecipe(_world, _derived, out var derivedRecipe));
        Assert.Equal(Layer.Derivation.Sum, derivedRecipe.Op);
        Assert.Equal(_layer, derivedRecipe.FirstLayer);
        Assert.Equal(_layer, derivedRecipe.SecondLayer);
        Assert.Equal(1, derivedRecipe.FirstWeight);
        Assert.Equal(2, derivedRecipe.SecondWeight);
        Assert.False(Layer.TryGetRecipe(_world, 31, out _));

        Assert.True(Stamp.TryGetSpec(_box, out var boxSpec));
        Assert.Equal(Stamp.Shape.Box, boxSpec.StampShape);
        Assert.Equal(8, boxSpec.Width);
        Assert.Equal(100, boxSpec.Value);
        Assert.Equal(360, boxSpec.ArcDegrees);

        Assert.True(Stamp.TryGetSpec(_cone, out var coneSpec));
        Assert.Equal(Stamp.Shape.Cone, coneSpec.StampShape);
        Assert.Equal(270, coneSpec.ArcDegrees);
        Assert.Equal(-80, coneSpec.Value);

        for (ushort arc = 1; arc <= 360; arc++)
        {
            var custom = Stamp.Dome(4, 42, arc);
            try
            {
                Assert.True(Stamp.TryGetSpec(custom, out var customSpec));
                Assert.Equal(Stamp.Shape.Dome, customSpec.StampShape);
                Assert.Equal(arc, customSpec.ArcDegrees);
            }
            finally
            {
                Assert.True(Stamp.Free(custom));
            }
        }

        Assert.False(Stamp.TryGetSpec(0, out _));
    }

    private static void FillCoordinates(Random random, Span<float> xs, Span<float> ys, int count)
    {
        for (var i = 0; i < count; i++)
        {
            xs[i] = 8f + (float)(random.NextDouble() * 110);
            ys[i] = 8f + (float)(random.NextDouble() * 110);
        }
    }

    private static void AssertMaskMatchesCompleteness(Span<ulong> whole, int count, bool allComplete)
    {
        if (!allComplete) return;
        for (var i = 0; i < count; i++)
            Assert.True((whole[i >> 6] & (1ul << (i & 63))) != 0);
    }
}
