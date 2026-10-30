using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Gi.Tests;

public sealed unsafe class AuditChangedMaskTests
{
    private const int Agents = 20000;
    private readonly ITestOutputHelper _output;

    public AuditChangedMaskTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Build =>
#if GI_NETSTANDARD
        "netstandard2.1";
#else
        "net10.0";
#endif

    [Theory]
    [InlineData(8f)]
    [InlineData(64f)]
    [InlineData(256f)]
    public void ChangedMaskCostTracksThePyramidChangedCostAtEveryReach(float reach)
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 30);
        try
        {
            Grid.New(world, 12, 0f, 0f, 4096f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            var rng = new Random(9);
            var xs = new float[Agents];
            var ys = new float[Agents];
            for (var i = 0; i < 300; i++) World.Place(world, layer, 10f + i * 13, 4000f, box, 2);
            World.Process(world);
            for (var i = 0; i < Agents; i++)
            {
                xs[i] = 300f + (float)(rng.NextDouble() * 3000);
                ys[i] = 300f + (float)(rng.NextDouble() * 3000);
            }

            var expected = new ulong[(Agents + 63) / 64];
            fixed (float* xp = xs)
            fixed (float* yp = ys)
            {
                for (var i = 0; i < Agents; i++)
                    if (World.Changed(world, layer, xp[i], yp[i], reach, 1))
                        expected[i >> 6] |= 1ul << (i & 63);
            }

            var words = new ulong[(Agents + 63) / 64];
            var mask = double.MaxValue;
            var perAgent = double.MaxValue;
            fixed (float* xp = xs)
            fixed (float* yp = ys)
            fixed (ulong* wp = words)
            fixed (ulong* ep = expected)
            {
                for (var run = 0; run < 4; run++)
                {
                    Array.Clear(words);
                    var stopwatch = Stopwatch.StartNew();
                    var hits = World.ChangedMask(world, layer, xp, yp, reach, 1, Agents, wp);
                    stopwatch.Stop();
                    mask = Math.Min(mask, stopwatch.Elapsed.TotalMilliseconds);

                    stopwatch.Restart();
                    var reference = 0;
                    for (var i = 0; i < Agents; i++)
                        if (World.Changed(world, layer, xp[i], yp[i], reach, 1))
                            reference++;
                    stopwatch.Stop();
                    perAgent = Math.Min(perAgent, stopwatch.Elapsed.TotalMilliseconds);

                    if (run != 0) continue;

                    Assert.Equal(reference, hits);
                    Assert.Equal(PopCount(ep), PopCount(wp));
                    for (var w = 0; w < words.Length; w++) Assert.Equal(ep[w], wp[w]);
                }
            }

            var maskPerAgent = mask * 1e6 / Agents;
            var singlePerAgent = perAgent * 1e6 / Agents;
            var ratio = maskPerAgent / singlePerAgent;
            _output.WriteLine(
                $"AUDIT-CHANGEDMASK build={Build} reach={reach} agents={Agents} (nothing changed near them): " +
                $"ChangedMask column={maskPerAgent:F0} ns/agent | World.Changed per agent={singlePerAgent:F0} ns/agent | ratio={ratio:F2}");
            Assert.True(ratio < 1.35,
                $"ChangedMask at reach {reach} costs {maskPerAgent:F0} ns/agent vs World.Changed {singlePerAgent:F0}");
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
        }
    }

    private static int PopCount(ulong* words)
    {
        var count = 0;
        for (var i = 0; i < (Agents + 63) / 64; i++)
        {
            var word = words[i];
            while (word != 0)
            {
                count += (int)(word & 1ul);
                word >>= 1;
            }
        }

        return count;
    }
}
