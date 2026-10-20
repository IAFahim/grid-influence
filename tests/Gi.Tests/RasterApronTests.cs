using Xunit;

namespace Gi.Tests;

public unsafe class RasterApronTests
{
    private const int Margin = 16384;

    private static readonly int[] Scales = [24, 40, 64, 100, 128, 160, 200, 255, 300, 512, 1024];
    private static readonly float[] Fractions = [0f, 0.125f, 0.25f, 0.5f, 0.75f, 0.875f, 0.99f];
    private static readonly (int Width, int Height)[] Sizes = [(2, 2), (4, 2), (3, 5), (8, 8), (13, 7), (16, 3)];

    [Theory]
    [InlineData(0x55)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void ScaledRasterEmissionNeverReadsOutsideTheStampBuffers(byte poison)
    {
        var rng = new SplitMix64(0x5EED_A9805ul);
        var stamps = new List<byte>();
        try
        {
            foreach (var (width, height) in Sizes)
            {
                var data = new sbyte[width * height];
                for (var i = 0; i < data.Length; i++) data[i] = (sbyte)(rng.NextInt(0, 201) - 100);
                data[0] = 100;
                data[^1] = -100;
                var stamp = Stamp.New(data, (ushort)width, (ushort)height);
                stamps.Add(stamp);
                Assert.Equal(Gi.StampKind.Raster, StampCatalog.Get(stamp)->Kind);
                Assert.Equal(Sample(stamp, 0), Sample(stamp, poison));
            }
        }
        finally
        {
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    private static long[] Sample(byte stamp, byte fill)
    {
        var original = *StampCatalog.Get(stamp);
        var dataBytes = original.Pitch * (original.Height + 2);
        var mipBytes = MipBytes(original.Width, original.Height);
        var dataBlock = Padded(original.Data - original.Pitch - 1, dataBytes, fill);
        var mipBlock = original.Mips == null ? null : Padded(original.Mips, mipBytes, fill);
        try
        {
            var copy = original;
            copy.Data = dataBlock + Margin + original.Pitch + 1;
            copy.Mips = mipBlock == null ? null : mipBlock + Margin;
            var results = new List<long>();
            foreach (var scale in Scales)
            foreach (var fractionX in Fractions)
            foreach (var fractionY in Fractions)
            {
                if (!TileBake.Place(20f + fractionX, 20f + fractionY, 0f, 0f, 256, 64, &copy, 0, scale,
                        out var p)) continue;
                if (p.Turned || p.Plane != Plane.Dense) continue;

                var x0 = Math.Max(p.X0, 0);
                var y0 = Math.Max(p.Y0, 0);
                var x1 = Math.Min(p.X1, 64);
                var y1 = Math.Min(p.Y1, 64);
                for (var cy = y0; cy < y1; cy++)
                for (var cx = x0; cx < x1; cx++)
                {
                    var cell = 0;
                    TileBake.EmitRaster(&cell, cx, cy, p.Px, p.Py, p.Fx, p.Fy,
                        Math.Min(p.X1, cx + 1), Math.Min(p.Y1, cy + 1), p.Sampling, p.V, 3);
                    results.Add(cell);
                }
            }

            Assert.NotEmpty(results);
            return results.ToArray();
        }
        finally
        {
            NativeHeap.Free(dataBlock);
            if (mipBlock != null) NativeHeap.Free(mipBlock);
        }
    }

    private static int MipBytes(int width, int height)
    {
        var total = 0;
        while (width > 1 || height > 1)
        {
            width = (width + 1) >> 1;
            height = (height + 1) >> 1;
            total += (width + 2) * (height + 2);
        }

        return total;
    }

    private static sbyte* Padded(sbyte* source, int bytes, byte fill)
    {
        var block = (byte*)NativeHeap.AllocZeroed((nuint)(bytes + 2 * Margin));
        new Span<byte>(block, Margin).Fill(fill);
        new Span<byte>(block + Margin + bytes, Margin).Fill(fill);
        Buffer.MemoryCopy(source, block + Margin, bytes, bytes);
        return (sbyte*)block;
    }
}
