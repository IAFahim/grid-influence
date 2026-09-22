using Xunit;

namespace GridInfluence.Tests;

public sealed class BudgetRetentionTests
{
    private static FieldStamp Disc(int radius, sbyte weight, Int2 origin = default)
        => new(InfluenceShape.Disc(Int2.Zero, radius, weight), origin);

    private static FieldStamp Cell(sbyte weight, Int2 origin = default)
        => new(InfluenceShape.SolidRect(Int2.Zero, new Int2(1, 1), weight), origin);

    [Fact]
    public void SpanBudget_DropsOversizedStampWhole()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        var stats = field.Tick([Disc(600_000, 10)], 1);

        Assert.Equal(1, stats.StampsIn);
        Assert.Equal(1, stats.StampsDroppedSpanBudget);
        Assert.Equal(0, stats.StampsDroppedChunkBudget);
        Assert.Equal(0, field.ActiveSlotCount);
        Assert.Equal(0, field.AsReader().ReadCell(new Int2(0, 0)));
    }

    [Fact]
    public void ChunkBudget_DropsStampThatSpawnsTooManyChunks()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        var stats = field.Tick([Disc(70_000, 10)], 1);

        Assert.Equal(0, stats.StampsDroppedSpanBudget);
        Assert.Equal(1, stats.StampsDroppedChunkBudget);
        Assert.Equal(0, field.ActiveSlotCount);
    }

    [Fact]
    public void BudgetDrops_AreInsertionOrderIndependent()
    {
        var big = Disc(600_000, 10);
        var small = Disc(3, 10);

        using var fieldA = new Field(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        using var fieldB = new Field(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        fieldA.Tick([small, big], 1);
        fieldB.Tick([big, small], 1);

        Assert.Equal(fieldA.ActiveSlotCount, fieldB.ActiveSlotCount);
        for (var y = -5; y <= 5; y++)
        for (var x = -5; x <= 5; x++)
            Assert.Equal(fieldA.AsReader().ReadCell(new Int2(x, y)), fieldB.AsReader().ReadCell(new Int2(x, y)));
    }

    [Fact]
    public void Retention_EvictsAfterConfiguredTicks()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(2, 4));
        field.Tick([Cell(5)], 2);
        Assert.Equal(1, field.SlotCount);
        Assert.Equal(0, field.FreeSlotCount);

        field.Tick([], 4);
        Assert.Equal(0, field.FreeSlotCount);

        field.Tick([], 8);
        Assert.Equal(1, field.FreeSlotCount);
        Assert.Equal(0, field.AsReader().ReadCell(new Int2(0, 0)));
    }

    [Fact]
    public void Retention_MaxValue_KeepsChunksResidentForever()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(2, uint.MaxValue));
        field.Tick([Cell(5)], 1);

        for (var tick = 2u; tick < 40u; tick++) field.Tick([], tick);

        Assert.Equal(1, field.SlotCount);
        Assert.Equal(0, field.FreeSlotCount);
    }

    [Fact]
    public void Compaction_RelocatesLiveChunksDownAndPreservesMappings()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(2, 4));
        field.Tick([Cell(9, new Int2(0, 0)), Cell(9, new Int2(24, 0))], 1);
        Assert.Equal(2, field.SlotCount);

        for (var tick = 2u; tick <= 59u; tick++) field.Tick([Cell(9, new Int2(24, 0))], tick);

        field.Tick([Cell(9, new Int2(24, 0))], 60);

        Assert.Equal(1, field.SlotCount);
        Assert.Equal(0, field.AsReader().ReadCell(new Int2(0, 0)));
        Assert.Equal(9, field.AsReader().ReadCell(new Int2(24, 0)));
    }

    [Fact]
    public void Compaction_TruncatesWhenEverythingIsFree()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(2, 4));
        field.Tick([Cell(9, new Int2(0, 0))], 59);
        field.Tick([Cell(9, new Int2(0, 0)), Cell(9, new Int2(24, 0))], 60);
        Assert.Equal(2, field.SlotCount);

        for (var tick = 61u; tick <= 120u; tick++) field.Tick([], tick);

        Assert.Equal(0, field.SlotCount);
        Assert.Equal(0, field.AsReader().ReadCell(new Int2(24, 0)));
    }
}
