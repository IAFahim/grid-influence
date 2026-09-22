namespace GridInfluence;

public sealed unsafe class FlowField : IDisposable
{
    private NativeBuffer<Int2> _direction;
    private GridSpec _spec;
    private bool _disposed;

    public bool IsCreated => _direction.Length > 0;

    public void Resolve(Field source)
    {
        if (!source.IsCreated) throw new ArgumentNullException(nameof(source));
        ObjectDisposedException.ThrowIf(_disposed, this);

        _spec = source.Spec;
        var needed = source.SlotCount * _spec.ElementsPerChunk;
        if (_direction.Length != needed)
        {
            _direction.Resize(needed);
            _direction.Span.Clear();
        }

        var reader = source.AsReader();
        var direction = _direction.Pointer;
        for (var i = 0; i < source.ActiveSlotCount; i++)
        {
            var slot = source.ActiveSlot(i);
            var baseCell = ChunkMath.ChunkBaseOf(source.CoordOf(slot), _spec.Log2);
            for (var ly = 0; ly < _spec.ChunkSize; ly++)
            for (var lx = 0; lx < _spec.ChunkSize; lx++)
            {
                var cell = baseCell + new Int2(lx, ly);
                var dx = reader.ReadCell(cell + new Int2(1, 0)) - reader.ReadCell(cell - new Int2(1, 0));
                var dy = reader.ReadCell(cell + new Int2(0, 1)) - reader.ReadCell(cell - new Int2(0, 1));
                direction[(long)slot * _spec.ElementsPerChunk + ly * _spec.Stride + lx] = new Int2(dx, dy);
            }
        }
    }

    public FlowReader AsReader(Field source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!source.IsCreated) throw new ArgumentNullException(nameof(source));
        return new FlowReader(
            source.SlotMap,
            source.LastWrittenPointer,
            source.LastWrittenLength,
            _direction.Pointer,
            source.Spec,
            source.FrameId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _direction.Dispose();
    }
}

public unsafe ref struct FlowReader
{
    private readonly CoordMap _slotByCoord;
    private readonly uint* _lastWritten;
    private readonly int _lastWrittenLength;
    private readonly Int2* _direction;
    private readonly GridSpec _spec;
    private readonly uint _frameId;
    private Int2 _memoCoord;
    private int _memoSlot;
    private bool _memoValid;

    internal FlowReader(CoordMap slotByCoord, uint* lastWritten, int lastWrittenLength, Int2* direction, GridSpec spec, uint frameId)
    {
        _slotByCoord = slotByCoord;
        _lastWritten = lastWritten;
        _lastWrittenLength = lastWrittenLength;
        _direction = direction;
        _spec = spec;
        _frameId = frameId;
        _memoCoord = default;
        _memoSlot = -1;
        _memoValid = false;
    }

    public Int2 ReadDirection(Int2 cell)
    {
        var coord = ChunkMath.ChunkCoordOf(cell, _spec.Log2);
        if (_memoValid && coord == _memoCoord) return ReadAt(_memoSlot, cell);

        _memoCoord = coord;
        _memoValid = true;
        _memoSlot = _slotByCoord.TryGetValue(coord, out var found)
                    && (uint)found < (uint)_lastWrittenLength
                    && _lastWritten[found] == _frameId
            ? found
            : -1;
        return ReadAt(_memoSlot, cell);
    }

    private Int2 ReadAt(int slot, Int2 cell)
    {
        if (slot < 0) return Int2.Zero;

        var mask = _spec.ChunkSize - 1;
        return _direction[(long)slot * _spec.ElementsPerChunk + (cell.Y & mask) * _spec.Stride + (cell.X & mask)];
    }
}
