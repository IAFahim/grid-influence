using Xunit;

namespace GridInfluence.Tests;

public sealed class FieldAlgebraTests
{
    private static Stamp Rect(int x, int y, int w, int h, sbyte weight)
        => new(InfluenceShape.SolidRect(new Int2(x, y), new Int2(w, h), weight), Int2.Zero);

    [Fact]
    public void Stamp_IsVisibleAfterTick()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        var stats = field.Tick([Rect(0, 0, 1, 1, 10)], 1);

        Assert.Equal(10, field.AsReader().ReadCell(new Int2(0, 0)));
        Assert.Equal(0, field.AsReader().ReadCell(new Int2(1, 0)));
        Assert.Equal(1, stats.ActiveSlots);
        Assert.Equal(1, stats.StampsIn);
    }

    [Fact]
    public void Reader_SeesPreviousTickOnly()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick([Rect(0, 0, 2, 2, 5)], 1);
        field.Tick([], 2);

        Assert.Equal(0, field.AsReader().ReadCell(new Int2(0, 0)));
    }

    [Fact]
    public void IdleField_KeepsLastResolvedValues()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick([Rect(0, 0, 2, 2, 5)], 1);

        Assert.Equal(5, field.AsReader().ReadCell(new Int2(0, 0)));
    }

    [Fact]
    public void OverlappingStamps_AddWeights()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        field.Tick([Rect(0, 0, 3, 3, 4), Rect(2, 2, 3, 3, 6)], 1);

        var reader = field.AsReader();
        Assert.Equal(4, reader.ReadCell(new Int2(0, 0)));
        Assert.Equal(10, reader.ReadCell(new Int2(2, 2)));
        Assert.Equal(6, reader.ReadCell(new Int2(4, 4)));
    }

    [Fact]
    public void NegativeStamps_CancelPositive()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        field.Tick([Rect(0, 0, 2, 2, 7), Rect(1, 1, 2, 2, -7)], 1);

        var reader = field.AsReader();
        Assert.Equal(7, reader.ReadCell(new Int2(0, 0)));
        Assert.Equal(0, reader.ReadCell(new Int2(1, 1)));
        Assert.Equal(-7, reader.ReadCell(new Int2(2, 2)));
    }

    [Fact]
    public void StampOrder_DoesNotAffectResolvedCells()
    {
        var stampsA = new[]
        {
            Rect(0, 0, 5, 5, 3),
            new Stamp(InfluenceShape.Disc(new Int2(8, 8), 6, -2), Int2.Zero),
            Rect(4, 4, 6, 6, 5),
            new Stamp(InfluenceShape.Capsule(new Int2(1, 1), new Int2(9, 3), 3, 4), Int2.Zero)
        };
        Array.Reverse(stampsA);

        using var fieldA = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        using var fieldB = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        fieldA.Tick(stampsA, 1);
        fieldB.Tick(stampsA.Reverse().ToArray(), 1);

        for (var y = -8; y < 20; y++)
        for (var x = -8; x < 20; x++)
            Assert.Equal(fieldA.AsReader().ReadCell(new Int2(x, y)), fieldB.AsReader().ReadCell(new Int2(x, y)));
    }

    [Fact]
    public void ZeroScaledWeight_DropsStamp()
    {
        var shape = InfluenceShape.Disc(Int2.Zero, 4, 1);
        Assert.False(shape.TryScaleWeight(0.4f, out _));

        Assert.True(shape.TryScaleWeight(0.5f, out var scaled));
        Assert.Equal(1, scaled.Weight); 
    }

    [Fact]
    public void CellsOutsideAnyChunk_ReadZero()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        field.Tick([Rect(0, 0, 4, 4, 2)], 1);

        var reader = field.AsReader();
        Assert.Equal(0, reader.ReadCell(new Int2(100, 100)));
        Assert.Equal(0, reader.ReadCell(new Int2(-100, -5)));
    }

    [Fact]
    public void ResetTick_EvictsAllChunks()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick([Rect(0, 0, 4, 4, 2)], 10);
        Assert.Equal(1, field.ActiveSlotCount);

        field.Tick([], 3);
        Assert.Equal(0, field.ActiveSlotCount);
        Assert.Equal(3u, field.FrameId);
    }

    [Fact]
    public void TryGetChunk_MissingChunkReturnsFalse()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, 60));
        field.Tick([Rect(0, 0, 2, 2, 1)], 1);

        Assert.False(field.AsReader().TryGetChunk(new Int2(5, 5), out _));
        Assert.True(field.AsReader().TryGetChunk(new Int2(0, 0), out var view));
        Assert.Equal(1, view.ReadWorld(new Int2(0, 0)));
        Assert.Equal(0, view.ReadLocal(new Int2(8, 8)));
    }
}
