using Xunit;

namespace Gi.Tests;

public unsafe class StatusTests
{
    [Fact]
    public void MutatorsReportWhyAWriteWasRejected()
    {
        var world = World.New();
        var stamp = Stamp.Box(2, 2, 3);
        try
        {
            Grid.New(world, 6, 0f, 0f, 64f);
            var persistent = Layer.New(world);
            var other = Layer.New(world);
            var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
            var derived = Layer.Sum(world, persistent, 1, other, 1);

            var id = World.Place(world, persistent, 8f, 8f, stamp, 1, out var status);
            Assert.Equal(World.WriteStatus.Applied, status);
            Assert.NotEqual(default, id);

            Assert.Equal(default, World.Place(world, 200, 8f, 8f, stamp, 1, out status));
            Assert.Equal(World.WriteStatus.InvalidLayer, status);
            World.Place(world, derived, 8f, 8f, stamp, 1, out status);
            Assert.Equal(World.WriteStatus.InvalidLayer, status);
            World.Place(world, transient, 8f, 8f, stamp, 1, out status);
            Assert.Equal(World.WriteStatus.WrongResidency, status);
            World.Place(world, persistent, 8f, 8f, 250, 1, out status);
            Assert.Equal(World.WriteStatus.InvalidStamp, status);
            World.Place(world, persistent, float.NaN, 8f, stamp, 1, out status);
            Assert.Equal(World.WriteStatus.NonFinite, status);
            World.Place(255, persistent, 8f, 8f, stamp, 1, out status);
            Assert.Equal(World.WriteStatus.InvalidLayer, status);

            Assert.Equal(World.WriteStatus.Applied, World.Move(world, id, 9f, 9f));
            Assert.Equal(World.WriteStatus.NonFinite, World.Move(world, id, float.PositiveInfinity, 9f));
            Assert.Equal(World.WriteStatus.NonFinite, World.Turn(world, id, float.NaN));
            Assert.Equal(World.WriteStatus.NonFinite, World.Scale(world, id, float.NaN));
            Assert.Equal(World.WriteStatus.Applied, World.Turn(world, id, 1f));
            Assert.Equal(World.WriteStatus.Applied, World.Scale(world, id, 1.5f));
            Assert.Equal(World.WriteStatus.Applied, World.SetGain(world, id, 2));
            Assert.Equal(World.WriteStatus.Applied, World.Fade(world, id, 1, 4));
            Assert.Equal(World.WriteStatus.Applied, World.Expire(world, id, 10));
            Assert.Equal(World.WriteStatus.Applied, World.Remove(world, id));

            Assert.Equal(World.WriteStatus.StaleSource, World.Remove(world, id));
            Assert.Equal(World.WriteStatus.StaleSource, World.Move(world, id, 1f, 1f));
            Assert.Equal(World.WriteStatus.StaleSource, World.SetGain(world, id, 1));
            Assert.Equal(World.WriteStatus.StaleSource, World.Fade(world, id, 1, 1));
            Assert.Equal(World.WriteStatus.StaleSource, World.Expire(world, id, 1));
            Assert.Equal(World.WriteStatus.StaleSource, World.Move(255, id, 1f, 1f));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void ColumnReadsClassifyTheirOutcome()
    {
        var world = World.New();
        var stamp = Stamp.Box(2, 2, 3);
        try
        {
            Grid.New(world, 5, 0f, 0f, 32f);
            var layer = Layer.New(world);
            World.Place(world, layer, 8f, 8f, stamp, 1);
            World.Process(world);

            var inside = new[] { 8f, 9f, 10f };
            var outside = new[] { -50f, 100f, 200f };
            var mixed = new[] { 8f, 100f, 9f };
            var values = new short[3];
            var whole = new ulong[1];
            fixed (float* pIn = inside, pOut = outside, pMixed = mixed)
            fixed (short* pv = values)
            fixed (ulong* pw = whole)
            {
                Assert.Equal(World.ReadStatus.Complete, World.SenseColumn(world, layer, pIn, pIn, 3, pv, pw));
                Assert.Equal(World.ReadStatus.Outside, World.SenseColumn(world, layer, pOut, pOut, 3, pv, pw));
                Assert.Equal(World.ReadStatus.Partial, World.SenseColumn(world, layer, pMixed, pIn, 3, pv, pw));
                Assert.Equal(World.ReadStatus.Partial, World.SenseColumn(world, layer, pMixed, pIn, 3, pv, null));
                Assert.Equal(World.ReadStatus.Complete, World.SenseColumn(world, layer, pIn, pIn, 0, pv, pw));
                Assert.Equal(World.ReadStatus.InvalidLayer, World.SenseColumn(world, 200, pIn, pIn, 3, pv, pw));
                Assert.Equal(World.ReadStatus.InvalidLayer, World.SenseColumn(255, layer, pIn, pIn, 3, pv, pw));
                Assert.Equal(World.ReadStatus.LengthMismatch, World.SenseColumn(world, layer, pIn, null, 3, pv, pw));
                Assert.Equal(World.ReadStatus.LengthMismatch, World.SenseColumn(world, layer, pIn, pIn, 3, null, pw));

                var gx = stackalloc float[3];
                var gy = stackalloc float[3];
                Assert.Equal(World.ReadStatus.Complete,
                    World.SenseGradientColumn(world, layer, pIn, pIn, null, 3, gx, gy, pw));
                Assert.Equal(World.ReadStatus.Partial,
                    World.SenseGradientColumn(world, layer, pMixed, pIn, null, 3, pv, gx, gy, pw));
                Assert.Equal(World.ReadStatus.LengthMismatch,
                    World.SenseGradientColumn(world, layer, pIn, pIn, null, 3, null, gy, pw));
            }
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }
}
