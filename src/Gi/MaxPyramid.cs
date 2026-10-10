#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal unsafe struct MaxPyramid
{
    internal const int FanOut = 8;
    internal const int NodeSlots = FanOut * FanOut;

    private NativeBuffer<short> _slots;
    private NativeBuffer<int> _offsets;
    private NativeBuffer<int> _sides;
    private int _levels;

    public readonly long Bytes =>
        (long)_slots.Capacity * sizeof(short) + ((long)_offsets.Capacity + _sides.Capacity) * sizeof(int);

    public void Ensure(int tilesPerSide)
    {
        if (_levels > 0) return;

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
        new Span<short>(_slots.Pointer, total).Clear();
        _levels = level;
    }

    public void Reset()
    {
        _slots.Free();
        _offsets.Free();
        _sides.Free();
        _levels = 0;
    }

    public void Update(int tilesPerSide, int tile, short max)
    {
        if (_levels == 0) Ensure(tilesPerSide);

        var slots = _slots.Pointer;
        var offsets = _offsets.Pointer;
        var sides = _sides.Pointer;
        var tx = tile % tilesPerSide;
        var ty = tile / tilesPerSide;
        var nodeX = tx >> 3;
        var nodeY = ty >> 3;
        var slot = (ty & 7) * FanOut + (tx & 7);
        var childSide = tilesPerSide;
        var value = max;
        var old = slots[offsets[0] + (nodeY * sides[0] + nodeX) * NodeSlots + slot];
        for (var level = 0; ; level++)
        {
            var side = sides[level];
            var target = slots + offsets[level] + (nodeY * side + nodeX) * NodeSlots;
            if (old == value) return;

            target[slot] = value;
            if (level == _levels - 1) return;

            var parentSlot = (nodeY & 7) * FanOut + (nodeX & 7);
            var parent = slots + offsets[level + 1] +
                ((nodeY >> 3) * sides[level + 1] + (nodeX >> 3)) * NodeSlots + parentSlot;
            var cache = *parent;
            short nodeMax;
            if (value > cache)
            {
                nodeMax = value;
            }
            else if (old == cache)
            {
                nodeMax = MaxNode(target,
                    Math.Min(FanOut, childSide - (nodeX << 3)),
                    Math.Min(FanOut, childSide - (nodeY << 3)));
                if (nodeMax == cache) return;
            }
            else
            {
                return;
            }

            *parent = nodeMax;
            old = cache;
            value = nodeMax;
            slot = parentSlot;
            childSide = side;
            nodeX >>= 3;
            nodeY >>= 3;
        }
    }

    public short Best(int tilesPerSide, PageMap* pages, out int x, out int y)
    {
        if (_levels == 0)
        {
            x = 0;
            y = 0;
            return 0;
        }

        var slots = _slots.Pointer;
        var offsets = _offsets.Pointer;
        var sides = _sides.Pointer;
        var root = _levels - 1;
        var childSide = root == 0 ? tilesPerSide : sides[root - 1];
        var target = MaxNode(slots + offsets[root], Math.Min(FanOut, childSide), Math.Min(FanOut, childSide));
        var nodeX = 0;
        var nodeY = 0;
        for (var level = root; level >= 0; level--)
        {
            var side = sides[level];
            var node = slots + offsets[level] + (nodeY * side + nodeX) * NodeSlots;
            var vx = Math.Min(FanOut, childSide - (nodeX << 3));
            var vy = Math.Min(FanOut, childSide - (nodeY << 3));
            var slot = FirstEqual(node, target, vx, vy);
            var childX = (nodeX << 3) + (slot & 7);
            var childY = (nodeY << 3) + (slot >> 3);
            if (level == 0)
            {
                x = childX << TileBake.TileBits;
                y = childY << TileBake.TileBits;
                if (!pages->TryGet(childY * tilesPerSide + childX, out var block)) return target;

                var page = (short*)(block + World.PageOffset);
                var count = TileBake.TileSize * TileBake.TileSize;
                var index = FirstEqualFlat(page, target, count);
                if (index == count)
                {
                    index = 0;
                    var best = page[0];
                    for (var i = 1; i < count; i++)
                        if (page[i] > best)
                        {
                            best = page[i];
                            index = i;
                        }
                }

                x += index & (TileBake.TileSize - 1);
                y += index >> TileBake.TileBits;
                return target;
            }

            nodeX = childX;
            nodeY = childY;
            childSide = level >= 2 ? sides[level - 2] : tilesPerSide;
        }

        x = 0;
        y = 0;
        return target;
    }

    private static short MaxNode(short* slots, int vx, int vy)
    {
        if (vx == FanOut && vy == FanOut)
        {
#if NET
            if (Avx2.IsSupported)
            {
                var v = Avx.LoadVector256(slots);
                v = Avx2.Max(v, Avx.LoadVector256(slots + 16));
                v = Avx2.Max(v, Avx.LoadVector256(slots + 32));
                v = Avx2.Max(v, Avx.LoadVector256(slots + 48));
                return HorizontalMax(Sse2.Max(v.GetLower(), v.GetUpper()));
            }

            if (Sse2.IsSupported)
            {
                var v = Sse2.LoadVector128(slots);
                for (var i = 8; i < NodeSlots; i += 8) v = Sse2.Max(v, Sse2.LoadVector128(slots + i));
                return HorizontalMax(v);
            }

            if (AdvSimd.IsSupported)
            {
                var v = AdvSimd.LoadVector128(slots);
                for (var i = 8; i < NodeSlots; i += 8) v = AdvSimd.Max(v, AdvSimd.LoadVector128(slots + i));
                return HorizontalMax(v);
            }
#endif
        }

        var best = short.MinValue;
        for (var y = 0; y < vy; y++)
        {
            var row = slots + y * FanOut;
            for (var x = 0; x < vx; x++)
                if (row[x] > best) best = row[x];
        }

        return best;
    }

#if NET
    private static short HorizontalMax(Vector128<short> v)
    {
        var best = v[0];
        for (var i = 1; i < 8; i++)
            if (v[i] > best) best = v[i];
        return best;
    }
#endif

    private static int FirstEqual(short* slots, short target, int vx, int vy)
    {
#if NET
        if (Sse2.IsSupported)
        {
            var goal = Vector128.Create(target);
            var lanes = (1 << (vx << 1)) - 1;
            for (var y = 0; y < vy; y++)
            {
                var row = slots + y * FanOut;
                var mask = Sse2.MoveMask(Sse2.CompareEqual(Sse2.LoadVector128(row), goal).AsByte()) & lanes;
                if (mask != 0) return y * FanOut + TrailingZeros(mask) / 2;
            }

            return NodeSlots;
        }
#endif
        for (var y = 0; y < vy; y++)
        {
            var row = slots + y * FanOut;
            for (var x = 0; x < vx; x++)
                if (row[x] == target) return y * FanOut + x;
        }

        return NodeSlots;
    }

    private static int FirstEqualFlat(short* values, short target, int count)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var goal = Vector256.Create(target);
            for (var i = 0; i < count; i += 16)
            {
                var mask = Avx2.MoveMask(Avx2.CompareEqual(Avx.LoadVector256(values + i), goal).AsByte());
                if (mask != 0) return i + TrailingZeros(mask) / 2;
            }

            return count;
        }

        if (Sse2.IsSupported)
        {
            var goal = Vector128.Create(target);
            for (var i = 0; i < count; i += 8)
            {
                var mask = Sse2.MoveMask(Sse2.CompareEqual(Sse2.LoadVector128(values + i), goal).AsByte());
                if (mask != 0) return i + TrailingZeros(mask) / 2;
            }

            return count;
        }
#endif
        for (var i = 0; i < count; i++)
            if (values[i] == target) return i;
        return count;
    }

    private static int TrailingZeros(int value)
    {
        var isolate = value & -value;
        var count = 0;
        while ((isolate >>= 1) != 0) count++;
        return count;
    }
}
