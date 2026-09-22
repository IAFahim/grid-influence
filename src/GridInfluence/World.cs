using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

public readonly struct Float2
{
    public readonly float X, Y;
    public Float2(float x, float y) { X = x; Y = y; }
}

internal struct MarkRec
{
    public int X, Y, R, W;
}

internal unsafe struct LayerState
{
    public MarkRec* Marks;
    public int MarksCount;
    public int MarksCursor;
    public int MarksCapacity;
    public int* TileOffset;
    public int* TileCount;
    public int* TileMarks;
    public int TileMarksCapacity;
    public uint BuiltGen;
}

internal unsafe struct GridCtx
{
    public int Log2, Size, Mask;
    public float OriginX, OriginY, WorldSize, Scale, InvSize;
    public int TileShift, TilesPerSide, TileCountPerLayer;
    public LayerState* Layers;
    public int GridMul;
    public byte GridFade;
    public byte* LayerFade;
    public int* LayerMul;
    public uint* LayerGen;
    public int* Gain;
    public int HasLayerFade;
    public uint ApplyGen;
}

internal unsafe struct QueueEntry
{
    public Float2* Pos;
    public float* Bounds;
    public byte* Stamps;
    public byte* Fades;
    public short* Mul;
    public sbyte* GridHint;
    public int Count;
    public int MulCapacity;
    public int HintCapacity;
    public int HasFade;
    public byte Layer;
}

internal unsafe struct WorldCtx
{
    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public QueueEntry* Queue;
    public int QueueCount;
    public int QueueCapacity;
}

public static unsafe class World
{
    private const int MaxWorlds = 32;
    private const int MaxGrids = 32;
    private const int MaxLayers = 32;
    private const int TileBits = 5;

    private static readonly WorldCtx* Worlds =
        (WorldCtx*)NativeMemory.AllocZeroed((nuint)(MaxWorlds * sizeof(WorldCtx)));
    private static int _worldCount;

    internal static readonly byte* StampShape = (byte*)NativeMemory.AllocZeroed(256);
    internal static readonly sbyte* StampStrength = (sbyte*)NativeMemory.AllocZeroed(256);
    internal static readonly ushort* StampData = (ushort*)NativeMemory.AllocZeroed(512);
    private static int _stampCount = 1;

    internal static readonly int* FadeRate = (int*)NativeMemory.AllocZeroed((nuint)(256 * sizeof(int)));
    private static int _fadeCount = 1;

    public static byte New()
    {
        var id = (byte)_worldCount++;
        var w = Worlds + id;
        w->Grids = (GridCtx*)NativeMemory.AllocZeroed((nuint)(MaxGrids * sizeof(GridCtx)));
        w->QueueCapacity = 64;
        w->Queue = (QueueEntry*)NativeMemory.AllocZeroed((nuint)(w->QueueCapacity * sizeof(QueueEntry)));
        return id;
    }

    public static byte Grid(byte world, int power, float x, float y, float size)
    {
        var w = Worlds + world;
        var id = (byte)w->GridCount++;
        var g = w->Grids + id;
        g->Log2 = power;
        g->Size = 1 << power;
        g->Mask = g->Size - 1;
        g->OriginX = x;
        g->OriginY = y;
        g->WorldSize = size;
        g->Scale = g->Size / size;
        g->InvSize = 1f / size;
        g->TileShift = Math.Min(TileBits, power);
        g->TilesPerSide = g->Size >> g->TileShift;
        g->TileCountPerLayer = g->TilesPerSide * g->TilesPerSide;
        g->Layers = (LayerState*)NativeMemory.AllocZeroed((nuint)(MaxLayers * sizeof(LayerState)));
        for (var l = 0; l < MaxLayers; l++)
        {
            var ls = g->Layers + l;
            ls->MarksCapacity = 4096;
            ls->Marks = (MarkRec*)NativeMemory.AlignedAlloc((nuint)(ls->MarksCapacity * sizeof(MarkRec)), 64);
            ls->TileOffset = (int*)NativeMemory.AlignedAlloc((nuint)(g->TileCountPerLayer * sizeof(int)), 64);
            ls->TileCount = (int*)NativeMemory.AlignedAlloc((nuint)(g->TileCountPerLayer * sizeof(int)), 64);
            ls->TileMarksCapacity = 1 << 14;
            ls->TileMarks = (int*)NativeMemory.AlignedAlloc((nuint)(ls->TileMarksCapacity * sizeof(int)), 64);
        }
        g->LayerMul = (int*)NativeMemory.AlignedAlloc((nuint)(MaxLayers * sizeof(int)), 64);
        g->LayerFade = (byte*)NativeMemory.AllocZeroed((nuint)MaxLayers);
        g->LayerGen = (uint*)NativeMemory.AllocZeroed((nuint)(MaxLayers * sizeof(uint)), 64);
        g->Gain = (int*)NativeMemory.AlignedAlloc((nuint)(MaxLayers * sizeof(int)), 64);
        g->GridMul = 1000;
        for (var i = 0; i < MaxLayers; i++) { g->LayerMul[i] = 1000; g->Gain[i] = 1000; }
        return id;
    }

