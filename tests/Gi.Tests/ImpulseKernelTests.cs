using System.Numerics;
using Xunit;

namespace Gi.Tests;

public sealed unsafe class ImpulseKernelTests
{
    private const int Cells = TileBake.TileSize * TileBake.TileSize;

    [Fact]
    public void Axis_impulses_equal_integer_profiles_across_phases_supports_and_windows()
    {
        int[] extents = [1, 127, 255, 256, 257, 511, 512, 513, 769, 1793, 4096, 16383, 32767, 65534, (1 << 23) - 2];
        int[] phases = [0, 1, 63, 127, 128, 193, 254, 255];
        int[] origins = [-41, -1, 0, 29];
        var impulses = stackalloc AxisImpulse[6];
        var values = stackalloc long[TileBake.TileSize];
        foreach (var kind in new[] { StampKind.Tent, StampKind.Bell })
        foreach (var extent in extents)
        {
            if (kind == StampKind.Bell && extent >= 65536) continue;
            foreach (var phase in phases)
            foreach (var origin in origins)
            {
                var axis = TileBake.Axis(kind, origin, phase, extent);
                int[] starts = [origin - 32, origin, origin + 1, axis.First - 1, axis.First, axis.Last - 31, axis.Last, axis.Last + 1];
                foreach (var start in starts)
                {
                    new Span<long>(values, TileBake.TileSize).Clear();
                    var count = TileBake.CollectImpulses(axis, start, impulses);
                    Assert.InRange(count, 0, 6);
                    for (var i = 0; i < count; i++) values[impulses[i].Offset] += impulses[i].Weight;
                    for (var pass = 0; pass < (axis.Bell ? 3 : 2); pass++)
                    for (var i = 1; i < TileBake.TileSize; i++) values[i] += values[i - 1];
                    for (var i = 0; i < TileBake.TileSize; i++)
                    {
                        var cell = start + i;
                        var expected = cell < axis.First || cell > axis.Last ? 0 : axis.Weight(cell);
                        Assert.True(expected == values[i], $"{kind} extent={extent} phase={phase} origin={origin} start={start} cell={cell}: {expected} != {values[i]}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Separable_impulses_equal_direct_products_including_wrapping_arithmetic_and_retraction()
    {
        var direct = stackalloc long[Cells];
        var impulses = stackalloc long[Cells];
        var integrated = stackalloc long[Cells];
        int[] extents = [1, 255, 256, 513, 2047, 4096, 8193, 16384, 65534, (1 << 23) - 2];
        var random = new Random(407);
        for (var trial = 0; trial < 1200; trial++)
        {
            var kind = (trial & 1) == 0 ? StampKind.Tent : StampKind.Bell;
            var ex = extents[random.Next(kind == StampKind.Bell ? extents.Length - 1 : extents.Length)];
            var ey = extents[random.Next(kind == StampKind.Bell ? extents.Length - 1 : extents.Length)];
            var px = random.Next(-40, 41);
            var py = random.Next(-40, 41);
            var fx = random.Next(256);
            var fy = random.Next(256);
            var ax = TileBake.Axis(kind, px, fx, ex);
            var ay = TileBake.Axis(kind, py, fy, ey);
            var tx = trial % 3 == 0 ? ax.Last - 31 : trial % 3 == 1 ? ax.First - 1 : (ax.First + ax.Last) / 2 - 15;
            var ty = trial % 4 == 0 ? ay.Last - 31 : trial % 4 == 1 ? ay.First - 1 : (ay.First + ay.Last) / 2 - 15;
            var v = new StampVariant { Kind = kind, Constant = (sbyte)random.Next(-128, 128) };
            var gain = trial % 5 == 0 ? int.MaxValue : random.Next(-16, 17);
            new Span<long>(direct, Cells).Clear();
            new Span<long>(impulses, Cells).Clear();
            new Span<long>(integrated, Cells).Clear();
            if (kind == StampKind.Tent) TileBake.EmitTent(direct, tx, ty, px, py, fx, fy, ex, ey, &v, gain);
            else TileBake.EmitBell(direct, tx, ty, px, py, fx, fy, ex, ey, &v, gain);
            TileBake.EmitSmoothImpulses(impulses, kind, tx, ty, px, py, fx, fy, ex, ey, &v, gain);
            TileBake.IntegrateSmooth(impulses, integrated, kind);
            for (var i = 0; i < Cells; i++)
                Assert.True(direct[i] == integrated[i], $"trial={trial}, kind={kind}, extent={ex},{ey}, cell={i}: {direct[i]} != {integrated[i]}");
            new Span<long>(impulses, Cells).Clear();
            TileBake.EmitSmoothImpulses(impulses, kind, tx, ty, px, py, fx, fy, ex, ey, &v, gain);
            TileBake.EmitSmoothImpulses(impulses, kind, tx, ty, px, py, fx, fy, ex, ey, &v, -gain);
            for (var i = 0; i < Cells; i++) Assert.Equal(0, impulses[i]);
        }
    }

    [Fact]
    public void Adaptive_transient_planes_match_direct_emitters_for_every_shape()
    {
        var variants = MakeVariants();
        var difference = stackalloc long[TileBake.DiffPitch * TileBake.DiffRows];
        var dense = stackalloc long[Cells];
        var tent = stackalloc long[Cells];
        var bell = stackalloc long[Cells];
        var ti = stackalloc long[Cells];
        var bi = stackalloc long[Cells];
        var oldDifference = stackalloc int[TileBake.DiffPitch * TileBake.DiffRows];
        var oldDense = stackalloc int[Cells];
        var oldTent = stackalloc long[Cells];
        var oldBell = stackalloc long[Cells];
        var previous = stackalloc int[TileBake.TileSize];
        var actual = stackalloc short[Cells];
        var expected = stackalloc short[Cells];
        try
        {
            foreach (var scale in new[] { 64, 256, 1024, 4096 })
            foreach (var angle in new[] { 0, 7937, 16384, 61111 })
            {
                new Span<long>(difference, TileBake.DiffPitch * TileBake.DiffRows).Clear();
                new Span<long>(dense, Cells).Clear();
                new Span<long>(tent, Cells).Clear();
                new Span<long>(bell, Cells).Clear();
                new Span<long>(ti, Cells).Clear();
                new Span<long>(bi, Cells).Clear();
                new Span<int>(oldDifference, TileBake.DiffPitch * TileBake.DiffRows).Clear();
                new Span<int>(oldDense, Cells).Clear();
                new Span<long>(oldTent, Cells).Clear();
                new Span<long>(oldBell, Cells).Clear();
                new Span<int>(previous, TileBake.TileSize).Clear();
                for (var i = 0; i < variants.Length; i++)
                {
                    Assert.True(TileBake.Place(30.375f + i * .375f, 33.996f - i * .25f, 0, 0, scale, 256,
                        StampCatalog.Get(variants[i]), angle, 256, out var p));
                    var gain = (i & 1) == 0 ? 16 : -11;
                    TileBake.EmitTransient(&p, gain, 32, 32, difference, dense, tent, bell, ti, bi);
                    EmitOld(&p, gain, 32, 32, oldDifference, oldDense, oldTent, oldBell);
                }
                var expectedSum = 0L;
                var actualSum = 0L;
                var expectedMax = (short)0;
                var actualMax = (short)0;
                var anyExpected = TileBake.Resolve(oldDifference, oldDense, previous, oldTent, oldBell, expected, &expectedSum, &expectedMax);
                var anyActual = TileBake.ResolveTransient(difference, dense, tent, bell, ti, bi, actual, &actualSum, &actualMax);
                Assert.Equal(anyExpected, anyActual);
                Assert.Equal(expectedSum, actualSum);
                Assert.Equal(expectedMax, actualMax);
                for (var i = 0; i < Cells; i++)
                    Assert.True(expected[i] == actual[i], $"scale={scale}, angle={angle}, cell={i}: {expected[i]} != {actual[i]}");
                for (var i = 0; i < Cells; i++)
                {
                    Assert.Equal(oldTent[i], tent[i]);
                    Assert.Equal(oldBell[i], bell[i]);
                }
            }
        }
        finally
        {
            foreach (var variant in variants) Assert.True(Stamp.Free(variant));
        }
    }

    [Fact]
    public void Wide_smooth_impulses_preserve_piles_above_int64_and_exact_cancellation()
    {
        var direct = stackalloc WideSum[Cells];
        var impulses = stackalloc WideSum[Cells];
        var expected = stackalloc long[Cells];
        var output = stackalloc short[Cells];
        foreach (var kind in new[] { StampKind.Tent, StampKind.Bell })
        foreach (var remaining in new[] { 1, -1, 0, 30000, -30000 })
        {
            new Span<WideSum>(direct, Cells).Clear();
            new Span<WideSum>(impulses, Cells).Clear();
            new Span<long>(expected, Cells).Clear();
            var v = new StampVariant { Kind = kind, Width = 16, Height = 16, OriginQ8X = -2048, OriginQ8Y = -2048, Constant = -128 };
            Assert.True(TileBake.Place(16.5f, 16.5f, 0, 0, 256, 32, &v, 0, 256, out var p));
            var positive = remaining < 0 ? 30000 : 30000 + remaining;
            var negative = remaining < 0 ? 30000 - remaining : 30000;
            for (var i = 0; i < positive; i++)
                TileBake.EmitTransientWide(&p, 16, 0, 0, null, null,
                    kind == StampKind.Tent ? direct : null, kind == StampKind.Bell ? direct : null,
                    kind == StampKind.Tent ? impulses : null, kind == StampKind.Bell ? impulses : null);
            for (var i = 0; i < negative; i++)
                TileBake.EmitTransientWide(&p, -16, 0, 0, null, null,
                    kind == StampKind.Tent ? direct : null, kind == StampKind.Bell ? direct : null,
                    kind == StampKind.Tent ? impulses : null, kind == StampKind.Bell ? impulses : null);
            if (kind == StampKind.Tent) TileBake.EmitTent(expected, 0, 0, p.Px, p.Py, p.Fx, p.Fy, p.ExtentX, p.ExtentY, &v, 16);
            else TileBake.EmitBell(expected, 0, 0, p.Px, p.Py, p.Fx, p.Fy, p.ExtentX, p.ExtentY, &v, 16);
            var sum = 0L;
            var max = (short)0;
            var any = TileBake.ResolveTransientWide(null, null,
                kind == StampKind.Tent ? direct : null, kind == StampKind.Bell ? direct : null,
                kind == StampKind.Tent ? impulses : null, kind == StampKind.Bell ? impulses : null,
                output, &sum, &max);
            var expectedSum = 0L;
            var expectedMax = short.MinValue;
            for (var i = 0; i < Cells; i++)
            {
                var raw = (Int128)expected[i] * remaining;
                Assert.Equal((ulong)raw, direct[i].Low);
                Assert.Equal((long)(raw >> 64), direct[i].High);
                var rounded = RoundWide(raw);
                Assert.Equal(rounded, direct[i].RoundQ40());
                var cell = (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
                Assert.Equal(cell, output[i]);
                expectedSum += cell;
                expectedMax = Math.Max(expectedMax, cell);
            }
            Assert.Equal(remaining != 0, any);
            Assert.Equal(expectedSum, sum);
            Assert.Equal(expectedMax, max);
        }
    }

    [Fact]
    public void Wide_addition_and_rounding_match_arbitrary_precision_near_signed_boundaries()
    {
        var values = new[] { long.MaxValue, long.MinValue, 549755813888L, -549755813888L, 1099511627776L, -1099511627776L, 0, 1, -1 };
        var total = default(WideSum);
        var oracle = BigInteger.Zero;
        for (var round = 0; round < 2000; round++)
        foreach (var value in values)
        {
            total.Add(value);
            oracle += value;
            Assert.Equal((ulong)(oracle & ulong.MaxValue), total.Low);
            Assert.Equal((long)(oracle >> 64), total.High);
            var magnitude = (BigInteger.Abs(oracle) + (BigInteger.One << 39)) >> 40;
            Assert.Equal((long)(oracle.Sign < 0 ? -magnitude : magnitude), total.RoundQ40());
        }
        foreach (var value in values)
        {
            var magnitude = ((BigInteger.Abs(value) + (BigInteger.One << 39)) >> 40);
            Assert.Equal((int)(value < 0 ? -magnitude : magnitude), TileBake.RoundQ40(value));
        }
    }

    [Fact]
    public void Transient_core_saturates_after_int64_cancellation_and_rounds_planes_separately()
    {
        var difference = stackalloc long[TileBake.DiffPitch * TileBake.DiffRows];
        var dense = stackalloc long[Cells];
        var tent = stackalloc long[Cells];
        var bell = stackalloc long[Cells];
        var output = stackalloc short[Cells];
        var rowSums = stackalloc int[TileBake.TileSize];
        var rowMax = stackalloc short[TileBake.TileSize];
        var minimum = (short)0;
        new Span<long>(difference, TileBake.DiffPitch * TileBake.DiffRows).Clear();
        new Span<long>(dense, Cells).Clear();
        new Span<long>(tent, Cells).Clear();
        new Span<long>(bell, Cells).Clear();
        difference[0] = 4_000_000_000;
        new Span<long>(dense, Cells).Fill(-3_999_999_999);
        tent[0] = (1L << 39) - 1;
        bell[0] = (1L << 39) - 1;
        tent[1] = 1L << 39;
        bell[1] = 1L << 39;
        tent[2] = -(1L << 39);
        bell[2] = -(1L << 39);
        var sum = 0L;
        var max = (short)0;
        Assert.True(TileBake.ResolveTransient(difference, dense, tent, bell, null, null, output, &sum, &max, rowSums, rowMax, &minimum));
        Assert.Equal(1, output[0]);
        Assert.Equal(3, output[1]);
        Assert.Equal(-1, output[2]);
        Assert.Equal(Cells, sum);
        Assert.Equal(3, max);
        Assert.Equal(-1, minimum);
        for (var y = 0; y < TileBake.TileSize; y++)
        {
            Assert.Equal(TileBake.TileSize, rowSums[y]);
            Assert.Equal(y == 0 ? 3 : 1, rowMax[y]);
        }
    }

    [Fact]
    public void Warm_transient_emit_and_resolve_allocate_zero_managed_bytes()
    {
        var dense = stackalloc long[Cells];
        var tent = stackalloc long[Cells];
        var impulses = stackalloc long[Cells];
        var output = stackalloc short[Cells];
        var v = new StampVariant { Kind = StampKind.Tent, Width = 16, Height = 16, OriginQ8X = -2048, OriginQ8Y = -2048, Constant = 127 };
        Assert.True(TileBake.Place(16.375f, 16.5f, 0, 0, 256, 32, &v, 0, 256, out var p));
        var sum = 0L;
        var max = (short)0;
        new Span<long>(dense, Cells).Clear();
        for (var frame = 0; frame < 10; frame++)
        {
            new Span<long>(tent, Cells).Clear();
            new Span<long>(impulses, Cells).Clear();
            TileBake.EmitTransient(&p, 16, 0, 0, null, dense, tent, null, impulses, null);
            TileBake.ResolveTransient(null, dense, tent, null, impulses, null, output, &sum, &max);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 100; frame++)
        {
            new Span<long>(tent, Cells).Clear();
            new Span<long>(impulses, Cells).Clear();
            TileBake.EmitTransient(&p, 16, 0, 0, null, dense, tent, null, impulses, null);
            TileBake.ResolveTransient(null, dense, tent, null, impulses, null, output, &sum, &max);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sum > 0);
        Assert.True(max > 0);
    }

    [Fact]
    public void Transient_vector_rounding_matches_scalar_at_signed_limits_and_q40_ties()
    {
        var dense = stackalloc long[Cells];
        var tent = stackalloc long[Cells];
        var bell = stackalloc long[Cells];
        var output = stackalloc short[Cells];
        var random = new Random(654091);
        long[] limits = [long.MinValue, long.MinValue + 1, long.MaxValue, long.MaxValue - 1,
            -(1L << 40), -(1L << 39) - 1, -(1L << 39), -(1L << 39) + 1, -1, 0, 1,
            (1L << 39) - 1, 1L << 39, (1L << 39) + 1, 1L << 40];
        for (var i = 0; i < Cells; i++)
        {
            tent[i] = i < limits.Length ? limits[i] : random.NextInt64(long.MinValue, long.MaxValue);
            bell[i] = i < limits.Length ? limits[limits.Length - 1 - i] : random.NextInt64(long.MinValue, long.MaxValue);
            dense[i] = -(long)TileBake.RoundQ40(tent[i]) - TileBake.RoundQ40(bell[i]) + i % 127 - 63;
        }
        var sum = 0L;
        var max = (short)0;
        Assert.True(TileBake.ResolveTransient(null, dense, tent, bell, null, null, output, &sum, &max));
        var expectedSum = 0L;
        for (var i = 0; i < Cells; i++)
        {
            Assert.Equal(i % 127 - 63, output[i]);
            expectedSum += i % 127 - 63;
        }
        Assert.Equal(expectedSum, sum);
        Assert.Equal(63, max);
    }

    private static long RoundWide(Int128 value)
    {
        var magnitude = ((value < 0 ? -value : value) + ((Int128)1 << 39)) >> 40;
        return (long)(value < 0 ? -magnitude : magnitude);
    }

    private static byte[] MakeVariants()
    {
        var raster = new sbyte[8 * 7];
        for (var i = 0; i < raster.Length; i++) raster[i] = (sbyte)(i * 31 % 256 - 128);
        return [Stamp.Box(9, 7, 127), Stamp.New(raster, 8, 7), Stamp.Tent(16, 9, -128), Stamp.Tent(3, 2, 90),
            Stamp.Bell(16, 11, 127), Stamp.Bell(2, 3, -128), Stamp.Disk(7, 100), Stamp.Cone(11, -90, 100), Stamp.Dome(8, 70, 110)];
    }

    private static void EmitOld(Placement* p, int gain, int tileX0, int tileY0,
        int* difference, int* dense, long* tent, long* bell)
    {
        switch (p->Plane)
        {
            case Plane.Difference:
                TileBake.EmitBox(difference, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
                break;
            case Plane.Dense:
                if (p->Turned) TileBake.EmitTurnedDense(dense, tileX0, tileY0, p, gain);
                else TileBake.EmitRaster(dense, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->X1, p->Y1, p->Sampling, p->V, gain);
                break;
            case Plane.Tent:
                if (p->Turned) TileBake.EmitTurnedSmooth(tent, tileX0, tileY0, p, gain);
                else TileBake.EmitTent(tent, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
                break;
            case Plane.Bell:
                if (p->Turned) TileBake.EmitTurnedSmooth(bell, tileX0, tileY0, p, gain);
                else TileBake.EmitBell(bell, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
                break;
        }
    }
}
