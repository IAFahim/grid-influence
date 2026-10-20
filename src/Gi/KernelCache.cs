namespace Gi;

internal enum KernelShape : byte
{
    Box,
    Separable,
    Spans,
    Dense
}

internal readonly unsafe struct KernelTaps
{
    public readonly Plane Plane;
    public readonly KernelShape Shape;
    public readonly int OriginX;
    public readonly int OriginY;
    public readonly int Width;
    public readonly int Height;
    public readonly int MaxTap;
    public readonly long MaxTapQ40;
    public readonly int* RowExtents;
    public readonly int* Cells;
    public readonly long* Q40;

    internal KernelTaps(
        Plane plane, KernelShape shape, int originX, int originY, int width, int height,
        int maxTap, long maxTapQ40, int* rowExtents, int* cells, long* q40)
    {
        Plane = plane;
        Shape = shape;
        OriginX = originX;
        OriginY = originY;
        Width = width;
        Height = height;
        MaxTap = maxTap;
        MaxTapQ40 = maxTapQ40;
        RowExtents = rowExtents;
        Cells = cells;
        Q40 = q40;
    }
}

internal readonly struct KernelKey
{
    public readonly ulong Main;
    public readonly ulong Turn;

    internal KernelKey(byte stamp, int sampling, int grid, int phaseX, int phaseY, int angle)
    {
        Main = stamp | ((ulong)sampling << 8) | ((ulong)grid << 36) | ((ulong)phaseX << 41) | ((ulong)phaseY << 49);
        Turn = (ulong)angle & 0xFFFFul;
    }
}

internal struct KernelEntry
{
    public ulong Key;
    public ulong Turn;
    public Plane Plane;
    public KernelShape Shape;
    public int OriginX;
    public int OriginY;
    public int Width;
    public int Height;
    public int MaxTap;
    public long MaxTapQ40;
    public int Slot;
}

internal unsafe struct KernelCache
{
    internal const int EntryCount = 32;
    internal const int MaxSide = 64;
    internal const int MaxCells = MaxSide * MaxSide;
    internal const int CanvasCellLimit = 1 << 22;
    internal const int FrameInsertCap = 8;

    private NativeBuffer<KernelEntry> _entries;
    private NativeBuffer<long> _taps;
    private NativeBuffer<int> _rows;
    private NativeBuffer<long> _canvas;
    private NativeBuffer<int> _canvasRows;
    private int _cursor;
    private int _inserts;

    public readonly long Bytes =>
        (long)_entries.Capacity * sizeof(KernelEntry) + (long)_taps.Capacity * sizeof(long) +
        ((long)_rows.Capacity + _canvasRows.Capacity) * sizeof(int) + (long)_canvas.Capacity * sizeof(long);

    public readonly int LiveEntries
    {
        get
        {
            var entries = _entries.Pointer;
            if (entries == null) return 0;

            var count = 0;
            for (var i = 0; i < EntryCount; i++)
                if (entries[i].Key != 0 || entries[i].Turn != 0)
                    count++;

            return count;
        }
    }

    public readonly KernelEntry* Find(ulong key, ulong turn)
    {
        var entries = _entries.Pointer;
        if (entries == null) return null;

        for (var i = 0; i < EntryCount; i++)
            if (entries[i].Key == key && entries[i].Turn == turn)
                return entries + i;

        return null;
    }

    public readonly KernelTaps View(KernelEntry* entry)
    {
        var taps = _taps.Pointer + (long)entry->Slot * MaxCells;
        var rows = _rows.Pointer + (long)entry->Slot * (2 * MaxSide);
        return new KernelTaps(
            entry->Plane, entry->Shape, entry->OriginX, entry->OriginY, entry->Width, entry->Height,
            entry->MaxTap, entry->MaxTapQ40, rows,
            entry->Plane == Plane.Difference || entry->Plane == Plane.Dense ? (int*)taps : null,
            entry->Plane == Plane.Tent || entry->Plane == Plane.Bell ? taps : null);
    }

    public bool InsertsDisabled;

    public readonly bool CanInsert => !InsertsDisabled && _inserts < FrameInsertCap;


    public void BeginFrame()
    {
        _inserts = 0;
    }

    public void Clear()
    {
        if (_entries.Pointer != null) new Span<KernelEntry>(_entries.Pointer, EntryCount).Clear();
        _cursor = 0;
        _inserts = 0;
    }

    public void Release()
    {
        _entries.Free();
        _taps.Free();
        _rows.Free();
        _canvas.Free();
        _canvasRows.Free();
        _cursor = 0;
    }

