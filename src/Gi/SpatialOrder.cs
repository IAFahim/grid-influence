using System.Runtime.CompilerServices;

namespace Gi;

internal static unsafe class SpatialOrder
{
    internal const uint Outside = uint.MaxValue;
    private const int DigitBits = 11;
    private const int Digits = 1 << DigitBits;

    [MethodImpl(Jit.Inline)]
    private static uint Spread(uint value)
    {
        value &= 0x0000FFFF;
        value = (value | (value << 8)) & 0x00FF00FF;
        value = (value | (value << 4)) & 0x0F0F0F0F;
        value = (value | (value << 2)) & 0x33333333;
        return (value | (value << 1)) & 0x55555555;
    }

    [MethodImpl(Jit.Inline)]
    internal static uint Key(int cellX, int cellY)
    {
        var tile = Spread((uint)(cellX >> TileBake.TileBits)) | (Spread((uint)(cellY >> TileBake.TileBits)) << 1);
        var local = Spread((uint)(cellX & (TileBake.TileSize - 1))) |
                    (Spread((uint)(cellY & (TileBake.TileSize - 1))) << 1);
        return (tile << (2 * TileBake.TileBits)) | local;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool Sort(ReadOnlySpan<uint> keys, Span<int> permutation, Span<ulong> scratch)
    {
        var count = keys.Length;
        if (permutation.Length < count || scratch.Length < 2 * count) return false;
        if (count == 0) return true;

        var counts = stackalloc int[3 * Digits];
        new Span<int>(counts, 3 * Digits).Clear();
        fixed (uint* source = keys)
        fixed (ulong* scratchStart = scratch)
        fixed (int* target = permutation)
        {
            for (var i = 0; i < count; i++)
            {
                var key = source[i];
                counts[key & (Digits - 1)]++;
                counts[Digits + ((key >> DigitBits) & (Digits - 1))]++;
                counts[2 * Digits + (key >> (2 * DigitBits))]++;
            }

            for (var pass = 0; pass < 3; pass++)
            {
                var sum = 0;
                for (var d = 0; d < Digits; d++)
                {
                    var next = sum + counts[pass * Digits + d];
                    counts[pass * Digits + d] = sum;
                    sum = next;
                }
            }

            var first = scratchStart;
            var second = scratchStart + count;
            for (var i = 0; i < count; i++)
            {
                var key = source[i];
                first[counts[key & (Digits - 1)]++] = ((ulong)key << 32) | (uint)i;
            }

            for (var i = 0; i < count; i++)
            {
                var pair = first[i];
                second[counts[Digits + (((uint)(pair >> 32) >> DigitBits) & (Digits - 1))]++] = pair;
            }

            for (var i = 0; i < count; i++)
            {
                var pair = second[i];
                first[counts[2 * Digits + ((uint)(pair >> 32) >> (2 * DigitBits))]++] = pair;
            }

            for (var i = 0; i < count; i++) target[i] = (int)(uint)first[i];
        }

        return true;
    }

    [MethodImpl(Jit.Hot)]
    internal static float Disorder(ReadOnlySpan<uint> keys)
    {
        if (keys.Length < 2) return 0f;

        var descents = 0;
        for (var i = 1; i < keys.Length; i++)
            if (keys[i] >> (2 * TileBake.TileBits) < keys[i - 1] >> (2 * TileBake.TileBits))
                descents++;

        return descents / (float)(keys.Length - 1);
    }
}
