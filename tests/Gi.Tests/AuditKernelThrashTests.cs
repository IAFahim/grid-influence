using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Gi.Tests;

public sealed unsafe class AuditKernelThrashTests
{
    private const int Sources = 20000;
    private const int Rotations = 96;
    private const int Frames = 9;
    private const int WarmFrames = 3;

    private readonly ITestOutputHelper _output;

    public AuditKernelThrashTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Build =>
#if GI_NETSTANDARD
        "netstandard2.1";
#else
        "net10.0";
#endif

    private static byte MakeRaster()
    {
        var samples = new sbyte[12 * 12];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(10 + i % 7);
        fixed (sbyte* data = samples)
        {
            return Stamp.New(data, 12, 12);
        }
    }

    private double MeasureRotating(bool rotate, byte stamp, out int kernelEntries, float constantTurn = 0f)
    {
        var world = World.New();
        try
        {
            Grid.New(world, 11, 0f, 0f, 2048f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var rng = new Random(11);
            var ids = new World.SourceId[Sources];
            var xs = new float[Sources];
            var ys = new float[Sources];
            for (var i = 0; i < Sources; i++)
            {
                xs[i] = 40f + (float)(rng.NextDouble() * 1960);
                ys[i] = 40f + (float)(rng.NextDouble() * 1960);
                ids[i] = World.Place(world, layer, xs[i], ys[i], stamp, 2);
            }

            World.Process(world);
            var best = double.MaxValue;
            for (var frame = 0; frame < Frames; frame++)
            {
                for (var i = 0; i < Sources; i++)
                {
                    xs[i] += 1f;
                    if (xs[i] > 2000f) xs[i] -= 1900f;
                    World.Move(world, ids[i], xs[i], ys[i]);
                    if (rotate)
                        World.Turn(world, ids[i],
                            constantTurn >= 0f ? constantTurn : (i % Rotations) * (6.2831853f / Rotations));
                }

                var stopwatch = Stopwatch.StartNew();
                World.Process(world);
                stopwatch.Stop();
                if (frame >= WarmFrames) best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
            }

            World.Memory(world, out _, out _, out _, out kernelEntries, out _);
            return best;
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void RotatingRastersThroughABoundedTurnSetStayCached()
    {
        var stamp = MakeRaster();
        try
        {
            var fixedMs = MeasureRotating(false, stamp, out var fixedEntries);
            var fixedTurnMs = MeasureRotating(true, stamp, out var fixedTurnEntries, 17 * (6.2831853f / Rotations));
            var rotatingMs = MeasureRotating(true, stamp, out var rotatingEntries, -1f);
            double PerSource(double ms) => ms * 1e6 / Sources;
            var ratio = rotatingMs / fixedTurnMs;
            _output.WriteLine(
                $"AUDIT-THRASH build={Build} stamp=raster sources={Sources} turn set={Rotations} " +
                $"fixed={PerSource(fixedMs):F0} ns/source/frame ({fixedEntries} kernels) " +
                $"fixed-turned={PerSource(fixedTurnMs):F0} ns/source/frame ({fixedTurnEntries} kernels) " +
                $"rotating={PerSource(rotatingMs):F0} ns/source/frame ({rotatingEntries} kernels) " +
                $"ratio-vs-fixed-turned={ratio:F2}");
            Assert.Equal(Rotations, rotatingEntries);
            Assert.Equal(2, fixedTurnEntries);
            Assert.True(ratio < 1.6, $"rotating rasters over a {Rotations}-turn working set cost {ratio:F1}x the same stamp at a fixed turn");
            Assert.True(PerSource(fixedMs) < 4000, $"fixed-stamp price regressed to {PerSource(fixedMs):F0} ns/source/frame");
        }
        finally
        {
            Stamp.Free(stamp);
        }
    }
}
