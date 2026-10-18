using Gi.Validation;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class SourceCheckTests
{
    [Fact]
    public void AnEmitterNeedsAPersistentLayerAndAGainInRange()
    {
        Assert.True(GiSourceCheck.TryEmitter(GiLayerKind.Persistent, 16, out var error));
        Assert.Null(error);
        Assert.False(GiSourceCheck.TryEmitter(GiLayerKind.Transient, 4, out error));
        Assert.Equal(GiSourceCheck.EmitterResidency, error);
        Assert.False(GiSourceCheck.TryEmitter(GiLayerKind.Sum, 4, out error));
        Assert.Equal(GiSourceCheck.EmitterResidency, error);

        GiValidation.TryGain(17, out var expected);
        Assert.False(GiSourceCheck.TryEmitter(GiLayerKind.Persistent, 17, out error));
        Assert.Equal(expected, error);
        Assert.False(GiSourceCheck.TryEmitter(GiLayerKind.Persistent, -17, out error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void ASplatNeedsATransientLayerAndAGainInRange()
    {
        Assert.True(GiSourceCheck.TrySplat(GiLayerKind.Transient, -16, out var error));
        Assert.Null(error);
        Assert.False(GiSourceCheck.TrySplat(GiLayerKind.Persistent, 4, out error));
        Assert.Equal(GiSourceCheck.SplatResidency, error);
        Assert.False(GiSourceCheck.TrySplat(GiLayerKind.Max, 4, out error));
        Assert.Equal(GiSourceCheck.SplatResidency, error);

        GiValidation.TryGain(99, out var expected);
        Assert.False(GiSourceCheck.TrySplat(GiLayerKind.Transient, 99, out error));
        Assert.Equal(expected, error);
    }
}
