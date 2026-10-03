using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gi;

internal struct SourceColumns
{
    public NativeBuffer<float> X;
    public NativeBuffer<float> Y;
    public NativeBuffer<byte> Stamp;
    public NativeBuffer<byte> Layer;
    public NativeBuffer<byte> Gain;
    public NativeBuffer<byte> Alive;
    public int Count;
}

internal unsafe struct LayerData
{
    public PageMap Pages;
    public byte* InDirty;
    public NativeBuffer<int> Dirty;
}

internal unsafe struct GridCtx
{
    public int Size;
    public float OriginX;
    public float OriginY;
    public float Scale;
    public float ScaleQ8;
    public int TilesPerSide;
    public int TileCount;
    public LayerData* Layers;
}

internal unsafe struct WorldCtx
{
    public GridCtx* Grids;
    public int GridCount;
    public int LayerCount;
    public SourceColumns Sources;
    public int* Prev;
}

public static unsafe class World
{
    internal const int MaxWorlds = 32;
    internal const int MaxGrids = 32;
    internal const int MaxLayers = 32;
    private const int MinPower = 5;
    private const int MaxPower = 14;
    private const int MaxGain = 16;
    private const int Cells = TileBake.TileSize * TileBake.TileSize;
    private const int DenseBytes = Cells * sizeof(int);
    private const int DiffBytes = TileBake.DiffRows * TileBake.DiffPitch * sizeof(int);
    private const int PageBytes = Cells * sizeof(short);
    private const int PageOffset = DiffBytes + DenseBytes;
    private const int BlockBytes = PageOffset + PageBytes;

    private static readonly WorldCtx* Worlds =
        (WorldCtx*)NativeMemory.AllocZeroed((nuint)(MaxWorlds * sizeof(WorldCtx)));
    private static int _worldCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static WorldCtx* GetContext(byte world) => world < _worldCount ? Worlds + world : null;

    public static byte New()
    {
        if (_worldCount >= MaxWorlds) throw new InvalidOperationException("World limit reached.");

        var id = (byte)_worldCount++;
        var w = Worlds + id;
        w->Grids = (GridCtx*)NativeMemory.AllocZeroed((nuint)(MaxGrids * sizeof(GridCtx)));
        w->Prev = (int*)NativeMemory.AlignedAlloc(TileBake.TileSize * sizeof(int), 64);
        return id;
    }

    internal static byte AddGrid(byte world, int power, float x, float y, float size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(power, MinPower);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(power, MaxPower);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(size, 0f);

        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->GridCount >= MaxGrids) throw new InvalidOperationException("Grid limit reached.");

