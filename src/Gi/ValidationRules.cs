using System.Runtime.CompilerServices;
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
    internal const int MaxFootprint = 512;
    internal const int MaxReachCells = 4096;

    [MethodImpl(Jit.Hot)]
    internal static bool IsAxis(int axis)
    {
        return axis >= 1 && axis <= MaxAxis;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsRadius(int radius)
    {
        return radius >= 1 && radius <= MaxRadius;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsArc(int arc)
    {
        return arc >= 1 && arc <= FullArc;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsValue(int value)
    {
        return value >= MinValue && value <= MaxValue;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsGain(int gain)
    {
        return gain >= -MaxGain && gain <= MaxGain;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsCount(int count, int maximum)
    {
        return count >= 0 && count <= maximum;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsIndex(int index, int maximum)
    {
        return index >= 0 && index < maximum;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsPower(int power)
    {
        return power >= MinPower && power <= MaxPower;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsWeight(int weight)
    {
        return weight >= -MaxWeight && weight <= MaxWeight;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsShift(int shift)
    {
        return shift >= 0 && shift <= MaxShift;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsPhaseBits(int phaseBits)
    {
        return phaseBits >= 0 && phaseBits <= MaxPhaseBits;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsTurnBits(int turnBits)
    {
        return turnBits >= 0 && turnBits <= MaxTurnBits;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsFootprint(long cells)
    {
        return cells >= 0 && cells <= MaxFootprint;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsReachCells(float cells)
    {
        return cells >= 0f && cells <= MaxReachCells;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsGridSize(float size)
    {
        return float.IsFinite(size) && size > 0f;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool IsGridOrigin(float origin)
    {
        return float.IsFinite(origin);
    }

    [MethodImpl(Jit.Hot)]
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
