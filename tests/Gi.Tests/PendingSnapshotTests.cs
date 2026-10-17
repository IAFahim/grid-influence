using Xunit;

namespace Gi.Tests;

public unsafe class PendingSnapshotTests
{
    [Fact]
    public void RewindWithoutRecordingCancelsPendingMutations()
    {
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamp = Stamp.Box(4, 4, 60);
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            Assert.Equal(g, rw.AddGrid(6, 0f, 0f, 64f));
            var l = Layer.New(w, Layer.Precision.Exact);
            Assert.Equal(l, rw.AddLayer(Layer.Precision.Exact));

            var id = World.Place(w, l, 20.5f, 20.5f, stamp, 2);
            Assert.Equal(rw.Place(l, 20.5f, 20.5f, stamp, 2), id);
            World.Process(w);
            rw.Process();

            World.Move(w, id, 40.25f, 12.75f);
            rw.Move(id, 40.25f, 12.75f);
            World.SetGain(w, id, -3);
            rw.SetGain(id, -3);
            World.Rewind(w);
            rw.Rewind();
            World.Process(w);
            rw.Process();

            Compare(w, rw, g, l);
            Assert.Equal(rw.QueryAt(g, l, 20.5f, 20.5f), World.QueryAt(w, g, l, 20.5f, 20.5f));
            Assert.NotEqual(0, World.QueryAt(w, g, l, 20.5f, 20.5f));
            Assert.Equal(0, World.QueryAt(w, g, l, 40.25f, 12.75f));
        }
        finally
        {
            World.Free(w);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RewindRestoresMutationsMadeBeforeRecord()
    {
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamp = Stamp.Box(4, 4, 60);
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            Assert.Equal(g, rw.AddGrid(6, 0f, 0f, 64f));
            var l = Layer.New(w, Layer.Precision.Exact);
            Assert.Equal(l, rw.AddLayer(Layer.Precision.Exact));

            var id = World.Place(w, l, 20.5f, 20.5f, stamp, 2);
            Assert.Equal(rw.Place(l, 20.5f, 20.5f, stamp, 2), id);
            World.Process(w);
            rw.Process();

            World.Move(w, id, 40.25f, 12.75f);
            rw.Move(id, 40.25f, 12.75f);
            World.Record(w);
            rw.Record();
            World.Process(w);
            rw.Process();
            World.Move(w, id, 50.5f, 50.5f);
            rw.Move(id, 50.5f, 50.5f);
            World.Process(w);
            rw.Process();
            Compare(w, rw, g, l);

            World.Rewind(w);
            rw.Rewind();
            World.Process(w);
            rw.Process();
            Compare(w, rw, g, l);
            Assert.Equal(rw.QueryAt(g, l, 20.5f, 20.5f), World.QueryAt(w, g, l, 20.5f, 20.5f));
        }
        finally
        {
            World.Free(w);
            Stamp.Free(stamp);
        }
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
