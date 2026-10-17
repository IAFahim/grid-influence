using System.Numerics;

namespace Gi.Tests;

internal sealed unsafe class ReferenceWorld
{
    private const byte Fading = 1;
    private const byte Expiring = 2;
    private const int MaxGain = ValidationRules.MaxGain;
    private const ushort UnitScale = 256;
    private const int MaxSourceSlots = World.MaxSourceSlots;
    private const long MaxReachQ8 = 1L << 24;
    private const float MaxCellQ8 = 1073741824f;

    internal struct Source
    {
        public float X;
        public float Y;
        public byte Stamp;
        public byte Layer;
        public sbyte Gain;
        public ushort Angle;
        public ushort Scale;
        public bool Alive;
        public uint Gen;
        public byte Timed;
        public sbyte FadeFrom;
        public sbyte FadeTo;
        public int FadeStart;
        public int FadeTicks;
        public int ExpireAt;
    }

    private struct ScheduleState
    {
        public byte Timed;
        public sbyte FadeFrom;
        public sbyte FadeTo;
        public int FadeStart;
        public int FadeTicks;
        public int ExpireAt;
    }

    private sealed class LayerData
    {
        public short[]? Field;
        public int[]? Epochs;
        public bool MaxInit;
        public List<int> Changed = new();
        public HashSet<int> Marked = new();
    }

    private sealed class GridData
    {
        public int Size;
        public float OriginX;
        public float OriginY;
        public int ScaleQ8;
        public int TilesPerSide;
        public int TileCount;
        public readonly LayerData?[] Layers = new LayerData?[World.MaxLayers];
    }

    private struct Recipe
    {
        public LayerOp Op;
        public byte A;
        public byte B;
        public byte Shift;
        public int WeightA;
        public int WeightB;
        public short Low;
        public short High;
        public uint Reads;
        public Layer.Precision Precision;
        public bool Transient;
    }

    private readonly List<Source> _splats = new();
    private readonly List<GridData> _grids = new();
    private readonly List<Recipe> _layers = new();
    private Source[] _now = new Source[64];
    private Source[] _deposited = new Source[64];
    private Source[] _pendingFrom = new Source[64];
    private sbyte[] _pendingToGain = new sbyte[64];
    private bool[] _pending = new bool[64];
    private readonly List<int> _pendingOrder = new();
    private readonly Queue<int> _free = new();
    private readonly List<(int Slot, Source From)> _journal = new();
    private readonly List<(int Slot, ScheduleState State)> _scheduleJournal = new();
    private readonly HashSet<int> _journaled = new();
    private readonly HashSet<int> _scheduleJournaled = new();
    private int _count;
    private int _tick;
    private int _recordTick;
    private bool _recording;
    private int _revision = 1;

    public int Tick => _tick;
    public int Revision => _revision;
    public int GridCount => _grids.Count;
    public int LayerCount => _layers.Count;
    public int SourceCount => _count;

    public byte AddGrid(int power, float x, float y, float size)
    {
        if (!ValidationRules.TryGridScaleQ8(power, size, out var scaleQ8)) throw new ArgumentOutOfRangeException(nameof(size));
        var cells = 1 << power;
        _grids.Add(new GridData
        {
            Size = cells,
            OriginX = x,
            OriginY = y,
            ScaleQ8 = scaleQ8,
            TilesPerSide = cells >> 5,
            TileCount = (cells >> 5) * (cells >> 5),
        });
        return (byte)(_grids.Count - 1);
    }

    public byte AddLayer(Layer.Precision precision, bool transient = false)
    {
        _layers.Add(new Recipe { Op = LayerOp.Base, Reads = 1u << _layers.Count, Precision = precision, Transient = transient });
        return (byte)(_layers.Count - 1);
    }