        var id = (byte)w->GridCount++;
        var g = w->Grids + id;
        g->Size = 1 << power;
        g->OriginX = x;
        g->OriginY = y;
        g->Scale = g->Size / size;
        g->ScaleQ8 = g->Scale * 256f;
        g->TilesPerSide = g->Size >> TileBake.TileBits;
        g->TileCount = g->TilesPerSide * g->TilesPerSide;
        g->Layers = (LayerData*)NativeMemory.AllocZeroed((nuint)(MaxLayers * sizeof(LayerData)));
        return id;
    }

    internal static byte AddLayer(byte world)
    {
        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (w->LayerCount >= MaxLayers) throw new InvalidOperationException("Layer limit reached.");
        return (byte)w->LayerCount++;
    }

    public static int Place(byte world, byte layer, float x, float y, byte stamp, int gain)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || stamp == 0 || stamp >= StampCatalog.Count) return -1;

        var s = &w->Sources;
        if (s->Count == s->X.Length) GrowSources(s);

        var i = s->Count++;
        var g = (byte)Math.Clamp(gain, 0, MaxGain);
        s->X.Pointer[i] = x;
        s->Y.Pointer[i] = y;
        s->Stamp.Pointer[i] = stamp;
        s->Layer.Pointer[i] = layer;
        s->Gain.Pointer[i] = g;
        s->Alive.Pointer[i] = 1;
        Deposit(w, x, y, stamp, layer, g);
        return i;
    }

    public static void Move(byte world, int source, float x, float y)
    {
        if (!TrySource(world, source, out var w, out var s)) return;

        var stamp = s->Stamp.Pointer[source];
        var layer = s->Layer.Pointer[source];
        var gain = s->Gain.Pointer[source];
        var oldX = s->X.Pointer[source];
        var oldY = s->Y.Pointer[source];
        if (oldX == x && oldY == y) return;
        s->X.Pointer[source] = x;
        s->Y.Pointer[source] = y;
        Deposit(w, oldX, oldY, stamp, layer, -gain);
        Deposit(w, x, y, stamp, layer, gain);
    }

    public static void SetGain(byte world, int source, int gain)
    {
        if (!TrySource(world, source, out var w, out var s)) return;

        var next = (byte)Math.Clamp(gain, 0, MaxGain);
        var current = s->Gain.Pointer[source];
        if (next == current) return;

        s->Gain.Pointer[source] = next;
        Deposit(w, s->X.Pointer[source], s->Y.Pointer[source],
            s->Stamp.Pointer[source], s->Layer.Pointer[source], next - current);
    }

    public static void Remove(byte world, int source)
    {
        if (!TrySource(world, source, out var w, out var s)) return;

        Deposit(w, s->X.Pointer[source], s->Y.Pointer[source],
            s->Stamp.Pointer[source], s->Layer.Pointer[source], -s->Gain.Pointer[source]);
        s->Alive.Pointer[source] = 0;
    }

    public static void Clear(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        var s = &w->Sources;
        new Span<byte>(s->Alive.Pointer, s->Count).Clear();
        s->Count = 0;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var span = ld->Dirty.Span;
                foreach (var tile in span) ld->InDirty[tile] = 0;
                ld->Dirty.Resize(0);

                var pages = &ld->Pages;
                var used = pages->Used;
                var blocks = pages->Blocks;
                var slots = pages->SlotCount;
                for (var i = 0; i < slots; i++)
                    if (used[i] == PageMap.Live) NativeMemory.AlignedFree(blocks[i]);
                pages->Reset();
            }
        }
    }

    private static bool TrySource(byte world, int source, out WorldCtx* w, out SourceColumns* s)
    {
        w = GetContext(world);
        s = w == null ? null : &w->Sources;
        return s != null && (uint)source < (uint)s->Count && s->Alive.Pointer[source] != 0;
    }

    private static void Deposit(WorldCtx* w, float x, float y, byte stampId, byte layer, int gain)
    {
        if (gain == 0) return;

        var v = StampCatalog.Get(stampId);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            TileBake.Footprint(x, y, g->OriginX, g->OriginY, g->ScaleQ8, v,
                out var px, out var py, out var fx, out var fy,
                out var x0, out var y0, out var x1, out var y1);

            var cx0 = Math.Max(x0, 0);
            var cy0 = Math.Max(y0, 0);
            var cx1 = Math.Min(x1, g->Size);
            var cy1 = Math.Min(y1, g->Size);
            if (cx1 <= cx0 || cy1 <= cy0) continue;

            var ld = EnsureDirty(g, layer);
            var tps = g->TilesPerSide;
            var tx0 = cx0 >> TileBake.TileBits;
            var tx1 = (cx1 - 1) >> TileBake.TileBits;
            var ty0 = cy0 >> TileBake.TileBits;
            var ty1 = (cy1 - 1) >> TileBake.TileBits;
            for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var tile = ty * tps + tx;
                var block = TileBlock(ld, tile);

                var tileX0 = tx * TileBake.TileSize;
                var tileY0 = ty * TileBake.TileSize;
                if (v->Kind == StampKind.ConstantRectangle)
                    TileBake.EmitBox((int*)block, tileX0, tileY0, px, py, fx, fy, v, gain);
                else
                    TileBake.EmitRaster((int*)(block + DiffBytes), tileX0, tileY0, px, py, fx, fy, v, gain);

                MarkDirty(ld, tile);
            }
        }
    }

    private static LayerData* EnsureDirty(GridCtx* g, byte layer)
    {
        var ld = g->Layers + layer;
        if (ld->InDirty == null)
            ld->InDirty = (byte*)NativeMemory.AllocZeroed((nuint)g->TileCount);
        return ld;
    }

    private static byte* TileBlock(LayerData* ld, int tile)
    {
        var pages = &ld->Pages;
        if (pages->TryGet(tile, out var block)) return block;

        block = (byte*)NativeMemory.AlignedAlloc(BlockBytes, 64);
        new Span<byte>(block, BlockBytes).Clear();
        pages->Put(tile, block);
        return block;
    }

    private static void MarkDirty(LayerData* ld, int tile)
    {
        if (ld->InDirty[tile] != 0) return;

        ld->InDirty[tile] = 1;
        var n = ld->Dirty.Length;
        ld->Dirty.Resize(n + 1);
        ld->Dirty.Pointer[n] = tile;
    }

    public static void Process(byte world)
    {
        var w = GetContext(world);
        if (w == null) return;
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var l = 0; l < w->LayerCount; l++)
            {
                var ld = g->Layers + l;
                var span = ld->Dirty.Span;
                var pages = &ld->Pages;
                foreach (var tile in span)
                {
                    ld->InDirty[tile] = 0;
                    if (!pages->TryGet(tile, out var block)) continue;

                    new Span<int>(w->Prev, TileBake.TileSize).Clear();
                    if (!TileBake.Resolve((int*)block, (int*)(block + DiffBytes), w->Prev, (short*)(block + PageOffset)))
                    {
                        pages->Remove(tile);
                        NativeMemory.AlignedFree(block);
                    }
                }

                ld->Dirty.Resize(0);
            }
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short Query(byte world, byte grid, byte layer, int x, int y)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        if ((uint)x >= (uint)g->Size || (uint)y >= (uint)g->Size) return 0;

        var pages = &g->Layers[layer].Pages;
        if (!pages->TryGet((y >> TileBake.TileBits) * g->TilesPerSide + (x >> TileBake.TileBits), out var block))
            return 0;

        var page = (short*)(block + PageOffset);
        return page[(y & (TileBake.TileSize - 1)) * TileBake.TileSize + (x & (TileBake.TileSize - 1))];
    }

    public static long Query(byte world, byte grid, byte layer, int x, int y, int width, int height)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount || width <= 0 || height <= 0) return 0;

        var g = w->Grids + grid;
        var x1 = (int)Math.Min((long)x + width, g->Size);
        var y1 = (int)Math.Min((long)y + height, g->Size);
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        if (x1 <= x0 || y1 <= y0) return 0;

        var pages = &g->Layers[layer].Pages;
        if (pages->Count == 0) return 0;
        var tps = g->TilesPerSide;
        var sum = 0L;
        if (x0 == 0 && y0 == 0 && x1 == g->Size && y1 == g->Size)
        {
            var used = pages->Used;
            var blocks = pages->Blocks;
            var slots = pages->SlotCount;
            for (var slot = 0; slot < slots; slot++)
            {
                if (used[slot] != PageMap.Live) continue;
                var page = (short*)(blocks[slot] + PageOffset);
                for (var cell = 0; cell < Cells; cell++) sum += page[cell];
            }

            return sum;
        }

        for (var ty = y0 >> TileBake.TileBits; ty <= (y1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = x0 >> TileBake.TileBits; tx <= (x1 - 1) >> TileBake.TileBits; tx++)
        {
            if (!pages->TryGet(ty * tps + tx, out var block)) continue;

            var page = (short*)(block + PageOffset);
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
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount) return 0;

        var g = w->Grids + grid;
        var cx = (int)MathF.Floor((x - g->OriginX) * g->Scale);
        var cy = (int)MathF.Floor((y - g->OriginY) * g->Scale);
        return Query(world, grid, layer, cx, cy);
    }
}
