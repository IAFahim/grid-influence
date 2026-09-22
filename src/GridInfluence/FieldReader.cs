namespace GridInfluence;

public unsafe ref struct FieldReader
{
    private const int MemoMiss = -1;

    private readonly CoordMap _slotByCoord;
    private readonly uint* _lastWritten;
    private readonly int _lastWrittenLength;
    private readonly short* _data;
    private readonly GridSpec _spec;
    private readonly uint _frameId;
    private Int2 _memoCoord;
    private int _memoSlot;
    private bool _memoValid;

    internal GridSpec Spec => _spec;

    internal FieldReader(CoordMap slotByCoord, uint* lastWritten, int lastWrittenLength, short* data, GridSpec spec, uint frameId)
    {
        _slotByCoord = slotByCoord;
        _lastWritten = lastWritten;
        _lastWrittenLength = lastWrittenLength;
        _data = data;
        _spec = spec;
        _frameId = frameId;
        _memoCoord = default;
        _memoSlot = MemoMiss;
        _memoValid = false;
    }

    public int ReadCell(Int2 cell)
    {
        var slot = ResolveSlot(ChunkMath.ChunkCoordOf(cell, _spec.Log2));
        if (slot < 0) return 0;

        var mask = _spec.ChunkSize - 1;
        return _data[(long)slot * _spec.ElementsPerChunk + (cell.Y & mask) * _spec.Stride + (cell.X & mask)];
    }

    public float SampleBilinear(float x, float y)
    {
        var shiftedX = x - 0.5f;
        var shiftedY = y - 0.5f;
        var flooredX = MathF.Floor(shiftedX);
        var flooredY = MathF.Floor(shiftedY);
        var fractionX = shiftedX - flooredX;
        var fractionY = shiftedY - flooredY;
        var baseCell = new Int2((int)flooredX, (int)flooredY);

        var v00 = ReadCell(baseCell);
        var v10 = ReadCell(baseCell + new Int2(1, 0));
        var v01 = ReadCell(baseCell + new Int2(0, 1));
        var v11 = ReadCell(baseCell + new Int2(1, 1));

        var top = v00 + (v10 - v00) * fractionX;
        var bottom = v01 + (v11 - v01) * fractionX;
        return top + (bottom - top) * fractionY;
    }

    public Int2 Gradient(Int2 cell)
    {
        var dx = ReadCell(cell + new Int2(1, 0)) - ReadCell(cell - new Int2(1, 0));
        var dy = ReadCell(cell + new Int2(0, 1)) - ReadCell(cell - new Int2(0, 1));
        return new Int2(dx, dy);
    }

    public bool TryGetChunk(Int2 coord, out ChunkView view)
    {
        var slot = ResolveSlot(coord);
        if (slot < 0)
        {
            view = default;
            return false;
        }

        view = new ChunkView(
            _data + (long)slot * _spec.ElementsPerChunk,
            ChunkMath.ChunkBaseOf(coord, _spec.Log2),
            _spec);
        return true;
    }

    private int ResolveSlot(Int2 coord)
    {
        if (_memoValid && coord == _memoCoord) return _memoSlot;

        var slot = _slotByCoord.TryGetValue(coord, out var found)
                   && (uint)found < (uint)_lastWrittenLength
                   && _lastWritten[found] == _frameId
            ? found
            : MemoMiss;

        _memoCoord = coord;
        _memoSlot = slot;
        _memoValid = true;
        return slot;
    }
}

public unsafe ref struct ChunkView
{
    private readonly short* _field;
    private readonly Int2 _base;
    private readonly int _stride;
    private readonly int _chunkSize;

    internal ChunkView(short* field, Int2 chunkBase, GridSpec spec)
    {
        _field = field;
        _base = chunkBase;
        _stride = spec.Stride;
        _chunkSize = spec.ChunkSize;
    }

    public Int2 Base => _base;

    internal short* Data => _field;

    internal int Stride => _stride;

    internal int Size => _chunkSize;

    public int ReadLocal(Int2 local)
    {
        if (!ChunkMath.ContainsLocal(local, _chunkSize)) return 0;

        return _field[local.Y * _stride + local.X];
    }

    public int ReadWorld(Int2 cell) => ReadLocal(cell - _base);
}