    public bool Splat(byte layer, float x, float y, byte stamp, sbyte gain)
    {
        if (layer >= _layers.Count || _layers[layer].Op != LayerOp.Base || !_layers[layer].Transient ||
            !StampCatalog.Valid(stamp) || !float.IsFinite(x) || !float.IsFinite(y)) return false;

        _splats.Add(new Source
        {
            X = x,
            Y = y,
            Stamp = stamp,
            Layer = layer,
            Gain = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain),
            Scale = UnitScale,
            Alive = true,
        });
        return true;
    }

    public byte AddDerived(LayerOp op, byte a, byte b, int weightA, int weightB, int shift, short low, short high)
    {
        var reads = _layers[a].Reads | _layers[b].Reads;
        var id = (byte)_layers.Count;
        _layers.Add(new Recipe
        {
            Op = op, A = a, B = b, Shift = (byte)shift, WeightA = weightA, WeightB = weightB,
            Low = low, High = high, Reads = reads, Precision = Layer.Precision.Exact,
        });
        foreach (var g in _grids)
        {
            var ld = Data(g, id);
            MarkParentTiles(g, ld, a);
            if (b != a) MarkParentTiles(g, ld, b);
        }

        return id;
    }

    private static void MarkParentTiles(GridData g, LayerData target, byte parent)
    {
        var field = LayerField(g, parent);
        if (field == null) return;
        var tps = g.TilesPerSide;
        for (var t = 0; t < g.TileCount; t++)
        {
            var tx = (t % tps) << 5;
            var ty = (t / tps) << 5;
            var any = false;
            for (var y = ty; y < ty + 32 && !any; y++)
            for (var x = tx; x < tx + 32 && !any; x++)
                any = field[y * g.Size + x] != 0;
            if (any)
            {
                target.Epochs ??= new int[g.TileCount];
                target.Marked.Add(t);
            }
        }
    }

    private static LayerData Data(GridData g, int layer)
        => g.Layers[layer] ??= new LayerData();

    private static short[]? LayerField(GridData g, int layer) => g.Layers[layer]?.Field;

    private static bool TrySource(Source[] now, int count, World.SourceId id, out int slot)
    {
        slot = (int)id.Slot;
        return (uint)slot < (uint)count && now[slot].Alive && id.Generation == now[slot].Gen;
    }

    private bool TrySource(World.SourceId id, out int slot) => TrySource(_now, _count, id, out slot);

    public World.SourceId Place(byte layer, float x, float y, byte stamp, sbyte gain)
    {
        if (layer >= _layers.Count || _layers[layer].Op != LayerOp.Base || !StampCatalog.Valid(stamp) ||
            !FootprintFits(stamp, 256) || !float.IsFinite(x) || !float.IsFinite(y)) return default;

        if (!_free.TryDequeue(out var slot))
        {
            if (_count >= MaxSourceSlots) return default;
            slot = _count++;
            if (_count > _now.Length)
            {
                var capacity = _now.Length * 2;
                Array.Resize(ref _now, capacity);
                Array.Resize(ref _deposited, capacity);
                Array.Resize(ref _pendingFrom, capacity);
                Array.Resize(ref _pendingToGain, capacity);
                Array.Resize(ref _pending, capacity);
            }
        }

        var g = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        var gen = _now[slot].Gen + 1;
        if (_recording && _scheduleJournaled.Add(slot)) _scheduleJournal.Add((slot, ScheduleOf(_now[slot])));
        if (!_pending[slot])
        {
            _pending[slot] = true;
            _pendingOrder.Add(slot);
            _pendingFrom[slot] = new Source { X = x, Y = y, Stamp = stamp, Layer = layer, Gen = gen, Scale = UnitScale };
        }

        _pendingToGain[slot] = g;
        _now[slot] = new Source
        {
            X = x, Y = y, Stamp = stamp, Layer = layer, Gain = g, Scale = UnitScale, Alive = true, Gen = gen,
        };
        return new World.SourceId((uint)slot, gen);
    }

    private static ScheduleState ScheduleOf(in Source s) => new()
    {
        Timed = s.Timed, FadeFrom = s.FadeFrom, FadeTo = s.FadeTo,
        FadeStart = s.FadeStart, FadeTicks = s.FadeTicks, ExpireAt = s.ExpireAt,
    };

    private static void ApplySchedule(ref Source s, in ScheduleState state)
    {
        s.Timed = state.Timed;
        s.FadeFrom = state.FadeFrom;
        s.FadeTo = state.FadeTo;
        s.FadeStart = state.FadeStart;
        s.FadeTicks = state.FadeTicks;
        s.ExpireAt = state.ExpireAt;
    }

    private void Mutate(int slot, sbyte toGain)
    {
        if (!_pending[slot])
        {
            _pending[slot] = true;
            _pendingOrder.Add(slot);
            _pendingFrom[slot] = _now[slot];
        }

        _pendingToGain[slot] = toGain;
    }

    private bool PositionChanged(byte layer, float oldX, float oldY, float x, float y)
    {
        var mask = _layers[layer].Precision.AnchorMask;
        foreach (var g in _grids)
        {
            if (g.ScaleQ8 == 0) continue;
            var dx = TileBake.AnchorQ8(oldX, g.OriginX, g.ScaleQ8, mask) ^ TileBake.AnchorQ8(x, g.OriginX, g.ScaleQ8, mask);
            var dy = TileBake.AnchorQ8(oldY, g.OriginY, g.ScaleQ8, mask) ^ TileBake.AnchorQ8(y, g.OriginY, g.ScaleQ8, mask);
            if ((dx | dy) != 0) return true;
        }

        return false;
    }

    private bool AngleChanged(byte layer, ushort oldAngle, ushort angle)
        => _grids.Count > 0 && ((oldAngle ^ angle) & _layers[layer].Precision.TurnMask) != 0;

    public void Move(World.SourceId source, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !TrySource(source, out var i)) return;
        ref var s = ref _now[i];
        if (s.X.Equals(x) && s.Y.Equals(y)) return;
        if (PositionChanged(s.Layer, s.X, s.Y, x, y)) Mutate(i, s.Gain);
        s.X = x;
        s.Y = y;
    }

    public void Turn(World.SourceId source, float angle)
    {
        if (!TrySource(source, out var i) || !float.IsFinite(angle)) return;
        var turns = angle * (1.0 / (2 * Math.PI));
        turns -= Math.Floor(turns);
        var next = (ushort)((int)(turns * 65536.0) & 0xFFFF);
        ref var s = ref _now[i];
        if (next == s.Angle) return;
        if (AngleChanged(s.Layer, s.Angle, next)) Mutate(i, s.Gain);
        s.Angle = next;
    }

    public void Scale(World.SourceId source, float scale)
    {
        if (!TrySource(source, out var i) || !float.IsFinite(scale)) return;
        var q8 = Math.Floor(scale * 256.0);
        var next = (ushort)(q8 < 1 ? 1 : q8 > ushort.MaxValue ? ushort.MaxValue : q8);
        ref var s = ref _now[i];
        if (next == s.Scale || !FootprintFits(s.Stamp, next)) return;
        Mutate(i, s.Gain);
        s.Scale = next;
    }

    public void SetGain(World.SourceId source, sbyte gain)
    {
        if (!TrySource(source, out var i)) return;
        var next = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        ref var s = ref _now[i];
        if ((s.Timed & Fading) != 0)
        {
            if (_recording && _scheduleJournaled.Add(i)) _scheduleJournal.Add((i, ScheduleOf(s)));
            s.Timed &= unchecked((byte)~Fading);
        }

        if (next == s.Gain) return;
        Mutate(i, next);
        s.Gain = next;
    }

    public void Remove(World.SourceId source)
    {
        if (!TrySource(source, out var i)) return;
        Mutate(i, 0);
        ref var s = ref _now[i];
        if (s.Timed != 0)
        {
            if (_recording && _scheduleJournaled.Add(i)) _scheduleJournal.Add((i, ScheduleOf(s)));
            s.Timed = 0;
        }

        s.Alive = false;
        if (s.Gen != uint.MaxValue) _free.Enqueue(i);
    }

    public void Fade(World.SourceId source, sbyte gain, int ticks)
    {
        if (!TrySource(source, out var i)) return;
        var target = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        ref var s = ref _now[i];
        if (_recording && _scheduleJournaled.Add(i)) _scheduleJournal.Add((i, ScheduleOf(s)));
        if (ticks <= 0 || target == s.Gain)
        {
            s.Timed &= unchecked((byte)~Fading);
            if (target != s.Gain)
            {
                Mutate(i, target);
                s.Gain = target;
            }
        }
        else
        {
            s.FadeFrom = s.Gain;
            s.FadeTo = target;
            s.FadeStart = _tick;
            s.FadeTicks = ticks;
            s.Timed |= Fading;
        }
    }

    public void Expire(World.SourceId source, int ticks)
    {
        if (!TrySource(source, out var i)) return;
        if (_recording && _scheduleJournaled.Add(i)) _scheduleJournal.Add((i, ScheduleOf(_now[i])));
        ref var s = ref _now[i];
        if (ticks <= 0) s.Timed &= unchecked((byte)~Expiring);
        else
        {
            s.ExpireAt = _tick + ticks;
            s.Timed |= Expiring;
        }
    }

    public void Sync(World.SourceId* sources, float* xs, float* ys, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = sources[i];
            if (!TrySource(id, out var slot)) continue;
            var x = xs[i];
            var y = ys[i];
            ref var s = ref _now[slot];
            if (BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(s.X) &&
                BitConverter.SingleToInt32Bits(y) == BitConverter.SingleToInt32Bits(s.Y)) continue;
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
            if (PositionChanged(s.Layer, s.X, s.Y, x, y)) Mutate(slot, s.Gain);
            s.X = x;
            s.Y = y;
        }
    }

    public bool TryGetSource(World.SourceId id, out Source source)
    {
        source = default;
        if (!TrySource(id, out var slot)) return false;
        source = _now[slot];
        return true;
    }

    public int LiveCount(byte layer)
    {
        var live = 0;
        for (var i = 0; i < _count; i++)
            if (_now[i].Alive && _now[i].Gain != 0 && _now[i].Layer == layer) live++;
        return live;
    }

    public void Process()
    {
        _tick++;
        for (var i = 0; i < _count; i++)
        {
            ref var s = ref _now[i];
            if (!s.Alive || s.Timed == 0) continue;
            if ((s.Timed & Expiring) != 0 && s.ExpireAt - _tick <= 0)
            {
                Mutate(i, 0);
                s.Alive = false;
                if (s.Gen != uint.MaxValue) _free.Enqueue(i);
                continue;
            }

            if ((s.Timed & Fading) == 0) continue;
            var elapsed = Math.Clamp(_tick - s.FadeStart, 0, s.FadeTicks);
            var delta = s.FadeTo - s.FadeFrom;
            var step = (int)(((long)Math.Abs(delta) * elapsed + s.FadeTicks / 2) / s.FadeTicks);
            var gain = (sbyte)(s.FadeFrom + (delta < 0 ? -step : step));
            if (gain == s.Gain) continue;
            Mutate(i, gain);
            s.Gain = gain;
        }

        var deposits = new int[_layers.Count];
        foreach (var slot in _pendingOrder)
        {
            if (_pendingFrom[slot].Gain != 0) deposits[_pendingFrom[slot].Layer]++;
            if (_pendingToGain[slot] != 0) deposits[_now[slot].Layer]++;
        }

        var crowded = 0u;
        for (var l = 0; l < _layers.Count; l++)
        {
            if (_layers[l].Op != LayerOp.Base || deposits[l] < 64) continue;
            var live = LiveCount((byte)l);
            if (live > 0 && deposits[l] >= live) crowded |= 1u << l;
        }

        if (_recording)
            foreach (var slot in _pendingOrder)
                if (_journaled.Add(slot))
                    _journal.Add((slot, _pendingFrom[slot]));

        foreach (var g in _grids)
        {
            for (var l = 0; l < _layers.Count; l++)
            {
                var ld = Data(g, l);
                var r = _layers[l];
                if (r.Op == LayerOp.Base) ResolveBase(g, ld, l, r.Transient || (crowded & (1u << l)) != 0);
                else ResolveDerived(g, ld, in r);
            }
        }

        _splats.Clear();

        foreach (var slot in _pendingOrder) _pending[slot] = false;
        _pendingOrder.Clear();
        Array.Copy(_now, _deposited, _count);
    }

    private void ResolveBase(GridData g, LayerData ld, int layer, bool crowded)
    {
        var size = g.Size;
        var tps = g.TilesPerSide;
        var precision = _layers[layer].Precision;
        var processed = new HashSet<int>();
        var transient = _layers[layer].Transient;
        var sources = transient ? _splats.ToArray() : _now;
        var sourceCount = transient ? sources.Length : _count;
        if (crowded)
        {
            if (!transient) ld.Epochs ??= new int[g.TileCount];
            for (var i = 0; i < sourceCount; i++)
            {
                ref readonly var s = ref sources[i];
                if (!s.Alive || s.Gain == 0 || s.Layer != layer) continue;
                MarkCovered(g, in s, precision, processed);
            }

            var prev = ld.Field;
            if (prev != null)
                for (var t = 0; t < g.TileCount; t++)
                    if (TileNonZero(prev, size, (t % tps) << 5, (t / tps) << 5)) processed.Add(t);
            if (transient && processed.Count != 0) ld.Epochs ??= new int[g.TileCount];
        }
        else
        {
            foreach (var slot in _pendingOrder)
            {
                ref readonly var from = ref _pendingFrom[slot];
                ref readonly var to = ref _now[slot];
                if (from.Gain != 0 && from.Layer == layer) MarkDeposited(g, ld, in from, processed);
                if (_pendingToGain[slot] != 0 && to.Layer == layer) MarkDeposited(g, ld, in to, processed);
            }
        }

        if (processed.Count != 0) ld.MaxInit = true;

        var field = new short[size * size];
        BigInteger[]? core = null;
        BigInteger[]? tent = null;
        BigInteger[]? bell = null;
        for (var i = 0; i < sourceCount; i++)
        {
            ref readonly var s = ref sources[i];
            if (!s.Alive || s.Gain == 0 || s.Layer != layer) continue;
            var v = StampCatalog.Get(s.Stamp);
            if (!TileBake.Place(s.X, s.Y, g.OriginX, g.OriginY, g.ScaleQ8, size, v, s.Angle, s.Scale, precision, out var p))
                continue;

            var x0 = Math.Max(p.X0, 0);
            var y0 = Math.Max(p.Y0, 0);
            var x1 = Math.Min(p.X1, size);
            var y1 = Math.Min(p.Y1, size);
            if (x1 <= x0 || y1 <= y0) continue;
            for (var cy = y0; cy < y1; cy++)
            for (var cx = x0; cx < x1; cx++)
            {
                var placement = p;
                var at = cy * size + cx;
                switch (p.Plane)
                {
                    case Plane.Difference:
                        core ??= new BigInteger[size * size];
                        core[at] += TileBake.BoxAt(p.Px, p.Py, p.Fx, p.Fy, p.ExtentX, p.ExtentY, p.V, s.Gain, cx, cy);
                        break;
                    case Plane.Dense when p.Turned:
                        core ??= new BigInteger[size * size];
                        core[at] += (long)TileBake.TurnedDenseAt(&placement, cx, cy) * s.Gain;
                        break;
                    case Plane.Dense:
                    {
                        var cell = 0;
                        TileBake.EmitRaster(&cell, cx, cy, p.Px, p.Py, p.Fx, p.Fy,
                            Math.Min(p.X1, cx + 1), Math.Min(p.Y1, cy + 1), p.Sampling, p.V, s.Gain);
                        core ??= new BigInteger[size * size];
                        core[at] += cell;
                        break;
                    }
                    case Plane.Tent:
                        tent ??= new BigInteger[size * size];
                        tent[at] += Smooth(&placement, s.Gain, cx, cy);
                        break;
                    default:
                        bell ??= new BigInteger[size * size];
                        bell[at] += Smooth(&placement, s.Gain, cx, cy);
                        break;
                }
            }
        }

        for (var i = 0; i < field.Length; i++)
        {
            var total = (core?[i] ?? 0) + RoundQ40(tent?[i] ?? 0) + RoundQ40(bell?[i] ?? 0);
            field[i] = (short)Math.Clamp((long)total, short.MinValue, short.MaxValue);
        }

        Publish(g, ld, field);
    }

    private void MarkDeposited(GridData g, LayerData ld, in Source s, HashSet<int> processed)
    {
        var v = StampCatalog.Get(s.Stamp);
        if (!TileBake.Place(s.X, s.Y, g.OriginX, g.OriginY, g.ScaleQ8, g.Size, v, s.Angle, s.Scale,
                _layers[s.Layer].Precision, out var p)) return;
        var x0 = Math.Max(p.X0, 0);
        var y0 = Math.Max(p.Y0, 0);
        var x1 = Math.Min(p.X1, g.Size);
        var y1 = Math.Min(p.Y1, g.Size);
        if (x1 <= x0 || y1 <= y0) return;
        var tps = g.TilesPerSide;
        for (var ty = y0 >> 5; ty <= (y1 - 1) >> 5; ty++)
        for (var tx = x0 >> 5; tx <= (x1 - 1) >> 5; tx++)
        {
            var placement = p;
            if (!TileBake.Touches(&placement, tx << 5, ty << 5)) continue;
            ld.Epochs ??= new int[g.TileCount];
            processed.Add(ty * tps + tx);
        }
    }

    private void MarkCovered(GridData g, in Source s, Layer.Precision precision, HashSet<int> processed)
    {
        var v = StampCatalog.Get(s.Stamp);
        if (!TileBake.Place(s.X, s.Y, g.OriginX, g.OriginY, g.ScaleQ8, g.Size, v, s.Angle, s.Scale,
                precision, out var p)) return;
        var x0 = Math.Max(p.X0, 0);
        var y0 = Math.Max(p.Y0, 0);
        var x1 = Math.Min(p.X1, g.Size);
        var y1 = Math.Min(p.Y1, g.Size);
        if (x1 <= x0 || y1 <= y0) return;
        var tps = g.TilesPerSide;
        for (var ty = y0 >> 5; ty <= (y1 - 1) >> 5; ty++)
        for (var tx = x0 >> 5; tx <= (x1 - 1) >> 5; tx++)
        {
            var placement = p;
            if (TileBake.Touches(&placement, tx << 5, ty << 5)) processed.Add(ty * tps + tx);
        }
    }

    private void ResolveDerived(GridData g, LayerData ld, in Recipe r)
    {
        var touched = new HashSet<int>(ld.Marked);
        ld.Marked.Clear();
        foreach (var t in g.Layers[r.A]?.Changed ?? (IEnumerable<int>)Array.Empty<int>()) touched.Add(t);
        if (r.B != r.A)
            foreach (var t in g.Layers[r.B]?.Changed ?? (IEnumerable<int>)Array.Empty<int>()) touched.Add(t);
        if (touched.Count == 0)
        {
            ld.Changed.Clear();
            return;
        }

        ld.Epochs ??= new int[g.TileCount];
        ld.MaxInit = true;
        var a = LayerField(g, r.A);
        var b = LayerField(g, r.B);
        var size = g.Size;
        var next = ld.Field ?? new short[size * size];
        var copy = (short[])next.Clone();
        for (var i = 0; i < copy.Length; i++)
            copy[i] = CombineCell(in r, a?[i] ?? 0, b?[i] ?? 0);
        Publish(g, ld, copy);
    }

    private void Publish(GridData g, LayerData ld, short[] field)
    {
        var tps = g.TilesPerSide;
        var previous = ld.Field;
        var changed = ld.Changed;
        changed.Clear();
        for (var t = 0; t < g.TileCount; t++)
        {
            var tx = (t % tps) << 5;
            var ty = (t / tps) << 5;
            var differs = false;
            for (var y = ty; y < ty + 32 && !differs; y++)
            for (var x = tx; x < tx + 32 && !differs; x++)
                differs = field[y * g.Size + x] != (previous?[y * g.Size + x] ?? 0);
            if (differs) changed.Add(t);
        }

        if (changed.Count != 0 && ld.Epochs != null)
            foreach (var t in changed) ld.Epochs[t] = _tick;
        ld.Field = field;
    }

    private static BigInteger RoundQ40(BigInteger value)
        => (value + 549755813888L + (value.Sign < 0 ? -1 : 0)) >> 40;

    private static short CombineCell(in Recipe r, int a, int b)
    {
        switch (r.Op)
        {
            case LayerOp.Sum:
            {
                var v = r.WeightA * a + r.WeightB * b;
                if (r.Shift != 0) v = (v + (1 << (r.Shift - 1)) + (v >> 31)) >> r.Shift;
                return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
            case LayerOp.Min:
                return (short)Math.Min(a, b);
            case LayerOp.Max:
                return (short)Math.Max(a, b);
            default:
                return b >= r.Low && b <= r.High ? (short)a : (short)0;
        }
    }

    public void Record()
    {
        _journal.Clear();
        _scheduleJournal.Clear();
        _journaled.Clear();
        _scheduleJournaled.Clear();
        _recordTick = _tick;
        _recording = true;
    }

    public void StopRecording()
    {
        _journal.Clear();
        _scheduleJournal.Clear();
        _journaled.Clear();
        _scheduleJournaled.Clear();
        _recording = false;
    }

    public void Rewind()
    {
        _revision++;
        for (var i = _pendingOrder.Count - 1; i >= 0; i--)
        {
            var slot = _pendingOrder[i];
            RestoreFields(ref _now[slot], _pendingFrom[slot]);
            _pending[slot] = false;
        }

        _pendingOrder.Clear();
        for (var j = _journal.Count - 1; j >= 0; j--)
        {
            var (slot, from) = _journal[j];
            RestoreFields(ref _now[slot], from);
            if (!_pending[slot])
            {
                _pending[slot] = true;
                _pendingOrder.Add(slot);
            }

            var deposited = _deposited[slot];
            if (!deposited.Alive) deposited.Gain = 0;
            _pendingFrom[slot] = deposited;
            _pendingToGain[slot] = _now[slot].Alive ? _now[slot].Gain : (sbyte)0;
        }

        _journal.Clear();
        _journaled.Clear();
        _scheduleJournaled.Clear();
        for (var j = _scheduleJournal.Count - 1; j >= 0; j--)
        {
            var (slot, state) = _scheduleJournal[j];
            ApplySchedule(ref _now[slot], state);
        }

        _scheduleJournal.Clear();
        if (_recording)
        {
            var delta = _tick - _recordTick;
            for (var i = 0; i < _count; i++)
            {
                ref var s = ref _now[i];
                if (!s.Alive || s.Timed == 0) continue;
                s.FadeStart += delta;
                s.ExpireAt += delta;
            }
        }

        _recording = false;
        _free.Clear();
        for (var i = 0; i < _count; i++)
            if (!_now[i].Alive && _now[i].Gen != uint.MaxValue) _free.Enqueue(i);
    }

    private static void RestoreFields(ref Source target, in Source from)
    {
        target.X = from.X;
        target.Y = from.Y;
        target.Stamp = from.Stamp;
        target.Layer = from.Layer;
        target.Gain = from.Gain;
        target.Angle = from.Angle;
        target.Scale = from.Scale;
        target.Alive = from.Alive;
        if (from.Alive) target.Gen = from.Gen;
    }

    public void Clear()
    {
        _revision++;
        for (var i = 0; i < _count; i++)
        {
            _now[i].Alive = false;
            _deposited[i].Alive = false;
        }
        _count = 0;
        _splats.Clear();
        _free.Clear();
        _pendingOrder.Clear();
        Array.Clear(_pending, 0, _pending.Length);
        _journal.Clear();
        _scheduleJournal.Clear();
        _journaled.Clear();
        _scheduleJournaled.Clear();
        _recording = false;
        foreach (var g in _grids)
        {
            for (var l = 0; l < _layers.Count; l++)
            {
                var ld = g.Layers[l];
                if (ld == null) continue;
                if (ld.Epochs != null && ld.Field != null)
                {
                    var tps = g.TilesPerSide;
                    for (var t = 0; t < g.TileCount; t++)
                    {
                        var tx = (t % tps) << 5;
                        var ty = (t / tps) << 5;
                        if (TileNonZero(ld.Field, g.Size, tx, ty)) ld.Epochs[t] = _tick + 1;
                    }
                }

                ld.Field = null;
                ld.Changed.Clear();
                ld.Marked.Clear();
                ld.MaxInit = false;
            }
        }
    }

    private bool Applied(World.SourceId id, byte layer, out int slot, out Source applied)
    {
        slot = -1;
        applied = default;
        var index = (int)id.Slot;
        if ((uint)index >= (uint)_count) return false;
        var generation = id.Generation;
        var reads = _layers[layer].Reads;
        Source candidate;
        if (_pending[index])
        {
            candidate = _pendingFrom[index];
            if (!candidate.Alive || candidate.Gen != generation || (reads & (1u << candidate.Layer)) == 0) return false;
        }
        else
        {
            candidate = _deposited[index];
            if (!candidate.Alive || candidate.Gen != generation || (reads & (1u << candidate.Layer)) == 0)
                return false;
        }

        if (candidate.Gain == 0) return false;
        slot = index;
        applied = candidate;
        return true;
    }

    private short ExcludedCell(GridData g, byte layer, int excludeSlot, in Source exclude, int cx, int cy)
    {
        if ((uint)cx >= (uint)g.Size || (uint)cy >= (uint)g.Size) return 0;
        var r = _layers[layer];
        if (r.Op == LayerOp.Base)
            return layer == exclude.Layer ? CellWithout(g, layer, excludeSlot, cx, cy) : Cell(g, layer, cx, cy);
        if ((r.Reads & (1u << exclude.Layer)) == 0) return Cell(g, layer, cx, cy);
        var a = ExcludedCell(g, r.A, excludeSlot, exclude, cx, cy);
        var b = r.B == r.A ? a : ExcludedCell(g, r.B, excludeSlot, exclude, cx, cy);
        return CombineCell(in r, a, b);
    }

    private short CellWithout(GridData g, byte layer, int excludeSlot, int cx, int cy)
    {
        BigInteger core = 0;
        BigInteger tent = 0;
        BigInteger bell = 0;
        for (var i = 0; i < _count; i++)
        {
            if (i == excludeSlot) continue;
            ref readonly var s = ref _deposited[i];
            if (!s.Alive || s.Gain == 0 || s.Layer != layer) continue;
            Contribution(g, layer, in s, cx, cy, ref core, ref tent, ref bell);
        }

        var total = core + RoundQ40(tent) + RoundQ40(bell);
        return (short)Math.Clamp((long)total, short.MinValue, short.MaxValue);
    }

    private void Contribution(GridData g, byte layer, in Source s, int cx, int cy,
        ref BigInteger core, ref BigInteger tent, ref BigInteger bell)
    {
        var v = StampCatalog.Get(s.Stamp);
        if (!TileBake.Place(s.X, s.Y, g.OriginX, g.OriginY, g.ScaleQ8, g.Size, v, s.Angle, s.Scale,
                _layers[layer].Precision, out var p) || !p.Reaches(cx, cy)) return;
        var placement = p;
        switch (p.Plane)
        {
            case Plane.Difference:
                core += TileBake.BoxAt(p.Px, p.Py, p.Fx, p.Fy, p.ExtentX, p.ExtentY, p.V, s.Gain, cx, cy);
                break;
            case Plane.Dense when p.Turned:
                core += (long)TileBake.TurnedDenseAt(&placement, cx, cy) * s.Gain;
                break;
            case Plane.Dense:
            {
                var cell = 0;
                TileBake.EmitRaster(&cell, cx, cy, p.Px, p.Py, p.Fx, p.Fy,
                    Math.Min(p.X1, cx + 1), Math.Min(p.Y1, cy + 1), p.Sampling, p.V, s.Gain);
                core += cell;
                break;
            }
            case Plane.Tent:
                tent += Smooth(&placement, s.Gain, cx, cy);
                break;
            default:
                bell += Smooth(&placement, s.Gain, cx, cy);
                break;
        }
    }

    private static long Smooth(Placement* p, int gain, int cx, int cy)
    {
        if (p->Turned) return TileBake.TurnedSmoothAt(p, cx, cy) * gain;
        long wx;
        long wy;
        TileBake.SmoothWeights(p->Kind, p->Px, p->Fx, p->ExtentX, cx, 1, &wx);
        TileBake.SmoothWeights(p->Kind, p->Py, p->Fy, p->ExtentY, cy, 1, &wy);
        return (long)p->V->Constant * gain * p->Curve * wx * wy;
    }

    public short Query(byte grid, byte layer, int x, int y)
    {
        if (grid >= _grids.Count || layer >= _layers.Count) return 0;
        return Cell(_grids[grid], layer, x, y);
    }

    private static short Cell(GridData g, byte layer, int cx, int cy)
    {
        if ((uint)cx >= (uint)g.Size || (uint)cy >= (uint)g.Size) return 0;
        var field = LayerField(g, layer);
        return field?[cy * g.Size + cx] ?? 0;
    }

    public long Query(byte grid, byte layer, int x, int y, int width, int height)
    {
        if (grid >= _grids.Count || layer >= _layers.Count || width <= 0 || height <= 0) return 0;
        var g = _grids[grid];
        var x1 = (int)Math.Min((long)x + width, g.Size);
        var y1 = (int)Math.Min((long)y + height, g.Size);
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        var field = LayerField(g, layer);
        if (field == null) return 0;
        var sum = 0L;
        for (var cy = y0; cy < y1; cy++)
        for (var cx = x0; cx < x1; cx++)
            sum += field[cy * g.Size + cx];
        return sum;
    }

    public void QueryRegion(byte grid, byte layer, int x, int y, int width, int height, short* destination)
    {
        if (grid >= _grids.Count || layer >= _layers.Count || width <= 0 || height <= 0) return;
        var g = _grids[grid];
        var field = LayerField(g, layer);
        for (var row = 0; row < height; row++)
        {
            var cy = y + row;
            var dst = destination + (long)row * width;
            for (var col = 0; col < width; col++)
            {
                var cx = x + col;
                dst[col] = field != null && (uint)cx < (uint)g.Size && (uint)cy < (uint)g.Size
                    ? field[cy * g.Size + cx]
                    : (short)0;
            }
        }
    }

    public short QueryAt(byte grid, byte layer, float x, float y)
    {
        if (grid >= _grids.Count || layer >= _layers.Count) return 0;
        var g = _grids[grid];
        return Query(grid, layer, (int)(CellQ8(x, g.OriginX, g.ScaleQ8) >> 8), (int)(CellQ8(y, g.OriginY, g.ScaleQ8) >> 8));
    }

    public short QueryMax(byte grid, byte layer, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (grid >= _grids.Count || layer >= _layers.Count) return 0;
        var g = _grids[grid];
        var ld = g.Layers[layer];
        if (ld == null || !ld.MaxInit) return 0;
        var field = ld.Field;
        var tps = g.TilesPerSide;
        var found = false;
        var best = short.MinValue;
        var bestKey = int.MaxValue;
        for (var ty = 0; ty < tps; ty++)
        for (var tx = 0; tx < tps; tx++)
        {
            var key = PyramidKey(tx, ty);
            var tileX = tx << 5;
            var tileY = ty << 5;
            if (TileNonZero(field, g.Size, tileX, tileY))
            {
                var tileBest = short.MinValue;
                var tileBestX = tileX;
                var tileBestY = tileY;
                for (var cy = tileY; cy < tileY + 32; cy++)
                for (var cx = tileX; cx < tileX + 32; cx++)
                {
                    var v = field![cy * g.Size + cx];
                    if (v > tileBest)
                    {
                        tileBest = v;
                        tileBestX = cx;
                        tileBestY = cy;
                    }
                }

                if (!found || tileBest > best || (tileBest == best && key < bestKey))
                {
                    found = true;
                    best = tileBest;
                    bestKey = key;
                    x = tileBestX;
                    y = tileBestY;
                }
            }
            else if (!found || 0 > best || (0 == best && key < bestKey))
            {
                found = true;
                best = 0;
                bestKey = key;
                x = tileX;
                y = tileY;
            }
        }

        return found ? best : (short)0;
    }

    public short QueryMax(byte grid, byte layer, int x, int y, int width, int height, out int bestX, out int bestY)
    {
        bestX = 0;
        bestY = 0;
        if (grid >= _grids.Count || layer >= _layers.Count || width <= 0 || height <= 0) return 0;
        var g = _grids[grid];
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        var x1 = (int)Math.Min((long)x + width, g.Size);
        var y1 = (int)Math.Min((long)y + height, g.Size);
        if (x1 <= x0 || y1 <= y0) return 0;
        bestX = x0;
        bestY = y0;
        var ld = g.Layers[layer];
        if (ld == null || !ld.MaxInit) return 0;
        var best = int.MinValue;
        var bestKey = int.MaxValue;
        for (var ty = y0 >> 5; ty <= (y1 - 1) >> 5; ty++)
        for (var tx = x0 >> 5; tx <= (x1 - 1) >> 5; tx++)
        {
            var key = PyramidKey(tx, ty);
            var tileX = tx << 5;
            var tileY = ty << 5;
            var ox0 = Math.Max(x0, tileX);
            var oy0 = Math.Max(y0, tileY);
            var ox1 = Math.Min(x1 - 1, tileX + 31);
            var oy1 = Math.Min(y1 - 1, tileY + 31);
            if (TileNonZero(ld.Field, g.Size, tileX, tileY))
            {
                var tileBest = int.MinValue;
                var tileBestX = ox0;
                var tileBestY = oy0;
                for (var cy = oy0; cy <= oy1; cy++)
                for (var cx = ox0; cx <= ox1; cx++)
                {
                    var v = ld.Field![cy * g.Size + cx];
                    if (v > tileBest)
                    {
                        tileBest = v;
                        tileBestX = cx;
                        tileBestY = cy;
                    }
                }

                if (tileBest > best || (tileBest == best && key < bestKey))
                {
                    best = tileBest;
                    bestKey = key;
                    bestX = tileBestX;
                    bestY = tileBestY;
                }
            }
            else if (0 > best || (0 == best && key < bestKey))
            {
                best = 0;
                bestKey = key;
                bestX = ox0;
                bestY = oy0;
            }
        }

        return best == int.MinValue ? (short)0 : (short)best;
    }

    private static int PyramidKey(int tx, int ty)
        => ((ty >> 6) << 15) | ((tx >> 6) << 12) | (((ty >> 3) & 7) << 9) | (((tx >> 3) & 7) << 6) | ((ty & 7) << 3) | (tx & 7);

    private static bool TileNonZero(short[]? field, int size, int tileX, int tileY)
    {
        if (field == null) return false;
        for (var y = tileY; y < tileY + 32; y++)
        for (var x = tileX; x < tileX + 32; x++)
            if (field[y * size + x] != 0) return true;
        return false;
    }

    public void QueryGradient(byte grid, byte layer, float x, float y, out int gx, out int gy)
    {
        gx = 0;
        gy = 0;
        if (grid >= _grids.Count || layer >= _layers.Count) return;
        var g = _grids[grid];
        var cx = (int)(CellQ8(x, g.OriginX, g.ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g.OriginY, g.ScaleQ8) >> 8);
        gx = Query(grid, layer, cx + 1, cy) - Query(grid, layer, cx - 1, cy);
        gy = Query(grid, layer, cx, cy + 1) - Query(grid, layer, cx, cy - 1);
    }

    public bool Covers(float x, float y)
    {
        foreach (var g in _grids)
            if (Contains(g, CellQ8(x, g.OriginX, g.ScaleQ8), CellQ8(y, g.OriginY, g.ScaleQ8))) return true;
        return false;
    }

    public bool TrySense(byte layer, float x, float y, out short value)
        => SenseValue(layer, x, y, default, out value);

    public bool TrySense(byte layer, float x, float y, World.SourceId exclude, out short value)
        => SenseValue(layer, x, y, exclude, out value);

    private bool SenseValue(byte layer, float x, float y, World.SourceId exclude, out short value)
    {
        value = 0;
        if (layer >= _layers.Count || !Pick(x, y, 0f, 0, false, out var gi, out var complete)) return false;
        var g = _grids[gi];
        var cx = (int)(CellQ8(x, g.OriginX, g.ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g.OriginY, g.ScaleQ8) >> 8);
        if (exclude.Generation != 0 && Applied(exclude, layer, out var slot, out var source))
        {
            value = ExcludedCell(g, layer, slot, source, cx, cy);
            return complete;
        }

        value = Cell(g, layer, cx, cy);
        return complete;
    }

    public bool TrySenseArea(byte layer, float x, float y, float reach, out long total)
        => SenseArea(layer, x, y, reach, default, out total);

    public bool TrySenseArea(byte layer, float x, float y, float reach, World.SourceId exclude, out long total)
        => SenseArea(layer, x, y, reach, exclude, out total);

    private bool SenseArea(byte layer, float x, float y, float reach, World.SourceId exclude, out long total)
    {
        total = 0;
        if (layer >= _layers.Count || !Pick(x, y, reach, 0, true, out var gi, out var complete)) return false;
        var g = _grids[gi];
        var disk = Disk(g, x, y, reach);
        var excluding = Applied(exclude, layer, out var slot, out var source);
        var sum = 0L;
        for (var cy = disk.Y0; cy <= disk.Y1; cy++)
        {
            if (!Span(disk, cy, out var lo, out var hi)) continue;
            for (var cx = lo; cx < hi; cx++)
                sum += excluding ? ExcludedCell(g, layer, slot, source, cx, cy) : Cell(g, layer, cx, cy);
        }

        total = WorldArea(sum, g.ScaleQ8);
        return complete;
    }

    public bool TrySenseMax(byte layer, float x, float y, float reach, out short value, out float bestX, out float bestY)
    {
        value = 0;
        bestX = 0;
        bestY = 0;
        if (layer >= _layers.Count || !Pick(x, y, reach, 0, true, out var gi, out var complete)) return false;
        var g = _grids[gi];
        var disk = Disk(g, x, y, reach);
        var found = false;
        var best = short.MinValue;
        var bx = 0;
        var by = 0;
        for (var cy = disk.Y0; cy <= disk.Y1; cy++)
        {
            if (!Span(disk, cy, out var lo, out var hi)) continue;
            for (var cx = lo; cx < hi; cx++)
            {
                var v = Cell(g, layer, cx, cy);
                if (!found || v > best)
                {
                    found = true;
                    best = v;
                    bx = cx;
                    by = cy;
                }
            }
        }

        value = best;
        bestX = g.OriginX + ((bx << 8) + 128) / (float)g.ScaleQ8;
        bestY = g.OriginY + ((by << 8) + 128) / (float)g.ScaleQ8;
        return complete;
    }

    public bool TrySenseNearest(byte layer, float x, float y, float reach, short threshold,
        out short value, out float nearestX, out float nearestY)
    {
        value = short.MinValue;
        nearestX = 0;
        nearestY = 0;
        if (layer >= _layers.Count || !Pick(x, y, reach, 0, true, out var gi, out var complete)) return false;
        var g = _grids[gi];
        var disk = Disk(g, x, y, reach);
        var best = long.MaxValue;
        var bx = 0;
        var by = 0;
        for (var cy = disk.Y0; cy <= disk.Y1; cy++)
        {
            if (!Span(disk, cy, out var lo, out var hi)) continue;
            var dy = ((long)cy << 8) + 128 - disk.Py;
            var dy2 = dy * dy;
            for (var cx = lo; cx < hi; cx++)
            {
                if (Cell(g, layer, cx, cy) < threshold) continue;
                var dx = ((long)cx << 8) + 128 - disk.Px;
                var d = dx * dx + dy2;
                if (d > best) continue;
                if (d == best && (cy > by || (cy == by && cx >= bx))) continue;
                best = d;
                bx = cx;
                by = cy;
            }
        }

        if (best == long.MaxValue)
        {
            nearestX = x;
            nearestY = y;
            return complete;
        }

        value = Cell(g, layer, bx, by);
        nearestX = g.OriginX + ((bx << 8) + 128) / (float)g.ScaleQ8;
        nearestY = g.OriginY + ((by << 8) + 128) / (float)g.ScaleQ8;
        return complete;
    }

    public bool TrySenseGradient(byte layer, float x, float y, out float gx, out float gy)
        => SenseGradient(layer, x, y, default, out gx, out gy);

    public bool TrySenseGradient(byte layer, float x, float y, World.SourceId exclude, out float gx, out float gy)
        => SenseGradient(layer, x, y, exclude, out gx, out gy);

    private bool SenseGradient(byte layer, float x, float y, World.SourceId exclude, out float gx, out float gy)
    {
        gx = 0f;
        gy = 0f;
        if (layer >= _layers.Count || !Pick(x, y, 0f, 1, false, out var gi, out var complete)) return false;
        var g = _grids[gi];
        var cx = (int)(CellQ8(x, g.OriginX, g.ScaleQ8) >> 8);
        var cy = (int)(CellQ8(y, g.OriginY, g.ScaleQ8) >> 8);
        var excluding = Applied(exclude, layer, out var slot, out var source);
        var perUnit = g.ScaleQ8 / 512f;
        gx = (NeighborCell(g, layer, excluding, slot, source, cx + 1, cy) - NeighborCell(g, layer, excluding, slot, source, cx - 1, cy)) * perUnit;
        gy = (NeighborCell(g, layer, excluding, slot, source, cx, cy + 1) - NeighborCell(g, layer, excluding, slot, source, cx, cy - 1)) * perUnit;
        return complete;
    }

    private short NeighborCell(GridData g, byte layer, bool excluding, int slot, in Source source, int cx, int cy)
        => excluding ? ExcludedCell(g, layer, slot, source, cx, cy) : Cell(g, layer, cx, cy);

    public bool Changed(byte layer, float x, float y, float reach, int since)
    {
        if (layer >= _layers.Count || !Pick(x, y, reach, 0, true, out var gi, out _)) return false;
        var g = _grids[gi];
        var epochs = g.Layers[layer]?.Epochs;
        if (epochs == null) return false;
        var disk = Disk(g, x, y, reach);
        for (var ty = disk.Y0 >> 5; ty <= disk.Y1 >> 5; ty++)
        for (var tx = disk.X0 >> 5; tx <= disk.X1 >> 5; tx++)
            if (epochs[ty * g.TilesPerSide + tx] - since > 0 && HoldsDiskCell(disk, tx, ty)) return true;
        return false;
    }

    private static bool HoldsDiskCell(in Circle disk, int tx, int ty)
    {
        if (Outside(disk, tx, ty)) return false;
        var tileX = tx << 5;
        var tileY = ty << 5;
        var rowEnd = Math.Min(tileY + 31, disk.Y1);
        for (var cy = Math.Max(tileY, disk.Y0); cy <= rowEnd; cy++)
            if (Span(disk, cy, out var lo, out var hi) && Math.Max(lo, tileX) < Math.Min(hi, tileX + 32)) return true;
        return false;
    }

    public int ChangedTiles(byte grid, byte layer, int since, int* destination)
    {
        if (grid >= _grids.Count || layer >= _layers.Count) return 0;
        var g = _grids[grid];
        var epochs = g.Layers[layer]?.Epochs;
        if (epochs == null) return 0;
        var count = 0;
        for (var tile = 0; tile < g.TileCount; tile++)
        {
            if (epochs[tile] - since <= 0) continue;
            if (destination != null) destination[count] = tile;
            count++;
        }

        return count;
    }

    public int ChangedTiles(byte grid, byte layer, int* destination)
    {
        if (grid >= _grids.Count || layer >= _layers.Count) return 0;
        var changed = _grids[grid].Layers[layer]?.Changed;
        if (changed == null) return 0;
        if (destination != null)
            for (var i = 0; i < changed.Count; i++) destination[i] = changed[i];
        return changed.Count;
    }

    public int ChangedMask(byte layer, float* xs, float* ys, float reach, int since, int count, ulong* words)
    {
        if (layer >= _layers.Count || xs == null || ys == null || words == null || !float.IsFinite(reach))
        {
            ClearBits(words, count);
            return 0;
        }

        if (count <= 0) return 0;
        ClearBits(words, count);
        var hits = 0;
        for (var i = 0; i < count; i++)
        {
            if (!Changed(layer, xs[i], ys[i], reach, since)) continue;
            words[i >> 6] |= 1ul << (i & 63);
            hits++;
        }

        return hits;
    }

    private static void ClearBits(ulong* words, int count)
    {
        if (words != null && count > 0) new Span<ulong>(words, (int)(((uint)count + 63) >> 6)).Clear();
    }

    public bool SenseColumn(byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
        => SenseColumn(layer, xs, ys, null, count, values, whole);

    public bool SenseColumn(byte layer, float* xs, float* ys, World.SourceId* excludes, int count, short* values, ulong* whole)
    {
        if (layer >= _layers.Count) return Unfilled(values, whole, count);
        if (count <= 0) return true;
        if (xs == null || ys == null || values == null) return Unfilled(values, whole, count);
        ClearBits(whole, count);
        var complete = true;
        for (var i = 0; i < count; i++)
        {
            var filled = excludes != null && excludes[i].Generation != 0
                ? TrySense(layer, xs[i], ys[i], excludes[i], out values[i])
                : TrySense(layer, xs[i], ys[i], out values[i]);
            complete &= filled;
            if (filled && whole != null) whole[i >> 6] |= 1ul << (i & 63);
        }

        return complete;
    }

    private static bool Unfilled(short* values, ulong* whole, int count)
    {
        if (values != null && count > 0) new Span<short>(values, count).Clear();
        ClearBits(whole, count);
        return false;
    }

    private bool FootprintFits(byte stamp, long scale)
    {
        var v = StampCatalog.Get(stamp);
        foreach (var g in _grids)
        {
            if (g.ScaleQ8 == 0) continue;
            var sampling = g.ScaleQ8 * scale >> 8;
            if (!ValidationRules.IsFootprint(v->Width * sampling + 255 >> 8) ||
                !ValidationRules.IsFootprint(v->Height * sampling + 255 >> 8))
                return false;
        }

        return true;
    }

    private bool Pick(float x, float y, float reach, int margin, bool disk, out int grid, out bool complete)
    {
        grid = -1;
        complete = false;
        if (disk && !float.IsFinite(reach)) return false;
        var bestScale = 0;
        for (var gi = 0; gi < _grids.Count; gi++)
        {
            var g = _grids[gi];
            if (g.ScaleQ8 == 0) continue;
            var qx = CellQ8(x, g.OriginX, g.ScaleQ8);
            var qy = CellQ8(y, g.OriginY, g.ScaleQ8);
            if (!Contains(g, qx, qy)) continue;
            bool whole;
            if (disk)
            {
                if (World.ReachQ8(reach, g.ScaleQ8) > (long)ValidationRules.MaxReachCells << 8) continue;

                var c = Disk(g, x, y, reach);
                whole = c.X0 >= 0 && c.Y0 >= 0 && c.X1 < g.Size && c.Y1 < g.Size && !c.Clipped;
            }
            else
            {
                var cx = qx >> 8;
                var cy = qy >> 8;
                whole = cx - margin >= 0 && cy - margin >= 0 && cx + margin < g.Size && cy + margin < g.Size;
            }

            var better = grid < 0 || (whole && !complete) || (whole == complete && g.ScaleQ8 > bestScale);
            if (!better) continue;
            grid = gi;
            complete = whole;
            bestScale = g.ScaleQ8;
        }

        return grid >= 0;
    }

    private struct Circle
    {
        public long Px;
        public long Py;
        public long R;
        public int Cx;
        public int Cy;
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public bool Clipped;
    }

    private static Circle Disk(GridData g, float x, float y, float reach)
    {
        var c = new Circle
        {
            Px = CellQ8(x, g.OriginX, g.ScaleQ8),
            Py = CellQ8(y, g.OriginY, g.ScaleQ8),
            R = ReachQ8(reach, g.ScaleQ8),
        };
        c.Cx = (int)(c.Px >> 8);
        c.Cy = (int)(c.Py >> 8);
        var x0 = Math.Min(CeilCell(c.Px - c.R - 128), c.Cx);
        var y0 = Math.Min(CeilCell(c.Py - c.R - 128), c.Cy);
        var x1 = Math.Max((c.Px + c.R - 128) >> 8, c.Cx);
        var y1 = Math.Max((c.Py + c.R - 128) >> 8, c.Cy);
        c.Clipped = x0 < 0 || y0 < 0 || x1 >= g.Size || y1 >= g.Size;
        c.X0 = (int)Math.Max(x0, 0);
        c.Y0 = (int)Math.Max(y0, 0);
        c.X1 = (int)Math.Min(x1, g.Size - 1);
        c.Y1 = (int)Math.Min(y1, g.Size - 1);
        return c;
    }

    private static bool Span(in Circle c, int cy, out int lo, out int hi)
    {
        var dy = ((long)cy << 8) + 128 - c.Py;
        var rem = c.R * c.R - dy * dy;
        long l;
        long h;
        if (rem < 0)
        {
            l = c.Cx;
            h = c.Cx;
            if (cy != c.Cy)
            {
                lo = 0;
                hi = 0;
                return false;
            }
        }
        else
        {
            var reach = SquareRoot(rem);
            l = CeilCell(c.Px - reach - 128);
            h = (c.Px + reach - 128) >> 8;
            if (cy == c.Cy)
            {
                l = Math.Min(l, c.Cx);
                h = Math.Max(h, c.Cx);
            }
        }

        lo = (int)Math.Max(l, c.X0);
        hi = (int)Math.Min(h, c.X1) + 1;
        return hi > lo;
    }

    private static bool Outside(in Circle c, int tx, int ty)
    {
        var x0 = tx << 5;
        var y0 = ty << 5;
        var x1 = x0 + 31;
        var y1 = y0 + 31;
        if (x1 < c.X0 || y1 < c.Y0 || x0 > c.X1 || y0 > c.Y1) return true;
        if (c.Cx >= x0 && c.Cx <= x1 && c.Cy >= y0 && c.Cy <= y1) return false;
        var nx = Math.Clamp(c.Px, ((long)x0 << 8) + 128, ((long)x1 << 8) + 128) - c.Px;
        var ny = Math.Clamp(c.Py, ((long)y0 << 8) + 128, ((long)y1 << 8) + 128) - c.Py;
        return nx * nx + ny * ny > c.R * c.R;
    }

    private static long CellQ8(float value, float origin, int scaleQ8)
    {
        var q = (value - origin) * scaleQ8;
        if (MathF.Abs(q) <= MaxCellQ8) return (int)MathF.Floor(q);
        return q > 0f ? (long)MaxCellQ8 : -(long)MaxCellQ8;
    }

    private static bool Contains(GridData g, long qx, long qy)
        => qx >= 0 && qy >= 0 && (qx >> 8) < g.Size && (qy >> 8) < g.Size;

    private static long ReachQ8(float reach, int scaleQ8)
    {
        if (!(reach > 0f)) return 0;
        var q = reach * scaleQ8;
        return q >= MaxReachQ8 ? MaxReachQ8 : (long)MathF.Floor(q);
    }

    private static long CeilCell(long q8) => -(-q8 >> 8);

    private static long SquareRoot(long value)
    {
        var root = (long)Math.Sqrt(value);
        while (root * root > value) root--;
        while ((root + 1) * (root + 1) <= value) root++;
        return root;
    }

    private static long WorldArea(long cells, int scaleQ8)
    {
        var den = (long)scaleQ8 * scaleQ8;
        var num = cells * 65536;
        var magnitude = ((num < 0 ? -num : num) + den / 2) / den;
        return num < 0 ? -magnitude : magnitude;
    }
}
