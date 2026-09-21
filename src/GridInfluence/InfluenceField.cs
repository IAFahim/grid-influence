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
    public InfluenceField Source { get; }
    public int DecayPerMille { get; }
    public int SpreadDenominator { get; }

    private Stencil(InfluenceField source, int decayPerMille, int spreadDenominator)
    {
        Source = source;
        DecayPerMille = decayPerMille;
        SpreadDenominator = spreadDenominator;
    }

    public static Stencil Create(InfluenceField source, int decayPerMille, int spreadDenominator)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(decayPerMille);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decayPerMille, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(spreadDenominator, 1);
        return new Stencil(source, decayPerMille, spreadDenominator);
    }
}

public sealed unsafe class InfluenceField : IDisposable
{
    private const int MaxSpansPerSchedule = 1 << 20;
    private const int MaxChunksPerSchedule = 1 << 14;
    private const int CompactionInterval = 60;

    private readonly GridSpec _spec;
    private CoordMap _slotByCoord;
    private NativeBuffer<Int2> _coordBySlot;
    private NativeBuffer<uint> _lastWritten;
    private NativeBuffer<byte> _nonZero;
    private NativeBuffer<uint> _prepared;
    private NativeBuffer<int> _freeSlots;
    private NativeBuffer<int> _activeSlots;
    private NativeBuffer<int> _data;
    private NativeBuffer<Stamp> _sortedStamps;
    private NativeBuffer<int> _offsets;
    private NativeBuffer<WeightedRect> _spans;
    private NativeBuffer<int> _halo;
    private int _slotCount;
    private int _activeCount;
    private int _stampCount;
    private int _chunksActivated;
    private int _spanDrops;
    private int _chunkDrops;
    private uint _frameId = 1;
    private uint _scheduleVersion;
    private bool _disposed;

    private readonly Thread[]? _workers;
    private readonly ManualResetEventSlim[]? _workerSignals;
    private int _haloSlice;
    private long _workClaim;
    private int _workGeneration;
    private int _workPending;
    private int _workCount;
    private int _workPhase;
    private Stencil _parallelStencil;
    private volatile bool _workExit;

    public GridSpec Spec => _spec;
    public uint FrameId => _frameId;
    public int ActiveSlotCount => _activeCount;
    public int SlotCount => _slotCount;

    public InfluenceField(GridSpec spec) : this(spec, 0)
    {
    }

    public InfluenceField(GridSpec spec, int parallelism)
    {
        _spec = spec;
        _slotByCoord = CoordMap.Create(64);
        var workers = parallelism > 1 ? parallelism - 1 : 0;
        _haloSlice = (spec.ChunkSize + 2) * (spec.ChunkSize + 2);
        _halo.Resize(_haloSlice * (workers + 1));
        if (workers > 0)
        {
            _workerSignals = new ManualResetEventSlim[workers];
            _workers = new Thread[workers];
            for (var i = 0; i < workers; i++)
            {
                var index = i + 1;
                _workerSignals[i] = new ManualResetEventSlim(false);
                _workers[i] = new Thread(() => WorkerEntry(index)) { IsBackground = true, Name = $"GridInfluence-Resolve-{index}" };
                _workers[i].Start();
            }
        }
    }

    public FieldStats Tick(ReadOnlySpan<Stamp> stamps, uint tick, Stencil stencil = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stencil.Source != null && !CompatibleSpecs(stencil.Source))
            throw new ArgumentException("Stencil source field was created with a different GridSpec.", nameof(stencil));

        var reset = AdvanceFrame(tick);
        _scheduleVersion = _scheduleVersion == uint.MaxValue ? 1u : _scheduleVersion + 1;
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

