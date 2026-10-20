#if NET
using System.Numerics;
using System.Runtime.Intrinsics;
#endif

namespace Gi;

public static unsafe partial class World
{
    public static int Splat(byte world, byte layer, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, byte stamp,
        sbyte gain)
    {
        var w = GetContext(world);
        if (w == null || !CanSplat(w, layer, stamp)) return 0;

        var count = Math.Min(xs.Length, ys.Length);
        if (w->Recipes[layer].PhaseBits == 0)
            fixed (float* px = xs)
            fixed (float* py = ys)
            {
                return SplatCells(w, layer, px, py, count, stamp, gain);
            }

        var applied = 0;
        for (var i = 0; i < count; i++)
            if (EmitSplat(w, layer, xs[i], ys[i], stamp, gain))
                applied++;
        return applied;
    }

    private static int SplatCells(WorldCtx* w, byte layer, float* xs, float* ys, int count, byte stamp, sbyte gain)
    {
        var applied = CountFinite(xs, ys, count);
        var clamped = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        if (clamped == 0) return applied;

        var mask = w->Recipes[layer].AnchorMask;
        var key = new DepositRecord { Layer = layer, Stamp = stamp, Gain = clamped, Alive = 1, Scale = UnitScale };
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            var ld = g->Layers + layer;
            var hist = EnsureHist(ld);
            var charge = (long)count * Math.Abs((int)clamped);
            if (hist->Total + charge > PlaneFold.LimitI(&ld->Store))
            {
                for (var i = 0; i < count; i++) SplatDirect(w, gi, g, ref key, mask, xs[i], ys[i]);
                continue;
            }

            hist->Total += charge;
            var cls = hist->Classes + stamp;
            if (cls->Directory == null)
                cls->Directory = (int*)NativeHeap.AllocZeroed((nuint)g->TileCount * sizeof(int));

            var done = 0;
#if NET
            if (Vector256.IsHardwareAccelerated)
                done =
                    SplatBlocks(w, gi, g, ld, hist, cls, ref key, mask, xs, ys, count);
#endif
            for (var i = done; i < count; i++)
                SplatOne(w, gi, g, ld, hist, cls, ref key, mask, xs[i], ys[i]);
        }

        return applied;
    }

    private static void SplatDirect(WorldCtx* w, int gi, GridCtx* g, ref DepositRecord key, int mask, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;

        key.Ax = (TileBake.AnchorQ8(x, g->OriginX, g->ScaleQ8, mask) >> 8) << 8;
        key.Ay = (TileBake.AnchorQ8(y, g->OriginY, g->ScaleQ8, mask) >> 8) << 8;
        EmitDelta(w, gi, in key, 1);
    }

    private static void SplatOne(
        WorldCtx* w, int gi, GridCtx* g, LayerData* ld, HistState* hist, HistClass* cls,
        ref DepositRecord key, int mask, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;

        var cx = TileBake.AnchorQ8(x, g->OriginX, g->ScaleQ8, mask) >> 8;
        var cy = TileBake.AnchorQ8(y, g->OriginY, g->ScaleQ8, mask) >> 8;
        if ((uint)cx >= (uint)g->Size || (uint)cy >= (uint)g->Size)
        {
            key.Ax = cx << 8;
            key.Ay = cy << 8;
            EmitDelta(w, gi, in key, 1);
            return;
        }

        var tile = (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
        HistPage(g, ld, hist, cls, tile)[
            (cy & (TileBake.TileSize - 1)) * TileBake.TileSize + (cx & (TileBake.TileSize - 1))] += key.Gain;
    }

    private static int CountFinite(float* xs, float* ys, int count)
    {
        var finite = 0;
        var i = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var limit = Vector256.Create(float.PositiveInfinity);
            for (; i + 8 <= count; i += 8)
            {
                var both = Vector256.LessThan(Vector256.Abs(Vector256.Load(xs + i)), limit)
                           & Vector256.LessThan(Vector256.Abs(Vector256.Load(ys + i)), limit);
                finite += BitOperations.PopCount(both.ExtractMostSignificantBits());
            }
        }
#endif
        for (; i < count; i++)
            if (float.IsFinite(xs[i]) && float.IsFinite(ys[i]))
                finite++;
        return finite;
    }

    public static int Splat(
        byte world, byte layer, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys,
        ReadOnlySpan<byte> stamps, ReadOnlySpan<sbyte> gains)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount || !IsTransient(w, layer)) return 0;

        var count = Math.Min(Math.Min(xs.Length, ys.Length), Math.Min(stamps.Length, gains.Length));
        var applied = 0;
        for (var i = 0; i < count; i++)
            if (StampCatalog.Valid(stamps[i]) && EmitSplat(w, layer, xs[i], ys[i], stamps[i], gains[i]))
                applied++;
        return applied;
    }

    public static SplatWriter BeginSplat(byte world, byte layer)
    {
        var w = GetContext(world);
        return w == null || layer >= w->LayerCount || !IsTransient(w, layer) ? default : new SplatWriter(w, layer);
    }

    private static bool CanSplat(WorldCtx* w, byte layer, byte stamp)
    {
        return layer < w->LayerCount && IsTransient(w, layer) && StampCatalog.Valid(stamp);
    }

    private static bool EmitSplat(WorldCtx* w, byte layer, float x, float y, byte stamp, sbyte gain)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;

        var mask = w->Recipes[layer].AnchorMask;
        var cellPrecision = w->Recipes[layer].PhaseBits == 0;
        var key = new DepositRecord
        {
            Layer = layer,
            Stamp = stamp,
            Gain = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain),
            Alive = 1,
            Scale = UnitScale
        };
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            if (g->ScaleQ8 == 0) continue;

            key.Ax = TileBake.AnchorQ8(x, g->OriginX, g->ScaleQ8, mask);
            key.Ay = TileBake.AnchorQ8(y, g->OriginY, g->ScaleQ8, mask);
            if (cellPrecision)
            {
                var cx = key.Ax >> 8;
                var cy = key.Ay >> 8;
                if ((uint)cx < (uint)g->Size && (uint)cy < (uint)g->Size)
                    if (key.Gain == 0 || HistAdd(g, g->Layers + layer, stamp, cx, cy, key.Gain))
                        continue;
            }

            EmitDelta(w, gi, in key, 1);
        }

        return true;
    }

    public ref struct SplatWriter
    {
        private readonly WorldCtx* _world;
        private readonly byte _layer;

        internal SplatWriter(WorldCtx* world, byte layer)
        {
            _world = world;
            _layer = layer;
            Count = 0;
        }

        public int Count { get; private set; }

        public bool Add(float x, float y, byte stamp, sbyte gain)
        {
            if (_world == null || !StampCatalog.Valid(stamp) || !EmitSplat(_world, _layer, x, y, stamp, gain))
                return false;

            Count++;
            return true;
        }
    }
}
