namespace Gi;

internal struct PrepKey
{
    public int Tile;
    public int Task;
    public byte Dense;
}

internal struct PrepTask
{
    public byte Grid;
    public byte Layer;
    public int First;
    public int Count;
}

public static unsafe partial class World
{
    internal static int BuildDepositsPooled(WorldCtx* w, DepositOp* ops, int count)
    {
        var journalBase = w->Journal.Length;
        if (w->Recording != 0) w->Journal.Ensure(journalBase + count);
        ResolvePool.RunDepositBuild(w, ops, count, journalBase);
        var fragments = StitchDeposits(w);
        w->Ops.Resize(0);
        if (w->Recording != 0) w->Journal.Resize(journalBase + count);
        ResolvePool.RunDepositPrep(w);
        return fragments;
    }

    internal static void BuildDepositRange(WorldCtx* w, DepositOp* ops, int first, int last,
        NativeBuffer<Placement>* placements, NativeBuffer<DepositFragment>* fragments, NativeBuffer<PrepKey>* keys,
        int journalBase)
    {
        for (var i = first; i < last; i++)
        {
            var op = ops + i;
            w->Pending.Pointer[op->Slot] = 0;
            if (w->Recording != 0) w->Journal.Pointer[journalBase + i] = *op;
            if (op->FromGain != 0 && (w->Crowded & (1u << op->FromLayer)) == 0)
                ScatterDeposit(w, op->FromX, op->FromY, op->FromStamp, op->FromLayer, -op->FromGain,
                    op->FromAngle, op->FromScale, placements, fragments, keys);
            if (op->ToGain != 0 && (w->Crowded & (1u << op->ToLayer)) == 0)
                ScatterDeposit(w, op->ToX, op->ToY, op->ToStamp, op->ToLayer, op->ToGain,
                    op->ToAngle, op->ToScale, placements, fragments, keys);
        }
    }

    private static void ScatterDeposit(WorldCtx* w, float x, float y, byte stampId, byte layer, int gain,
        int angle, int scale, NativeBuffer<Placement>* placements,
        NativeBuffer<DepositFragment>* fragments, NativeBuffer<PrepKey>* keys)
    {
        if (gain == 0) return;

        var v = StampCatalog.Get(stampId);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0 || !TileBake.Place(x, y, g->OriginX, g->OriginY, g->ScaleQ8, g->Size, v, angle, scale, out var p))
                continue;

            var cx0 = Math.Max(p.X0, 0);
            var cy0 = Math.Max(p.Y0, 0);
            var cx1 = Math.Min(p.X1, g->Size);
            var cy1 = Math.Min(p.Y1, g->Size);
            if (cx1 <= cx0 || cy1 <= cy0) continue;

