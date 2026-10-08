using System;
using Gi;

namespace GiDemo;

internal static class GiDemoStamps
{
    internal static readonly byte FoodCrumb = Stamp.Box(3, 3, 40);
    internal static readonly byte WolfAura = Gaussian(17, 110);
    internal static readonly byte FearBrush = Gaussian(15, 140);

    private static byte Gaussian(int size, int peak)
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
        return Stamp.New(samples, size, size);
    }
}
