namespace Gi;

internal unsafe struct BumpScratch
{
    private byte* _region;
    private int _capacity;
    private int _offset;

    public readonly long Bytes => _capacity;

    public byte* At(int offset) => _region + offset;

    public int Reserve(int bytes)
    {
        var offset = (_offset + 63) & ~63;
        var end = offset + bytes;
        if (end > _capacity) Grow(end);

        _offset = end;
        return offset;
    }

    public void Reset()
    {
        _offset = 0;
    }

    public void Release()
    {
        if (_region != null) NativeHeap.AlignedFree(_region);

        _region = null;
        _capacity = 0;
        _offset = 0;
    }

    private void Grow(int end)
    {
        var capacity = Math.Max(_capacity * 2, end + 4096) & ~63;
        var region = (byte*)NativeHeap.AlignedAlloc((nuint)capacity);
        if (_region != null)
        {
            Buffer.MemoryCopy(_region, region, capacity, _offset);
            NativeHeap.AlignedFree(_region);
        }

        _region = region;
        _capacity = capacity;
    }
}
