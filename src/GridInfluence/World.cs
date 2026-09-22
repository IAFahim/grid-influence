using System.Runtime.InteropServices;

namespace GridInfluence;

internal unsafe struct SourceColumns
{
    public NativeBuffer<float> X;
    public NativeBuffer<float> Y;
    public NativeBuffer<byte> Stamp;
    public NativeBuffer<byte> Layer;
    public NativeBuffer<byte> Gain;
    public NativeBuffer<byte> Alive;
    public NativeBuffer<uint> ChangeGen;
    public int Count;
}

internal unsafe struct LayerData
{
    public PageMap Pages;
    public uint* Mark;
    public uint* Rebuild;
    public int* Head;
    public NativeBuffer<int> Dirty;
    public NativeBuffer<int> RebuildList;
    public NativeBuffer<int> PairNext;
    public NativeBuffer<int> PairSrc;
    public int PairCount;
}

internal unsafe struct GridCtx
{
    public int Log2;
    public int Size;
    public float OriginX;
    public float OriginY;
    public float WorldSize;
    public float Scale;
    public int TilesPerSide;
    public int TileCount;
    public LayerData* Layers;
    public uint BuiltGen;
}

internal unsafe struct WorldCtx
{
    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public SourceColumns Sources;
    public int* LayerLive;
    public uint SourceGen;
    public int* Dense;
    public int* Diff;
    public int* Prev;
}

public static unsafe class World
{
    private const int MaxWorlds = 32;
    private const int MaxGrids = 32;
    private const int MaxLayers = 32;
    private const int MinPower = 5;
    private const int MaxPower = 14;
    private const int MaxGain = 16;

    private static readonly WorldCtx* Worlds =
        (WorldCtx*)NativeMemory.AllocZeroed((nuint)(MaxWorlds * sizeof(WorldCtx)));
    private static int _worldCount;

    public static byte New()
    {
        if (_worldCount >= MaxWorlds) throw new InvalidOperationException("World limit reached.");

        var id = (byte)_worldCount++;
        var w = Worlds + id;
        w->Grids = (GridCtx*)NativeMemory.AllocZeroed((nuint)(MaxGrids * sizeof(GridCtx)));
        w->LayerLive = (int*)NativeMemory.AllocZeroed((nuint)(MaxLayers * sizeof(int)));
        w->Dense = (int*)NativeMemory.AlignedAlloc((nuint)(TileBake.TileSize * TileBake.TileSize * sizeof(int)), 64);
        w->Diff = (int*)NativeMemory.AlignedAlloc((nuint)(TileBake.DiffRows * TileBake.DiffPitch * sizeof(int)), 64);
        w->Prev = (int*)NativeMemory.AlignedAlloc((nuint)(TileBake.TileSize * sizeof(int)), 64);
        return id;
    }

    internal static byte AddGrid(byte world, int power, float x, float y, float size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(power, MinPower);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(power, MaxPower);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(size, 0f);

        var w = Worlds + world;
        if (w->GridCount >= MaxGrids) throw new InvalidOperationException("Grid limit reached.");

        var id = (byte)w->GridCount++;
        var g = w->Grids + id;
        g->Log2 = power;
        g->Size = 1 << power;
        g->OriginX = x;
        g->OriginY = y;
        g->WorldSize = size;
        g->Scale = g->Size / size;
        g->TilesPerSide = g->Size >> TileBake.TileBits;
        g->TileCount = g->TilesPerSide * g->TilesPerSide;
        g->Layers = (LayerData*)NativeMemory.AllocZeroed((nuint)(MaxLayers * sizeof(LayerData)));
        return id;
    }

    internal static byte AddLayer(byte world)
    {
        var w = Worlds + world;
        if (w->LayerCount >= MaxLayers) throw new InvalidOperationException("Layer limit reached.");
        return (byte)w->LayerCount++;
    }

    public static int Place(byte world, byte layer, float x, float y, byte stamp, int gain)
    {
        var w = Worlds + world;
        if (layer >= w->LayerCount || stamp == 0 || stamp >= StampCatalog.Count) return -1;

        var s = &w->Sources;
        if (s->Count == s->X.Length) GrowSources(s);

        var i = s->Count++;
        s->X.Span[i] = x;
        s->Y.Span[i] = y;
        s->Stamp.Span[i] = stamp;
        s->Layer.Span[i] = layer;
        s->Gain.Span[i] = (byte)Math.Clamp(gain, 0, MaxGain);
        s->Alive.Span[i] = 1;
        w->SourceGen++;
        s->ChangeGen.Span[i] = w->SourceGen;
        w->LayerLive[layer]++;
        return i;
    }

    public static void Remove(byte world, int source)
    {
        var w = Worlds + world;
        var s = &w->Sources;
        if ((uint)source >= (uint)s->Count || s->Alive.Span[source] == 0) return;

        s->Alive.Span[source] = 0;
        w->SourceGen++;
        s->ChangeGen.Span[source] = w->SourceGen;
        w->LayerLive[s->Layer.Span[source]]--;
    }

