using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Gi.Tests;

public sealed unsafe class KernelTrackTiming(ITestOutputHelper output)
{
    private const int Size = 1 << 10;
    private const int Warmup = 200;
    private const int Passes = 5;

    private static byte RasterStamp()
    {
        var data = new sbyte[64];
        for (var i = 0; i < data.Length; i++) data[i] = (sbyte)(i % 9 - 3);
        fixed (sbyte* p = data)
        {
            return Stamp.New(p, 8, 8);
        }
    }

    private static double Best(Action<int> body, int count)
    {
        var best = double.MaxValue;
        for (var pass = 0; pass < Passes; pass++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++) body(i);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds / count;
            if (pass > 0) best = Math.Min(best, elapsed);
        }

        return best;
    }

    [Fact]
    public void OneshotRasterStages_CarryTheirPrice()
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, Size);
        var layer = Layer.New(world, Layer.Precision.Exact);
        var stamp = RasterStamp();
        var w = World.GetContext(world);
        var v = StampCatalog.Get(stamp);
        try
        {
            var anchorX = 256 * 512;
            var anchorY = 256 * 512;

            var placeCost = Best(i =>
            {
                Assert.True(TileBake.PlaceAnchored(anchorX + ((i * 37) & 255), anchorY, 256, Size, v,
                    (i * 7919) & 0xFFFF, 256, Layer.Precision.Exact, out _));
            }, 20_000);
            output.WriteLine($"placeAnchored turned raster: {placeCost:F1} ns");

            long bakeSum = 0;
            var bakeCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                if (!KernelFactory.TryGetTaps(w, 0, stamp, (i * 7919) & 0xFFFF, Layer.Precision.Exact, &p, out var taps))
                    Assert.Fail("bake failed");
                bakeSum += taps.Width + taps.Height;
            }, 20_000);
            output.WriteLine($"tryGetTaps bake+insert turned raster: {bakeCost:F1} ns ({bakeSum & 1})");

            long findSum = 0;
            TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, 1234, 256, Layer.Precision.Exact, out var fixed_);
            KernelFactory.TryGetTaps(w, 0, stamp, 1234, Layer.Precision.Exact, &fixed_, out _);
            var findCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, 1234, 256, Layer.Precision.Exact, out var p);
                if (!KernelFactory.TryFindTaps(w, 0, stamp, 1234, Layer.Precision.Exact, &p, out var taps))
                    Assert.Fail("find failed");
                findSum += taps.Width;
            }, 20_000);
            output.WriteLine($"tryFindTaps cache hit: {findCost:F1} ns ({findSum & 1})");

            var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
            long denseSum = 0;
            var denseCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                TileBake.EmitTurnedDense(dense, 512 & ~31, 512 & ~31, &p, 1);
                denseSum += dense[0] + dense[513];
            }, 20_000);
            output.WriteLine($"emitTurnedDense one tile: {denseCost:F1} ns ({denseSum & 1})");

            long tilesTouched = 0;
            var tilesCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                for (var ty = p.Y0 >> TileBake.TileBits; ty <= (p.Y1 - 1) >> TileBake.TileBits; ty++)
                for (var tx = p.X0 >> TileBake.TileBits; tx <= (p.X1 - 1) >> TileBake.TileBits; tx++)
                {
                    new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                    TileBake.EmitTurnedDense(dense, tx << TileBake.TileBits, ty << TileBake.TileBits, &p, 1);
                    tilesTouched++;
                }
            }, 20_000);
            output.WriteLine($"emitTurnedDense full footprint: {tilesCost:F1} ns ({tilesTouched & 1})");

            long keepSum = 0;
            var keepCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                for (var ty = p.Y0 >> TileBake.TileBits; ty <= (p.Y1 - 1) >> TileBake.TileBits; ty++)
                for (var tx = p.X0 >> TileBake.TileBits; tx <= (p.X1 - 1) >> TileBake.TileBits; tx++)
                {
                    if (!TileBake.Touches(&p, tx << TileBake.TileBits, ty << TileBake.TileBits)) continue;
                    TileBake.EmitTurnedDense(dense, tx << TileBake.TileBits, ty << TileBake.TileBits, &p, 1);
                    keepSum += dense[513];
                }
            }, 20_000);
            output.WriteLine($"fallback emit into persistent plane: {keepCost:F1} ns ({keepSum & 1})");

            var clearCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                for (var ty = p.Y0 >> TileBake.TileBits; ty <= (p.Y1 - 1) >> TileBake.TileBits; ty++)
                for (var tx = p.X0 >> TileBake.TileBits; tx <= (p.X1 - 1) >> TileBake.TileBits; tx++)
                {
                    new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                }
            }, 20_000);
            output.WriteLine($"canvas clears alone: {clearCost:F1} ns");

            Assert.True(placeCost > 0 && bakeCost > 0 && findCost > 0 && denseCost > 0 && tilesCost > 0);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void OneshotDiskStages_CarryTheirPrice()
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, Size);
        var stamp = Stamp.Disk(4, 3);
        var w = World.GetContext(world);
        var v = StampCatalog.Get(stamp);
        try
        {
            var anchorX = 256 * 512;
            var anchorY = 256 * 512;
            var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
            long denseSum = 0;
            var denseCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                TileBake.EmitTurnedDense(dense, 512 & ~31, 512 & ~31, &p, 1);
                denseSum += dense[0] + dense[513];
            }, 20_000);
            output.WriteLine($"disk r4 emitTurnedDense one tile: {denseCost:F1} ns ({denseSum & 1})");

            long bakeSum = 0;
            var bakeCost = Best(i =>
            {
                TileBake.PlaceAnchored(anchorX, anchorY, 256, Size, v, (i * 7919) & 0xFFFF, 256,
                    Layer.Precision.Exact, out var p);
                if (!KernelFactory.TryGetTaps(w, 0, stamp, (i * 7919) & 0xFFFF, Layer.Precision.Exact, &p, out var taps))
                    Assert.Fail("bake failed");
                bakeSum += taps.Width + taps.Height;
            }, 20_000);
            output.WriteLine($"disk r4 tryGetTaps bake+insert: {bakeCost:F1} ns ({bakeSum & 1})");

            Assert.True(denseCost > 0 && bakeCost > 0);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void HugeBellRasterFallback_MatchesPerCellEvaluation()
    {
        var world = World.New();
        var stamp = Stamp.Bell(128, 128, 51);
        var variant = StampCatalog.Get(stamp);
        var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
        try
        {
            var angle = (ushort)((int)(1.1f / (2 * Math.PI) * 65536) & 0xFFFF);
            Assert.True(TileBake.Place(8.25f, 7.875f, 0f, 0f, 512, 32, variant, angle, 256,
                Layer.Precision.Exact, out var p));
            Assert.True(p.Kind == Gi.StampKind.Raster);
            for (var ty = -1; ty <= 1; ty++)
            for (var tx = -1; tx <= 1; tx++)
            {
                var tileX0 = tx << TileBake.TileBits;
                var tileY0 = ty << TileBake.TileBits;
                new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
                TileBake.EmitTurnedDense(dense, tileX0, tileY0, &p, -3);
                for (var y = 0; y < TileBake.TileSize; y++)
                for (var x = 0; x < TileBake.TileSize; x++)
                {
                    var cx = tileX0 + x;
                    var cy = tileY0 + y;
                    if (cx < p.X0 || cx >= p.X1 || cy < p.Y0 || cy >= p.Y1) continue;
                    var emitted = dense[y * TileBake.TileSize + x];
                    var oracle = TileBake.TurnedDenseAt(&p, cx, cy) * -3;
                    Assert.True(emitted == oracle, $"tile({tx},{ty}) cell({cx},{cy}) emitted {emitted} oracle {oracle}");
                }
            }
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }
}