    internal KernelEntry* Insert(
        ulong key, ulong turn, Plane plane, KernelShape shape,
        int originX, int originY, int width, int height, int maxTap, long maxTapQ40,
        int* rowExtents, int* cells, long* q40)
    {
        EnsureStorage();
        _inserts++;
        var slot = _cursor;
        _cursor = slot + 1 == EntryCount ? 0 : slot + 1;

        var entry = _entries.Pointer + slot;
        entry->Key = key;
        entry->Turn = turn;
        entry->Plane = plane;
        entry->Shape = shape;
        entry->OriginX = originX;
        entry->OriginY = originY;
        entry->Width = width;
        entry->Height = height;
        entry->MaxTap = maxTap;
        entry->MaxTapQ40 = maxTapQ40;
        entry->Slot = slot;

        var taps = _taps.Pointer + (long)slot * MaxCells;
        if (q40 != null) Buffer.MemoryCopy(q40, taps, MaxCells * sizeof(long), width * height * sizeof(long));
        else Buffer.MemoryCopy(cells, taps, MaxCells * sizeof(long), width * height * sizeof(int));

        var rows = _rows.Pointer + (long)slot * (2 * MaxSide);
        Buffer.MemoryCopy(rowExtents, rows, 2 * MaxSide * sizeof(int), 2 * height * sizeof(int));
        return entry;
    }

    internal int* CanvasInt(int cells)
    {
        _canvas.Resize(cells);
        return (int*)_canvas.Pointer;
    }

    internal long* CanvasLong(int cells)
    {
        _canvas.Resize(cells);
        return _canvas.Pointer;
    }

    internal int* CanvasRows(int rows)
    {
        _canvasRows.Resize(2 * rows);
        return _canvasRows.Pointer;
    }

    private void EnsureStorage()
    {
        if (_entries.Pointer != null) return;

        _entries.Resize(EntryCount);
        _taps.Resize(EntryCount * MaxCells);
        _rows.Resize(EntryCount * 2 * MaxSide);
    }
}

internal static unsafe class KernelFactory
{
    public static bool TryFindTaps(
        WorldCtx* w, int grid, byte stamp, int angle, Layer.Precision precision, Placement* p, out KernelTaps taps)
    {
        taps = default;
        if (!TileBake.Unclipped(p->V, p)) return false;

        var masked = p->V->Kind >= StampKind.Disk && p->V->Arc == 0 ? 0 : angle & precision.TurnMask;
        var key = new KernelKey(stamp, p->Sampling, grid, p->Fx, p->Fy, masked);
        var hit = w->Kernels.Find(key.Main, key.Turn);
        if (hit == null) return false;

        taps = w->Kernels.View(hit);
        return true;
    }

    public static bool TryGetTaps(
        WorldCtx* w, int grid, byte stamp, int angle, Layer.Precision precision, Placement* p, out KernelTaps taps)
    {
        var cache = &w->Kernels;
        var clipped = !TileBake.Unclipped(p->V, p);
        var masked = p->V->Kind >= StampKind.Disk && p->V->Arc == 0 ? 0 : angle & precision.TurnMask;
        var key = new KernelKey(stamp, p->Sampling, grid, p->Fx, p->Fy, masked);
        if (!clipped)
        {
            var hit = cache->Find(key.Main, key.Turn);
            if (hit != null)
            {
                taps = cache->View(hit);
                return true;
            }
        }

        RelativeRect(p, masked, out var rx0, out var ry0, out var rx1, out var ry1, out var rasterX1, out var rasterY1);
        var width = rx1 - rx0;
        var height = ry1 - ry0;
        if (width <= 0 || height <= 0)
        {
            taps = new KernelTaps(p->Plane, ShapeOf(p), 0, 0, 0, 0, 0, 0, null, null, null);
            return true;
        }

        if ((long)width * height > KernelCache.CanvasCellLimit)
        {
            taps = default;
            return false;
        }

        var cells = width * height;
        var intKernel = p->Plane == Plane.Difference || p->Plane == Plane.Dense;
        var ints = intKernel ? cache->CanvasInt(cells) : null;
        var longs = intKernel ? null : cache->CanvasLong(cells);
        if (intKernel) new Span<int>(ints, cells).Clear();
        else new Span<long>(longs, cells).Clear();

        var q = *p;
        if (p->Turned)
        {
            q.X0 = p->Px + rx0;
            q.X1 = p->Px + rx1;
            q.Y0 = p->Py + ry0;
            q.Y1 = p->Py + ry1;
        }

        Capture(&q, p->Plane, ints, longs, p->Px + rx0, p->Py + ry0, p->Px + rx1, p->Py + ry1, width, rasterX1,
            rasterY1);

        var extents = cache->CanvasRows(height);
        Measure(ints, longs, width, height, extents,
            out var minX, out var minY, out var maxX, out var maxY, out var maxTap);
        var maxTapInt = intKernel ? (int)maxTap : 0;

        var originX = rx0 + minX;
        var originY = ry0 + minY;
        var kernelWidth = maxX - minX + 1;
        var kernelHeight = maxY - minY + 1;
        if (maxX >= 0) Compact(ints, longs, width, minX, minY, kernelWidth, kernelHeight);
        if (!clipped && width <= KernelCache.MaxSide && height <= KernelCache.MaxSide)
        {
            var entry = cache->Insert(
                key.Main, key.Turn, p->Plane, ShapeOf(p),
                originX, originY, kernelWidth, kernelHeight, maxTapInt, maxTap,
                extents, ints, longs);
            taps = cache->View(entry);
            return true;
        }

        taps = new KernelTaps(
            p->Plane, ShapeOf(p), originX, originY, kernelWidth, kernelHeight,
            maxTapInt, maxTap, extents, ints, longs);
        return true;
    }

