using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static unsafe class FloorQuantizeKernel
{
    public static void Run(float* xs, float* ys, int* output, int count, float originX, float originY)
    {
        var done = Avx2.IsSupported ? RunVector(xs, ys, output, count, originX, originY) : 0;
        RunScalar(xs, ys, output, done, count, originX, originY);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int RunVector(float* xs, float* ys, int* output, int count, float originX, float originY)
    {
        var ox = Vector256.Create(originX);
        var oy = Vector256.Create(originY);
        var scale = Vector256.Create(256f);
        var outside = Vector256.Create(~(FloorShape.Size - 1));
        var low = Vector256.Create(FloorShape.TileSize - 1);
        var high = Vector256.Create(~(FloorShape.TileSize - 1));
        var i = 0;
        for (; i + 8 <= count; i += 8)
        {
            var cx = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((Vector256.Load(xs + i) - ox) * scale)), 8);
            var cy = Vector256.ShiftRightArithmetic(
                Avx.ConvertToVector256Int32WithTruncation(Vector256.Floor((Vector256.Load(ys + i) - oy) * scale)), 8);
            var inside = Vector256.Equals((cx | cy) & outside, Vector256<int>.Zero);
            var cell = Vector256.ShiftLeft(cy & high, 10)
                       | Vector256.ShiftLeft((cy & low) | (cx & high), 5)
                       | (cx & low);
            (cell | ~inside).Store(output + i);
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void RunScalar(float* xs, float* ys, int* output, int from, int to, float originX, float originY)
    {
        for (var i = from; i < to; i++)
        {
            var cx = (int)MathF.Floor((xs[i] - originX) * 256f) >> 8;
            var cy = (int)MathF.Floor((ys[i] - originY) * 256f) >> 8;
            output[i] = ((cx | cy) & ~(FloorShape.Size - 1)) != 0
                ? -1
                : ((cy >> 5) << 15) | ((cx >> 5) << 10) | ((cy & 31) << 5) | (cx & 31);
        }
    }

    public static int Reference(float x, float y, float originX, float originY)
    {
        var qx = (long)Math.Floor(((double)x - originX) * 256.0);
        var qy = (long)Math.Floor(((double)y - originY) * 256.0);
        var limit = (long)FloorShape.Size * 256;
        if (qx < 0 || qy < 0 || qx >= limit || qy >= limit) return -1;
        return FloorShape.Paged((int)(qx / 256), (int)(qy / 256), FloorShape.TilesPerSide);
    }
}

internal sealed unsafe class FloorQuantizeSuite : FloorSuite
{
    private const float OriginX = 0f;
    private const float OriginY = 0f;
    private const int Block = 32768;
    private const int Repeats = 30;

    private static readonly float[] EdgeValues =
    [
        0f, -0.0f, -1e-6f, 31.999998f, 32f, 1023.99994f, 1024f, -0.0001f,
        1023.5f, 0.5f, 511.99997f, 512f, 31.5f, 1000f, -16f, 1039.99f
    ];

    private readonly FloorCase[] _cases;
    private readonly int* _expected;
    private readonly int* _output;

    private readonly float* _xs;
    private readonly float* _ys;

    public FloorQuantizeSuite()
    {
        const int count = FloorShape.Splats;
        _xs = Arena.Alloc<float>(count);
        _ys = Arena.Alloc<float>(count);
        _output = Arena.Alloc<int>(count);
        _expected = Arena.Alloc<int>(count);
        var rng = new FloorRandom(0x51);
        for (var i = 0; i < count; i++)
        {
            _xs[i] = -16f + 1056f * rng.Unit();
            _ys[i] = -16f + 1056f * rng.Unit();
        }

        for (var i = 0; i < EdgeValues.Length; i++)
        {
            _xs[i] = EdgeValues[i];
            _ys[i] = EdgeValues[(i + 5) % EdgeValues.Length];
        }

        for (var i = 0; i < count; i++) _expected[i] = FloorQuantizeKernel.Reference(_xs[i], _ys[i], OriginX, OriginY);
        _cases =
        [
            new FloorCase("P1", "quantize-1M", count,
                () => FloorQuantizeKernel.Run(_xs, _ys, _output, count, OriginX, OriginY),
                () =>
                {
                    FloorQuantizeKernel.Run(_xs, _ys, _output, count, OriginX, OriginY);
                    return FloorHash.Of(_output, count);
                }),
            new FloorCase("P1", "quantize-L2-resident", Block * Repeats,
                () =>
                {
                    for (var r = 0; r < Repeats; r++)
                        FloorQuantizeKernel.Run(_xs, _ys, _output, Block, OriginX, OriginY);
                },
                () =>
                {
                    FloorQuantizeKernel.Run(_xs, _ys, _output, Block, OriginX, OriginY);
                    return FloorHash.Of(_output, Block);
                })
        ];
    }

    public override string Id => "P1";

    public override IReadOnlyList<FloorCase> Cases => _cases;

    public override void Receipt(FloorReceipts receipts)
    {
        const int count = FloorShape.Splats;
        var outside = 0;
        for (var i = 0; i < count; i++)
            if (_expected[i] < 0)
                outside++;
        receipts.Check("floor-p1-reference-has-inside-and-outside-cells",
            outside > count / 100 && outside < count / 10);

        new Span<int>(_output, count).Clear();
        FloorQuantizeKernel.RunScalar(_xs, _ys, _output, 0, count, OriginX, OriginY);
        receipts.Check("floor-p1-scalar-matches-reference",
            new ReadOnlySpan<int>(_output, count).SequenceEqual(new ReadOnlySpan<int>(_expected, count)));

        new Span<int>(_output, count).Clear();
        FloorQuantizeKernel.Run(_xs, _ys, _output, Block, OriginX, OriginY);
        receipts.Check("floor-p1-block-matches-leading-cells",
            new ReadOnlySpan<int>(_output, Block).SequenceEqual(new ReadOnlySpan<int>(_expected, Block)));

        if (!Avx2.IsSupported)
        {
            receipts.Skip("floor-p1-vector-matches-reference", "avx2 unavailable");
            return;
        }

        new Span<int>(_output, count).Clear();
        var done = FloorQuantizeKernel.RunVector(_xs, _ys, _output, count, OriginX, OriginY);
        receipts.Check("floor-p1-vector-matches-reference",
            done == count &&
            new ReadOnlySpan<int>(_output, count).SequenceEqual(new ReadOnlySpan<int>(_expected, count)));
    }
}
