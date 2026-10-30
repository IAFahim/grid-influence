using Xunit;

namespace Gi.Tests;

public sealed unsafe class KernelTrackWindows
{
    private const int Size = 1 << 10;

    public static TheoryData<string> Stamps()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[]
                 {
                     "raster3x2", "raster8x8", "raster17x9", "rect4x4", "rect1x1", "tent5x5", "bell6x6", "tent12x7",
                     "diskr4", "diskr16", "coner7", "domer9", "arcr6", "diskr64"
                 })
            data.Add(name);
        return data;
    }

    private static byte MakeStamp(string name)
    {
        switch (name)
        {
            case "raster3x2":
            {
                var samples = new sbyte[6];
                for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 41 % 231 - 115);
                fixed (sbyte* p = samples)
                {
                    return Stamp.New(p, 3, 2);
                }
            }
            case "raster8x8":
            {
                var samples = new sbyte[64];
                for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 9 - 3);
                fixed (sbyte* p = samples)
                {
                    return Stamp.New(p, 8, 8);
                }
            }
            case "raster17x9":
            {
                var samples = new sbyte[17 * 9];
                for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)((i * 29 % 251) - 125);
                fixed (sbyte* p = samples)
                {
                    return Stamp.New(p, 17, 9);
                }
            }
            case "rect4x4":
                return Stamp.Box(4, 4, 37);
            case "rect1x1":
                return Stamp.Box(1, 1, 37);
            case "tent5x5":
                return Stamp.Tent(5, 5, 40);
            case "tent12x7":
                return Stamp.Tent(12, 7, -40);
            case "bell6x6":
                return Stamp.Bell(6, 6, 40);
            case "diskr4":
                return Stamp.Disk(4, 40);
            case "diskr16":
                return Stamp.Disk(16, 40);
            case "coner7":
                return Stamp.Cone(7, 40);
            case "domer9":
                return Stamp.Dome(9, 40);
            case "arcr6":
                return Stamp.Disk(6, 40, 90);
            case "diskr64":
                return Stamp.Disk(64, 40);
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(Stamps))]
    public void TurnedEmission_CoversEveryNonzeroCellOfTheSupport(string name)
    {
        var stamp = MakeStamp(name);
        var variant = StampCatalog.Get(stamp);
        var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
        var smooth = stackalloc long[TileBake.TileSize * TileBake.TileSize];
        try
        {
            foreach (var scale in new[] { 128, 256, 947 })
            foreach (var phase in new[] { 0, 1, 63, 128, 200, 255 })
            foreach (var angleSeed in new[] { 0, 1, 5, 7919, 30011, 60013 })
            {
                var angle = (angleSeed * 7919) & 0xFFFF;
                var x = (((35 << 8) + phase - (((long)variant->OriginQ8X * scale) >> 8)) + 0.5f) / 256f;
                var y = (((41 << 8) + phase - (((long)variant->OriginQ8Y * scale) >> 8)) + 0.5f) / 256f;
                Assert.True(TileBake.Place(x, y, 0f, 0f, 256, Size, variant, angle, scale, Layer.Precision.Exact,
                    out var p));
                var margin = (int)((long)Math.Max(variant->Width, variant->Height) * scale * 3 / 512) + (scale >> 6) + 6;
                if (margin > 140) continue;
                var cx0 = Math.Max(p.Px - margin, 0);
                var cy0 = Math.Max(p.Py - margin, 0);
                var cx1 = Math.Min(p.Px + margin, Size);
                var cy1 = Math.Min(p.Py + margin, Size);
                if (cx1 <= cx0 || cy1 <= cy0) continue;

                var denseStamp = p.Plane == Plane.Dense;
                for (var ty = cy0 >> TileBake.TileBits; ty <= (cy1 - 1) >> TileBake.TileBits; ty++)
                for (var tx = cx0 >> TileBake.TileBits; tx <= (cx1 - 1) >> TileBake.TileBits; tx++)
                {
                    var tileX0 = tx << TileBake.TileBits;
                    var tileY0 = ty << TileBake.TileBits;
                    if (denseStamp)
                    {
                        new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                        TileBake.EmitTurnedDense(dense, tileX0, tileY0, &p, 3);
                    }
                    else
                    {
                        new Span<long>(smooth, TileBake.TileSize * TileBake.TileSize).Clear();
                        TileBake.EmitTurnedSmooth(smooth, tileX0, tileY0, &p, 3);
                    }

                    for (var cy = Math.Max(cy0, tileY0); cy < Math.Min(cy1, tileY0 + TileBake.TileSize); cy++)
                    for (var cx = Math.Max(cx0, tileX0); cx < Math.Min(cx1, tileX0 + TileBake.TileSize); cx++)
                    {
                        var at = (cy - tileY0) * TileBake.TileSize + (cx - tileX0);
                        var emitted = denseStamp ? dense[at] : smooth[at];
                        var oracle = denseStamp
                            ? (long)TileBake.TurnedDenseAt(&p, cx, cy) * 3
                            : TileBake.TurnedSmoothAt(&p, cx, cy) * 3;
                        if (emitted != oracle)
                            Assert.Fail(
                                $"{name} scale {scale} phase {phase} angle {angle}: cell ({cx},{cy}) emitted {emitted} oracle {oracle}");
                    }
                }
            }
        }
        finally
        {
            Stamp.Free(stamp);
        }
    }

    [Theory]
    [MemberData(nameof(Stamps))]
    public void TurnedEmission_MatchesAtEveryAngleStep(string name)
    {
        var stamp = MakeStamp(name);
        var variant = StampCatalog.Get(stamp);
        var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
        var smooth = stackalloc long[TileBake.TileSize * TileBake.TileSize];
        try
        {
            for (var angle = 0; angle < 65536; angle += 4099)
            {
                var x = (((35 << 8) + 77 - (((long)variant->OriginQ8X * 256) >> 8)) + 0.5f) / 256f;
                var y = (((41 << 8) + 201 - (((long)variant->OriginQ8Y * 256) >> 8)) + 0.5f) / 256f;
                Assert.True(TileBake.Place(x, y, 0f, 0f, 256, Size, variant, angle, 256, Layer.Precision.Exact,
                    out var p));
                var cx0 = Math.Max(p.X0, 0);
                var cy0 = Math.Max(p.Y0, 0);
                var cx1 = Math.Min(p.X1, Size);
                var cy1 = Math.Min(p.Y1, Size);
                if (cx1 <= cx0 || cy1 <= cy0) continue;

                var denseStamp = p.Plane == Plane.Dense;
                for (var cy = cy0; cy < cy1; cy++)
                for (var cx = cx0; cx < cx1; cx++)
                {
                    var tx = cx >> TileBake.TileBits;
                    var ty = cy >> TileBake.TileBits;
                    var tileX0 = tx << TileBake.TileBits;
                    var tileY0 = ty << TileBake.TileBits;
                    if (denseStamp)
                    {
                        new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                        TileBake.EmitTurnedDense(dense, tileX0, tileY0, &p, -2);
                    }
                    else
                    {
                        new Span<long>(smooth, TileBake.TileSize * TileBake.TileSize).Clear();
                        TileBake.EmitTurnedSmooth(smooth, tileX0, tileY0, &p, -2);
                    }

                    var at = (cy - tileY0) * TileBake.TileSize + (cx - tileX0);
                    var emitted = denseStamp ? dense[at] : smooth[at];
                    var oracle = denseStamp
                        ? (long)TileBake.TurnedDenseAt(&p, cx, cy) * -2
                        : TileBake.TurnedSmoothAt(&p, cx, cy) * -2;
                    if (emitted != oracle)
                        Assert.Fail($"{name} angle {angle}: cell ({cx},{cy}) emitted {emitted} oracle {oracle}");
                }
            }
        }
        finally
        {
            Stamp.Free(stamp);
        }
    }
}