    private static void EmitTaps(
        Placement* p, int* difference, int* dense, long* smooth, int tileX0, int tileY0, int gain, int rasterX1,
        int rasterY1)
    {
        if (p->Turned)
        {
            if (p->Plane == Plane.Dense) TileBake.EmitTurnedDense(dense, tileX0, tileY0, p, gain);
            else TileBake.EmitTurnedSmooth(smooth, tileX0, tileY0, p, gain);
            return;
        }

        switch (p->Plane)
        {
            case Plane.Difference:
                TileBake.EmitBox(difference, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V,
                    gain);
                return;
            case Plane.Dense:
                TileBake.EmitRaster(dense, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, rasterX1, rasterY1, p->Sampling,
                    p->V, gain);
                return;
            case Plane.Tent:
                TileBake.EmitTent(smooth, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V,
                    gain);
                return;
            default:
                TileBake.EmitBell(smooth, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V,
                    gain);
                return;
        }
    }

    private static KernelShape ShapeOf(Placement* p)
    {
        if (p->Turned) return p->Kind >= StampKind.Disk && p->V->Arc == 0 ? KernelShape.Spans : KernelShape.Dense;
        return p->Plane switch
        {
            Plane.Difference => KernelShape.Box,
            Plane.Tent or Plane.Bell => KernelShape.Separable,
            _ => KernelShape.Dense
        };
    }

    private static void RelativeRect(
        Placement* p, int maskedAngle, out int rx0, out int ry0, out int rx1, out int ry1, out int rasterX1,
        out int rasterY1)
    {
        if (p->Turned)
        {
            long bx0, by0, bx1, by1;
            if (p->Kind >= StampKind.Disk)
                TileBake.RoundFootprintCells(p->Arc, p->CentreX, p->CentreY, p->Bound, p->V, maskedAngle, out bx0,
                    out by0, out bx1, out by1);
            else
                TileBake.TurnedFootprintCells(p->CentreX, p->CentreY, p->Cos, p->Sin, p->SupportU, p->SupportV, out bx0,
                    out by0, out bx1, out by1);
            TileBake.KernelCells(bx0, bx1, out var fx0, out var fx1);
            TileBake.KernelCells(by0, by1, out var fy0, out var fy1);
            rx0 = fx0 - p->Px;
            rx1 = fx1 - p->Px;
            ry0 = fy0 - p->Py;
            ry1 = fy1 - p->Py;
            rasterX1 = 0;
            rasterY1 = 0;
            return;
        }

        if (p->Plane == Plane.Tent || p->Plane == Plane.Bell)
        {
            var ax = TileBake.Axis(p->Kind, p->Px, p->Fx, p->ExtentX);
            var ay = TileBake.Axis(p->Kind, p->Py, p->Fy, p->ExtentY);
            rx0 = ax.First - p->Px;
            rx1 = ax.Last + 1 - p->Px;
            ry0 = ay.First - p->Py;
            ry1 = ay.Last + 1 - p->Py;
            rasterX1 = 0;
            rasterY1 = 0;
            return;
        }

        rx0 = 0;
        rx1 = (p->Fx + p->ExtentX + 255) >> 8;
        ry0 = 0;
        ry1 = (p->Fy + p->ExtentY + 255) >> 8;
        rasterX1 = p->Px + rx1;
        rasterY1 = p->Py + ry1;
    }

