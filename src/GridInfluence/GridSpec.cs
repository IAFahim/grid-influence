namespace GridInfluence;

public readonly struct GridSpec
{
    private const int SeamColumns = 1;

    public readonly int Log2;
    public readonly int ChunkSize;
    public readonly int Dimension;
    public readonly int Stride;
    public readonly int ElementsPerChunk;
    public readonly uint RetentionFrames;

    private GridSpec(int log2, int chunkSize, int dimension, int stride, int elementsPerChunk, uint retentionFrames)
    {
        Log2 = log2;
        ChunkSize = chunkSize;
        Dimension = dimension;
        Stride = stride;
        ElementsPerChunk = elementsPerChunk;
        RetentionFrames = retentionFrames;
    }

    public static GridSpec FromPowerOfTwo(int chunkSizePowerOfTwo, uint retentionFrames, int strideAlignment = 8)
    {
        var log2 = Math.Clamp(chunkSizePowerOfTwo, 1, 8);
        var chunkSize = 1 << log2;
        var dimension = chunkSize + SeamColumns;
        var alignment = Math.Max(8, strideAlignment);
        var alignMask = alignment - 1;
        var stride = (dimension + alignMask) & ~alignMask;
        var elementsPerChunk = stride * dimension;
        return new GridSpec(log2, chunkSize, dimension, stride, elementsPerChunk, retentionFrames);
    }
}

public static class ChunkMath
{
    public static Int2 ChunkCoordOf(Int2 cell, int log2) => new(cell.X >> log2, cell.Y >> log2);

    public static Int2 ChunkBaseOf(Int2 coord, int log2) => new(coord.X << log2, coord.Y << log2);

    public static ChunkRange ChunkRangeOf(CellRect bounds, int log2)
        => new(
            new Int2(bounds.Min.X >> log2, bounds.Min.Y >> log2),
            new Int2((bounds.Max.X - 1) >> log2, (bounds.Max.Y - 1) >> log2));

    public static Int2 LocalOf(Int2 cell, Int2 chunkBase) => cell - chunkBase;

    public static int DataIndex(int slot, int localX, int localY, GridSpec spec)
        => slot * spec.ElementsPerChunk + localY * spec.Stride + localX;

    public static bool ContainsLocal(Int2 local, int chunkSize)
        => (uint)local.X < (uint)chunkSize && (uint)local.Y < (uint)chunkSize;
}
