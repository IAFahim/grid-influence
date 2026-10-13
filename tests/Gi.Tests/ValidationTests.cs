using Gi.Validation;
using Xunit;

namespace Gi.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData(1, 1, -128, true)]
    [InlineData(256, 256, 127, true)]
    [InlineData(0, 1, 0, false)]
    [InlineData(257, 1, 0, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(1, 257, 0, false)]
    [InlineData(1, 1, -129, false)]
    [InlineData(1, 1, 128, false)]
    public void Rectangle_rejects_each_invalid_dimension_and_value(int width, int height, int value, bool valid)
    {
        Assert.Equal(valid, GiValidation.TryRectangle(width, height, value, out var error));
        Assert.Equal(valid, error == null);
    }

    [Theory]
    [InlineData(1, 1, -128, true)]
    [InlineData(128, 360, 127, true)]
    [InlineData(0, 360, 0, false)]
    [InlineData(129, 360, 0, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(1, 361, 0, false)]
    [InlineData(1, 1, -129, false)]
    [InlineData(1, 1, 128, false)]
    public void Round_rejects_each_invalid_radius_arc_and_value(int radius, int arc, int value, bool valid)
    {
        Assert.Equal(valid, GiValidation.TryRound(radius, arc, value, out var error));
        Assert.Equal(valid, error == null);
    }

    [Theory]
    [InlineData(-17, false)]
    [InlineData(-16, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void Gain_rejects_out_of_range_inputs_instead_of_clamping(int gain, bool valid)
    {
        Assert.Equal(valid, GiValidation.TryGain(gain, out var error));
        Assert.Equal(valid, error == null);
    }

    [Theory]
    [InlineData(-1, false, false, false)]
    [InlineData(0, true, true, true)]
    [InlineData(32, true, true, true)]
    [InlineData(33, true, false, true)]
    [InlineData(255, true, false, true)]
    [InlineData(256, false, false, false)]
    public void Count_limits_include_empty_registers_and_reserve_stamp_zero(int count, bool worlds, bool layers, bool stamps)
    {
        Assert.Equal(worlds, GiValidation.TryWorldCount(count, out var worldError));
        Assert.Equal(worlds, worldError == null);
        Assert.Equal(layers, GiValidation.TryLayerCount(count, out var layerError));
        Assert.Equal(layers, layerError == null);
        Assert.Equal(stamps, GiValidation.TryStampCount(count, out var stampError));
        Assert.Equal(stamps, stampError == null);
    }

    [Theory]
    [InlineData(4, 1f, false)]
    [InlineData(5, 1f, true)]
    [InlineData(14, 1f, true)]
    [InlineData(15, 1f, false)]
    [InlineData(5, 0f, false)]
    [InlineData(5, -1f, false)]
    [InlineData(5, float.NaN, false)]
    [InlineData(5, float.PositiveInfinity, false)]
    [InlineData(5, float.NegativeInfinity, false)]
    public void Grid_requires_supported_power_and_finite_positive_size(int power, float size, bool valid)
    {
        Assert.Equal(valid, GiValidation.TryGrid(power, size, out var error));
        Assert.Equal(valid, error == null);
    }

    [Theory]
    [InlineData(int.MinValue, false, false, false)]
    [InlineData(-1, false, false, false)]
    [InlineData(0, true, true, true)]
    [InlineData(31, true, true, true)]
    [InlineData(32, true, false, true)]
    [InlineData(254, true, false, true)]
    [InlineData(255, false, false, false)]
    [InlineData(int.MaxValue, false, false, false)]
    public void Register_indices_reject_missing_selections_and_exclusive_upper_bounds(int index, bool worlds, bool layers, bool stamps)
    {
        Assert.Equal(worlds, GiValidation.TryWorldIndex(index, out var worldError));
        Assert.Equal(worlds, worldError == null);
        Assert.Equal(layers, GiValidation.TryLayerIndex(index, out var layerError));
        Assert.Equal(layers, layerError == null);
        Assert.Equal(stamps, GiValidation.TryStampIndex(index, out var stampError));
        Assert.Equal(stamps, stampError == null);
    }

    [Fact]
    public void Validation_assembly_has_no_Gi_runtime_dependency()
    {
        Assert.DoesNotContain(typeof(GiValidation).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "Gi");
    }

    [Fact]
    public void Shape_limits_agree_with_runtime_exceptions_and_parameter_names()
    {
        Assert.False(GiValidation.TryRectangle(257, 1, 0, out _));
        Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Box(257, 1, 0)).ParamName);
        Assert.False(GiValidation.TryRectangle(1, 0, 0, out _));
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Box(1, 0, 0)).ParamName);
        Assert.False(GiValidation.TryRound(129, 360, 0, out _));
        Assert.Equal("radius", Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Disk(129, 0)).ParamName);
        Assert.False(GiValidation.TryRound(1, 361, 0, out _));
        Assert.Equal("arc", Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Disk(1, 0, 361)).ParamName);
    }
}