    public FieldReader AsReader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new FieldReader(_slotByCoord, _lastWritten.Pointer, _lastWritten.Length, _data.Pointer, _spec, _frameId);
    }

    public void WriteRegion(Int2 min, Int2 size, ReadOnlySpan<int> weights)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            var source = row * size.X;
            for (var cx = min.X >> _spec.Log2; cx <= (min.X + size.X - 1) >> _spec.Log2; cx++)
            {
                var lo = Math.Max(min.X, cx << _spec.Log2);
                var hi = Math.Min(min.X + size.X, (cx + 1) << _spec.Log2);
                var slot = EnsureSlot(new Int2(cx, coordY));
                var chunk = _data.Pointer + (long)slot * elements;
                weights.Slice(source + lo - min.X, hi - lo)
                    .CopyTo(new Span<int>(chunk + (long)localY * stride + (lo & (chunkSize - 1)), hi - lo));
            }
        }

        ActivateRegion(min, size);
    }

    public void ReadRegion(Int2 min, Int2 size, Span<int> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                new ReadOnlySpan<int>(data, width).CopyTo(destination.Slice((y - min.Y) * size.X + lo.X - min.X, width));
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
            _lastWritten.Span[slot] = _frameId;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        if (_workers != null)
        {
            _workExit = true;
            var signals = _workerSignals!;
            for (var i = 0; i < signals.Length; i++) signals[i].Set();
            foreach (var worker in _workers) worker.Join();
            foreach (var signal in signals) signal.Dispose();
        }
        _slotByCoord.Dispose();
        _coordBySlot.Dispose();
        _lastWritten.Dispose();
        _nonZero.Dispose();
        _prepared.Dispose();
        _freeSlots.Dispose();
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

    private bool CompatibleSpecs(InfluenceField other)
        => _spec.Log2 == other._spec.Log2
           && _spec.Stride == other._spec.Stride
           && _spec.RetentionFrames == other._spec.RetentionFrames;

    private bool AdvanceFrame(uint tick)
    {
        var normalized = tick == 0u ? 1u : tick;
        var reset = normalized < _frameId;
        _frameId = normalized;
        return reset;
    }

    private int Prepare(ReadOnlySpan<Stamp> stamps, Stencil stencil, bool reset)
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
        if (_spec.RetentionFrames == uint.MaxValue) return 0;

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
            (long)elements * sizeof(int),
            (long)elements * sizeof(int));

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

    private void ActivateStencilFrontier(Stencil stencil)
    {
        var source = stencil.Source;
        for (var i = 0; i < source._activeCount; i++) ActivateStencilSlot(stencil, source._activeSlots.Span[i]);
    }

    private void ActivateStencilSlot(Stencil stencil, int slot)
    {
        var source = stencil.Source;
        if (!IsStencilSlotLive(source, slot)) return;

        var coord = source._coordBySlot.Span[slot];
        Activate(coord);
        ActivateStencilNeighbours(source, stencil, slot * _spec.ElementsPerChunk, coord);
    }

    private bool IsStencilSlotLive(InfluenceField source, int slot)
    {
        if ((uint)slot < (uint)source._lastWritten.Length && source._lastWritten.Span[slot] != source._frameId)
            return false;

        return (uint)slot >= (uint)source._nonZero.Length || source._nonZero.Span[slot] != 0;
    }

    private void ActivateStencilNeighbours(InfluenceField source, Stencil stencil, int baseIndex, Int2 coord)
    {
        var size = _spec.ChunkSize;
        var stride = _spec.Stride;
        if (NeedsActivationEdge(source, stencil, baseIndex, 0, 0, 0, 1, size, stride)) Activate(coord + new Int2(-1, 0));
        if (NeedsActivationEdge(source, stencil, baseIndex, size - 1, 0, 0, 1, size, stride)) Activate(coord + new Int2(1, 0));
        if (NeedsActivationEdge(source, stencil, baseIndex, 0, 0, 1, 0, size, stride)) Activate(coord + new Int2(0, -1));
        if (NeedsActivationEdge(source, stencil, baseIndex, 0, size - 1, 1, 0, size, stride)) Activate(coord + new Int2(0, 1));
    }

    private bool NeedsActivationEdge(
        InfluenceField source,
        Stencil stencil,
        int baseIndex,
        int startX,
        int startY,
        int dx,
        int dy,
        int count,
        int stride)
    {
        for (var i = 0; i < count; i++)
        {
            var x = startX + i * dx;
            var y = startY + i * dy;
            if (IntegerMath.Outflow(source._data.Span[baseIndex + y * stride + x], stencil.DecayPerMille, stencil.SpreadDenominator) != 0)
                return true;
        }

        return false;
    }

    private void PrepareStamps(ReadOnlySpan<Stamp> stamps)
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
        new Span<int>(_data.Pointer + (long)slot * _spec.ElementsPerChunk, _spec.ElementsPerChunk).Clear();
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
        new Span<int>(_data.Pointer + (long)slot * _spec.ElementsPerChunk, _spec.ElementsPerChunk).Clear();
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
        if (_workers != null && _activeCount >= 128)
        {
            RunParallel(0, _activeCount);
            return;
        }
        for (var i = 0; i < _activeCount; i++)
            new Span<int>(data + (long)active[i] * elements, elements).Clear();
    }

    private void RasterizeAll()
    {
        var offsets = _offsets.Span;
        var sorted = _sortedStamps.Span;
        var spanPointer = _spans.Pointer;
        if (_workers != null && _stampCount >= 64 && _activeCount >= 128)
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
        if (_workers != null && _stampCount >= 64 && _activeCount >= 128)
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

    private void AddCorners(int* field, Int2 lo, Int2 hi, int weight, bool atomic)
    {
        var stride = _spec.Stride;
        if (atomic)
        {
            Interlocked.Add(ref field[(long)lo.Y * stride + lo.X], weight);
            Interlocked.Add(ref field[(long)lo.Y * stride + hi.X], -weight);
            Interlocked.Add(ref field[(long)hi.Y * stride + lo.X], -weight);
            Interlocked.Add(ref field[(long)hi.Y * stride + hi.X], weight);
            return;
        }
        field[(long)lo.Y * stride + lo.X] += weight;
        field[(long)lo.Y * stride + hi.X] -= weight;
        field[(long)hi.Y * stride + lo.X] -= weight;
        field[(long)hi.Y * stride + hi.X] += weight;
    }

    private void RunParallel(int phase, int count)
    {
        _workPhase = phase;
        _workCount = count;
        _workPending = count;
        _workGeneration++;
        Volatile.Write(ref _workClaim, (long)_workGeneration << 32);
        var signals = _workerSignals!;
        var wake = Math.Min(signals.Length, Math.Max(1, count >> 3));
        for (var i = 0; i < wake; i++) signals[i].Set();
        DrainWork(0);
        while (Volatile.Read(ref _workPending) != 0) Thread.SpinWait(1);
    }

    private void WorkerEntry(int worker)
    {
        var signal = _workerSignals![worker - 1];
        while (true)
        {
            signal.Wait();
            if (_workExit) return;
            DrainWork(worker);
            signal.Reset();
        }
    }

    private void DrainWork(int worker)
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
                    default:
                        new Span<int>(_data.Pointer + (long)active[i] * _spec.ElementsPerChunk, _spec.ElementsPerChunk).Clear();
                        break;
                }
                done++;
            }
            Interlocked.Add(ref _workPending, -done);
        }
    }

    private void Resolve(Stencil stencil)
    {
        var active = _activeSlots.Pointer;
        var hasStencil = stencil.Source != null;
        if (_workers != null && _activeCount >= 128 && !ReferenceEquals(stencil.Source, this))
        {
            _parallelStencil = stencil;
            RunParallel(1, _activeCount);
            return;
        }
        for (var i = 0; i < _activeCount; i++) ResolveSlot(active[i], stencil, hasStencil, 0);
    }

    private void ResolveSlot(int slot, Stencil stencil, bool hasStencil, int worker)
    {
        var field = _data.Pointer + (long)slot * _spec.ElementsPerChunk;
        PrefixSumRun(field);

        if (hasStencil) ApplyStencil(stencil, _coordBySlot.Span[slot], field, worker);

        _nonZero.Span[slot] = AnyNonZero(field);
    }

    private void PrefixSumRun(int* field)
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
                a += row0[x]; row0[x] = a;
                b += row1[x]; row1[x] = b;
                c += row2[x]; row2[x] = c;
                d += row3[x]; row3[x] = d;
            }
        }
        for (; y < dimension; y++)
        {
            var row = field + (long)y * stride;
            var running = 0;
            for (var x = 0; x < dimension; x++)
            {
                running += row[x];
                row[x] = running;
            }
        }

        if (Vector.IsHardwareAccelerated && stride >= Vector<int>.Count)
        {
            var lanes = Vector<int>.Count;
            for (var ry = 1; ry < dimension; ry++)
            {
                var above = field + (long)(ry - 1) * stride;
                var current = field + (long)ry * stride;
                for (var x = 0; x <= stride - lanes; x += lanes)
                    (new Vector<int>(new ReadOnlySpan<int>(current + x, lanes)) + new Vector<int>(new ReadOnlySpan<int>(above + x, lanes)))
                        .CopyTo(new Span<int>(current + x, lanes));
            }
        }
        else
        {
            for (var ry = 1; ry < dimension; ry++)
            {
                var above = field + (long)(ry - 1) * stride;
                var current = field + (long)ry * stride;
                for (var x = 0; x < stride; x++) current[x] += above[x];
            }
        }
    }

    private byte AnyNonZero(int* field)
    {
        var chunkSize = _spec.ChunkSize;
        var stride = _spec.Stride;
        var acc = 0;
        var y = 0;
        if (Vector.IsHardwareAccelerated && chunkSize >= Vector<int>.Count)
        {
            var lanes = Vector<int>.Count;
            var vacc = Vector<int>.Zero;
            for (; y < chunkSize; y++)
            {
                var row = field + (long)y * stride;
                for (var x = 0; x <= chunkSize - lanes; x += lanes)
                    vacc |= new Vector<int>(new ReadOnlySpan<int>(row + x, lanes));
                for (var x = chunkSize - chunkSize % lanes; x < chunkSize; x++) acc |= row[x];
            }
            for (var i = 0; i < lanes; i++) acc |= vacc[i];
            return acc != 0 ? (byte)1 : (byte)0;
        }
        for (; y < chunkSize; y++)
        {
            var row = field + (long)y * stride;
            for (var x = 0; x < chunkSize; x++) acc |= row[x];
        }

        return acc != 0 ? (byte)1 : (byte)0;
    }

    private void ApplyStencil(Stencil stencil, Int2 coord, int* field, int worker = 0)
    {
        var source = stencil.Source;
        var haloStride = _spec.ChunkSize + 2;
        var halo = _halo.Pointer + (long)worker * _haloSlice;
        new Span<int>(halo, haloStride * haloStride).Clear();
        DecaySelf(source, stencil, coord, field, halo, haloStride);
        FillHalo(source, stencil, coord, halo, haloStride);
        AddInflow(field, halo, haloStride);
    }

    private void DecaySelf(InfluenceField source, Stencil stencil, Int2 coord, int* field, int* halo, int haloStride)
    {
        if (!source._slotByCoord.TryGetValue(coord, out var slot) || !IsStencilSlotLive(source, slot)) return;

        var self = source._data.Pointer + (long)slot * _spec.ElementsPerChunk;
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

    private static Vector<int> DecayKeepExact(Vector<int> values, long keepFactor)
    {
        Vector.Widen(values, out var lo, out var hi);
        var thousand = new Vector<double>(1000.0);
        return Vector.Narrow(
            Vector.ConvertToInt64(Vector.ConvertToDouble(lo * keepFactor) / thousand),
            Vector.ConvertToInt64(Vector.ConvertToDouble(hi * keepFactor) / thousand));
    }

    private void DecayRow(int* source, int* haloRow, int* target, Stencil stencil)
    {
        var decay = stencil.DecayPerMille;
        var spread = stencil.SpreadDenominator;
        var chunkSize = _spec.ChunkSize;
        var divisor = MagicDivisor.Of(spread);
        var x = 0;
        if (Vector.IsHardwareAccelerated && divisor.IsVectorizable && chunkSize >= Vector<int>.Count)
        {
            var lanes = Vector<int>.Count;
            var keepFactor = (long)(1000 - decay);
            var four = new Vector<int>(4);
            for (; x <= chunkSize - lanes; x += lanes)
            {
                var v = new Vector<int>(new ReadOnlySpan<int>(source + x, lanes));
                var kept = DecayKeepExact(v, keepFactor);
                var outflow = DivideExact(kept, divisor);
                outflow.CopyTo(new Span<int>(haloRow + x, lanes));
                (new Vector<int>(new ReadOnlySpan<int>(target + x, lanes)) + kept - four * outflow)
                    .CopyTo(new Span<int>(target + x, lanes));
            }
        }
        for (; x < chunkSize; x++)
        {
            var kept = IntegerMath.DecayKeep(source[x], decay);
            var outflow = kept / spread;
            haloRow[x] = outflow;
            target[x] += kept - 4 * outflow;
        }
    }

    private void FillHalo(InfluenceField source, Stencil stencil, Int2 coord, int* halo, int haloStride)
    {
        var chunkSize = _spec.ChunkSize;
        FillHaloColumn(source, stencil, coord + new Int2(-1, 0), chunkSize - 1, 0, halo, haloStride);
        FillHaloColumn(source, stencil, coord + new Int2(1, 0), 0, chunkSize + 1, halo, haloStride);
        FillHaloRow(source, stencil, coord + new Int2(0, -1), chunkSize - 1, 0, halo, haloStride);
        FillHaloRow(source, stencil, coord + new Int2(0, 1), 0, chunkSize + 1, halo, haloStride);
    }

    private void FillHaloColumn(
        InfluenceField source,
        Stencil stencil,
        Int2 coord,
        int sourceX,
        int haloX,
        int* halo,
        int haloStride)
    {
        if (!source._slotByCoord.TryGetValue(coord, out var slot) || !IsStencilSlotLive(source, slot)) return;

        var start = source._data.Pointer + (long)slot * _spec.ElementsPerChunk + sourceX;
        var chunkSize = _spec.ChunkSize;
        var stride = _spec.Stride;
        var decay = stencil.DecayPerMille;
        var spread = stencil.SpreadDenominator;
        var divisor = MagicDivisor.Of(spread);
        var y = 0;
        if (Vector.IsHardwareAccelerated && divisor.IsVectorizable && chunkSize >= Vector<int>.Count)
        {
            var lanes = Vector<int>.Count;
            Span<int> gathered = stackalloc int[Vector<int>.Count];
            var keepFactor = (long)(1000 - decay);
            for (; y <= chunkSize - lanes; y += lanes)
            {
                for (var j = 0; j < lanes; j++) gathered[j] = start[(long)(y + j) * stride];
                var outflow = DivideExact(DecayKeepExact(new Vector<int>(gathered), keepFactor), divisor);
                for (var j = 0; j < lanes; j++)
                    halo[(long)(y + j + 1) * haloStride + haloX] = outflow[j];
            }
        }
        for (; y < chunkSize; y++)
            halo[(long)(y + 1) * haloStride + haloX] =
                IntegerMath.Outflow(start[(long)y * stride], decay, spread);
    }

    private void FillHaloRow(
        InfluenceField source,
        Stencil stencil,
        Int2 coord,
        int sourceY,
        int haloY,
        int* halo,
        int haloStride)
    {
        if (!source._slotByCoord.TryGetValue(coord, out var slot) || !IsStencilSlotLive(source, slot)) return;

        var start = source._data.Pointer + (long)slot * _spec.ElementsPerChunk + (long)sourceY * _spec.Stride;
        var chunkSize = _spec.ChunkSize;
        var decay = stencil.DecayPerMille;
        var spread = stencil.SpreadDenominator;
        var divisor = MagicDivisor.Of(spread);
        var x = 0;
        if (Vector.IsHardwareAccelerated && divisor.IsVectorizable && chunkSize >= Vector<int>.Count)
        {
            var lanes = Vector<int>.Count;
            var keepFactor = (long)(1000 - decay);
            for (; x <= chunkSize - lanes; x += lanes)
            {
                var outflow = DivideExact(
                    DecayKeepExact(new Vector<int>(new ReadOnlySpan<int>(start + x, lanes)), keepFactor), divisor);
                outflow.CopyTo(new Span<int>(halo + (long)haloY * haloStride + x + 1, lanes));
            }
        }
        for (; x < chunkSize; x++)
            halo[(long)haloY * haloStride + x + 1] =
                IntegerMath.Outflow(start[x], decay, spread);
    }

    private void AddInflow(int* field, int* halo, int haloStride)
    {
        for (var y = 0; y < _spec.ChunkSize; y++)
            AddInflowRow(field + (long)y * _spec.Stride, halo + (long)(y + 1) * haloStride + 1, haloStride);
    }

    private void AddInflowRow(int* target, int* center, int haloStride)
    {
        var below = center - haloStride;
        var above = center + haloStride;
        var chunkSize = _spec.ChunkSize;
        var x = 0;
        if (Vector.IsHardwareAccelerated && chunkSize >= Vector<int>.Count)
        {
            var lanes = Vector<int>.Count;
            for (; x <= chunkSize - lanes; x += lanes)
            {
                var sum = new Vector<int>(new ReadOnlySpan<int>(center + x - 1, lanes))
                    + new Vector<int>(new ReadOnlySpan<int>(center + x + 1, lanes))
                    + new Vector<int>(new ReadOnlySpan<int>(below + x, lanes))
                    + new Vector<int>(new ReadOnlySpan<int>(above + x, lanes));
                (new Vector<int>(new ReadOnlySpan<int>(target + x, lanes)) + sum)
                    .CopyTo(new Span<int>(target + x, lanes));
            }
        }
        for (; x < chunkSize; x++)
            target[x] += center[x - 1] + center[x + 1] + below[x] + above[x];
    }
}