    public static byte Layer(byte world) => (byte)(Worlds + world)->LayerCount++;

    internal static byte StampNext() => (byte)_stampCount++;
    internal static byte FadeNext(int percent)
    {
        var id = (byte)_fadeCount++;
        FadeRate[id] = 1000 - Math.Clamp(percent, 0, 100) * 10;
        return id;
    }

    public static void Queue(
        byte world, byte layer,
        Float2* positions, float* bounds, byte* stamps, byte* fades, int count)
    {
        var w = Worlds + world;
        if (w->QueueCount >= w->QueueCapacity) GrowQueue(w);
        var q = w->Queue + w->QueueCount++;
        q->Pos = positions;
        q->Bounds = bounds;
        q->Stamps = stamps;
        q->Fades = fades;
        q->Count = count;
        q->Layer = layer;
        if (q->MulCapacity < count)
        {
            if (q->Mul != null) NativeMemory.Free(q->Mul);
            q->MulCapacity = Math.Max(count, 64);
            q->Mul = (short*)NativeMemory.AlignedAlloc((nuint)(q->MulCapacity * sizeof(short)), 64);
            for (var i = 0; i < q->MulCapacity; i++) q->Mul[i] = 1000;
        }
        if (q->HintCapacity < count)
        {
            if (q->GridHint != null) NativeMemory.Free(q->GridHint);
            q->HintCapacity = Math.Max(count, 64);
            q->GridHint = (sbyte*)NativeMemory.Alloc((nuint)q->HintCapacity);
            for (var i = 0; i < q->HintCapacity; i++) q->GridHint[i] = -1;
        }
        q->HasFade = 0;
        for (var i = 0; i < count; i++) if (fades[i] != 0) { q->HasFade = 1; break; }
    }

    public static void ClearQueue(byte world)
    {
        var w = Worlds + world;
        for (var e = 0; e < w->QueueCount; e++)
        {
            var q = w->Queue + e;
            if (q->Mul != null) { NativeMemory.Free(q->Mul); q->Mul = null; }
            if (q->GridHint != null) { NativeMemory.Free(q->GridHint); q->GridHint = null; }
            q->MulCapacity = 0; q->HintCapacity = 0;
        }
        w->QueueCount = 0;
    }

    public static void FadeLayer(byte world, byte grid, byte layer, byte fade)
    {
        var g = (Worlds + world)->Grids + grid;
        g->LayerFade[layer] = fade;
        g->HasLayerFade = 1;
    }

    public static void FadeGrid(byte world, byte grid, byte fade)
        => (Worlds + world)->Grids[grid].GridFade = fade;

    private static void GrowQueue(WorldCtx* w)
    {
        var cap = w->QueueCapacity * 2;
        var next = (QueueEntry*)NativeMemory.AllocZeroed((nuint)(cap * sizeof(QueueEntry)));
        Buffer.MemoryCopy(w->Queue, next, (long)cap * sizeof(QueueEntry),
            (long)w->QueueCapacity * sizeof(QueueEntry));
        NativeMemory.Free(w->Queue);
        w->Queue = next;
        w->QueueCapacity = cap;
    }

