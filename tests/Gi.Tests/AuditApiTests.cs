using Xunit;
using Xunit.Abstractions;

namespace Gi.Tests;

public sealed unsafe class AuditApiTests
{
    private readonly ITestOutputHelper _output;

    public AuditApiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Audit", "Defect")]
    public void BatchPlaceMustHonourTheFootprintCapForItsScale()
    {
        var world = World.New();
        var box = Stamp.Box(100, 100, 20);
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var scalar = World.Place(world, layer, 128f, 128f, box, 1);
            Assert.NotEqual(default, scalar);
            Assert.Equal(World.WriteStatus.CapacityReached, World.Scale(world, scalar, 10f));

            var commands = new[] { World.SourceCommand.Place(layer, 128f, 128f, box, 1, 0f, 10f) };
            var results = new World.SourceResult[1];
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
            World.TryGetSourceSpec(world, results[0].SourceId, out var spec);
            _output.WriteLine(
                $"AUDIT-BATCHSCALE batch Place status={results[0].Status} stored scale={spec.Scale} (100-cell stamp x scale 10 = 1000 cells > cap 512)");
            Assert.NotEqual(World.SourceResultStatus.Applied, results[0].Status);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Defect")]
    public void SyncTransformsMustRejectAMaskThatOverlapsTheSecondHalfOfTheIdColumn()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 20);
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            const int rows = 16;
            var block = (byte*)NativeMemoryAlloc(rows * sizeof(World.SourceId) + 64);
            var sources = (World.SourceId*)block;
            var xs = stackalloc float[rows];
            var ys = stackalloc float[rows];
            for (var i = 0; i < rows; i++)
            {
                sources[i] = World.Place(world, layer, 10f + i, 10f, box, 1);
                xs[i] = 20f + i;
                ys[i] = 20f;
            }

            World.Process(world);
            var token = World.Capture(world);
            var mask = (ulong*)(block + 104);
            var ranges = new[]
            {
                new World.TransformRange { Sources = sources, Xs = xs, Ys = ys, Invalid = mask, InvalidWords = 1, Count = rows }
            };
            var status = World.SyncTransforms(token, ranges);
            _output.WriteLine(
                $"AUDIT-OVERLAP mask placed at byte 104 of a {rows * sizeof(World.SourceId)}-byte id column: status={status}");
            Assert.Equal(World.BatchStatus.OverlappingBuffers, status);
            NativeMemoryFree(block);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Defect")]
    public void AGridAddedAfterSourcesExistMustReceiveThem()
    {
        var world = World.New();
        var box = Stamp.Box(8, 8, 20);
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            World.Place(world, layer, 100f, 100f, box, 1);
            World.Process(world);
            var late = Grid.New(world, 8, 0f, 0f, 256f);
            World.Process(world);
            var first = World.Query(world, 0, layer, 0, 0, 256, 256);
            var second = World.Query(world, late, layer, 0, 0, 256, 256);
            _output.WriteLine($"AUDIT-LATEGRID grid0 sum={first} late grid sum={second}");
            Assert.Equal(first, second);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void FieldViewPagePointerGoesStaleOnSplatBecauseDirectEmissionGrowsThePageSlab()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 20);
        try
        {
            Grid.New(world, 9, 0f, 0f, 512f);
            var layer = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Exact);
            var xs = new float[16];
            var ys = new float[16];
            for (var i = 0; i < 16; i++)
            {
                xs[i] = i * 32 + 16.5f;
                ys[i] = 16.5f;
            }

            World.Splat(world, layer, xs, ys, box, 3);
            World.Process(world);
            var w = World.GetContext(world);
            var before = w->Grids[0].Layers[layer].PageOf(0);
            Assert.True(before != null);
            var view = World.View(world, 0, layer);
            Assert.NotEqual((short)0, view[16, 16]);

