namespace Gi;

internal static class ValidationRules
{
    internal const int MaxWorlds = 255;
    internal const int MaxGrids = 32;
    internal const int MaxLayers = 32;
    internal const int MaxStamps = 256;
    internal const int MaxUsableStamps = MaxStamps - 1;
    internal const int MaxAxis = 256;
    internal const int MaxRadius = MaxAxis / 2;
    internal const int FullArc = 360;
    internal const int MinValue = -128;
    internal const int MaxValue = 127;
    internal const int MaxGain = 16;
    internal const int MinPower = 5;
    internal const int MaxPower = 14;
    internal const int MaxWeight = short.MaxValue;
    internal const int MaxShift = 15;
    internal const int MinScaleQ8 = 1;
    internal const int MaxScaleQ8 = 1 << 20;
    internal const int MaxPhaseBits = 8;
    internal const int MaxTurnBits = 16;

    internal static bool IsAxis(int axis) => axis >= 1 && axis <= MaxAxis;
    internal static bool IsRadius(int radius) => radius >= 1 && radius <= MaxRadius;
    internal static bool IsArc(int arc) => arc >= 1 && arc <= FullArc;
    internal static bool IsValue(int value) => value >= MinValue && value <= MaxValue;
    internal static bool IsGain(int gain) => gain >= -MaxGain && gain <= MaxGain;
    internal static bool IsCount(int count, int maximum) => count >= 0 && count <= maximum;
    internal static bool IsIndex(int index, int maximum) => index >= 0 && index < maximum;
    internal static bool IsPower(int power) => power >= MinPower && power <= MaxPower;
    internal static bool IsWeight(int weight) => weight >= -MaxWeight && weight <= MaxWeight;
    internal static bool IsShift(int shift) => shift >= 0 && shift <= MaxShift;
    internal static bool IsPhaseBits(int phaseBits) => phaseBits >= 0 && phaseBits <= MaxPhaseBits;
    internal static bool IsTurnBits(int turnBits) => turnBits >= 0 && turnBits <= MaxTurnBits;
    internal static bool IsGridSize(float size) => float.IsFinite(size) && size > 0f;
    internal static bool IsGridOrigin(float origin) => float.IsFinite(origin);

    internal static bool TryGridScaleQ8(int power, float size, out int scaleQ8)
    {
        scaleQ8 = 0;
        if (!IsPower(power) || !IsGridSize(size)) return false;

        var scaled = (1 << power) / size * 256f;
        if (!(scaled >= MinScaleQ8 && scaled < MaxScaleQ8 + 1f)) return false;

        scaleQ8 = (int)scaled;
        return true;
    }
}
