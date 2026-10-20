using System.Runtime.CompilerServices;

namespace Gi;

internal unsafe struct FixedSlab
{
    private const int GatherSlack = 64;

    private NativeBuffer<int> _free;
    private int _stride;
    private int _capacity;
    private int _taken;

    public readonly long Bytes => (long)_capacity * _stride + (long)_free.Capacity * sizeof(int);

    public readonly int FreeCount => _free.Length;

    public readonly int LiveCount => _taken - _free.Length;

    public byte* Region { get; private set; }

    public readonly long ByteCapacity => (long)_capacity * _stride;

    public int Take(int stride)
    {
        if (Region == null)
        {
            _stride = stride;
            _capacity = 16;
            Region = (byte*)NativeHeap.AlignedAlloc((nuint)_capacity * (nuint)stride + GatherSlack);
        }

        var freeCount = _free.Length;
        if (freeCount != 0)
        {
            var recycled = _free.Pointer[freeCount - 1];
            _free.ResizeUninitialized(freeCount - 1);
            return recycled;
        }

        if (_taken == _capacity) Grow();
        return _taken++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte* Unit(int index)
    {
        return Region + (nuint)index * (nuint)_stride;
    }

    public void Give(int index)
    {
        var freeCount = _free.Length;
        _free.ResizeUninitialized(freeCount + 1);
        _free.Pointer[freeCount] = index;
    }

    public void Reset()
    {
        if (Region != null) NativeHeap.AlignedFree(Region);

        Region = null;
        _stride = 0;
        _capacity = 0;
        _taken = 0;
        _free.Free();
    }

    private void Grow()
    {
        var capacity = _capacity * 2;
        var region = (byte*)NativeHeap.AlignedAlloc((nuint)capacity * (nuint)_stride + GatherSlack);
        Buffer.MemoryCopy(Region, region, (long)capacity * _stride, (long)_capacity * _stride);
        NativeHeap.AlignedFree(Region);
        Region = region;
        _capacity = capacity;
    }
}
