using System;
using Xunit;

namespace Gi.Tests;

public sealed class RowScanTests
{
    [Fact]
    public void Prefix_Matches_Saturating_Chain_On_Random_Rows()
    {
        var random = new Random(20261001);
        for (var trial = 0; trial < 10_000; trial++)
        {
            var count = random.Next(1, 80);
            var deltas = new short[count];
            var reference = new short[count];
            for (var i = 0; i < count; i++)
            {
                if (random.Next(3) == 0)
                {
                    var magnitude = random.Next(1, 4000) * (random.Next(2) == 0 ? 1 : -1);
                    deltas[i] = (short)magnitude;
                }
            }
            deltas.CopyTo(reference, 0);
            var expected = SaturatingPrefix(reference);
            unsafe
            {
                fixed (short* p = deltas)
                {
                    RowScan.Prefix(p, count);
                    RowScan.PrefixPair(p, p, 0);
                }
            }
            for (var i = 0; i < count; i++)
                Assert.Equal(expected[i], deltas[i]);
        }
    }

    [Fact]
    public void PrefixPair_Matches_Two_Saturating_Chains()
    {
        var random = new Random(987654321);
        for (var trial = 0; trial < 10_000; trial++)
        {
            var count = random.Next(1, 80);
            var first = RandomRow(random, count);
            var second = RandomRow(random, count);
            var expectedFirst = SaturatingPrefix((short[])first.Clone());
            var expectedSecond = SaturatingPrefix((short[])second.Clone());
            unsafe
            {
                fixed (short* a = first)
                fixed (short* b = second)
                {
                    RowScan.PrefixPair(a, b, count);
                }
            }
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(expectedFirst[i], first[i]);
                Assert.Equal(expectedSecond[i], second[i]);
            }
        }
    }

    private static short[] RandomRow(Random random, int count)
    {
        var row = new short[count];
        for (var i = 0; i < count; i++)
            if (random.Next(3) == 0)
                row[i] = (short)(random.Next(1, 4000) * (random.Next(2) == 0 ? 1 : -1));
        return row;
    }

    private static short[] SaturatingPrefix(short[] row)
    {
        var result = (short[])row.Clone();
        var running = 0;
        for (var i = 0; i < result.Length; i++)
        {
            running += row[i];
            result[i] = (short)Math.Clamp(running, short.MinValue, short.MaxValue);
        }
        return result;
    }
}
