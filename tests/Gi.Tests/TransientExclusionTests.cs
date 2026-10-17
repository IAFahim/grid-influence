using Xunit;

namespace Gi.Tests;

public class TransientExclusionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public void ExcludingASplatEqualsTheFieldWithoutIt(int phaseBits, bool saturate)
    {
        var rng = new Random(11 + phaseBits);
        var stamps = new[] { Stamp.Box(4, 4, 9), Stamp.Tent(9, 9, 25), Stamp.Disk(4, 7) };
        var world = World.New();
        var twin = World.New();
        try
        {
            var precision = new Layer.Precision((byte)phaseBits, 0);
            Grid.New(world, 7, 0f, 0f, 128f);
            Grid.New(twin, 7, 0f, 0f, 128f);
            var layers = new[]
            {
                Layer.New(world, Layer.Residency.Transient, precision),
                Layer.New(twin, Layer.Residency.Transient, precision),
            };

            for (var frame = 0; frame < 3; frame++)
            {
                var count = saturate ? 228 : 40;
                var xs = new float[count];
                var ys = new float[count];
                var stampOf = new byte[count];
                var gains = new sbyte[count];
                for (var i = 0; i < count; i++)
                {
                    xs[i] = saturate ? 60.5f : (float)rng.NextDouble() * 120f + 4f;
                    ys[i] = saturate ? 60.5f : (float)rng.NextDouble() * 120f + 4f;
                    stampOf[i] = stamps[saturate ? 0 : rng.Next(stamps.Length)];
                    gains[i] = (sbyte)(saturate ? 16 : rng.Next(-5, 6));
                }

                for (var i = 0; i < count; i++)
                    World.Splat(world, layers[0], new[] { xs[i] }, new[] { ys[i] }, stampOf[i], gains[i]);
                World.Process(world);

                for (var probe = 0; probe < (saturate ? 4 : 25); probe++)
                {
                    var k = rng.Next(count);
                    for (var i = 0; i < count; i++)
                        if (i != k) World.Splat(twin, layers[1], new[] { xs[i] }, new[] { ys[i] }, stampOf[i], gains[i]);
                    World.Process(twin);

                    Assert.Equal(
                        World.TrySense(twin, layers[1], xs[k], ys[k], out var expected),
                        World.TrySense(world, layers[0], xs[k], ys[k], stampOf[k], gains[k], out var got));
                    Assert.Equal(expected, got);
                    World.TrySenseGradient(twin, layers[1], xs[k], ys[k], out var egx, out var egy);
                    World.TrySenseGradient(world, layers[0], xs[k], ys[k], stampOf[k], gains[k], out var ggx, out var ggy);
                    Assert.Equal(egx, ggx);
                    Assert.Equal(egy, ggy);
                    World.TrySenseArea(twin, layers[1], xs[k], ys[k], 6f, out var earea);
                    World.TrySenseArea(world, layers[0], xs[k], ys[k], 6f, stampOf[k], gains[k], out var garea);
                    Assert.Equal(earea, garea);
                }

                World.Clear(twin);
                World.Process(twin);
            }
        }
        finally
        {
            World.Free(world);
            World.Free(twin);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    [Fact]
    public void PersistentLayersZeroGainAndBadStampsExcludeNothing()
    {
        var world = World.New();
        var stamp = Stamp.Box(3, 3, 5);
        try
        {
            Grid.New(world, 6, 0f, 0f, 64f);
            var persistent = Layer.New(world);
            var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
            World.Place(world, persistent, 20.5f, 20.5f, stamp, 2);
            World.Splat(world, transient, new[] { 20.5f }, new[] { 20.5f }, stamp, 2);
            World.Process(world);
            World.TrySense(world, persistent, 20.5f, 20.5f, out var plain);
            World.TrySense(world, persistent, 20.5f, 20.5f, stamp, 2, out var same);
            Assert.Equal(plain, same);
            World.TrySense(world, transient, 20.5f, 20.5f, out plain);
            World.TrySense(world, transient, 20.5f, 20.5f, stamp, 0, out same);
            Assert.Equal(plain, same);
            World.TrySense(world, transient, 20.5f, 20.5f, 200, 2, out same);
            Assert.Equal(plain, same);
            World.TrySense(world, transient, 20.5f, 20.5f, stamp, 2, out var excluded);
            Assert.Equal(0, excluded);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }
}