            var placement = placements->Length;
            placements->Resize(placement + 1);
            placements->Pointer[placement] = p;
            var dense = p.Plane == Plane.Dense;
            var task = gi * MaxLayers + layer;
            var tps = g->TilesPerSide;
            for (var ty = cy0 >> TileBake.TileBits; ty <= (cy1 - 1) >> TileBake.TileBits; ty++)
            for (var tx = cx0 >> TileBake.TileBits; tx <= (cx1 - 1) >> TileBake.TileBits; tx++)
            {
                if (!TileBake.Touches(&p, tx << TileBake.TileBits, ty << TileBake.TileBits)) continue;

                var tile = ty * tps + tx;
                var n = fragments->Length;
                fragments->Resize(n + 1);
                fragments->Pointer[n] = new DepositFragment
                {
                    Placement = placement,
                    Gain = gain,
                    Tile = tile,
                    Grid = (byte)gi,
                    Layer = layer,
                    Dense = (byte)(dense ? 1 : 0),
                };
                var k = keys->Length;
                keys->Resize(k + 1);
                keys->Pointer[k] = new PrepKey { Tile = tile, Task = task, Dense = (byte)(dense ? 1 : 0) };
            }
        }
    }

    private static int StitchDeposits(WorldCtx* w)
    {
        var slots = ResolvePool.Slots;
        var fragmentTotal = 0;
        var placementTotal = 0;
        var keyTotal = 0;
        for (var k = 0; k < slots; k++)
        {
            fragmentTotal += ResolvePool.DepositFragments(k)->Length;
            placementTotal += ResolvePool.ScatterPlacements(k)->Length;
            keyTotal += ResolvePool.DepositKeys(k)->Length;
        }

        w->Fragments.Resize(fragmentTotal);
        w->Placements.Resize(placementTotal);
        var keys = ResolvePool.PrepKeyPool();
        keys->Resize(keyTotal);
        var tasks = ResolvePool.PrepTaskPool();
        tasks->Resize(MaxGrids * MaxLayers);

        var counts = stackalloc int[MaxGrids * MaxLayers];
        new Span<int>(counts, MaxGrids * MaxLayers).Clear();
        for (var k = 0; k < slots; k++)
        {
            var buffer = ResolvePool.DepositKeys(k);
            for (var i = 0; i < buffer->Length; i++) counts[buffer->Pointer[i].Task]++;
        }

        var taskCount = 0;
        var running = 0;
        var table = tasks->Pointer;
        for (var task = 0; task < MaxGrids * MaxLayers; task++)
        {
            if (counts[task] == 0) continue;

            table[taskCount] = new PrepTask
            {
                Grid = (byte)(task / MaxLayers),
                Layer = (byte)(task % MaxLayers),
                First = running,
                Count = counts[task],
            };
            running += counts[task];
            taskCount++;
        }

        var cursors = stackalloc int[MaxGrids * MaxLayers];
        for (var t = 0; t < taskCount; t++)
        {
            cursors[table[t].Grid * MaxLayers + table[t].Layer] = table[t].First;
        }

        for (var k = 0; k < slots; k++)
        {
            var buffer = ResolvePool.DepositKeys(k);
            for (var i = 0; i < buffer->Length; i++)
            {
                var key = buffer->Pointer[i];
                keys->Pointer[cursors[key.Task]++] = key;
            }
        }

        tasks->Resize(taskCount);
        ResolvePool.BeginDepositPrep(w, taskCount);

        var fragmentBase = 0;
        var placementBase = 0;
        var fragments = w->Fragments.Pointer;
        for (var k = 0; k < slots; k++)
        {
            var built = ResolvePool.DepositFragments(k);
            var placements = ResolvePool.ScatterPlacements(k);
            for (var i = 0; i < built->Length; i++)
            {
                var fragment = built->Pointer[i];
                fragment.Placement += placementBase;
                fragments[fragmentBase + i] = fragment;
            }

            if (placements->Length != 0)
                Buffer.MemoryCopy(placements->Pointer, w->Placements.Pointer + placementBase,
                    (long)placements->Length * sizeof(Placement), (long)placements->Length * sizeof(Placement));
            fragmentBase += built->Length;
            placementBase += placements->Length;
        }

        return fragmentTotal;
    }

    internal static void PrepareDepositTask(WorldCtx* w, PrepTask* task, NativeBuffer<PrepKey>* scratch)
    {
        var g = w->Grids + task->Grid;
        var ld = EnsureDirty(g, task->Layer);
        var keys = ResolvePool.PrepKeyPool();
        var first = task->First;
        var count = task->Count;
        SortPrepKeys(keys, scratch, first, count);
        var pointer = keys->Pointer + first;
        for (var i = 0; i < count; i++)
        {
            var key = pointer + i;
            var dense = key->Dense != 0;
            var block = TileBlock(ld, key->Tile, dense);
            if (dense) EnsureDense(ld, key->Tile, block);
            MarkDirty(ld, key->Tile);
        }
    }

    private static void SortPrepKeys(NativeBuffer<PrepKey>* keys, NativeBuffer<PrepKey>* scratch, int first, int count)
    {
        if (count < 2) return;
        var pointer = keys->Pointer + first;
        if (count <= 32)
        {
            for (var i = 1; i < count; i++)
            for (var j = i; j > 0 && pointer[j].Tile < pointer[j - 1].Tile; j--)
            {
                var swap = pointer[j];
                pointer[j] = pointer[j - 1];
                pointer[j - 1] = swap;
            }

            return;
        }

        scratch->Ensure(count);
        var source = pointer;
        var target = scratch->Pointer;
        var counts = stackalloc int[256];
        for (var shift = 0; shift < 24; shift += 8)
        {
            new Span<int>(counts, 256).Clear();
            for (var i = 0; i < count; i++) counts[(uint)(source[i].Tile >> shift) & 255u]++;
            var running = 0;
            for (var bucket = 0; bucket < 256; bucket++)
            {
                var size = counts[bucket];
                counts[bucket] = running;
                running += size;
            }

            for (var i = 0; i < count; i++) target[counts[(uint)(source[i].Tile >> shift) & 255u]++] = source[i];
            var swapped = source;
            source = target;
            target = swapped;
        }

        if (source != pointer) new Span<PrepKey>(source, count).CopyTo(new Span<PrepKey>(pointer, count));
    }
}