    private static void Capture(
        Placement* q, Plane plane, int* ints, long* longs,
        int ax0, int ay0, int ax1, int ay1, int width, int rasterX1, int rasterY1)
    {
        var difference = stackalloc int[TileBake.DiffRows * TileBake.DiffPitch];
        var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
        var smooth = stackalloc long[TileBake.TileSize * TileBake.TileSize];
        var carryRow = stackalloc int[TileBake.TileSize];

        for (var ty = ay0 >> TileBake.TileBits; ty <= (ay1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = ax0 >> TileBake.TileBits; tx <= (ax1 - 1) >> TileBake.TileBits; tx++)
        {
            var tx0 = tx << TileBake.TileBits;
            var ty0 = ty << TileBake.TileBits;
            if (plane == Plane.Difference) new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
            else if (plane == Plane.Dense) new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
            else new Span<long>(smooth, TileBake.TileSize * TileBake.TileSize).Clear();
            new Span<int>(carryRow, TileBake.TileSize).Clear();
            EmitTaps(q, difference, dense, smooth, tx0, ty0, 1, rasterX1, rasterY1);

            for (var y = 0; y < TileBake.TileSize; y++)
            {
                var cellY = ty0 + y;
                if (cellY < ay0 || cellY >= ay1) continue;
                var row = cellY - ay0;
                if (plane == Plane.Difference)
                {
                    var diffRow = difference + y * TileBake.DiffPitch;
                    var carry = 0;
                    for (var x = 0; x < TileBake.TileSize; x++)
                    {
                        carry += diffRow[x];
                        var value = carryRow[x] + carry;
                        carryRow[x] = value;
                        var cellX = tx0 + x;
                        if (cellX < ax0 || cellX >= ax1) continue;
                        ints[row * width + cellX - ax0] += value;
                    }
                }
                else if (plane == Plane.Dense)
                {
                    var denseRow = dense + y * TileBake.TileSize;
                    for (var x = 0; x < TileBake.TileSize; x++)
                    {
                        var cellX = tx0 + x;
                        if (cellX < ax0 || cellX >= ax1) continue;
                        var value = denseRow[x];
                        if (value != 0) ints[row * width + cellX - ax0] += value;
                    }
                }
                else
                {
                    var smoothRow = smooth + y * TileBake.TileSize;
                    for (var x = 0; x < TileBake.TileSize; x++)
                    {
                        var cellX = tx0 + x;
                        if (cellX < ax0 || cellX >= ax1) continue;
                        var value = smoothRow[x];
                        if (value != 0) longs[row * width + cellX - ax0] += value;
                    }
                }
            }
        }
    }

    private static void Measure(
        int* ints, long* longs, int width, int height, int* extents,
        out int minX, out int minY, out int maxX, out int maxY, out long maxTap)
    {
        minX = int.MaxValue;
        minY = int.MaxValue;
        maxX = -1;
        maxY = -1;
        maxTap = 0;
        for (var y = 0; y < height; y++)
        {
            var first = -1;
            var last = -1;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                long value;
                long magnitude;
                if (ints != null)
                {
                    value = ints[row + x];
                    magnitude = value < 0 ? -value : value;
                }
                else
                {
                    value = longs[row + x];
                    magnitude = value == long.MinValue ? long.MaxValue : value < 0 ? -value : value;
                }

                if (value == 0) continue;
                if (first < 0) first = x;
                last = x;
                if (magnitude > maxTap) maxTap = magnitude;
            }

            extents[2 * y] = first < 0 ? 0 : first;
            extents[2 * y + 1] = last < 0 ? 0 : last + 1;
            if (last < 0) continue;
            if (first < minX) minX = first;
            if (last > maxX) maxX = last;
            if (y < minY) minY = y;
            maxY = y;
        }

        if (maxX < 0)
        {
            minX = 0;
            minY = 0;
            extents[0] = 0;
            extents[1] = 0;
            return;
        }

        var write = 0;
        for (var y = minY; y <= maxY; y++)
        {
            var end = extents[2 * y + 1];
            extents[2 * write] = end == 0 ? 0 : extents[2 * y] - minX;
            extents[2 * write + 1] = end == 0 ? 0 : end - minX;
            write++;
        }
    }

    private static void Compact(int* ints, long* longs, int width, int minX, int minY, int kernelWidth,
        int kernelHeight)
    {
        if (minX == 0 && minY == 0 && kernelWidth == width) return;

        for (var j = 0; j < kernelHeight; j++)
        {
            var source = (long)(minY + j) * width + minX;
            var target = (long)j * kernelWidth;
            if (ints != null)
                Buffer.MemoryCopy(ints + source, ints + target, kernelWidth * sizeof(int), kernelWidth * sizeof(int));
            else
                Buffer.MemoryCopy(longs + source, longs + target, kernelWidth * sizeof(long),
                    kernelWidth * sizeof(long));
        }
    }
}