    public static void Clear(byte world)
    {
        var w = Worlds + world;
        var s = &w->Sources;
        new Span<byte>(s->Alive.Pointer, s->Count).Clear();
        s->Count = 0;
        new Span<int>(w->LayerLive, MaxLayers).Clear();
        w->SourceGen++;
    }

    public static void Process(byte world)
    {
        var w = Worlds + world;
        var gen = w->SourceGen;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->BuiltGen == gen) continue;

            for (var l = 0; l < w->LayerCount; l++) ProcessLayer(w, g, l, gen);
            g->BuiltGen = gen;
        }
    }

    private static void ProcessLayer(WorldCtx* w, GridCtx* g, int layer, uint gen)
    {
        var ld = g->Layers + layer;
        if (w->LayerLive[layer] == 0)
        {
            RetireAll(&ld->Pages);
            return;
        }

        if (ld->Mark == null)
        {
            var tiles = (nuint)(g->TileCount * sizeof(uint));
            ld->Mark = (uint*)NativeMemory.AllocZeroed(tiles);
            ld->Rebuild = (uint*)NativeMemory.AllocZeroed(tiles);
            ld->Head = (int*)NativeMemory.AlignedAlloc((nuint)(g->TileCount * sizeof(int)), 64);
        }

        var rebuildList = &ld->RebuildList;
        rebuildList->Resize(0);
        ld->PairCount = 0;

        var s = &w->Sources;
        var alive = s->Alive.Pointer;
        var layers = s->Layer.Pointer;
        var stamps = s->Stamp.Pointer;
        var xs = s->X.Pointer;
        var ys = s->Y.Pointer;
        var changes = s->ChangeGen.Pointer;
        var mark = ld->Mark;
        var rebuild = ld->Rebuild;
        var head = ld->Head;
        var tps = g->TilesPerSide;
        var builtGen = g->BuiltGen;

        for (var i = 0; i < s->Count; i++)
        {
            if (layers[i] != layer) continue;

            var isLive = alive[i] != 0;
            var changed = changes[i] > builtGen;
            if (!isLive && !changed) continue;

            var v = StampCatalog.Get(stamps[i]);
            TileBake.Footprint(xs[i], ys[i], g->OriginX, g->OriginY, g->Scale, v,
                out _, out _, out _, out _, out var x0, out var y0, out var x1, out var y1);

            var cx0 = Math.Max(x0, 0);
            var cy0 = Math.Max(y0, 0);
            var cx1 = Math.Min(x1, g->Size);
            var cy1 = Math.Min(y1, g->Size);
            if (cx1 <= cx0 || cy1 <= cy0) continue;

            var tx0 = cx0 >> TileBake.TileBits;
            var tx1 = (cx1 - 1) >> TileBake.TileBits;
            var ty0 = cy0 >> TileBake.TileBits;
            var ty1 = (cy1 - 1) >> TileBake.TileBits;
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var tile = ty * tps + tx;
                if (isLive)
                {
                    if (mark[tile] != gen)
                    {
                        mark[tile] = gen;
                        head[tile] = -1;
                    }

                    var p = ld->PairCount++;
                    if (ld->PairNext.Length < ld->PairCount)
                    {
                        var cap = Math.Max(64, ld->PairNext.Length * 2);
                        ld->PairNext.Resize(cap);
                        ld->PairSrc.Resize(cap);
                    }

                    ld->PairNext.Span[p] = head[tile];
                    ld->PairSrc.Span[p] = i;
                    head[tile] = p;
                }

                if (changed && rebuild[tile] != gen)
                {
                    rebuild[tile] = gen;
                    var n = rebuildList->Length;
                    rebuildList->Resize(n + 1);
                    rebuildList->Span[n] = tile;
                }
            }
        }

        var pages = &ld->Pages;
        var used = pages->Used;
        var keys = pages->Keys;
        var pagePtrs = pages->Pages;
        var slots = pages->SlotCount;
        for (var i = 0; i < slots; i++)
        {
            if (used[i] != PageMap.Live || mark[keys[i]] == gen) continue;

            NativeMemory.AlignedFree(pagePtrs[i]);
            pages->TombstoneAt(i);
        }

        var rspan = rebuildList->Span;
        for (var i = 0; i < rspan.Length; i++) BuildTile(w, g, layer, rspan[i], gen);
    }

    private static void BuildTile(WorldCtx* w, GridCtx* g, int layer, int tile, uint gen)
    {
        var tps = g->TilesPerSide;
        var tileX0 = (tile % tps) * TileBake.TileSize;
        var tileY0 = (tile / tps) * TileBake.TileSize;
        var dense = w->Dense;
        var diff = w->Diff;
        var prev = w->Prev;

        new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
        new Span<int>(diff, TileBake.DiffRows * TileBake.DiffPitch).Clear();
        new Span<int>(prev, TileBake.TileSize).Clear();

        var s = &w->Sources;
        var stamps = s->Stamp.Pointer;
        var gains = s->Gain.Pointer;
        var xs = s->X.Pointer;
        var ys = s->Y.Pointer;

        var ld = g->Layers + layer;
        var pairNext = ld->PairNext.Pointer;
        var pairSrc = ld->PairSrc.Pointer;
        var p = ld->Mark[tile] == gen ? ld->Head[tile] : -1;
        for (; p >= 0; p = pairNext[p])
        {
            var i = pairSrc[p];
            var v = StampCatalog.Get(stamps[i]);
            TileBake.Footprint(xs[i], ys[i], g->OriginX, g->OriginY, g->Scale, v,
                out var px, out var py, out var fx, out var fy,
                out var x0, out var y0, out var x1, out var y1);
            if (x1 <= tileX0 || x0 >= tileX0 + TileBake.TileSize ||
                y1 <= tileY0 || y0 >= tileY0 + TileBake.TileSize) continue;

            if (v->Kind == StampKind.ConstantRectangle)
                TileBake.EmitBox(diff, tileX0, tileY0, px, py, fx, fy, v, gains[i]);
            else
                TileBake.EmitRaster(dense, tileX0, tileY0, px, py, fx, fy, v, gains[i]);
        }

        var pages = &g->Layers[layer].Pages;
        var exists = pages->TryGet(tile, out var page);
        var temp = stackalloc short[TileBake.TileSize * TileBake.TileSize];
        var any = TileBake.Resolve(diff, dense, prev, exists ? page : temp);

        if (any)
        {
            if (!exists)
            {
                page = (short*)NativeMemory.AlignedAlloc(
                    (nuint)(TileBake.TileSize * TileBake.TileSize * sizeof(short)), 64);
                Buffer.MemoryCopy(temp, page,
                    TileBake.TileSize * TileBake.TileSize * sizeof(short),
                    TileBake.TileSize * TileBake.TileSize * sizeof(short));
                pages->Put(tile, page);
            }
        }
        else if (exists)
        {
            pages->Remove(tile);
            NativeMemory.AlignedFree(page);
        }
    }

    private static void RetireAll(PageMap* pages)
    {
        var used = pages->Used;
        var pagePtrs = pages->Pages;
        var slots = pages->SlotCount;
        for (var i = 0; i < slots; i++)
        {
            if (used[i] != PageMap.Live) continue;

            NativeMemory.AlignedFree(pagePtrs[i]);
            pages->TombstoneAt(i);
        }
    }

    private static void GrowSources(SourceColumns* s)
    {
        var capacity = Math.Max(64, s->X.Length * 2);
        s->X.Resize(capacity);
        s->Y.Resize(capacity);
        s->Stamp.Resize(capacity);
        s->Layer.Resize(capacity);
        s->Gain.Resize(capacity);
        s->Alive.Resize(capacity);
        s->ChangeGen.Resize(capacity);
    }

    public static short Query(byte world, byte grid, byte layer, int x, int y)
    {
        var w = Worlds + world;
        if (grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        if ((uint)x >= (uint)g->Size || (uint)y >= (uint)g->Size) return 0;

        var pages = &g->Layers[layer].Pages;
        if (!pages->TryGet((y >> TileBake.TileBits) * g->TilesPerSide + (x >> TileBake.TileBits), out var page))
            return 0;

        return page[(y & (TileBake.TileSize - 1)) * TileBake.TileSize + (x & (TileBake.TileSize - 1))];
    }

    public static long Query(byte world, byte grid, byte layer, int x, int y, int width, int height)
    {
        var w = Worlds + world;
        if (grid >= w->GridCount || layer >= w->LayerCount || width <= 0 || height <= 0) return 0;

        var g = w->Grids + grid;
        var x1 = Math.Min(x + width, g->Size);
        var y1 = Math.Min(y + height, g->Size);
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        if (x1 <= x0 || y1 <= y0) return 0;

        var pages = &g->Layers[layer].Pages;
        var tps = g->TilesPerSide;
        var sum = 0L;
        for (var ty = y0 >> TileBake.TileBits; ty <= (y1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = x0 >> TileBake.TileBits; tx <= (x1 - 1) >> TileBake.TileBits; tx++)
        {
            if (!pages->TryGet(ty * tps + tx, out var page)) continue;

            var lx0 = Math.Max(x0 - tx * TileBake.TileSize, 0);
            var ly0 = Math.Max(y0 - ty * TileBake.TileSize, 0);
            var lx1 = Math.Min(x1 - tx * TileBake.TileSize, TileBake.TileSize);
            var ly1 = Math.Min(y1 - ty * TileBake.TileSize, TileBake.TileSize);
            for (var ly = ly0; ly < ly1; ly++)
            {
                var row = page + ly * TileBake.TileSize;
                for (var lx = lx0; lx < lx1; lx++) sum += row[lx];
            }
        }

        return sum;
    }

    public static short QueryAt(byte world, byte grid, byte layer, float x, float y)
    {
        var w = Worlds + world;
        if (grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var cx = (int)MathF.Floor((x - g->OriginX) * g->Scale);
        var cy = (int)MathF.Floor((y - g->OriginY) * g->Scale);
        return Query(world, grid, layer, cx, cy);
    }
}
