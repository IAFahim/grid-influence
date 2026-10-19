using Xunit;

namespace Gi.Tests;

public unsafe class RecordRewindTests
{
    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    public void RewindAfterRecordRestoresTheCheckpointAndResumesLikeATwin(int seed)
    {
        var a = World.New();
        var b = World.New();
        var stamps = new List<byte>
            { Stamp.Box(3, 3, 30), Stamp.Box(6, 2, -25), Stamp.Tent(6, 6, 40), Stamp.Disk(4, 50) };
        try
        {
            var idsA = new List<World.SourceId>();
            var idsB = new List<World.SourceId>();
            foreach (var world in new[] { a, b })
            {
                Grid.New(world, 6, 0f, 0f, 64f);
                Layer.New(world, Layer.Precision.Exact);
                Layer.New(world, Layer.Precision.Cell);
            }

            var setup = new Random(seed);
            for (var i = 0; i < 400; i++)
            {
                var layer = (byte)setup.Next(2);
                var stamp = stamps[setup.Next(stamps.Count)];
                var x = (float)(setup.NextDouble() * 64.0);
                var y = (float)(setup.NextDouble() * 64.0);
                var gain = (sbyte)setup.Next(1, 7);
                var ia = World.Place(a, layer, x, y, stamp, gain);
                var ib = World.Place(b, layer, x, y, stamp, gain);
                Assert.Equal(ia, ib);
                idsA.Add(ia);
                idsB.Add(ib);
                if (setup.Next(3) == 0)
                {
                    var fadeTo = (sbyte)setup.Next(-6, 7);
                    var fadeTicks = setup.Next(1, 16);
                    var expireTicks = setup.Next(0, 30);
                    World.Fade(a, ia, fadeTo, fadeTicks);
                    World.Fade(b, ib, fadeTo, fadeTicks);
                    World.Expire(a, ia, expireTicks);
                    World.Expire(b, ib, expireTicks);
                }
            }

            for (var frame = 0; frame < 3; frame++)
            {
                foreach (var id in idsA)
                {
                    var x = (float)(setup.NextDouble() * 64.0);
                    var y = (float)(setup.NextDouble() * 64.0);
                    World.Move(a, id, x, y);
                    World.Move(b, id, x, y);
                }

                World.Process(a);
                World.Process(b);
            }

            World.Record(b);
            var mutate = new Random(seed * 7919);
            for (var frame = 0; frame < 9; frame++)
            {
                for (var op = 0; op < 120; op++)
                {
                    var id = idsB[mutate.Next(idsB.Count)];
                    var x = (float)(mutate.NextDouble() * 64.0);
                    var y = (float)(mutate.NextDouble() * 64.0);
                    switch (mutate.Next(8))
                    {
                        case 0: World.Move(b, id, x, y); break;
                        case 1: World.SetGain(b, id, (sbyte)mutate.Next(-6, 7)); break;
                        case 2: World.Remove(b, id); break;
                        case 3: World.Turn(b, id, (float)(mutate.NextDouble() * 6.0)); break;
                        case 4: World.Scale(b, id, (float)(mutate.NextDouble() * 2.0 + 0.5)); break;
                        case 5: World.Fade(b, id, (sbyte)mutate.Next(-6, 7), mutate.Next(0, 10)); break;
                        case 6: World.Expire(b, id, mutate.Next(0, 12)); break;
                        default:
                            var fresh = World.Place(b, (byte)mutate.Next(2), x, y, stamps[mutate.Next(stamps.Count)],
                                4);
                            if (fresh != default) idsB.Add(fresh);
                            break;
                    }
                }

                World.Process(b);
            }

            World.Rewind(b);
            World.Process(b);
            World.Process(a);
            Compare(a, b, idsA);

            for (var frame = 0; frame < 8; frame++)
            {
                World.Process(a);
                World.Process(b);
                Compare(a, b, idsA);
            }
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void Compare(byte a, byte b, List<World.SourceId> ids)
    {
        var fieldA = new short[64 * 64];
        var fieldB = new short[64 * 64];
        for (byte layer = 0; layer < 2; layer++)
        {
            fixed (short* pa = fieldA)
            fixed (short* pb = fieldB)
            {
                World.QueryRegion(a, 0, layer, 0, 0, 64, 64, pa);
                World.QueryRegion(b, 0, layer, 0, 0, 64, 64, pb);
            }

            Assert.Equal(fieldA, fieldB);
        }

        foreach (var id in ids)
        {
            var foundA = World.TryGetSourceSpec(a, id, out var specA);
            var foundB = World.TryGetSourceSpec(b, id, out var specB);
            Assert.Equal(foundA, foundB);
            if (foundA) Assert.Equal(specA, specB);
        }
    }
}
