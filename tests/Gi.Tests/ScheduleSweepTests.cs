using Xunit;

namespace Gi.Tests;

public unsafe class ScheduleSweepTests
{
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void ThousandsOfTimedSourcesMatchTheReferenceThroughChurnAndRewind(int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30), Stamp.Box(5, 2, -20), Stamp.Tent(6, 6, 40) };
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            Assert.Equal(g, rw.AddGrid(6, 0f, 0f, 64f));
            var l = Layer.New(w, Layer.Precision.Cell);
            Assert.Equal(l, rw.AddLayer(Layer.Precision.Cell));

            var ids = new List<World.SourceId>();
            for (var i = 0; i < 3000; i++)
            {
                var stamp = stamps[rng.Next(stamps.Count)];
                var x = (float)(rng.NextDouble() * 64.0);
                var y = (float)(rng.NextDouble() * 64.0);
                var gain = (sbyte)rng.Next(1, 9);
                var id = World.Place(w, l, x, y, stamp, gain);
                Assert.Equal(rw.Place(l, x, y, stamp, gain), id);
                ids.Add(id);
                Schedule(w, rw, id, rng);
            }

            var recording = false;
            for (var frame = 0; frame < 48; frame++)
            {
                if (frame == 16)
                {
                    World.Record(w);
                    rw.Record();
                    recording = true;
                }

                if (frame == 30 && recording)
                {
                    World.Rewind(w);
                    rw.Rewind();
                    recording = false;
                }

                for (var op = 0; op < 150; op++)
                {
                    var index = rng.Next(ids.Count);
                    var id = ids[index];
                    switch (rng.Next(6))
                    {
                        case 0:
                            Schedule(w, rw, id, rng);
                            break;
                        case 1:
                            var gain = (sbyte)rng.Next(-8, 9);
                            World.SetGain(w, id, gain);
                            rw.SetGain(id, gain);
                            break;
                        case 2:
                            World.Remove(w, id);
                            rw.Remove(id);
                            break;
                        case 3:
                            var x = (float)(rng.NextDouble() * 64.0);
                            var y = (float)(rng.NextDouble() * 64.0);
                            var fresh = World.Place(w, l, x, y, stamps[0], 3);
                            Assert.Equal(rw.Place(l, x, y, stamps[0], 3), fresh);
                            if (fresh != default)
                            {
                                ids.Add(fresh);
                                Schedule(w, rw, fresh, rng);
                            }

                            break;
                        default:
                            var mx = (float)(rng.NextDouble() * 64.0);
                            var my = (float)(rng.NextDouble() * 64.0);
                            World.Move(w, id, mx, my);
                            rw.Move(id, mx, my);
                            break;
                    }
                }

                World.Process(w);
                rw.Process();
                Compare(w, rw, g, l);
            }
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void Schedule(byte w, ReferenceWorld rw, World.SourceId id, Random rng)
    {
        var gain = (sbyte)rng.Next(-8, 9);
        var ticks = rng.Next(0, 12);
        var expire = rng.Next(0, 20);
        World.Fade(w, id, gain, ticks);
        rw.Fade(id, gain, ticks);
        World.Expire(w, id, expire);
        rw.Expire(id, expire);
    }

    private static void Compare(byte w, ReferenceWorld rw, byte grid, byte layer)
    {
        var actual = new short[64 * 64];
        var expected = new short[64 * 64];
        fixed (short* pa = actual)
        fixed (short* pe = expected)
        {
            World.QueryRegion(w, grid, layer, 0, 0, 64, 64, pa);
            rw.QueryRegion(grid, layer, 0, 0, 64, 64, pe);
        }

        Assert.Equal(expected, actual);
    }
}
