using System;
using Gi;

namespace GiDemo
{

internal static class GiDemoStamps
{
    internal static readonly byte FoodCrumb = Stamp.Box(3, 3, 40);
    internal static readonly byte WolfAura = Stamp.Cone(20, 110, arc: 100);
    internal static readonly byte FearBrush = Gaussian(15, 140);
    internal static readonly byte HerdPing = Stamp.Tent(9, 9, 60);

    private static unsafe byte Gaussian(int size, int peak)
    {
        var samples = new sbyte[size * size];
        double c = (size - 1) / 2.0, sigma = size / 5.0;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            double dx = x - c, dy = y - c;
            var v = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
            samples[y * size + x] = (sbyte)Math.Clamp((int)Math.Round(v), -128, 127);
        }
        fixed (sbyte* p = samples) return Stamp.New(p, size, size);
    }
}
}
