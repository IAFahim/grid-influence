using Xunit;

namespace Gi.Tests;

public sealed class ShapeTests
{
    [Fact]
    public void RoundStamps_PeakAtValueTimesGainAndStayRound()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        foreach (var stamp in new[] { Stamp.Disk(10, 60), Stamp.Cone(10, 60), Stamp.Dome(10, 60) })
        {
            var id = World.Place(w, l, 64.5f, 64.5f, stamp, 5);
            World.Process(w);
            Assert.Equal(300, World.Query(w, g, l, 64, 64));
            for (var d = 1; d <= 11; d++)
            {
                var axis = World.Query(w, g, l, 64 + d, 64);
                Assert.Equal(axis, World.Query(w, g, l, 64, 64 - d));
                Assert.Equal(axis, World.Query(w, g, l, 64 - d, 64));
                Assert.True(axis <= World.Query(w, g, l, 64 + d - 1, 64));
            }

            Assert.Equal(World.Query(w, g, l, 71, 71), World.Query(w, g, l, 57, 57));
            Assert.Equal(0, World.Query(w, g, l, 64 + 12, 64));
            World.Remove(w, id);
            World.Process(w);
            Assert.True(Stamp.Free(stamp));
        }
    }

    [Fact]
    public void VisionCone_CoversWhereItFacesAndFollowsTurn()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        var id = World.Place(w, l, 64.5f, 64.5f, Stamp.Cone(20, 100, 60), 1);
        World.Process(w);
        Assert.True(World.Query(w, g, l, 74, 64) > 40);
        Assert.Equal(0, World.Query(w, g, l, 54, 64));
        Assert.Equal(0, World.Query(w, g, l, 64, 74));

        World.Turn(w, id, MathF.PI / 2f);
        World.Process(w);
        Assert.True(World.Query(w, g, l, 64, 74) > 40);
        Assert.Equal(0, World.Query(w, g, l, 74, 64));

        World.Turn(w, id, MathF.PI);
        World.Process(w);
        Assert.True(World.Query(w, g, l, 54, 64) > 40);
        Assert.Equal(0, World.Query(w, g, l, 74, 64));
    }

    [Fact]
    public unsafe void Scale_MatchesTheResizedStampExactly()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var scaled = Layer.New(w);
        var resized = Layer.New(w);
        var id = World.Place(w, scaled, 40.3f, 70.6f, Stamp.Tent(10, 6, 80), 4);
        World.Scale(w, id, 2.5f);
        World.Place(w, resized, 40.3f, 70.6f, Stamp.Tent(25, 15, 80), 4);
        var box = World.Place(w, scaled, 90.1f, 30.9f, Stamp.Box(6, 6, 50), 3);
        World.Scale(w, box, 2f);
        World.Place(w, resized, 90.1f, 30.9f, Stamp.Box(12, 12, 50), 3);
        World.Process(w);
        var a = new short[128 * 128];
        var b = new short[128 * 128];
        fixed (short* pa = a) World.QueryRegion(w, g, scaled, 0, 0, 128, 128, pa);
        fixed (short* pb = b) World.QueryRegion(w, g, resized, 0, 0, 128, 128, pb);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Turn_BackToZeroRestoresTheAxisAlignedField()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        var id = World.Place(w, l, 50.3f, 60.7f, Stamp.Bell(18, 10, 90), 6);
        World.Process(w);
        var before = World.Query(w, g, l, 0, 0, 128, 128);
        var peak = World.QueryMax(w, g, l, out var px, out var py);
        var along = World.Query(w, g, l, px + 7, py);
        World.Turn(w, id, 1.1f);
        World.Process(w);
        Assert.True(World.Query(w, g, l, px + 7, py) < along);
        World.Turn(w, id, 0f);
        World.Process(w);
        Assert.Equal(before, World.Query(w, g, l, 0, 0, 128, 128));
        Assert.Equal(peak, World.QueryMax(w, g, l, out var qx, out var qy));
        Assert.Equal((px, py), (qx, qy));
    }

    [Fact]
    public void StampFree_RefusesWhileReferencedAndReusesTheId()
    {
        var w = World.New();
        Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var samples = new sbyte[] { 1, 2, 3, 4, 5, 6 };
        var stamp = Stamp.New(samples, 3, 2);
        var id = World.Place(w, l, 20f, 20f, stamp, 2);
        Assert.False(Stamp.Free(stamp));
        World.Process(w);
        Assert.False(Stamp.Free(stamp));
        World.Remove(w, id);
        Assert.False(Stamp.Free(stamp));
        World.Process(w);
        Assert.True(Stamp.Free(stamp));
        Assert.False(Stamp.Free(stamp));
        Assert.Equal(-1, World.Place(w, l, 20f, 20f, stamp, 2));

        for (var i = 0; i < 600; i++)
        {
            var transient = Stamp.Dome(1 + i % 100, 40);
            var source = World.Place(w, l, 30f, 30f, transient, 1);
            Assert.NotEqual(-1, source);
            World.Remove(w, source);
            World.Process(w);
            Assert.True(Stamp.Free(transient));
        }
    }

    [Fact]
    public void BoxWiderThanTheGrid_CoversEveryCell()
    {
        var w = World.New();
        var g = Grid.New(w, 5, 0f, 0f, 32f);
        var l = Layer.New(w);
        World.Place(w, l, 2f, 30f, Stamp.Box(100, 100, 20), 1);
        World.Process(w);
        Assert.Equal(32 * 32 * 20, World.Query(w, g, l, 0, 0, 32, 32));
    }
}
