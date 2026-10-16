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
        NativeMemory.Clear(ring, (nuint)(RingRows * Side * sizeof(long)));
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
        NativeMemory.Clear(ring, (nuint)(RingRows * Side * sizeof(long)));
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
        => tap < taps ? Vector256.Create((long)weights[tap]).AsInt32() : Vector256<int>.Zero;

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

    private readonly int* origin;
    private readonly int* zeroRow;
    private readonly int* boxOutput;
    private readonly long* smoothOutput;
    private readonly long* ring;
    private readonly int* tent;
    private readonly int* bell;
    private readonly FloorCase[] cases;

    public FloorConvolveSuite()
    {
        var side = FloorConvolveKernel.Side;
        var pitch = FloorConvolveKernel.Pitch;
        var rows = FloorConvolveKernel.TopRows + side;
        var block = Arena.Alloc<int>((long)rows * pitch);
        origin = block + FloorConvolveKernel.TopRows * pitch + FloorConvolveKernel.Margin;
        zeroRow = Arena.Alloc<int>(side);
        boxOutput = Arena.Alloc<int>(FloorShape.Cells);
        smoothOutput = Arena.Alloc<long>(FloorShape.Cells);
        ring = Arena.Alloc<long>(FloorConvolveKernel.RingRows * side);
        tent = Arena.Alloc<int>(FloorConvolveKernel.MaxTaps);
        bell = Arena.Alloc<int>(FloorConvolveKernel.MaxTaps);
        for (var i = 0; i < TentWeights.Length; i++) tent[i] = TentWeights[i];
        for (var i = 0; i < BellWeights.Length; i++) bell[i] = BellWeights[i];
        var rng = new FloorRandom(0x54);
        for (var i = 0; i < FloorShape.Splats; i++) origin[rng.Below(side) * pitch + rng.Below(side)]++;
        cases =
        [
            new FloorCase("P4", "box4x4-int32", FloorShape.Cells,
                () => FloorConvolveKernel.Box(origin, boxOutput, zeroRow, FloorConvolveKernel.Side),
                () =>
                {
                    FloorConvolveKernel.Box(origin, boxOutput, zeroRow, FloorConvolveKernel.Side);
                    return FloorHash.Of(boxOutput, FloorShape.Cells);
                }),
            Smooth("tent6-int64", tent, TentWeights.Length),
            Smooth("bell8-int64", bell, BellWeights.Length),
            new FloorCase("P4", "box4x4-int32-L2-resident", BlockRows * Blocks * FloorConvolveKernel.Side,
                () =>
                {
                    for (var b = 0; b < Blocks; b++) FloorConvolveKernel.Box(origin, boxOutput, zeroRow, BlockRows);
                },
                () =>
                {
                    FloorConvolveKernel.Box(origin, boxOutput, zeroRow, BlockRows);
                    return FloorHash.Of(boxOutput, BlockRows * FloorConvolveKernel.Side);
                }),
            SmoothBlocks("tent6-int64-L2-resident", tent, TentWeights.Length),
            SmoothBlocks("bell8-int64-L2-resident", bell, BellWeights.Length),
        ];
    }

    public override string Id => "P4";

    public override IReadOnlyList<FloorCase> Cases => cases;

    public override void Receipt(FloorReceipts receipts)
    {
        var side = FloorConvolveKernel.Side;
        var pitch = FloorConvolveKernel.Pitch;
        var maxCount = 0;
        var total = 0L;
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            maxCount = Math.Max(maxCount, origin[y * pitch + x]);
            total += origin[y * pitch + x];
        }

        receipts.Check("floor-p4-histogram-holds-every-splat", total == FloorShape.Splats);
        receipts.Check("floor-p4-counts-keep-row-pass-inside-int32", maxCount < 2048);

        var boxExpected = new int[FloorShape.Cells];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var count = origin[y * pitch + x];
            if (count == 0) continue;
            for (var j = 0; j < FloorConvolveKernel.BoxSide && y + j < side; j++)
            for (var i = 0; i < FloorConvolveKernel.BoxSide && x + i < side; i++)
                boxExpected[(y + j) * side + x + i] += count;
        }

        new Span<int>(boxOutput, FloorShape.Cells).Clear();
        FloorConvolveKernel.BoxScalar(origin, boxOutput, side);
        receipts.Check("floor-p4-box-scalar-matches-direct-emission", Equal(boxOutput, boxExpected));
        if (Avx2.IsSupported)
        {
            new Span<int>(boxOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.BoxVector(origin, boxOutput, zeroRow, side);
            receipts.Check("floor-p4-box-vector-matches-direct-emission", Equal(boxOutput, boxExpected));
            new Span<int>(boxOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.Box(origin, boxOutput, zeroRow, BlockRows);
            receipts.Check("floor-p4-box-block-matches-leading-rows",
                new ReadOnlySpan<int>(boxOutput, BlockRows * side).SequenceEqual(boxExpected.AsSpan(0, BlockRows * side)));
        }
        else receipts.Skip("floor-p4-box-vector-matches-direct-emission", "avx2 unavailable");

        SmoothReceipt(receipts, "tent6", TentWeights, tent);
        SmoothReceipt(receipts, "bell8", BellWeights, bell);
    }

    private FloorCase Smooth(string variant, int* weights, int taps)
        => new("P4", variant, FloorShape.Cells,
            () => FloorConvolveKernel.Separable(origin, smoothOutput, ring, weights, taps, FloorConvolveKernel.Side),
            () =>
            {
                FloorConvolveKernel.Separable(origin, smoothOutput, ring, weights, taps, FloorConvolveKernel.Side);
                return FloorHash.Of(smoothOutput, FloorShape.Cells);
            });

    private FloorCase SmoothBlocks(string variant, int* weights, int taps)
        => new("P4", variant, BlockRows * Blocks * FloorConvolveKernel.Side,
            () =>
            {
                for (var b = 0; b < Blocks; b++)
                    FloorConvolveKernel.Separable(origin, smoothOutput, ring, weights, taps, BlockRows);
            },
            () =>
            {
                FloorConvolveKernel.Separable(origin, smoothOutput, ring, weights, taps, BlockRows);
                return FloorHash.Of(smoothOutput, BlockRows * FloorConvolveKernel.Side);
            });

    private void SmoothReceipt(FloorReceipts receipts, string name, int[] weights, int* native)
    {
        var side = FloorConvolveKernel.Side;
        var pitch = FloorConvolveKernel.Pitch;
        var expected = new long[FloorShape.Cells];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var count = origin[y * pitch + x];
            if (count == 0) continue;
            for (var j = 0; j < weights.Length && y + j < side; j++)
            for (var i = 0; i < weights.Length && x + i < side; i++)
                expected[(y + j) * side + x + i] += (long)count * weights[i] * weights[j];
        }

        new Span<long>(smoothOutput, FloorShape.Cells).Clear();
        FloorConvolveKernel.SeparableScalar(origin, smoothOutput, ring, native, weights.Length, side);
        receipts.Check($"floor-p4-{name}-scalar-matches-direct-emission", Equal(smoothOutput, expected));
        if (Avx2.IsSupported)
        {
            new Span<long>(smoothOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.SeparableVector(origin, smoothOutput, ring, native, weights.Length, side);
            receipts.Check($"floor-p4-{name}-vector-matches-direct-emission", Equal(smoothOutput, expected));
            new Span<long>(smoothOutput, FloorShape.Cells).Clear();
            FloorConvolveKernel.SeparableVector(origin, smoothOutput, ring, native, weights.Length, BlockRows);
            receipts.Check($"floor-p4-{name}-block-matches-leading-rows",
                new ReadOnlySpan<long>(smoothOutput, BlockRows * side).SequenceEqual(expected.AsSpan(0, BlockRows * side)));
        }
        else receipts.Skip($"floor-p4-{name}-vector-matches-direct-emission", "avx2 unavailable");
    }

    private static bool Equal(int* actual, int[] expected)
        => new ReadOnlySpan<int>(actual, FloorShape.Cells).SequenceEqual(expected);

    private static bool Equal(long* actual, long[] expected)
        => new ReadOnlySpan<long>(actual, FloorShape.Cells).SequenceEqual(expected);
}
