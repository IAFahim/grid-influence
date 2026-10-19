#if NET
using System.Runtime.Intrinsics;
#endif

namespace Gi;

internal unsafe interface IPyramidOp<T> where T : unmanaged
{
    bool Greater(T a, T b);

    T Reduce(T* slots, int validX, int validY);
}

internal struct ShortMaxOp : IPyramidOp<short>
{
    public readonly bool Greater(short a, short b)
    {
        return a > b;
    }

    public readonly unsafe short Reduce(short* slots, int validX, int validY)
    {
        return Pyramid<short, ShortMaxOp>.ReduceMax(slots, validX, validY);
    }
}

internal struct EpochMaxOp : IPyramidOp<int>
{
    public readonly bool Greater(int a, int b)
    {
        return a - b > 0;
    }

    public readonly unsafe int Reduce(int* slots, int validX, int validY)
    {
        var best = slots[0];
        for (var y = 0; y < validY; y++)
        for (var x = 0; x < validX; x++)
            if (slots[y * Pyramid<int, EpochMaxOp>.FanOut + x] - best > 0)
                best = slots[y * Pyramid<int, EpochMaxOp>.FanOut + x];
        return best;
    }
}

internal unsafe struct Pyramid<T, TOp> where T : unmanaged where TOp : struct, IPyramidOp<T>
{
    internal const int FanOut = 8;
    internal const int NodeSlots = FanOut * FanOut;

    private NativeBuffer<T> _slots;
    private NativeBuffer<int> _offsets;
    private NativeBuffer<int> _sides;

    public int Levels { get; private set; }

    public readonly long Bytes =>
        (long)_slots.Capacity * sizeof(T) + ((long)_offsets.Capacity + _sides.Capacity) * sizeof(int);

    public readonly T* Node(int level, int nodeX, int nodeY)
    {
        return _slots.Pointer + _offsets.Pointer[level] + (nodeY * _sides.Pointer[level] + nodeX) * NodeSlots;
    }

    public readonly int ChildSide(int level, int tilesPerSide)
    {
        return level == 0 ? tilesPerSide : _sides.Pointer[level - 1];
    }

    public void Ensure(int tilesPerSide)
    {
        if (Levels > 0) return;

        var side = (tilesPerSide + FanOut - 1) >> 3;
        var total = 0;
        var level = 0;
        while (true)
        {
            _sides.Resize(level + 1);
            _offsets.Resize(level + 1);
            _sides.Pointer[level] = side;
            _offsets.Pointer[level] = total;
            total += side * side * NodeSlots;
            level++;
            if (side == 1) break;
            side = (side + FanOut - 1) >> 3;
        }

        _slots.Resize(total);
        new Span<T>(_slots.Pointer, total).Clear();
        Levels = level;
    }

    public void Reset()
    {
        _slots.Free();
        _offsets.Free();
        _sides.Free();
        Levels = 0;
    }

    public void Update(int tilesPerSide, int tile, T leaf)
    {
        if (Levels == 0) Ensure(tilesPerSide);

        var op = default(TOp);
        var tx = tile % tilesPerSide;
        var ty = tile / tilesPerSide;
        var nodeX = tx >> 3;
        var nodeY = ty >> 3;
        var slot = (ty & 7) * FanOut + (tx & 7);
        var childSide = tilesPerSide;
        var value = leaf;
        var old = Node(0, nodeX, nodeY)[slot];
        for (var level = 0;; level++)
        {
            var target = Node(level, nodeX, nodeY);
            if (!op.Greater(old, value) && !op.Greater(value, old)) return;

            target[slot] = value;
            if (level == Levels - 1) return;

            var parentSlot = (nodeY & 7) * FanOut + (nodeX & 7);
            var parent = Node(level + 1, nodeX >> 3, nodeY >> 3) + parentSlot;
            var cache = *parent;
            T nodeMax;
            if (op.Greater(value, cache))
            {
                nodeMax = value;
            }
            else if (!op.Greater(old, cache) && !op.Greater(cache, old))
            {
                nodeMax = op.Reduce(target,
                    Math.Min(FanOut, childSide - (nodeX << 3)),
                    Math.Min(FanOut, childSide - (nodeY << 3)));
                if (!op.Greater(nodeMax, cache) && !op.Greater(cache, nodeMax)) return;
            }
            else
            {
                return;
            }

            *parent = nodeMax;
            old = cache;
            value = nodeMax;
            slot = parentSlot;
            childSide = _sides.Pointer[level];
            nodeX >>= 3;
            nodeY >>= 3;
        }
    }

    internal static short ReduceMax(short* slots, int vx, int vy)
    {
        if (vx == FanOut && vy == FanOut)
        {
#if NET
            if (Vector256.IsHardwareAccelerated)
            {
                var v = Vector256.Max(Vector256.Load(slots), Vector256.Load(slots + 16));
                v = Vector256.Max(v, Vector256.Load(slots + 32));
                v = Vector256.Max(v, Vector256.Load(slots + 48));
                return SimdOps.HorizontalMax(v);
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var v = Vector128.Load(slots);
                for (var i = 8; i < NodeSlots; i += 8) v = Vector128.Max(v, Vector128.Load(slots + i));
                return SimdOps.HorizontalMax(v);
            }
#endif
        }

        var best = short.MinValue;
        for (var y = 0; y < vy; y++)
        {
            var row = slots + y * FanOut;
            for (var x = 0; x < vx; x++)
                if (row[x] > best)
                    best = row[x];
        }

        return best;
    }
}
