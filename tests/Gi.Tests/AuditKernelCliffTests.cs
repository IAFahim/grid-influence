using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Gi.Tests;

public sealed unsafe class AuditKernelCliffTests
{
    private const int Sources = 20000;
    private readonly ITestOutputHelper _output;

    public AuditKernelCliffTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Build =>
#if GI_NETSTANDARD
        "netstandard2.1";
#else
        "net10.0";
#endif

    private enum Motion
    {
        WholeCells,
        QuantizedPhase16,
        RandomPhase,
        RandomPhaseAndTurn,
        TurnOnly,
        QuantizedTurn16
    }

    private double Measure(string stampKind, Layer.Precision precision, Motion motion, out int kernelEntries)
    {
        var world = World.New();
        var stamp = stampKind switch
        {
            "disk" => Stamp.Disk(6, 40),
            "cone" => Stamp.Cone(6, 40),
            "tent" => Stamp.Tent(12, 12, 40),
            "raster" => MakeRaster(),
            _ => Stamp.Box(12, 12, 40)
        };
        try
        {
            Grid.New(world, 11, 0f, 0f, 2048f);
            var layer = Layer.New(world, precision);
            var rng = new Random(11);
            var ids = new World.SourceId[Sources];
            var xs = new float[Sources];
            var ys = new float[Sources];
            var angles = new float[Sources];
            for (var i = 0; i < Sources; i++)
            {
                xs[i] = 40f + (float)(rng.NextDouble() * 1960);
                ys[i] = 40f + (float)(rng.NextDouble() * 1960);
                ids[i] = World.Place(world, layer, xs[i], ys[i], stamp, 2);
            }

            World.Process(world);
            var best = double.MaxValue;
            for (var frame = 0; frame < 9; frame++)
            {
                for (var i = 0; i < Sources; i++)
                {
                    switch (motion)
                    {
                        case Motion.WholeCells:
                            xs[i] += 1f;
                            if (xs[i] > 2000f) xs[i] -= 1900f;
                            break;
                        case Motion.QuantizedPhase16:
                            xs[i] = MathF.Floor(xs[i]) + rng.Next(4) * 0.25f + (rng.Next(2) == 0 ? 1f : 2f);
                            ys[i] = MathF.Floor(ys[i]) + rng.Next(4) * 0.25f;
                            if (xs[i] > 2000f) xs[i] -= 1900f;
                            break;
                        case Motion.RandomPhase:
                        case Motion.RandomPhaseAndTurn:
                            xs[i] += 1f + (float)rng.NextDouble();
                            ys[i] += (float)rng.NextDouble();
                            if (xs[i] > 2000f) xs[i] -= 1900f;
                            if (ys[i] > 2000f) ys[i] -= 1900f;
                            break;
                    }

                    if (motion == Motion.RandomPhaseAndTurn || motion == Motion.TurnOnly)
                        angles[i] = (float)(rng.NextDouble() * 6.283);
                    if (motion == Motion.QuantizedTurn16) angles[i] = rng.Next(16) * (6.2831853f / 16f);
                }

                for (var i = 0; i < Sources; i++)
                {
                    if (motion is Motion.RandomPhaseAndTurn or Motion.TurnOnly or Motion.QuantizedTurn16)
                        World.Turn(world, ids[i], angles[i]);
                    if (motion != Motion.TurnOnly && motion != Motion.QuantizedTurn16)
                        World.Move(world, ids[i], xs[i], ys[i]);
                }

                var stopwatch = Stopwatch.StartNew();
                World.Process(world);
                stopwatch.Stop();
                if (frame >= 3) best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
            }

            World.Memory(world, out _, out _, out _, out kernelEntries, out _);
            return best;
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    private static byte MakeRaster()
    {
        var samples = new sbyte[12 * 12];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(10 + i % 7);
        fixed (sbyte* data = samples)
        {
            return Stamp.New(data, 12, 12);
        }
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("cone")]
    [InlineData("tent")]
    [InlineData("raster")]
    public void ProcessCostPerSourceAcrossKernelKeyVariety(string kind)
    {
        Measure(kind, Layer.Precision.Cell, Motion.WholeCells, out _);
        var hit = Measure(kind, Layer.Precision.Cell, Motion.WholeCells, out var hitEntries);
        var phase16 = Measure(kind, Layer.Precision.Exact, Motion.QuantizedPhase16, out var phaseEntries);
        var random = Measure(kind, Layer.Precision.Exact, Motion.RandomPhase, out var randomEntries);
        var turned = Measure(kind, Layer.Precision.Exact, Motion.RandomPhaseAndTurn, out var turnedEntries);
        var turnOnly = Measure(kind, Layer.Precision.Cell, Motion.TurnOnly, out var turnOnlyEntries);
        var turn16 = Measure(kind, Layer.Precision.Cell, Motion.QuantizedTurn16, out var turn16Entries);
        double PerSource(double ms) => ms * 1e6 / Sources;
        _output.WriteLine(
            $"AUDIT-KERNEL build={Build} stamp={kind} sources={Sources} ns/source/frame (every source moves each frame, 2 emissions each): " +
            $"cell+wholeCells={PerSource(hit):F0} (kernels cached {hitEntries}) | exact+16 phases={PerSource(phase16):F0} ({phaseEntries}) | " +
            $"exact+random phase={PerSource(random):F0} ({randomEntries}) | exact+random phase+random turn={PerSource(turned):F0} ({turnedEntries}) | " +
            $"cell+random turn only={PerSource(turnOnly):F0} ({turnOnlyEntries}) | cell+16 turns={PerSource(turn16):F0} ({turn16Entries})");
    }
}
