namespace Gi.Validation;

public static class GiValidation
{
    public static bool TryRectangle(int width, int height, int value, out string? error)
    {
        if (!ValidationRules.IsAxis(width) || !ValidationRules.IsAxis(height) || !ValidationRules.IsValue(value))
            return Reject("Rectangular stamp dimensions must be 1 through 256 and value -128 through 127.", out error);
        return Accept(out error);
    }

    public static bool TryRound(int radius, int arc, int value, out string? error)
    {
        if (!ValidationRules.IsRadius(radius) || !ValidationRules.IsArc(arc) || !ValidationRules.IsValue(value))
            return Reject("Round stamp radius must be 1 through 128, arc 1 through 360, and value -128 through 127.", out error);
        return Accept(out error);
    }

    public static bool TryGain(int gain, out string? error)
    {
        if (!ValidationRules.IsGain(gain))
            return Reject("Influence stamp gain must be between -16 and 16.", out error);
        return Accept(out error);
    }

    public static bool TryWorldCount(int count, out string? error)
    {
        if (!ValidationRules.IsCount(count, ValidationRules.MaxWorlds))
            return Reject("World count must be between 0 and 255.", out error);
        return Accept(out error);
    }

    public static bool TryLayerCount(int count, out string? error)
    {
        if (!ValidationRules.IsCount(count, ValidationRules.MaxLayers))
            return Reject("Layer count must be between 0 and 32.", out error);
        return Accept(out error);
    }

    public static bool TryStampCount(int count, out string? error)
    {
        if (!ValidationRules.IsCount(count, ValidationRules.MaxUsableStamps))
            return Reject("Stamp count must be between 0 and 255; Gi reserves handle zero.", out error);
        return Accept(out error);
    }

    public static bool TryWorldIndex(int index, out string? error)
    {
        if (!ValidationRules.IsIndex(index, ValidationRules.MaxWorlds))
            return Reject("World register index must be between 0 and 254.", out error);
        return Accept(out error);
    }

    public static bool TryLayerIndex(int index, out string? error)
    {
        if (!ValidationRules.IsIndex(index, ValidationRules.MaxLayers))
            return Reject("Layer register index must be between 0 and 31.", out error);
        return Accept(out error);
    }

    public static bool TryStampIndex(int index, out string? error)
    {
        if (!ValidationRules.IsIndex(index, ValidationRules.MaxUsableStamps))
            return Reject("Stamp register index must be between 0 and 254; Gi reserves handle zero.", out error);
        return Accept(out error);
    }

    public static bool TryDerivedLayer(int firstWeight, int secondWeight, int shift, out string? error)
    {
        if (!ValidationRules.IsWeight(firstWeight) || !ValidationRules.IsWeight(secondWeight) || !ValidationRules.IsShift(shift))
            return Reject("Derived layer weights must be -32767 through 32767 and shift 0 through 15.", out error);
        return Accept(out error);
    }

    public static bool TryGrid(int power, float size, out string? error)
        => TryGrid(power, 0f, 0f, size, out error);

    public static bool TryGrid(int power, float x, float y, float size, out string? error)
    {
        if (!ValidationRules.IsPower(power) || !ValidationRules.IsGridOrigin(x) || !ValidationRules.IsGridOrigin(y) ||
            !ValidationRules.IsGridSize(size) || !ValidationRules.TryGridScaleQ8(power, size, out _))
            return Reject(
                "Grid power must be between 5 and 14, origin and size must be finite, size must be positive, " +
                "and 256 times cells per world unit must truncate to between 1 and 1048576.", out error);
        return Accept(out error);
    }

    private static bool Accept(out string? error)
    {
        error = null;
        return true;
    }

    private static bool Reject(string message, out string? error)
    {
        error = message;
        return false;
    }
}
