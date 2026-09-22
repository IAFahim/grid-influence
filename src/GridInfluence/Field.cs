using System.Numerics;

namespace GridInfluence;

public readonly struct FieldStats
{
    public readonly int StampsIn;
    public readonly int StampsDroppedSpanBudget;
    public readonly int StampsDroppedChunkBudget;
    public readonly int ChunksActivated;
    public readonly int ChunksEvicted;
    public readonly int ActiveSlots;

    public FieldStats(
        int stampsIn,
        int stampsDroppedSpanBudget,
        int stampsDroppedChunkBudget,
        int chunksActivated,
        int chunksEvicted,
        int activeSlots)
    {
        StampsIn = stampsIn;
        StampsDroppedSpanBudget = stampsDroppedSpanBudget;
        StampsDroppedChunkBudget = stampsDroppedChunkBudget;
        ChunksActivated = chunksActivated;
        ChunksEvicted = chunksEvicted;
        ActiveSlots = activeSlots;
    }
}

public readonly struct Stencil
{
    public Field Source { get; }
    public int DecayPerMille { get; }
    public int SpreadDenominator { get; }

    private Stencil(Field source, int decayPerMille, int spreadDenominator)
    {
        Source = source;
        DecayPerMille = decayPerMille;
        SpreadDenominator = spreadDenominator;
    }

    public static Stencil Create(Field source, int decayPerMille, int spreadDenominator)
    {
        if (!source.IsCreated) throw new ArgumentNullException(nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(decayPerMille);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decayPerMille, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(spreadDenominator, 1);
        return new Stencil(source, decayPerMille, spreadDenominator);
    }
}

internal unsafe struct StencilCore
{
    public FieldContext* Source;
    public int DecayPerMille;
    public int SpreadDenominator;
}

internal unsafe struct FieldContext
{
    private const int MaxSpansPerSchedule = 1 << 20;
    private const int MaxChunksPerSchedule = 1 << 14;
    private const int CompactionInterval = 60;

    internal GridSpec _spec;
    private CoordMap _slotByCoord;
    private NativeBuffer<Int2> _coordBySlot;
    private NativeBuffer<uint> _lastWritten;
    private NativeBuffer<byte> _nonZero;
    private NativeBuffer<uint> _prepared;
    private NativeBuffer<int> _freeSlots;
    private NativeBuffer<byte> _frontierMasks;
    private NativeBuffer<int> _activeSlots;
    private NativeBuffer<short> _data;
    private NativeBuffer<FieldStamp> _sortedStamps;
    private NativeBuffer<int> _offsets;
    private NativeBuffer<WeightedRect> _spans;
    private NativeBuffer<short> _halo;
    private StencilCore _scanStencil;
    private StencilCore _parallelStencil;
    private int _slotCount;
    private int _liveSlots;
    private int _activeCount;
    private int _stampCount;
    private int _chunksActivated;
    private int _spanDrops;
    private int _chunkDrops;
    private uint _frameId;
    private uint _scheduleVersion;
    internal bool _disposed;

    internal int _workerCount;
    internal nint _poolHandle;
    private int _haloSlice;
    private long _workClaim;
    private int _workGeneration;
    private int _workPending;
    private int _workCount;
    private int _workPhase;
    internal int _workExit;

    internal readonly GridSpec Spec => _spec;
    internal readonly uint FrameId => _frameId;
    internal readonly int ActiveSlotCount => _activeCount;
    internal readonly int SlotCount => _slotCount;

    internal void Init(GridSpec spec, int parallelism, nint poolHandle)
    {
        _spec = spec;
        _frameId = 1;
        _slotByCoord = CoordMap.Create(64);
        _workerCount = parallelism > 1 ? parallelism - 1 : 0;
        _poolHandle = poolHandle;
        _haloSlice = (spec.ChunkSize + 2) * (spec.ChunkSize + 2);
        _halo.Resize(_haloSlice * (_workerCount + 1));
    }

    internal FieldStats Tick(ReadOnlySpan<FieldStamp> stamps, uint tick, StencilCore stencil)
    {
        var reset = AdvanceFrame(tick);
        _scheduleVersion = _scheduleVersion == uint.MaxValue ? 1u : _scheduleVersion + 1;
        if (stamps.IsEmpty
            && _activeCount == 0
            && (stencil.Source == null || stencil.Source->_activeCount == 0))
        {
            var idleEvicted = reset ? EvictAllSlots() : EvictStaleSlots();
            if (_frameId % CompactionInterval == 0 && _freeSlots.Length > 0) CompactSlots();
            _spanDrops = 0;
            _chunkDrops = 0;
            _chunksActivated = 0;
            _stampCount = 0;
            return new FieldStats(0, 0, 0, 0, idleEvicted, 0);
        }
        var evicted = Prepare(stamps, stencil, reset);
        ClearActive();
        RasterizeAll();
        ScatterAll();
        Resolve(stencil);
        return new FieldStats(
            stamps.Length,
            _spanDrops,
            _chunkDrops,
            _chunksActivated,
            evicted,
            _activeCount);
    }

    internal readonly FieldReader AsReader()
        => new(_slotByCoord, _lastWritten.Pointer, _lastWritten.Length, _data.Pointer, _spec, _frameId);

    internal void WriteRegion(Int2 min, Int2 size, ReadOnlySpan<int> weights)
    {
        if (size.X < 0 || size.Y < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (size.X * size.Y > weights.Length)
            throw new ArgumentException("Weights span is smaller than the region.", nameof(weights));

        var stride = _spec.Stride;
        var elements = _spec.ElementsPerChunk;
        var chunkSize = _spec.ChunkSize;
        for (var row = 0; row < size.Y; row++)
        {
            var cellY = min.Y + row;
            var coordY = cellY >> _spec.Log2;
            var localY = cellY & (chunkSize - 1);
            for (var cx = min.X >> _spec.Log2; cx <= (min.X + size.X - 1) >> _spec.Log2; cx++)
            {
                var lo = Math.Max(min.X, cx << _spec.Log2);
                var hi = Math.Min(min.X + size.X, (cx + 1) << _spec.Log2);
                var slot = EnsureSlot(new Int2(cx, coordY));
                var chunk = _data.Pointer + (long)slot * elements;
                var target = chunk + (long)localY * stride + (lo & (chunkSize - 1));
                var weightsRow = weights.Slice(row * size.X + lo - min.X, hi - lo);
                for (var x = 0; x < weightsRow.Length; x++)
                    target[x] = Saturate(weightsRow[x]);
            }
        }

        ActivateRegion(min, size);
    }

    internal void ReadRegion(Int2 min, Int2 size, Span<int> destination)
    {
        if (size.X < 0 || size.Y < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (size.X * size.Y > destination.Length)
            throw new ArgumentException("Destination span is smaller than the region.", nameof(destination));

        var reader = AsReader();
        var stride = _spec.Stride;
        var chunkSize = _spec.ChunkSize;
        var chunks = ChunkMath.ChunkRangeOf(new CellRect(min, min + size), _spec.Log2);
        for (var cy = chunks.Min.Y; cy <= chunks.Max.Y; cy++)
        for (var cx = chunks.Min.X; cx <= chunks.Max.X; cx++)
        {
            var chunkCoord = new Int2(cx, cy);
            var chunkBase = ChunkMath.ChunkBaseOf(chunkCoord, _spec.Log2);
            var lo = Int2.Max(min, chunkBase);
            var hi = Int2.Min(min + size, chunkBase + new Int2(chunkSize, chunkSize));
            var width = hi.X - lo.X;
            if (width <= 0 || hi.Y <= lo.Y) continue;

            if (!reader.TryGetChunk(chunkCoord, out var view))
            {
                for (var y = lo.Y; y < hi.Y; y++)
                    destination.Slice((y - min.Y) * size.X + lo.X - min.X, width).Clear();
                continue;
            }

            var data = view.Data + (long)(lo.Y - chunkBase.Y) * stride + (lo.X - chunkBase.X);
            for (var y = lo.Y; y < hi.Y; y++)
            {
                var dst = destination.Slice((y - min.Y) * size.X + lo.X - min.X, width);
                for (var x = 0; x < width; x++) dst[x] = data[x];
                data += stride;
            }
        }
    }

    private void ActivateRegion(Int2 min, Int2 size)
    {
        if (size.X == 0 || size.Y == 0) return;

        var chunks = ChunkMath.ChunkRangeOf(new CellRect(min, min + size), _spec.Log2);
        for (var cy = chunks.Min.Y; cy <= chunks.Max.Y; cy++)
        for (var cx = chunks.Min.X; cx <= chunks.Max.X; cx++)
        {
            if (!_slotByCoord.TryGetValue(new Int2(cx, cy), out var slot)) continue;

            var chunk = _data.Pointer + (long)slot * _spec.ElementsPerChunk;
            _nonZero.Span[slot] = AnyNonZero(chunk);
            if (_lastWritten.Span[slot] == 0) _liveSlots++;
            _lastWritten.Span[slot] = _frameId;
        }
    }

    internal void DisposeState()
    {
        if (_disposed) return;

        _disposed = true;
        _slotByCoord.Dispose();
        _coordBySlot.Dispose();
        _lastWritten.Dispose();
        _nonZero.Dispose();
        _prepared.Dispose();
        _freeSlots.Dispose();
        _frontierMasks.Dispose();
        _activeSlots.Dispose();
        _data.Dispose();
        _sortedStamps.Dispose();
        _offsets.Dispose();
        _spans.Dispose();
        _halo.Dispose();
    }

    internal int ActiveSlot(int index) => _activeSlots.Span[index];

    internal Int2 CoordOf(int slot) => _coordBySlot.Span[slot];

    internal CoordMap SlotMap => _slotByCoord;

    internal uint* LastWrittenPointer => _lastWritten.Pointer;

    internal int LastWrittenLength => _lastWritten.Length;

    internal int FreeSlotCount => _freeSlots.Length;

    internal readonly bool CompatibleSpecs(FieldContext* other)
        => _spec.Log2 == other->_spec.Log2
           && _spec.Stride == other->_spec.Stride
           && _spec.RetentionFrames == other->_spec.RetentionFrames;

    private bool AdvanceFrame(uint tick)
    {
        var normalized = tick == 0u ? 1u : tick;
        var reset = normalized < _frameId;
        _frameId = normalized;
        return reset;
    }

    private int Prepare(ReadOnlySpan<FieldStamp> stamps, StencilCore stencil, bool reset)
    {
        var evicted = reset ? EvictAllSlots() : EvictStaleSlots();
        if (_frameId % CompactionInterval == 0 && _freeSlots.Length > 0) CompactSlots();

        _activeCount = 0;
        _chunksActivated = 0;
        _spanDrops = 0;
        _chunkDrops = 0;
        if (stencil.Source != null) ActivateStencilFrontier(stencil);
        PrepareStamps(stamps);
        return evicted;
    }

    private int EvictAllSlots()
    {
        if (_liveSlots == 0) return 0;
        var evicted = 0;
        for (var slot = 0; slot < _slotCount; slot++)
        {
            if (_lastWritten.Span[slot] == 0) continue;

            EvictSlot(slot);
            evicted++;
        }

        return evicted;
    }

    private int EvictStaleSlots()
    {
        if (_spec.RetentionFrames == uint.MaxValue || _liveSlots == 0) return 0;

        var minValidFrame = _frameId > _spec.RetentionFrames ? _frameId - _spec.RetentionFrames : 0;
        var evicted = 0;
        for (var slot = 0; slot < _slotCount; slot++)
        {
            var written = _lastWritten.Span[slot];
            if (written == 0 || written >= minValidFrame) continue;

            EvictSlot(slot);
            evicted++;
        }

        return evicted;
    }

    private void EvictSlot(int slot)
    {
        if (_lastWritten.Span[slot] != 0) _liveSlots--;
        _lastWritten.Span[slot] = 0;
        PushFreeSlot(slot);
        _slotByCoord.Remove(_coordBySlot.Span[slot]);
    }

    private void PushFreeSlot(int slot)
    {
        var length = _freeSlots.Length;
        _freeSlots.Resize(length + 1);
        _freeSlots.Span[length] = slot;
    }

    private int PopFreeSlot()
    {
        var slot = _freeSlots.Span[_freeSlots.Length - 1];
        _freeSlots.SetLength(_freeSlots.Length - 1);
        return slot;
    }

    private void CompactSlots()
    {
        _slotByCoord.Rehash();
        var highestSlot = _slotCount - 1;
        var freeSlots = _freeSlots.Span;
        for (var i = 0; i < freeSlots.Length; i++) MoveSlotDown(freeSlots[i], ref highestSlot);

        var newCount = highestSlot + 1;
        _slotCount = newCount;
        RebuildFreeSlots(newCount);
    }

    private void MoveSlotDown(int freeSlot, ref int highestSlot)
    {
        while (highestSlot >= 0 && _lastWritten.Span[highestSlot] == 0) highestSlot--;

        if (freeSlot >= highestSlot) return;

        CopyChunk(highestSlot, freeSlot);
        _lastWritten.Span[highestSlot] = 0;
        highestSlot--;
    }

    private void CopyChunk(int from, int to)
    {
        var elements = _spec.ElementsPerChunk;
        Buffer.MemoryCopy(
            _data.Pointer + (long)from * elements,
            _data.Pointer + (long)to * elements,
            (long)elements * sizeof(short),
            (long)elements * sizeof(short));

        _coordBySlot.Span[to] = _coordBySlot.Span[from];
        _lastWritten.Span[to] = _lastWritten.Span[from];
        _nonZero.Span[to] = _nonZero.Span[from];
        _prepared.Span[to] = _prepared.Span[from];
        _slotByCoord.Add(_coordBySlot.Span[to], to);
    }

    private void RebuildFreeSlots(int newCount)
    {
        _freeSlots.Resize(0);
        for (var slot = 0; slot < newCount; slot++)
        {
            if (_lastWritten.Span[slot] == 0) PushFreeSlot(slot);
        }
    }

    private void ActivateStencilFrontier(StencilCore stencil)
    {
        var source = stencil.Source;
        if (source == null) return;
        var count = source->_activeCount;
        if (count == 0) return;
        if (_frontierMasks.Length < count) _frontierMasks.Resize(count);
        _scanStencil = stencil;
        if (_workerCount > 0 && count >= 64)
            RunParallel(4, count);
        else
            for (var i = 0; i < count; i++) ScanFrontierSlot(i);
        var sourceActive = source->_activeSlots.Span;
        var masks = _frontierMasks.Span;
        for (var i = 0; i < count; i++)
        {
            var slot = sourceActive[i];
            var mask = masks[i];
            if (mask == 0) continue;
            var coord = source->_coordBySlot.Span[slot];
            Activate(coord);
            if ((mask & 1) != 0) Activate(coord + new Int2(-1, 0));
            if ((mask & 2) != 0) Activate(coord + new Int2(1, 0));
            if ((mask & 4) != 0) Activate(coord + new Int2(0, -1));
            if ((mask & 8) != 0) Activate(coord + new Int2(0, 1));
        }
    }

    private void ScanFrontierSlot(int i)
    {
        var source = _scanStencil.Source;
        var slot = source->_activeSlots.Span[i];
        if (!IsStencilSlotLive(source, slot))
        {
            _frontierMasks.Span[i] = 0;
            return;
        }
        var stencil = _scanStencil;
        var size = _spec.ChunkSize;
        var stride = _spec.Stride;
        var baseIndex = slot * _spec.ElementsPerChunk;
        var mask = (byte)16;
        if (NeedsActivationEdge(source, stencil, baseIndex, 0, 0, 0, 1, size, stride)) mask |= 1;
        if (NeedsActivationEdge(source, stencil, baseIndex, size - 1, 0, 0, 1, size, stride)) mask |= 2;
        if (NeedsActivationEdge(source, stencil, baseIndex, 0, 0, 1, 0, size, stride)) mask |= 4;
        if (NeedsActivationEdge(source, stencil, baseIndex, 0, size - 1, 1, 0, size, stride)) mask |= 8;
        _frontierMasks.Span[i] = mask;
    }

    private static bool IsStencilSlotLive(FieldContext* source, int slot)
    {
        if ((uint)slot < (uint)source->_lastWritten.Length && source->_lastWritten.Span[slot] != source->_frameId)
            return false;

        return (uint)slot >= (uint)source->_nonZero.Length || source->_nonZero.Span[slot] != 0;
    }

    private static bool NeedsActivationEdge(
        FieldContext* source,
        StencilCore stencil,
        int baseIndex,
        int startX,
        int startY,
        int dx,
        int dy,
        int count,
        int stride)
    {
        var keepFactor = 1000 - stencil.DecayPerMille;
        if (keepFactor <= 0) return false;
        var thresholdLong = ((long)stencil.SpreadDenominator * 1000 + keepFactor - 1) / keepFactor;
        if (thresholdLong > short.MaxValue) return false;
        var threshold = (int)thresholdLong;

        var data = source->_data.Pointer + baseIndex;
        if (dx == 1 && Vector.IsHardwareAccelerated && count >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            var bound = new Vector<int>(threshold);
            var row = data + (long)startY * stride + startX;
            var i = 0;
            for (; i <= count - lanes; i += lanes)
            {
                Vector.Widen(new Vector<short>(new ReadOnlySpan<short>(row + i, lanes)), out var lo, out var hi);
                if (Vector.GreaterThanOrEqualAny(Vector.Abs(lo), bound)
                    || Vector.GreaterThanOrEqualAny(Vector.Abs(hi), bound))
                    return true;
            }
            for (; i < count; i++)
                if (row[i] >= threshold || row[i] <= -threshold) return true;
            return false;
        }
        for (var i = 0; i < count; i++)
        {
            var v = data[(long)(startY + i * dy) * stride + startX + i * dx];
            if (v >= threshold || v <= -threshold) return true;
        }

        return false;
    }

    private void PrepareStamps(ReadOnlySpan<FieldStamp> stamps)
    {
        var stampCount = stamps.Length;
        _stampCount = stampCount;
        _sortedStamps.Resize(stampCount);
        stamps.CopyTo(_sortedStamps.Span);
        StampOrder.Sort(_sortedStamps.Span);

        _offsets.Resize(stampCount + 1);
        var running = 0;
        var activatedChunks = 0L;
        var offsets = _offsets.Span;
        var sorted = _sortedStamps.Span;
        for (var i = 0; i < stampCount; i++)
        {
            offsets[i] = running;
            var stamp = sorted[i];
            var estimate = Rasterizer.EstimateSpanCount(stamp.Shape);
            if (estimate > 0 && estimate > MaxSpansPerSchedule - running)
            {
                _spanDrops++;
                continue;
            }

            if (estimate <= 0) continue;

            var bounds = Rasterizer.Bounds(stamp.Shape, stamp.Origin);
            if (bounds.IsEmpty) continue;

            var chunkCount = ChunkCountOf(bounds);
            if (chunkCount > MaxChunksPerSchedule - activatedChunks)
            {
                _chunkDrops++;
                continue;
            }

            activatedChunks += chunkCount;
            running += estimate;
            ActivateBounds(bounds);
        }

        offsets[stampCount] = running;
        _spans.Resize(running);
    }

    private long ChunkCountOf(CellRect bounds)
    {
        var chunks = ChunkMath.ChunkRangeOf(bounds, _spec.Log2);
        return (long)(chunks.Max.X - chunks.Min.X + 1) * (chunks.Max.Y - chunks.Min.Y + 1);
    }

    private void ActivateBounds(CellRect bounds)
    {
        if (bounds.IsEmpty) return;

        var chunks = ChunkMath.ChunkRangeOf(bounds, _spec.Log2);
        for (var cy = chunks.Min.Y; cy <= chunks.Max.Y; cy++)
        for (var cx = chunks.Min.X; cx <= chunks.Max.X; cx++)
            Activate(new Int2(cx, cy));
    }

    private void Activate(Int2 coord)
    {
        var slot = EnsureSlot(coord);
        if (_prepared.Span[slot] == _scheduleVersion) return;

        _prepared.Span[slot] = _scheduleVersion;
        if (_lastWritten.Span[slot] == 0) _liveSlots++;
        _lastWritten.Span[slot] = _frameId;
        _activeSlots.Span[_activeCount++] = slot;
        _chunksActivated++;
    }

    private int EnsureSlot(Int2 coord)
    {
        if (_slotByCoord.TryGetValue(coord, out var existing)) return existing;

        var slot = _freeSlots.Length > 0 ? ReuseFreeSlot(coord) : AppendSlot(coord);
        _slotByCoord.Add(coord, slot);
        return slot;
    }

    private int ReuseFreeSlot(Int2 coord)
    {
        var slot = PopFreeSlot();
        _coordBySlot.Span[slot] = coord;
        _lastWritten.Span[slot] = 0;
        _nonZero.Span[slot] = 0;
        _prepared.Span[slot] = 0;
        new Span<short>(_data.Pointer + (long)slot * _spec.ElementsPerChunk, _spec.ElementsPerChunk).Clear();
        return slot;
    }

    private int AppendSlot(Int2 coord)
    {
        var slot = _slotCount++;
        ResizeSlotTables(_slotCount);
        _coordBySlot.Span[slot] = coord;
        _lastWritten.Span[slot] = 0;
        _nonZero.Span[slot] = 0;
        _prepared.Span[slot] = 0;
        _data.Resize(_slotCount * _spec.ElementsPerChunk);
        new Span<short>(_data.Pointer + (long)slot * _spec.ElementsPerChunk, _spec.ElementsPerChunk).Clear();
        return slot;
    }

    private void ResizeSlotTables(int slotCount)
    {
        _coordBySlot.Resize(slotCount);
        _lastWritten.Resize(slotCount);
        _nonZero.Resize(slotCount);
        _prepared.Resize(slotCount);
        _activeSlots.Resize(slotCount);
    }

    private void ClearActive()
    {
        var elements = _spec.ElementsPerChunk;
        var data = _data.Pointer;
        var active = _activeSlots.Pointer;
        if (_workerCount > 0 && _activeCount >= 128)
        {
            RunParallel(0, _activeCount);
            return;
        }
        for (var i = 0; i < _activeCount; i++)
            new Span<short>(data + (long)active[i] * elements, elements).Clear();
    }

    private void RasterizeAll()
    {
        var offsets = _offsets.Span;
        var sorted = _sortedStamps.Span;
        var spanPointer = _spans.Pointer;
        if (_workerCount > 0 && _stampCount >= 64 && _activeCount >= 128)
        {
            RunParallel(2, _stampCount);
            return;
        }
        for (var i = 0; i < _stampCount; i++)
        {
            var sink = new SpanSink(spanPointer + offsets[i], offsets[i + 1] - offsets[i]);
            Rasterizer.Emit(sorted[i], ref sink);
            sink.SealRemaining();
        }
    }

    private void ScatterAll()
    {
        var offsets = _offsets.Span;
        var spans = _spans.Pointer;
        if (_workerCount > 0 && _stampCount >= 64 && _activeCount >= 128)
        {
            RunParallel(3, _stampCount);
            return;
        }
        for (var i = 0; i < _stampCount; i++)
        {
            for (var s = offsets[i]; s < offsets[i + 1]; s++) ScatterSpan(spans[s], false);
        }
    }

    private void ScatterSpan(in WeightedRect span, bool atomic)
    {
        if (span.IsEmpty) return;

        var bounds = span.Bounds;
        var chunks = ChunkMath.ChunkRangeOf(bounds, _spec.Log2);
        for (var cy = chunks.Min.Y; cy <= chunks.Max.Y; cy++)
        for (var cx = chunks.Min.X; cx <= chunks.Max.X; cx++)
            ScatterChunk(in bounds, span.Weight, new Int2(cx, cy), atomic);
    }

    private void ScatterChunk(in CellRect bounds, int weight, Int2 chunk, bool atomic)
    {
        if (!_slotByCoord.TryGetValue(chunk, out var slot)) return;

        var origin = ChunkMath.ChunkBaseOf(chunk, _spec.Log2);
        var chunkSpan = new Int2(_spec.ChunkSize, _spec.ChunkSize);
        var lo = Int2.Max(bounds.Min, origin) - origin;
        var hi = Int2.Min(bounds.Max, origin + chunkSpan) - origin;
        if (lo.X >= hi.X || lo.Y >= hi.Y) return;

        AddCorners(_data.Pointer + (long)slot * _spec.ElementsPerChunk, lo, hi, weight, atomic);
    }

    private void AddCorners(short* field, Int2 lo, Int2 hi, int weight, bool atomic)
    {
        var stride = _spec.Stride;
        if (atomic)
        {
            AddSaturatedAtomic(field + (long)lo.Y * stride + lo.X, weight);
            AddSaturatedAtomic(field + (long)lo.Y * stride + hi.X, -weight);
            AddSaturatedAtomic(field + (long)hi.Y * stride + lo.X, -weight);
            AddSaturatedAtomic(field + (long)hi.Y * stride + hi.X, weight);
            return;
        }
        var cell = field + (long)lo.Y * stride + lo.X;
        *cell = Saturate(*cell + weight);
        cell = field + (long)lo.Y * stride + hi.X;
        *cell = Saturate(*cell - weight);
        cell = field + (long)hi.Y * stride + lo.X;
        *cell = Saturate(*cell - weight);
        cell = field + (long)hi.Y * stride + hi.X;
        *cell = Saturate(*cell + weight);
    }

    private static short Saturate(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static void AddSaturatedAtomic(short* cell, int weight)
    {
        var pair = (int*)((long)cell & ~3L);
        var high = ((long)cell & 2) != 0;
        int observed;
        while (true)
        {
            observed = *pair;
            var current = high ? (short)(observed >> 16) : (short)observed;
            var next = Math.Clamp(current + weight, short.MinValue, short.MaxValue);
            var updated = high
                ? (observed & 0x0000FFFF) | (next << 16)
                : (observed & unchecked((int)0xFFFF0000)) | (next & 0xFFFF);
            if (Interlocked.CompareExchange(ref *pair, updated, observed) == observed) return;
        }
    }

    private void RunParallel(int phase, int count)
    {
        _workPhase = phase;
        _workCount = count;
        _workPending = count;
        _workGeneration++;
        Volatile.Write(ref _workClaim, (long)_workGeneration << 32);
        var signals = ((WorkerPool)System.Runtime.InteropServices.GCHandle.FromIntPtr(_poolHandle).Target!).Signals;
        var wake = Math.Min(signals.Length, Math.Max(1, count >> 3));
        for (var i = 0; i < wake; i++) signals[i].Set();
        DrainWork(0);
        while (Volatile.Read(ref _workPending) != 0) Thread.SpinWait(1);
    }

    internal void DrainWork(int worker)
    {
        const int batch = 8;
        var generation = Volatile.Read(ref _workGeneration);
        var phase = _workPhase;
        var count = _workCount;
        var stencil = _parallelStencil;
        var active = _activeSlots.Pointer;
        while (true)
        {
            var ticket = Interlocked.Add(ref _workClaim, batch);
            var ticketGeneration = (int)(ticket >> 32);
            if (ticketGeneration != generation)
            {
                generation = ticketGeneration;
                phase = _workPhase;
                count = _workCount;
                stencil = _parallelStencil;
                active = _activeSlots.Pointer;
            }
            var i = (int)ticket - batch;
            if (i >= count) return;
            var end = Math.Min(i + batch, count);
            var hasStencil = stencil.Source != null;
            var done = 0;
            for (; i < end; i++)
            {
                switch (phase)
                {
                    case 1:
                        ResolveSlot(active[i], stencil, hasStencil, worker);
                        break;
                    case 2:
                        var sink = new SpanSink(_spans.Pointer + _offsets.Pointer[i], _offsets.Pointer[i + 1] - _offsets.Pointer[i]);
                        Rasterizer.Emit(_sortedStamps.Pointer[i], ref sink);
                        sink.SealRemaining();
                        break;
                    case 3:
                        for (var s = _offsets.Pointer[i]; s < _offsets.Pointer[i + 1]; s++) ScatterSpan(_spans.Pointer[s], true);
                        break;
                    case 4:
                        ScanFrontierSlot(i);
                        break;
                    default:
                        new Span<short>(_data.Pointer + (long)active[i] * _spec.ElementsPerChunk, _spec.ElementsPerChunk).Clear();
                        break;
                }
                done++;
            }
            Interlocked.Add(ref _workPending, -done);
        }
    }

    private void Resolve(StencilCore stencil)
    {
        var active = _activeSlots.Pointer;
        var hasStencil = stencil.Source != null;
        if (_workerCount > 0 && _activeCount >= 128 && stencil.Source != (FieldContext*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref this))
        {
            _parallelStencil = stencil;
            RunParallel(1, _activeCount);
            return;
        }
        for (var i = 0; i < _activeCount; i++) ResolveSlot(active[i], stencil, hasStencil, 0);
    }

    private void ResolveSlot(int slot, StencilCore stencil, bool hasStencil, int worker)
    {
        var field = _data.Pointer + (long)slot * _spec.ElementsPerChunk;
        _nonZero.Span[slot] = hasStencil
            ? PrefixSumRunStenciled(field, stencil, _coordBySlot.Span[slot], worker)
            : PrefixSumRun(field);
    }

    private byte PrefixSumRun(short* field)
        => PrefixSumPass(field);

    private byte PrefixSumRunStenciled(short* field, StencilCore stencil, Int2 coord, int worker)
    {
        PrefixSumPass(field);
        ApplyStencil(stencil, coord, field, worker);
        return AnyNonZero(field);
    }

    private byte PrefixSumPass(short* field)
    {
        var stride = _spec.Stride;
        var dimension = _spec.Dimension;
        var y = 0;
        for (; y + 4 <= dimension; y += 4)
        {
            var row0 = field + (long)y * stride;
            var row1 = row0 + stride;
            var row2 = row1 + stride;
            var row3 = row2 + stride;
            var a = 0;
            var b = 0;
            var c = 0;
            var d = 0;
            for (var x = 0; x < dimension; x++)
            {
                a += row0[x]; row0[x] = Saturate(a);
                b += row1[x]; row1[x] = Saturate(b);
                c += row2[x]; row2[x] = Saturate(c);
                d += row3[x]; row3[x] = Saturate(d);
            }
        }
        for (; y < dimension; y++)
        {
            var row = field + (long)y * stride;
            var running = 0;
            for (var x = 0; x < dimension; x++)
            {
                running += row[x];
                row[x] = Saturate(running);
            }
        }

        var acc = 0;
        if (Vector.IsHardwareAccelerated && stride >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            var vacc = Vector<short>.Zero;
            for (var ry = 1; ry < dimension; ry++)
            {
                var above = field + (long)(ry - 1) * stride;
                var current = field + (long)ry * stride;
                for (var x = 0; x <= stride - lanes; x += lanes)
                {
                    var v = Vector.AddSaturate(
                        new Vector<short>(new ReadOnlySpan<short>(current + x, lanes)),
                        new Vector<short>(new ReadOnlySpan<short>(above + x, lanes)));
                    v.CopyTo(new Span<short>(current + x, lanes));
                    vacc |= v;
                }
            }
            for (var i = 0; i < lanes; i++) acc |= (ushort)vacc[i];
        }
        else
        {
            for (var ry = 1; ry < dimension; ry++)
            {
                var above = field + (long)(ry - 1) * stride;
                var current = field + (long)ry * stride;
                for (var x = 0; x < stride; x++)
                {
                    current[x] = Saturate(current[x] + above[x]);
                    acc |= (ushort)current[x];
                }
            }
        }
        var row0acc = 0;
        var first = field;
        for (var x = 0; x < stride; x++) row0acc |= (ushort)first[x];
        return (acc | row0acc) != 0 ? (byte)1 : (byte)0;
    }

    private byte AnyNonZero(short* field)
    {
        var chunkSize = _spec.ChunkSize;
        var stride = _spec.Stride;
        var acc = 0;
        var y = 0;
        if (Vector.IsHardwareAccelerated && chunkSize >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            var vacc = Vector<short>.Zero;
            for (; y < chunkSize; y++)
            {
                var row = field + (long)y * stride;
                for (var x = 0; x <= chunkSize - lanes; x += lanes)
                    vacc |= new Vector<short>(new ReadOnlySpan<short>(row + x, lanes));
                for (var x = chunkSize - chunkSize % lanes; x < chunkSize; x++) acc |= (ushort)row[x];
            }
            for (var i = 0; i < lanes; i++) acc |= (ushort)vacc[i];
            return acc != 0 ? (byte)1 : (byte)0;
        }
        for (; y < chunkSize; y++)
        {
            var row = field + (long)y * stride;
            for (var x = 0; x < chunkSize; x++) acc |= (ushort)row[x];
        }

        return acc != 0 ? (byte)1 : (byte)0;
    }

    private void ApplyStencil(StencilCore stencil, Int2 coord, short* field, int worker = 0)
    {
        var source = stencil.Source;
        var haloStride = _spec.ChunkSize + 2;
        var halo = _halo.Pointer + (long)worker * _haloSlice;
        new Span<short>(halo, haloStride * haloStride).Clear();
        FillHalo(source, stencil, coord, halo, haloStride);
        DecaySelf(source, stencil, coord, field, halo, haloStride);
        AddInflow(field, halo, haloStride);
    }

    private void DecaySelf(FieldContext* source, StencilCore stencil, Int2 coord, short* field, short* halo, int haloStride)
    {
        if (!source->_slotByCoord.TryGetValue(coord, out var slot) || !IsStencilSlotLive(source, slot)) return;

        var self = source->_data.Pointer + (long)slot * _spec.ElementsPerChunk;
        for (var y = 0; y < _spec.ChunkSize; y++)
            DecayRow(
                self + (long)y * _spec.Stride,
                halo + (long)(y + 1) * haloStride + 1,
                field + (long)y * _spec.Stride,
                stencil);
    }

    private readonly struct MagicDivisor
    {
        public readonly long Multiplier;
        public readonly int Shift;
        public readonly int BiasMask;
        public readonly bool IsVectorizable;

        private MagicDivisor(long multiplier, int shift, int biasMask, bool vectorizable)
        {
            Multiplier = multiplier;
            Shift = shift;
            BiasMask = biasMask;
            IsVectorizable = vectorizable;
        }

        public static MagicDivisor Of(int divisor)
        {
            if ((divisor & (divisor - 1)) == 0)
                return new MagicDivisor(0, BitOperations.TrailingZeroCount(divisor), divisor - 1, true);
            for (var l = 0; l < 33; l++)
            {
                var m = ((1L << (32 + l)) + divisor - 1) / divisor;
                if (m <= int.MaxValue && m * divisor - (1L << (32 + l)) <= (1L << l))
                    return new MagicDivisor(m, 32 + l, 0, true);
            }
            return default;
        }

        public Vector<int> Divide(Vector<int> values)
        {
            if (Multiplier == 0)
                return (values + ((values >> 31) & new Vector<int>(BiasMask))) >> Shift;
            Vector.Widen(values, out var lo, out var hi);
            var m = new Vector<long>(Multiplier);
            var q = Vector.Narrow((lo * m) >> Shift, (hi * m) >> Shift);
            return q + ((values >> 31) & Vector<int>.One);
        }
    }

    private static Vector<int> DivideExact(Vector<int> values, MagicDivisor divisor) => divisor.Divide(values);

    private void DecayRow(short* source, short* haloRow, short* target, StencilCore stencil)
    {
        var decay = stencil.DecayPerMille;
        var spread = stencil.SpreadDenominator;
        var chunkSize = _spec.ChunkSize;
        var divisor = MagicDivisor.Of(spread);
        var thousand = MagicDivisor.Of(1000);
        var x = 0;
        if (Vector.IsHardwareAccelerated && divisor.IsVectorizable && thousand.IsVectorizable && chunkSize >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            var keepFactor = new Vector<int>(1000 - decay);
            var min = new Vector<int>(short.MinValue);
            var max = new Vector<int>(short.MaxValue);
            for (; x <= chunkSize - lanes; x += lanes)
            {
                Vector.Widen(new Vector<short>(new ReadOnlySpan<short>(source + x, lanes)), out var vlo, out var vhi);
                var keptLo = thousand.Divide(vlo * keepFactor);
                var keptHi = thousand.Divide(vhi * keepFactor);
                var outflowLo = divisor.Divide(keptLo);
                var outflowHi = divisor.Divide(keptHi);
                Vector.Narrow(outflowLo, outflowHi).CopyTo(new Span<short>(haloRow + x, lanes));
                Vector.Widen(new Vector<short>(new ReadOnlySpan<short>(target + x, lanes)), out var tlo, out var thi);
                Vector.Narrow(
                    Vector.Min(Vector.Max(tlo + keptLo - (outflowLo << 2), min), max),
                    Vector.Min(Vector.Max(thi + keptHi - (outflowHi << 2), min), max))
                    .CopyTo(new Span<short>(target + x, lanes));
            }
        }
        for (; x < chunkSize; x++)
        {
            var kept = IntegerMath.DecayKeep(source[x], decay);
            var outflow = kept / spread;
            haloRow[x] = (short)outflow;
            target[x] = Saturate(target[x] + kept - 4 * outflow);
        }
    }

    private void FillHalo(FieldContext* source, StencilCore stencil, Int2 coord, short* halo, int haloStride)
    {
        var chunkSize = _spec.ChunkSize;
        FillHaloColumn(source, stencil, coord + new Int2(-1, 0), chunkSize - 1, 0, halo, haloStride);
        FillHaloColumn(source, stencil, coord + new Int2(1, 0), 0, chunkSize + 1, halo, haloStride);
        FillHaloRow(source, stencil, coord + new Int2(0, -1), chunkSize - 1, 0, halo, haloStride);
        FillHaloRow(source, stencil, coord + new Int2(0, 1), 0, chunkSize + 1, halo, haloStride);
    }

    private void FillHaloColumn(
        FieldContext* source,
        StencilCore stencil,
        Int2 coord,
        int sourceX,
        int haloX,
        short* halo,
        int haloStride)
    {
        if (!source->_slotByCoord.TryGetValue(coord, out var slot) || !IsStencilSlotLive(source, slot)) return;

        var start = source->_data.Pointer + (long)slot * _spec.ElementsPerChunk + sourceX;
        var chunkSize = _spec.ChunkSize;
        var stride = _spec.Stride;
        var decay = stencil.DecayPerMille;
        var spread = stencil.SpreadDenominator;
        var divisor = MagicDivisor.Of(spread);
        var thousand = MagicDivisor.Of(1000);
        var y = 0;
        if (Vector.IsHardwareAccelerated && divisor.IsVectorizable && thousand.IsVectorizable && chunkSize >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            Span<short> gathered = stackalloc short[Vector<short>.Count];
            var keepFactor = new Vector<int>(1000 - decay);
            for (; y <= chunkSize - lanes; y += lanes)
            {
                for (var j = 0; j < lanes; j++) gathered[j] = start[(long)(y + j) * stride];
                Vector.Widen(new Vector<short>(gathered), out var vlo, out var vhi);
                var outflow = Vector.Narrow(
                    divisor.Divide(thousand.Divide(vlo * keepFactor)),
                    divisor.Divide(thousand.Divide(vhi * keepFactor)));
                for (var j = 0; j < lanes; j++)
                    halo[(long)(y + j + 1) * haloStride + haloX] = outflow[j];
            }
        }
        for (; y < chunkSize; y++)
            halo[(long)(y + 1) * haloStride + haloX] =
                (short)IntegerMath.Outflow(start[(long)y * stride], decay, spread);
    }

    private void FillHaloRow(
        FieldContext* source,
        StencilCore stencil,
        Int2 coord,
        int sourceY,
        int haloY,
        short* halo,
        int haloStride)
    {
        if (!source->_slotByCoord.TryGetValue(coord, out var slot) || !IsStencilSlotLive(source, slot)) return;

        var start = source->_data.Pointer + (long)slot * _spec.ElementsPerChunk + (long)sourceY * _spec.Stride;
        var chunkSize = _spec.ChunkSize;
        var decay = stencil.DecayPerMille;
        var spread = stencil.SpreadDenominator;
        var divisor = MagicDivisor.Of(spread);
        var thousand = MagicDivisor.Of(1000);
        var x = 0;
        if (Vector.IsHardwareAccelerated && divisor.IsVectorizable && thousand.IsVectorizable && chunkSize >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            var keepFactor = new Vector<int>(1000 - decay);
            for (; x <= chunkSize - lanes; x += lanes)
            {
                Vector.Widen(new Vector<short>(new ReadOnlySpan<short>(start + x, lanes)), out var vlo, out var vhi);
                var outflow = Vector.Narrow(
                    divisor.Divide(thousand.Divide(vlo * keepFactor)),
                    divisor.Divide(thousand.Divide(vhi * keepFactor)));
                outflow.CopyTo(new Span<short>(halo + (long)haloY * haloStride + x + 1, lanes));
            }
        }
        for (; x < chunkSize; x++)
            halo[(long)haloY * haloStride + x + 1] =
                (short)IntegerMath.Outflow(start[x], decay, spread);
    }

    private void AddInflow(short* field, short* halo, int haloStride)
    {
        for (var y = 0; y < _spec.ChunkSize; y++)
            AddInflowRow(field + (long)y * _spec.Stride, halo + (long)(y + 1) * haloStride + 1, haloStride);
    }

    private void AddInflowRow(short* target, short* center, int haloStride)
    {
        var below = center - haloStride;
        var above = center + haloStride;
        var chunkSize = _spec.ChunkSize;
        var x = 0;
        if (Vector.IsHardwareAccelerated && chunkSize >= Vector<short>.Count)
        {
            var lanes = Vector<short>.Count;
            for (; x <= chunkSize - lanes; x += lanes)
            {
                var sum = Vector.AddSaturate(
                    Vector.AddSaturate(
                        new Vector<short>(new ReadOnlySpan<short>(center + x - 1, lanes)),
                        new Vector<short>(new ReadOnlySpan<short>(center + x + 1, lanes))),
                    Vector.AddSaturate(
                        new Vector<short>(new ReadOnlySpan<short>(below + x, lanes)),
                        new Vector<short>(new ReadOnlySpan<short>(above + x, lanes))));
                Vector.AddSaturate(new Vector<short>(new ReadOnlySpan<short>(target + x, lanes)), sum)
                    .CopyTo(new Span<short>(target + x, lanes));
            }
        }
        for (; x < chunkSize; x++)
            target[x] = Saturate(target[x] + center[x - 1] + center[x + 1] + below[x] + above[x]);
    }
}

internal sealed unsafe class WorkerPool : IDisposable
{
    internal readonly Thread[] Threads;
    internal readonly ManualResetEventSlim[] Signals;
    internal FieldContext* Ctx;

    internal WorkerPool(FieldContext* ctx, int workers)
    {
        Ctx = ctx;
        Signals = new ManualResetEventSlim[workers];
        Threads = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            var index = i + 1;
            Signals[i] = new ManualResetEventSlim(false);
            Threads[i] = new Thread(() => Entry(index)) { IsBackground = true, Name = $"GridInfluence-Resolve-{index}" };
            Threads[i].Start();
        }
    }

    private void Entry(int worker)
    {
        var signal = Signals[worker - 1];
        while (true)
        {
            signal.Wait();
            if (Volatile.Read(ref Ctx->_workExit) != 0) return;
            Ctx->DrainWork(worker);
            signal.Reset();
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref Ctx->_workExit, 1);
        foreach (var signal in Signals) signal.Set();
        foreach (var thread in Threads) thread.Join();
        foreach (var signal in Signals) signal.Dispose();
    }
}

public unsafe struct Field : IDisposable, IEquatable<Field>
{
    internal FieldContext* _ctx;
    internal WorkerPool? _pool;

    public Field(GridSpec spec) : this(spec, 0)
    {
    }

    public Field(GridSpec spec, int parallelism)
    {
        _ctx = (FieldContext*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(FieldContext));
        _pool = null;
        var workers = parallelism > 1 ? parallelism - 1 : 0;
        if (workers > 0)
        {
            _pool = new WorkerPool(_ctx, workers);
            _ctx->Init(spec, parallelism, System.Runtime.InteropServices.GCHandle.ToIntPtr(
                System.Runtime.InteropServices.GCHandle.Alloc(_pool)));
        }
        else
        {
            _ctx->Init(spec, parallelism, 0);
        }
    }

    internal readonly bool IsCreated => _ctx != null && !_ctx->_disposed;
    internal readonly FieldContext* Ctx => _ctx;

    public readonly GridSpec Spec => _ctx->_spec;
    public readonly uint FrameId => _ctx->FrameId;
    public readonly int ActiveSlotCount => _ctx->ActiveSlotCount;
    public readonly int SlotCount => _ctx->SlotCount;

    public readonly FieldStats Tick(ReadOnlySpan<FieldStamp> stamps)
        => Tick(stamps, _ctx->FrameId + 1, default);

    public readonly FieldStats Tick(ReadOnlySpan<FieldStamp> stamps, Stencil stencil)
        => Tick(stamps, _ctx->FrameId + 1, stencil);

    public readonly FieldStats Tick(ReadOnlySpan<FieldStamp> stamps, uint tick, Stencil stencil = default)
    {
        if (_ctx == null || _ctx->_disposed) throw new ObjectDisposedException(nameof(Field));
        var source = stencil.Source._ctx;
        if (source != null && !_ctx->CompatibleSpecs(source))
            throw new ArgumentException("Stencil source field was created with a different GridSpec.", nameof(stencil));
        return _ctx->Tick(stamps, tick, new StencilCore
        {
            Source = source,
            DecayPerMille = stencil.DecayPerMille,
            SpreadDenominator = stencil.SpreadDenominator,
        });
    }

    public readonly FieldReader AsReader()
    {
        if (_ctx == null || _ctx->_disposed) throw new ObjectDisposedException(nameof(Field));
        return _ctx->AsReader();
    }

    public readonly void WriteRegion(Int2 min, Int2 size, ReadOnlySpan<int> weights)
    {
        if (_ctx == null || _ctx->_disposed) throw new ObjectDisposedException(nameof(Field));
        _ctx->WriteRegion(min, size, weights);
    }

    public readonly void ReadRegion(Int2 min, Int2 size, Span<int> destination)
    {
        if (_ctx == null || _ctx->_disposed) throw new ObjectDisposedException(nameof(Field));
        _ctx->ReadRegion(min, size, destination);
    }

    internal readonly int ActiveSlot(int index) => _ctx->ActiveSlot(index);
    internal readonly Int2 CoordOf(int slot) => _ctx->CoordOf(slot);
    internal readonly CoordMap SlotMap => _ctx->SlotMap;
    internal readonly uint* LastWrittenPointer => _ctx->LastWrittenPointer;
    internal readonly int LastWrittenLength => _ctx->LastWrittenLength;
    internal readonly int FreeSlotCount => _ctx->FreeSlotCount;

    public readonly bool Equals(Field other) => _ctx == other._ctx;
    public readonly override bool Equals(object? obj) => obj is Field other && Equals(other);
    public readonly override int GetHashCode() => (int)(nint)_ctx;
    public static bool operator ==(Field left, Field right) => left._ctx == right._ctx;
    public static bool operator !=(Field left, Field right) => left._ctx != right._ctx;

    public void Dispose()
    {
        if (_ctx == null) return;
        _pool?.Dispose();
        if (_ctx->_poolHandle != 0)
            System.Runtime.InteropServices.GCHandle.FromIntPtr(_ctx->_poolHandle).Free();
        _ctx->DisposeState();
        System.Runtime.InteropServices.NativeMemory.Free(_ctx);
        _ctx = null;
    }
}
