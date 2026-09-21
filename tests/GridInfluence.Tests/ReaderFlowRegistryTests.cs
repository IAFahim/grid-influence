using Xunit;

using GridInfluence.Io;

namespace GridInfluence.Tests;

public sealed class ReaderFlowRegistryTests
{
    [Fact]
    public void SampleBilinear_InterpolatesCellCenters()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick([new Stamp(InfluenceShape.SolidRect(Int2.Zero, new Int2(1, 1), 100), Int2.Zero)], 1);

        var reader = field.AsReader();
        Assert.Equal(100, reader.SampleBilinear(0.5f, 0.5f));
        Assert.Equal(0, reader.SampleBilinear(1.5f, 1.5f));
        Assert.Equal(0, reader.SampleBilinear(0.5f, -0.5f));
        Assert.Equal(50, reader.SampleBilinear(0.5f, 1.0f));
    }

    [Fact]
    public void Gradient_IsUnnormalizedCentralDifference()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick(
        [
            new Stamp(InfluenceShape.SolidRect(new Int2(0, 0), new Int2(1, 1), 100), Int2.Zero),
            new Stamp(InfluenceShape.SolidRect(new Int2(2, 0), new Int2(1, 1), 127), Int2.Zero)
        ], 1);

        var gradient = field.AsReader().Gradient(new Int2(1, 0));
        Assert.Equal(27, gradient.X);
        Assert.Equal(0, gradient.Y);
    }

    [Fact]
    public void FlowField_ResolvesDescentDirections()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick([new Stamp(InfluenceShape.SolidRect(new Int2(4, 0), new Int2(2, 2), 50), Int2.Zero)], 1);

        using var flow = new FlowField();
        flow.Resolve(field);
        var reader = flow.AsReader(field);

        var left = reader.ReadDirection(new Int2(3, 1));
        Assert.Equal(50, left.X);
        Assert.Equal(0, left.Y);

        var onEdge = reader.ReadDirection(new Int2(5, 1));
        Assert.Equal(-50, onEdge.X);
    }

    [Fact]
    public void FlowReader_MissingChunksReturnZero()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick([new Stamp(InfluenceShape.SolidRect(Int2.Zero, new Int2(1, 1), 10), Int2.Zero)], 1);

        using var flow = new FlowField();
        flow.Resolve(field);

        Assert.Equal(Int2.Zero, flow.AsReader(field).ReadDirection(new Int2(50, 50)));
    }

    [Fact]
    public void FeatureHelpers_BehaveAsDocumented()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.Tick(
        [
            new Stamp(InfluenceShape.SolidRect(Int2.Zero, new Int2(2, 2), 40), Int2.Zero),
            new Stamp(InfluenceShape.SolidRect(new Int2(4, 0), new Int2(2, 2), -40), Int2.Zero)
        ], 1);

        var reader = field.AsReader();
        Assert.Equal(1, Territory.Controller(reader, new Int2(0, 0)));
        Assert.Equal(-1, Territory.Controller(reader, new Int2(4, 0)));
        Assert.Equal(0, Territory.Controller(reader, new Int2(3, 0)));
        Assert.True(Territory.IsFrontline(reader, new Int2(3, 0), 0));
        Assert.True(Vision.IsSeen(reader, new Int2(1, 1)));
        Assert.True(Vision.InShadow(reader, new Int2(3, 3)));
        Assert.Equal(160, Capture.Score(reader, Int2.Zero, new Int2(2, 2)));
        Assert.True(Placement.IsValid(reader, new Int2(6, 0), new Int2(2, 2), 0));
        Assert.False(Placement.IsValid(reader, Int2.Zero, new Int2(2, 2), 0));
    }

    [Fact]
    public void Registry_DoubleBufferedSwap_ExposesWrittenBufferAsFront()
    {
        using var pair = new FieldPair(new FieldConfig(5, 3, uint.MaxValue, doubleBuffered: true));
        pair.Step([new Stamp(InfluenceShape.SolidRect(Int2.Zero, new Int2(1, 1), 5), Int2.Zero)]);

        Assert.Equal(5, pair.Front.AsReader().ReadCell(new Int2(0, 0)));
        Assert.Equal(1u, pair.Tick);
    }

    [Fact]
    public void Registry_IdlePair_SkipsScheduling()
    {
        using var pair = new FieldPair(new FieldConfig(5, 3, uint.MaxValue));
        var stats = pair.Step([]);

        Assert.Equal(1u, pair.Tick);
        Assert.Equal(0, stats.ActiveSlots);
        Assert.Equal(0, pair.Front.ActiveSlotCount);
    }

    [Fact]
    public void Registry_DuplicateKey_ReturnsInvalidId()
    {
        using var registry = new FieldRegistry();
        var first = registry.Register(new FieldConfig(11, 3, 60));
        var second = registry.Register(new FieldConfig(11, 4, 60));

        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void WriteRegion_InjectsReadableCells()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        var weights = new int[8];
        for (var i = 0; i < weights.Length; i++) weights[i] = i + 1;

        field.WriteRegion(new Int2(6, -2), new Int2(4, 2), weights);

        var reader = field.AsReader();
        Assert.Equal(1, reader.ReadCell(new Int2(6, -2)));
        Assert.Equal(4, reader.ReadCell(new Int2(9, -2)));
        Assert.Equal(5, reader.ReadCell(new Int2(6, -1)));
        Assert.Equal(8, reader.ReadCell(new Int2(9, -1)));
        Assert.Equal(0, reader.ReadCell(new Int2(10, -2)));

        var exported = new int[8];
        field.ReadRegion(new Int2(6, -2), new Int2(4, 2), exported);
        Assert.Equal(weights, exported);
    }

    [Fact]
    public void WriteRegion_ThenStampTick_FollowsStampSemantics()
    {
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.WriteRegion(new Int2(0, 0), new Int2(2, 2), [7, 7, 7, 7]);
        Assert.Equal(7, field.AsReader().ReadCell(new Int2(1, 1)));

        field.Tick([], 2);
        Assert.Equal(0, field.AsReader().ReadCell(new Int2(1, 1)));
    }

    [Fact]
    public void PaintedCanvas_DecomposesToEquivalentStamps()
    {
        var cells = new CellWeight[]
        {
            new(new Int2(0, 0), 5),
            new(new Int2(1, 0), 5),
            new(new Int2(2, 0), 5),
            new(new Int2(0, 1), 2),
            new(new Int2(4, 1), 7)
        };

        var stamps = PaintedCanvas.ToStamps(cells, new Int2(10, 10));

        Assert.Equal(3, stamps.Length);
        Assert.Equal(10, stamps[0].Origin.X);
        Assert.Equal(0, stamps[0].Shape.RectMin.X);
        Assert.Equal(3, stamps[0].Shape.RectSize.X);
        Assert.Equal(5, stamps[0].Shape.Weight);
        Assert.Equal(2, stamps[1].Shape.Weight);
        Assert.Equal(7, stamps[2].Shape.Weight);
    }
}