            World.Splat(world, layer, new[] { 16.5f }, new[] { 400.5f }, box, 3);
            var after = w->Grids[0].Layers[layer].PageOf(0);
            _output.WriteLine(
                $"AUDIT-VIEWSTALE page of tile 0 before Splat={(long)before:x} after Splat (no Process)={(long)after:x} moved={before != after}");
            Assert.True(before != after, "Splat grew the page slab and moved published pages; a cached FieldView page is now dangling");
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void StampIdsAreReusedSoAStaleStampHandleSilentlyMeansADifferentStamp()
    {
        var first = Stamp.Box(4, 4, 10);
        Assert.True(Stamp.Free(first));
        var second = Stamp.Disk(6, 50);
        try
        {
            Assert.Equal(first, second);
            Stamp.TryGetSpec(first, out var spec);
            _output.WriteLine($"AUDIT-STAMPID id {first} was a Box, is now {spec.StampShape} after reuse");
            Assert.Equal(Stamp.Shape.Disk, spec.StampShape);
        }
        finally
        {
            Stamp.Free(second);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void SplatReturnsTheFiniteRowCountNotTheDepositedCountAndZeroForEveryRejection()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 10);
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
            var persistent = Layer.New(world);
            var inside = new[] { 10f, 20f, 30f };
            var outside = new[] { 5000f, -5000f, 1e20f };
            var nan = new[] { float.NaN, 1f };
            Assert.Equal(3, World.Splat(world, transient, inside, inside, box, 2));
            Assert.Equal(3, World.Splat(world, transient, outside, outside, box, 2));
            Assert.Equal(3, World.Splat(world, transient, inside, inside, box, 0));
            Assert.Equal(1, World.Splat(world, transient, nan, nan, box, 2));
            Assert.Equal(0, World.Splat(world, persistent, inside, inside, box, 2));
            Assert.Equal(0, World.Splat(world, 99, inside, inside, box, 2));
            Assert.Equal(0, World.Splat(world, transient, inside, inside, 77, 2));
            _output.WriteLine("AUDIT-SPLAT in-grid, fully off-grid and gain-0 batches all return 3; invalid layer/stamp/persistent return 0");
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void GainIsSilentlyClampedAndReportedAsApplied()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 10);
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var id = World.Place(world, layer, 50f, 50f, box, 127);
            Assert.Equal(World.WriteStatus.Applied, World.SetGain(world, id, -128));
            World.TryGetSourceSpec(world, id, out var spec);
            _output.WriteLine($"AUDIT-GAIN SetGain(-128) reported Applied, stored gain={spec.Gain}");
            Assert.Equal(-16, spec.Gain);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void DefaultPrecisionIsCellWithNoRotationNotExact()
    {
        var world = World.New();
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, default(Layer.Precision));
            Layer.TryGetPrecision(world, layer, out var precision);
            _output.WriteLine($"AUDIT-PRECISION default(Precision) -> PhaseBits={precision.PhaseBits} TurnBits={precision.TurnBits}");
            Assert.Equal(0, precision.PhaseBits);
            Assert.Equal(0, precision.TurnBits);
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    [Trait("Audit", "Defect")]
    public void RewindMustRestoreSubAnchorPositionsOfTheSourceColumns()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 10);
        try
        {
            Grid.New(world, 7, 0f, 0f, 128f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var id = World.Place(world, layer, 50.25f, 50.25f, box, 2);
            World.Process(world);
            World.Record(world);
            World.Move(world, id, 50.5f, 50.5f);
            World.Process(world);
            World.Rewind(world);
            World.Process(world);
            World.TryGetSourceSpec(world, id, out var spec);
            _output.WriteLine($"AUDIT-REWIND position after Rewind = ({spec.X}, {spec.Y}); checkpoint was (50.25, 50.25)");
            Assert.Equal(50.25f, spec.X);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void StampFreeFlushesTheKernelCacheOfEveryWorldEvenForAnUnrelatedStamp()
    {
        var world = World.New();
        var disk = Stamp.Disk(5, 40);
        var unrelated = Stamp.Box(2, 2, 1);
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            for (var i = 0; i < 10; i++) World.Place(world, layer, 20f + i * 10, 100f, disk, 2);
            World.Process(world);
            World.Memory(world, out _, out _, out _, out var kernelsBefore, out _);
            Assert.True(Stamp.Free(unrelated));
            World.Memory(world, out _, out _, out _, out var kernelsAfter, out _);
            _output.WriteLine($"AUDIT-FLUSH kernel entries before Stamp.Free(unrelated)={kernelsBefore} after={kernelsAfter}");
            Assert.True(kernelsBefore > 0);
            Assert.Equal(0, kernelsAfter);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(disk);
        }
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void WorldHandleIsReusedSoAStaleByteHandleReadsTheNewWorld()
    {
        var first = World.New();
        var box = Stamp.Box(4, 4, 10);
        try
        {
            Grid.New(first, 7, 0f, 0f, 128f);
            var layer = Layer.New(first, Layer.Precision.Cell);
            World.Free(first);
            var second = World.New();
            Grid.New(second, 7, 0f, 0f, 128f);
            var other = Layer.New(second, Layer.Precision.Cell);
            World.Place(second, other, 50f, 50f, box, 3);
            World.Process(second);
            _output.WriteLine($"AUDIT-HANDLE first={first} second={second}; stale read through the old byte handle = {World.Query(first, 0, layer, 50, 50)}");
            Assert.Equal(first, second);
            Assert.NotEqual((short)0, World.Query(first, 0, layer, 50, 50));
            World.Free(second);
        }
        finally
        {
            Stamp.Free(box);
        }
    }

    private static void* NativeMemoryAlloc(int bytes)
    {
        return System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)bytes);
    }

    private static void NativeMemoryFree(void* pointer)
    {
        System.Runtime.InteropServices.NativeMemory.Free(pointer);
    }

    [Fact]
    [Trait("Audit", "Hazard")]
    public void CapBypassCostOneBatchPlacedMaxScaleRasterOnA4096Grid()
    {
        var world = World.New();
        var samples = new sbyte[16 * 16];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(1 + i % 5);
        byte raster;
        fixed (sbyte* data = samples)
        {
            raster = Stamp.New(data, 16, 16);
        }

        try
        {
            Grid.New(world, 12, 0f, 0f, 4096f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var commands = new[] { World.SourceCommand.Place(layer, 2048f, 2048f, raster, 1, 0f, 255f) };
            var results = new World.SourceResult[1];
            World.ApplyBatch(world, commands, results);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            World.Process(world);
            stopwatch.Stop();
            World.Memory(world, out var bytes, out var tiles, out _, out _, out _);
            _output.WriteLine(
                $"AUDIT-CAPBYPASS status={results[0].Status} one 16x16 raster at scale 255: Process={stopwatch.Elapsed.TotalMilliseconds:F0} ms, live tiles={tiles}, store={bytes / 1048576.0:F1} MB");
        }
        finally
        {
            World.Free(world);
            Stamp.Free(raster);
        }
    }
}