    public static void BeginApply(byte world)
    {
        var w = Worlds + world;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->GridFade != 0) g->GridMul = g->GridMul * FadeRate[g->GridFade] / 1000;
            if (g->HasLayerFade != 0)
                for (var l = 0; l < w->LayerCount; l++)
                    if (g->LayerFade[l] != 0) g->LayerMul[l] = g->LayerMul[l] * FadeRate[g->LayerFade[l]] / 1000;
            for (var l = 0; l < w->LayerCount; l++)
            {
                g->Gain[l] = g->GridMul * g->LayerMul[l] / 1000;
                g->Layers[l].MarksCursor = 0;
            }
            g->ApplyGen++;
        }
    }

    public static void Apply(byte world)
    {
        BeginApply(world);
        var w = Worlds + world;
        for (var e = 0; e < w->QueueCount; e++) ApplySlice(world, e, 0, (w->Queue + e)->Count);
    }

    public static void ApplySlice(byte world, int queueEntry, int start, int count)
    {
        var w = Worlds + world;
        var q = w->Queue + queueEntry;
        var pos = q->Pos;
        var bounds = q->Bounds;
        var stamps = q->Stamps;
        var fades = q->Fades;
        var mul = q->Mul;
        var hints = q->GridHint;
        var hasFade = q->HasFade;
        var end = Math.Min(start + count, q->Count);
        var layer = q->Layer;
        for (var i = start; i < end; i++)
        {
            var data = StampData[stamps[i]];
            var wgt = hasFade != 0
                ? (sbyte)(data & 0xFF) * mul[i] / 1000
                : (sbyte)(data & 0xFF);
            var fade = fades[i];
            if (fade != 0) mul[i] = (short)(mul[i] * FadeRate[fade] / 1000);
            if (wgt == 0) continue;
            var shape = data >> 8;
            var px = pos[i].X;
            var py = pos[i].Y;
            var bd = bounds[i];
            var wx0 = px - bd; var wx1 = px + bd;
            var wy0 = py - bd; var wy1 = py + bd;
            var hint = hints[i];
            if (hint >= 0 && hint < w->GridCount)
            {
                var g = w->Grids + hint;
                if (wx0 >= g->OriginX && wx1 <= g->OriginX + g->WorldSize &&
                    wy0 >= g->OriginY && wy1 <= g->OriginY + g->WorldSize)
                {
                    EmitMark(w, g, layer, px, py, bd, wgt, shape);
                    continue;
                }
            }
            var hit = false;
            for (var gi = 0; gi < w->GridCount; gi++)
            {
                var g = w->Grids + gi;
                if (wx1 < g->OriginX || wx0 > g->OriginX + g->WorldSize ||
                    wy1 < g->OriginY || wy0 > g->OriginY + g->WorldSize) continue;
                EmitMark(w, g, layer, px, py, bd, wgt, shape);
                if (!hit) { hints[i] = (sbyte)gi; hit = true; }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitMark(WorldCtx* w, GridCtx* g, int layer, float px, float py, float bd, int wgt, int shape)
    {
        var fx = (px - g->OriginX) * g->InvSize;
        var fy = (py - g->OriginY) * g->InvSize;
        var cx = (int)(fx * g->Size);
        var cy = (int)(fy * g->Size);
        var r = (int)(bd * g->Scale);
        if (r < 0) r = 0;
        var wfinal = wgt * g->Gain[layer] / 1000;
        if (wfinal == 0) return;
        var ls = g->Layers + layer;
        var slot = Interlocked.Increment(ref ls->MarksCursor) - 1;
        if (slot >= ls->MarksCapacity) return;
        var m = ls->Marks + slot;
        m->X = cx; m->Y = cy; m->R = r; m->W = wfinal | (shape << 24);
    }

    private static void GrowMarks(LayerState* ls)
    {
        var cap = ls->MarksCapacity * 2;
        var next = (MarkRec*)NativeMemory.AlignedAlloc((nuint)(cap * sizeof(MarkRec)), 64);
        Buffer.MemoryCopy(ls->Marks, next, (long)cap * sizeof(MarkRec), (long)ls->MarksCount * sizeof(MarkRec));
        NativeMemory.AlignedFree(ls->Marks);
        ls->Marks = next;
        ls->MarksCapacity = cap;
    }

    private static void BuildBuckets(GridCtx* g, int layer)
    {
        var ls = g->Layers + layer;
        var tiles = g->TileCountPerLayer;
        new Span<int>(ls->TileCount, tiles).Clear();
        var t = g->TileShift;
        var ts = g->TilesPerSide;
        var n = ls->MarksCount;
        var marks = ls->Marks;
        for (var i = 0; i < n; i++)
        {
            var m = marks + i;
            var tx0 = Math.Max(0, (m->X - m->R) >> t); var tx1 = Math.Min(ts - 1, (m->X + m->R) >> t);
            var ty0 = Math.Max(0, (m->Y - m->R) >> t); var ty1 = Math.Min(ts - 1, (m->Y + m->R) >> t);
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
                ls->TileCount[ty * ts + tx]++;
        }
        var run = 0;
        for (var i = 0; i < tiles; i++) { ls->TileOffset[i] = run; run += ls->TileCount[i]; }
        if (run > ls->TileMarksCapacity)
        {
            var cap = Math.Max(run, ls->TileMarksCapacity * 2);
            var next = (int*)NativeMemory.AlignedAlloc((nuint)(cap * sizeof(int)), 64);
            NativeMemory.AlignedFree(ls->TileMarks);
            ls->TileMarks = next;
            ls->TileMarksCapacity = cap;
        }
        var cursor = stackalloc int[4096];
        for (var i = 0; i < tiles && i < 4096; i++) cursor[i] = ls->TileOffset[i];
        int* curHeap = null;
        if (tiles > 4096)
        {
            curHeap = (int*)NativeMemory.AlignedAlloc((nuint)(tiles * sizeof(int)), 64);
            for (var i = 0; i < tiles; i++) curHeap[i] = ls->TileOffset[i];
            cursor = curHeap;
        }
        for (var i = 0; i < n; i++)
        {
            var m = marks + i;
            var tx0 = Math.Max(0, (m->X - m->R) >> t); var tx1 = Math.Min(ts - 1, (m->X + m->R) >> t);
            var ty0 = Math.Max(0, (m->Y - m->R) >> t); var ty1 = Math.Min(ts - 1, (m->Y + m->R) >> t);
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var at = ty * ts + tx;
                ls->TileMarks[cursor[at]++] = i;
            }
        }
        if (curHeap != null) NativeMemory.Free(curHeap);
        ls->BuiltGen = g->ApplyGen;
    }

    public static short Cell(byte world, byte grid, byte layer, int x, int y)
    {
        var w = Worlds + world;
        if (grid >= w->GridCount || layer >= w->LayerCount) return 0;
        var g = w->Grids + grid;
        var ls = g->Layers + layer;
        if (ls->MarksCursor <= 0) return 0;
        ls->MarksCount = Math.Min(ls->MarksCursor, ls->MarksCapacity);
        if (ls->BuiltGen != g->ApplyGen) BuildBuckets(g, layer);
        var ts = g->TilesPerSide;
        var tile = (y >> g->TileShift) * ts + (x >> g->TileShift);
        var start = ls->TileOffset[tile];
        var count = ls->TileCount[tile];
        var marks = ls->Marks;
        var sum = 0;
        for (var i = start; i < start + count; i++)
        {
            var m = marks + ls->TileMarks[i];
            var dx = x - m->X; if (dx < 0) dx = -dx;
            var dy = y - m->Y; if (dy < 0) dy = -dy;
            var covered = (m->W >> 24) == 0
                ? dx * dx + dy * dy <= (long)m->R * m->R
                : dx <= m->R && dy <= m->R;
            if (covered) sum += m->W & 0xFFFFFF;
        }
        return (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
    }

    public static long Total(byte world, byte grid, byte layer, int x, int y, int wdt, int hgt)
    {
        var sum = 0L;
        for (var cy = y; cy < y + hgt; cy++)
        for (var cx = x; cx < x + wdt; cx++)
            sum += Cell(world, grid, layer, cx, cy);
        return sum;
    }
}

public static unsafe class Fade
{
    public static byte Stamp(int percent) => World.FadeNext(percent);
    public static byte Layer(int percent) => World.FadeNext(percent);
    public static byte Grid(int percent) => World.FadeNext(percent);
}

public static unsafe class Stamps
{
    public static byte Circle(sbyte strength) => Register(0, strength);
    public static byte Box(sbyte strength) => Register(1, strength);
    public static byte Ring(sbyte strength) => Register(2, strength);

    private static byte Register(byte shape, sbyte strength)
    {
        var id = World.StampNext();
        World.StampShape[id] = shape;
        World.StampStrength[id] = strength;
        World.StampData[id] = (ushort)((byte)strength | (shape << 8));
        return id;
    }
}
