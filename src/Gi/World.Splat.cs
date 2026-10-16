using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
    public static int Splat(byte world, byte layer, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, byte stamp, sbyte gain)
    {
        var w = GetContext(world);
        if (w == null || !CanSplat(w, layer, stamp)) return 0;

        var count = Math.Min(xs.Length, ys.Length);
        var applied = 0;
        for (var i = 0; i < count; i++)
            if (EmitSplat(w, layer, xs[i], ys[i], stamp, gain)) applied++;
        return applied;
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
            if (StampCatalog.Valid(stamps[i]) && EmitSplat(w, layer, xs[i], ys[i], stamps[i], gains[i])) applied++;
        return applied;
    }

    public static SplatWriter BeginSplat(byte world, byte layer)
    {
        var w = GetContext(world);
        return w == null || layer >= w->LayerCount || !IsTransient(w, layer) ? default : new SplatWriter(w, layer);
    }

    public ref struct SplatWriter
    {
        private readonly WorldCtx* _world;
        private readonly byte _layer;
        private int _count;

        internal SplatWriter(WorldCtx* world, byte layer)
        {
            _world = world;
            _layer = layer;
            _count = 0;
        }

        public readonly int Count => _count;

        public bool Add(float x, float y, byte stamp, sbyte gain)
        {
            if (_world == null || !StampCatalog.Valid(stamp) || !EmitSplat(_world, _layer, x, y, stamp, gain)) return false;

            _count++;
            return true;
        }
    }

    private static bool CanSplat(WorldCtx* w, byte layer, byte stamp)
        => layer < w->LayerCount && IsTransient(w, layer) && StampCatalog.Valid(stamp);

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
            Scale = UnitScale,
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
                {
                    if (key.Gain != 0) HistAdd(g, g->Layers + layer, stamp, cx, cy, key.Gain);
                    continue;
                }
            }

            EmitDelta(w, gi, in key, 1);
        }

        return true;
    }
}
