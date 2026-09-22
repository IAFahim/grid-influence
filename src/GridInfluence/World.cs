using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

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

internal unsafe struct SourceColumns
{
    public NativeBuffer<float> X;
    public NativeBuffer<float> Y;
    public NativeBuffer<float> Bound;
    public NativeBuffer<byte> Stamp;
    public NativeBuffer<byte> Fade;
    public NativeBuffer<byte> Alive;
    public NativeBuffer<byte> Layer;
    public NativeBuffer<short> Mul;
    public NativeBuffer<sbyte> Hint;
    public int Count;
}

internal unsafe struct WorldCtx
{
    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public SourceColumns Sources;
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
        if (_worldCount >= MaxWorlds) throw new InvalidOperationException("World limit reached.");

        var id = (byte)_worldCount++;
        var w = Worlds + id;
        w->Grids = (GridCtx*)NativeMemory.AllocZeroed((nuint)(MaxGrids * sizeof(GridCtx)));
        return id;
    }

    internal static byte AddGrid(byte world, int power, float x, float y, float size)
    {
        var w = Worlds + world;
        if (w->GridCount >= MaxGrids) throw new InvalidOperationException("Grid limit reached.");
        if (power < 5 || power > 14) throw new ArgumentOutOfRangeException(nameof(power));
        if (size <= 0f) throw new ArgumentOutOfRangeException(nameof(size));

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

    internal static byte AddLayer(byte world)
    {
        var w = Worlds + world;
        if (w->LayerCount >= MaxLayers) throw new InvalidOperationException("Layer limit reached.");
        return (byte)w->LayerCount++;
    }

    internal static byte StampNext() => (byte)_stampCount++;
    internal static byte FadeNext(int percent)
    {
        var id = (byte)_fadeCount++;
        FadeRate[id] = 1000 - Math.Clamp(percent, 0, 100) * 10;
        return id;
    }

    public static int Place(byte world, byte layer, float x, float y, float bound, byte stamp, byte fade = 0)
    {
        var w = Worlds + world;
        var s = &w->Sources;
        if (layer >= w->LayerCount || stamp == 0 || stamp >= _stampCount) return -1;
        if (s->Count == s->X.Length) GrowSources(s);

        var i = s->Count++;
        s->X.Span[i] = x;
        s->Y.Span[i] = y;
        s->Bound.Span[i] = bound;
        s->Stamp.Span[i] = stamp;
        s->Fade.Span[i] = fade;
        s->Alive.Span[i] = 1;
        s->Layer.Span[i] = layer;
        s->Mul.Span[i] = 1000;
        s->Hint.Span[i] = -1;
        return i;
    }

    public static void Move(byte world, int source, float x, float y)
    {
        var s = &(Worlds + world)->Sources;
        if ((uint)source >= (uint)s->Count || s->Alive.Span[source] == 0) return;

        s->X.Span[source] = x;
        s->Y.Span[source] = y;
        s->Hint.Span[source] = -1;
    }

    public static void SetBound(byte world, int source, float bound)
    {
        var s = &(Worlds + world)->Sources;
        if ((uint)source >= (uint)s->Count || s->Alive.Span[source] == 0) return;

        s->Bound.Span[source] = bound;
        s->Hint.Span[source] = -1;
    }

    public static void SetFade(byte world, int source, byte fade)
    {
        var s = &(Worlds + world)->Sources;
        if ((uint)source >= (uint)s->Count || s->Alive.Span[source] == 0) return;

        s->Fade.Span[source] = fade;
    }

    public static void Remove(byte world, int source)
    {
        var s = &(Worlds + world)->Sources;
        if ((uint)source >= (uint)s->Count) return;

        s->Alive.Span[source] = 0;
    }

    public static void Clear(byte world)
    {
        var s = &(Worlds + world)->Sources;
        new Span<byte>(s->Alive.Pointer, s->Count).Clear();
        s->Count = 0;
    }

    private static void GrowSources(SourceColumns* s)
    {
        var capacity = Math.Max(64, s->X.Length * 2);
        s->X.Resize(capacity);
        s->Y.Resize(capacity);
        s->Bound.Resize(capacity);
        s->Stamp.Resize(capacity);
        s->Fade.Resize(capacity);
        s->Alive.Resize(capacity);
        s->Layer.Resize(capacity);
        s->Mul.Resize(capacity);
        s->Hint.Resize(capacity);
    }

    public static void FadeLayer(byte world, byte grid, byte layer, byte fade)
    {
        var g = (Worlds + world)->Grids + grid;
        g->LayerFade[layer] = fade;
        g->HasLayerFade = 1;
    }

    public static void FadeGrid(byte world, byte grid, byte fade)
        => (Worlds + world)->Grids[grid].GridFade = fade;

    public static void BeginProcess(byte world)
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

    public static void Process(byte world)
    {
        BeginProcess(world);
        ProcessSlice(world, 0, (Worlds + world)->Sources.Count);
    }

    public static void ProcessSlice(byte world, int start, int count)
    {
        var w = Worlds + world;
        var s = &w->Sources;
        var xs = s->X.Pointer;
        var ys = s->Y.Pointer;
        var bounds = s->Bound.Pointer;
        var stamps = s->Stamp.Pointer;
        var fades = s->Fade.Pointer;
        var alive = s->Alive.Pointer;
        var layers = s->Layer.Pointer;
        var mul = s->Mul.Pointer;
        var hints = s->Hint.Pointer;
        var end = Math.Min(start + count, s->Count);
        for (var i = start; i < end; i++)
        {
            if (alive[i] == 0) continue;
            var fade = fades[i];
            var data = StampData[stamps[i]];
            var wgt = fade != 0
                ? (sbyte)(data & 0xFF) * mul[i] / 1000
                : (sbyte)(data & 0xFF);
            if (fade != 0) mul[i] = (short)(mul[i] * FadeRate[fade] / 1000);
            if (wgt == 0) continue;
            var shape = data >> 8;
            var px = xs[i];
            var py = ys[i];
            var bd = bounds[i];
            var wx0 = px - bd; var wx1 = px + bd;
            var wy0 = py - bd; var wy1 = py + bd;
            var layer = layers[i];
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

    public static short Query(byte world, byte grid, byte layer, int x, int y)
    {
        var w = Worlds + world;
        if (grid >= w->GridCount || layer >= w->LayerCount) return 0;
        var g = w->Grids + grid;
        if ((uint)x >= (uint)g->Size || (uint)y >= (uint)g->Size) return 0;
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

    public static long Query(byte world, byte grid, byte layer, int x, int y, int wdt, int hgt)
    {
        var sum = 0L;
        for (var cy = y; cy < y + hgt; cy++)
        for (var cx = x; cx < x + wdt; cx++)
            sum += Query(world, grid, layer, cx, cy);
        return sum;
    }

    public static short QueryAt(byte world, byte grid, byte layer, float x, float y)
    {
        var w = Worlds + world;
        if (grid >= w->GridCount) return 0;
        var g = w->Grids + grid;
        var cx = (int)((x - g->OriginX) * g->Scale);
        var cy = (int)((y - g->OriginY) * g->Scale);
        if ((uint)cx >= (uint)g->Size || (uint)cy >= (uint)g->Size) return 0;
        return Query(world, grid, layer, cx, cy);
    }
}

public static unsafe class Fade
{
    public static byte Stamp(int percent) => World.FadeNext(percent);
    public static byte Layer(int percent) => World.FadeNext(percent);
    public static byte Grid(int percent) => World.FadeNext(percent);
}
