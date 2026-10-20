using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static unsafe class FloorConvolveKernel
{
    public const int Side = FloorShape.Size;
    public const int Margin = 16;
    public const int Pitch = Side + Margin;
    public const int TopRows = 4;
    public const int BoxSide = 4;
    public const int RingRows = 8;
    public const int MaxTaps = 8;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BoxVector(int* origin, int* output, int* zeroRow, int rows)
    {
        var fourth = Vector256.Create(3);
        var last = Vector256.Create(7);
        for (var y = 0; y < rows; y++)
        {
            var row = origin + y * Pitch;
            var above = row - BoxSide * Pitch;
            var previous = y == 0 ? zeroRow : output + (y - 1) * Side;
            var target = output + y * Side;
            var carry = Vector256<int>.Zero;
            for (var x = 0; x < Side; x += 8)
            {
                var d = Vector256.Load(row + x) - Vector256.Load(row + x - BoxSide)
                                                - Vector256.Load(above + x) + Vector256.Load(above + x - BoxSide);
                d += Avx2.ShiftLeftLogical128BitLane(d, 4);
                d += Avx2.ShiftLeftLogical128BitLane(d, 8);
                d += Avx2.Blend(Vector256<int>.Zero, Avx2.PermuteVar8x32(d, fourth), 0xF0);
                d += carry;
                carry = Avx2.PermuteVar8x32(d, last);
                (d + Vector256.Load(previous + x)).Store(target + x);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BoxScalar(int* origin, int* output, int rows)
    {
        for (var y = 0; y < rows; y++)
        {
            var row = origin + y * Pitch;
            var above = row - BoxSide * Pitch;
            var target = output + y * Side;
            var carry = 0;
            for (var x = 0; x < Side; x++)
            {
                carry += row[x] - row[x - BoxSide] - above[x] + above[x - BoxSide];
                target[x] = carry + (y == 0 ? 0 : target[x - Side]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void SeparableVector(int* origin, long* output, long* ring, int* weights, int taps, int rows)
    {
        NativeMemory.Clear(ring, RingRows * Side * sizeof(long));
        var w0 = BroadcastWeight(weights, 0, taps);
        var w1 = BroadcastWeight(weights, 1, taps);
        var w2 = BroadcastWeight(weights, 2, taps);
        var w3 = BroadcastWeight(weights, 3, taps);
        var w4 = BroadcastWeight(weights, 4, taps);
        var w5 = BroadcastWeight(weights, 5, taps);
        var w6 = BroadcastWeight(weights, 6, taps);
        var w7 = BroadcastWeight(weights, 7, taps);
        var wide = taps == MaxTaps;
        for (var y = 0; y < rows; y++)
        {
            var h = origin + y * Pitch;
            var row = ring + (y & (RingRows - 1)) * Side;
            for (var x = 0; x < Side; x += 4)
            {
                var acc = Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x).AsInt32(), w0)
                          + Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 1).AsInt32(), w1)
                          + Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 2).AsInt32(), w2)
                          + Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 3).AsInt32(), w3)
                          + Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 4).AsInt32(), w4)
                          + Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 5).AsInt32(), w5);
                if (wide)
                    acc += Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 6).AsInt32(), w6)
                           + Avx2.Multiply(Avx2.ConvertToVector256Int64(h + x - 7).AsInt32(), w7);
                acc.Store(row + x);
            }

            var r0 = ring + (y & (RingRows - 1)) * Side;
            var r1 = ring + ((y - 1) & (RingRows - 1)) * Side;
            var r2 = ring + ((y - 2) & (RingRows - 1)) * Side;
            var r3 = ring + ((y - 3) & (RingRows - 1)) * Side;
            var r4 = ring + ((y - 4) & (RingRows - 1)) * Side;
            var r5 = ring + ((y - 5) & (RingRows - 1)) * Side;
            var r6 = ring + ((y - 6) & (RingRows - 1)) * Side;
            var r7 = ring + ((y - 7) & (RingRows - 1)) * Side;
            var target = output + y * Side;
            for (var x = 0; x < Side; x += 4)
            {
                var acc = Avx2.Multiply(Vector256.Load(r0 + x).AsInt32(), w0)
                          + Avx2.Multiply(Vector256.Load(r1 + x).AsInt32(), w1)
                          + Avx2.Multiply(Vector256.Load(r2 + x).AsInt32(), w2)
                          + Avx2.Multiply(Vector256.Load(r3 + x).AsInt32(), w3)
                          + Avx2.Multiply(Vector256.Load(r4 + x).AsInt32(), w4)
                          + Avx2.Multiply(Vector256.Load(r5 + x).AsInt32(), w5);
                if (wide)
                    acc += Avx2.Multiply(Vector256.Load(r6 + x).AsInt32(), w6)
                           + Avx2.Multiply(Vector256.Load(r7 + x).AsInt32(), w7);
                acc.Store(target + x);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void SeparableScalar(int* origin, long* output, long* ring, int* weights, int taps, int rows)
    {
        NativeMemory.Clear(ring, RingRows * Side * sizeof(long));
        for (var y = 0; y < rows; y++)
        {
            var h = origin + y * Pitch;
            var row = ring + (y & (RingRows - 1)) * Side;
            for (var x = 0; x < Side; x++)
            {
                var sum = 0L;
                for (var t = 0; t < taps; t++) sum += (long)h[x - t] * weights[t];
                row[x] = sum;
            }

            var target = output + y * Side;
            for (var x = 0; x < Side; x++)
            {
                var sum = 0L;
                for (var t = 0; t < taps; t++) sum += ring[((y - t) & (RingRows - 1)) * Side + x] * weights[t];
                target[x] = sum;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> BroadcastWeight(int* weights, int tap, int taps)
    {
        return tap < taps ? Vector256.Create((long)weights[tap]).AsInt32() : Vector256<int>.Zero;
    }

    public static void Separable(int* origin, long* output, long* ring, int* weights, int taps, int rows)
    {
        if (Avx2.IsSupported) SeparableVector(origin, output, ring, weights, taps, rows);
        else SeparableScalar(origin, output, ring, weights, taps, rows);
    }

    public static void Box(int* origin, int* output, int* zeroRow, int rows)
    {
        if (Avx2.IsSupported) BoxVector(origin, output, zeroRow, rows);
        else BoxScalar(origin, output, rows);
    }
}

internal sealed unsafe class FloorConvolveSuite : FloorSuite
{
    private const int BlockRows = 64;
    private const int Blocks = FloorConvolveKernel.Side / BlockRows;

    private static readonly int[] TentWeights = [87381, 174763, 262144, 262144, 174763, 87381];
    private static readonly int[] BellWeights = [16384, 81920, 180224, 245760, 245760, 180224, 81920, 16384];
    private readonly int* _bell;
    private readonly int* _boxOutput;
    private readonly FloorCase[] _cases;

    private readonly int* _origin;
    private readonly long* _ring;
    private readonly long* _smoothOutput;
    private readonly int* _tent;
    private readonly int* _zeroRow;

    public FloorConvolveSuite()
    {
        var side = FloorConvolveKernel.Side;
        var pitch = FloorConvolveKernel.Pitch;
        var rows = FloorConvolveKernel.TopRows + side;
        var block = Arena.Alloc<int>((long)rows * pitch);
        _origin = block + FloorConvolveKernel.TopRows * pitch + FloorConvolveKernel.Margin;
        _zeroRow = Arena.Alloc<int>(side);
        _boxOutput = Arena.Alloc<int>(FloorShape.Cells);
        _smoothOutput = Arena.Alloc<long>(FloorShape.Cells);
        _ring = Arena.Alloc<long>(FloorConvolveKernel.RingRows * side);
        _tent = Arena.Alloc<int>(FloorConvolveKernel.MaxTaps);
        _bell = Arena.Alloc<int>(FloorConvolveKernel.MaxTaps);
        for (var i = 0; i < TentWeights.Length; i++) _tent[i] = TentWeights[i];
        for (var i = 0; i < BellWeights.Length; i++) _bell[i] = BellWeights[i];
        var rng = new FloorRandom(0x54);
        for (var i = 0; i < FloorShape.Splats; i++) _origin[rng.Below(side) * pitch + rng.Below(side)]++;
        _cases =
        [
            new FloorCase("P4", "box4x4-int32", FloorShape.Cells,
                () => FloorConvolveKernel.Box(_origin, _boxOutput, _zeroRow, FloorConvolveKernel.Side),
                () =>
                {
                    FloorConvolveKernel.Box(_origin, _boxOutput, _zeroRow, FloorConvolveKernel.Side);
                    return FloorHash.Of(_boxOutput, FloorShape.Cells);
                }),
            Smooth("tent6-int64", _tent, TentWeights.Length),
            Smooth("bell8-int64", _bell, BellWeights.Length),
            new FloorCase("P4", "box4x4-int32-L2-resident", BlockRows * Blocks * FloorConvolveKernel.Side,
                () =>
                {
                    for (var b = 0; b < Blocks; b++) FloorConvolveKernel.Box(_origin, _boxOutput, _zeroRow, BlockRows);
                },
                () =>
                {
                    FloorConvolveKernel.Box(_origin, _boxOutput, _zeroRow, BlockRows);
                    return FloorHash.Of(_boxOutput, BlockRows * FloorConvolveKernel.Side);
                }),
            SmoothBlocks("tent6-int64-L2-resident", _tent, TentWeights.Length),
            SmoothBlocks("bell8-int64-L2-resident", _bell, BellWeights.Length)
        ];
    }

    public override string Id => "P4";

    public override IReadOnlyList<FloorCase> Cases => _cases;

    public override void Receipt(FloorReceipts receipts)
    {
        var side = FloorConvolveKernel.Side;
        var pitch = FloorConvolveKernel.Pitch;
        var maxCount = 0;
        var total = 0L;
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            maxCount = Math.Max(maxCount, _origin[y * pitch + x]);
            total += _origin[y * pitch + x];
        }

        receipts.Check("floor-p4-histogram-holds-every-splat", total == FloorShape.Splats);
        receipts.Check("floor-p4-counts-keep-row-pass-inside-int32", maxCount < 2048);

        var boxExpected = new int[FloorShape.Cells];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var count = _origin[y * pitch + x];
            if (count == 0) continue;
            for (var j = 0; j < FloorConvolveKernel.BoxSide && y + j < side; j++)
            for (var i = 0; i < FloorConvolveKernel.BoxSide && x + i < side; i++)
                boxExpected[(y + j) * side + x + i] += count;
        }

        new Span<int>(_boxOutput, FloorShape.Cells).Clear();
        FloorConvolveKernel.BoxScalar(_origin, _boxOutput, side);
        receipts.Check("floor-p4-box-scalar-matches-direct-emission", Equal(_boxOutput, boxExpected));
        if (Avx2.IsSupported)
        {
            new Span<int>(_boxOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.BoxVector(_origin, _boxOutput, _zeroRow, side);
            receipts.Check("floor-p4-box-vector-matches-direct-emission", Equal(_boxOutput, boxExpected));
            new Span<int>(_boxOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.Box(_origin, _boxOutput, _zeroRow, BlockRows);
            receipts.Check("floor-p4-box-block-matches-leading-rows",
                new ReadOnlySpan<int>(_boxOutput, BlockRows * side).SequenceEqual(
                    boxExpected.AsSpan(0, BlockRows * side)));
        }
        else
        {
            receipts.Skip("floor-p4-box-vector-matches-direct-emission", "avx2 unavailable");
        }

        SmoothReceipt(receipts, "tent6", TentWeights, _tent);
        SmoothReceipt(receipts, "bell8", BellWeights, _bell);
    }

    private FloorCase Smooth(string variant, int* weights, int taps)
    {
        return new FloorCase("P4", variant, FloorShape.Cells,
            () => FloorConvolveKernel.Separable(_origin, _smoothOutput, _ring, weights, taps, FloorConvolveKernel.Side),
            () =>
            {
                FloorConvolveKernel.Separable(_origin, _smoothOutput, _ring, weights, taps, FloorConvolveKernel.Side);
                return FloorHash.Of(_smoothOutput, FloorShape.Cells);
            });
    }

    private FloorCase SmoothBlocks(string variant, int* weights, int taps)
    {
        return new FloorCase("P4", variant, BlockRows * Blocks * FloorConvolveKernel.Side,
            () =>
            {
                for (var b = 0; b < Blocks; b++)
                    FloorConvolveKernel.Separable(_origin, _smoothOutput, _ring, weights, taps, BlockRows);
            },
            () =>
            {
                FloorConvolveKernel.Separable(_origin, _smoothOutput, _ring, weights, taps, BlockRows);
                return FloorHash.Of(_smoothOutput, BlockRows * FloorConvolveKernel.Side);
            });
    }

    private void SmoothReceipt(FloorReceipts receipts, string name, int[] weights, int* native)
    {
        var side = FloorConvolveKernel.Side;
        var pitch = FloorConvolveKernel.Pitch;
        var expected = new long[FloorShape.Cells];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var count = _origin[y * pitch + x];
            if (count == 0) continue;
            for (var j = 0; j < weights.Length && y + j < side; j++)
            for (var i = 0; i < weights.Length && x + i < side; i++)
                expected[(y + j) * side + x + i] += (long)count * weights[i] * weights[j];
        }

        new Span<long>(_smoothOutput, FloorShape.Cells).Clear();
        FloorConvolveKernel.SeparableScalar(_origin, _smoothOutput, _ring, native, weights.Length, side);
        receipts.Check($"floor-p4-{name}-scalar-matches-direct-emission", Equal(_smoothOutput, expected));
        if (Avx2.IsSupported)
        {
            new Span<long>(_smoothOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.SeparableVector(_origin, _smoothOutput, _ring, native, weights.Length, side);
            receipts.Check($"floor-p4-{name}-vector-matches-direct-emission", Equal(_smoothOutput, expected));
            new Span<long>(_smoothOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.SeparableVector(_origin, _smoothOutput, _ring, native, weights.Length, BlockRows);
            receipts.Check($"floor-p4-{name}-block-matches-leading-rows",
                new ReadOnlySpan<long>(_smoothOutput, BlockRows * side).SequenceEqual(expected.AsSpan(0,
                    BlockRows * side)));
        }
        else
        {
            receipts.Skip($"floor-p4-{name}-vector-matches-direct-emission", "avx2 unavailable");
        }
    }

    private static bool Equal(int* actual, int[] expected)
    {
        return new ReadOnlySpan<int>(actual, FloorShape.Cells).SequenceEqual(expected);
    }

    private static bool Equal(long* actual, long[] expected)
    {
        return new ReadOnlySpan<long>(actual, FloorShape.Cells).SequenceEqual(expected);
    }
}
