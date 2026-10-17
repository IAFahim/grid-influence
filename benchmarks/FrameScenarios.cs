using System.Diagnostics;
using System.Runtime.CompilerServices;

internal abstract unsafe class FrameScenario : IDisposable
{
    protected readonly FrameArena Arena = new();
    protected readonly ulong Seed;
    protected readonly Gi.Layer.Precision LayerPrecision;
    protected byte WorldId;
    protected byte GridId;

    protected FrameScenario(int entities, int power, ulong seed, bool cell = false)
    {
        Entities = entities;
        Power = power;
        Seed = seed;
        LayerPrecision = cell ? Gi.Layer.Precision.Cell : Gi.Layer.Precision.Exact;
    }

    public int Entities { get; }

    public int Power { get; }

    public int Size => 1 << Power;

    public long CrossCell;
    public long CrossQ8;
    public long MoverSamples;

    public byte World => WorldId;

    public byte Grid => GridId;

    public abstract string Name { get; }

    public virtual string Variant => "";

    public abstract int Movers { get; }

    public abstract string[] Stages { get; }

    public abstract string Layout { get; }

    public abstract byte[] LayerIds { get; }

    public virtual double? TargetMs => null;

    public virtual int ResortInterval => 0;

    public virtual long[] ResortTicks => [];

    public double CrossingCell => MoverSamples == 0 ? double.NaN : (double)CrossCell / MoverSamples;

    public double CrossingQ8 => MoverSamples == 0 ? double.NaN : (double)CrossQ8 / MoverSamples;

    public abstract void Setup();

    public virtual void Step(int frame)
    {
    }

    public abstract bool Run(int frame, long* marks);

    public abstract ulong Consume();

    public abstract bool VerifyState();

    public virtual void ResetCounters()
    {
        CrossCell = 0;
        CrossQ8 = 0;
        MoverSamples = 0;
    }

    public ulong FinalState()
    {
        var hash = FrameHash.Seed;
        foreach (var layer in LayerIds)
            hash = FrameHash.Mix(hash, (ulong)Gi.World.Query(WorldId, GridId, layer, 0, 0, Size, Size));
        return FrameHash.Mix(hash, (uint)Gi.World.Tick(WorldId));
    }

    public virtual void Dispose()
    {
        Gi.World.Free(WorldId);
        Arena.Dispose();
    }

    protected byte NewLayer() => Gi.Layer.New(WorldId, LayerPrecision);

    protected Gi.World.SourceId Place(byte layer, float x, float y, byte stamp, sbyte gain)
    {
        var id = Gi.World.Place(WorldId, layer, x, y, stamp, gain);
        if (id == default) throw new InvalidOperationException("Frame setup could not place a source.");
        return id;
    }

    protected bool SourceAt(Gi.World.SourceId id, float x, float y)
        => Gi.World.TryGetSourceSpec(WorldId, id, out var spec) &&
           BitConverter.SingleToInt32Bits(spec.X) == BitConverter.SingleToInt32Bits(x) &&
           BitConverter.SingleToInt32Bits(spec.Y) == BitConverter.SingleToInt32Bits(y);
}

internal abstract unsafe class FrameCanonical : FrameScenario
{
    protected const int Width = 8;
    protected byte BaseLayer;
    protected byte BoxStamp;
    protected Gi.World.SourceId* Ids;
    protected float* X;
    protected float* Y;
    protected float* NextX;
    protected float* NextY;
    private long _cellFlips;
    private long _q8Flips;

    protected FrameCanonical(int entities, int power, ulong seed, bool cell = false) : base(entities, power, seed, cell)
    {
    }

    public override byte[] LayerIds => [BaseLayer];

    public override void Setup()
    {
        WorldId = Gi.World.New();
        GridId = Gi.Grid.New(WorldId, Power, 0f, 0f, Size);
        BaseLayer = NewLayer();
        BoxStamp = Gi.Stamp.Box(Width, Width, 16);
        Ids = Arena.Alloc<Gi.World.SourceId>(Entities);
        X = Arena.Alloc<float>(Entities);
        Y = Arena.Alloc<float>(Entities);
        NextX = Arena.Alloc<float>(Entities);
        NextY = Arena.Alloc<float>(Entities);
        var rng = new FrameRandom(Seed);
        var span = Size - 2 * Width - 132;
        for (var i = 0; i < Entities; i++)
        {
            X[i] = Width + 65 + rng.Unit() * span;
            Y[i] = Width + 65 + rng.Unit() * span;
            NextX[i] = X[i] + 0.5f;
            NextY[i] = Y[i] + 0.25f;
            Ids[i] = Place(BaseLayer, X[i], Y[i], BoxStamp, (sbyte)(i % 3 == 0 ? -1 : 1));
            if ((int)X[i] != (int)NextX[i] || (int)Y[i] != (int)NextY[i]) _cellFlips++;
            if ((int)(X[i] * 256f) != (int)(NextX[i] * 256f) || (int)(Y[i] * 256f) != (int)(NextY[i] * 256f)) _q8Flips++;
        }

        Gi.World.Process(WorldId);
        ResetCounters();
    }

    public override void ResetCounters()
    {
        CrossCell = _cellFlips;
        CrossQ8 = _q8Flips;
        MoverSamples = Entities;
    }

    public override void Dispose()
    {
        Gi.Stamp.Free(BoxStamp);
        base.Dispose();
    }
}

internal sealed unsafe class FrameLegacy(int entities, int power, ulong seed, bool cell = false) : FrameCanonical(entities, power, seed, cell)
{
    private static readonly string[] StageNames = ["sync", "process", "point", "gradient", "changed"];
    private float* _gradientX;
    private float* _gradientY;
    private short* _values;
    private ulong* _whole;
    private ulong* _gradientWhole;
    private ulong* _changed;
    private long _hits;
    private int _last = -1;

    public override string Name => "B1";

    public override int Movers => Entities;

    public override string[] Stages => StageNames;

    public override string Layout
        => $"{Entities} boxes {Width}x{Width} on a {Size}x{Size} grid, every source moves 0.5 x 0.25 cells per frame";

    public override void Setup()
    {
        base.Setup();
        _gradientX = Arena.Alloc<float>(Entities);
        _gradientY = Arena.Alloc<float>(Entities);
        _values = Arena.Alloc<short>(Entities);
        _whole = Arena.Alloc<ulong>((Entities + 63) >> 6);
        _gradientWhole = Arena.Alloc<ulong>((Entities + 63) >> 6);
        _changed = Arena.Alloc<ulong>((Entities + 63) >> 6);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override bool Run(int frame, long* marks)
    {
        var px = (frame & 1) == 1 ? NextX : X;
        var py = (frame & 1) == 1 ? NextY : Y;
        _last = frame;
        marks[0] = Stopwatch.GetTimestamp();
        Gi.World.Sync(WorldId, Ids, px, py, Entities);
        marks[1] = Stopwatch.GetTimestamp();
        var since = Gi.World.Tick(WorldId);
        Gi.World.Process(WorldId);
        marks[2] = Stopwatch.GetTimestamp();
        var ok = (Gi.World.SenseColumn(WorldId, BaseLayer, px, py, Entities, _values, _whole) == Gi.World.ReadStatus.Complete);
        marks[3] = Stopwatch.GetTimestamp();
        ok &= (Gi.World.SenseGradientColumn(WorldId, BaseLayer, px, py, null, Entities, _gradientX, _gradientY, _gradientWhole) == Gi.World.ReadStatus.Complete);
        marks[4] = Stopwatch.GetTimestamp();
        _hits = Gi.World.ChangedMask(WorldId, BaseLayer, px, py, 0f, since, Entities, _changed);
        marks[5] = Stopwatch.GetTimestamp();
        return ok;
    }

    public override ulong Consume()
    {
        var hash = FrameHash.Mix(FrameHash.Seed, (ulong)_hits);
        hash = FrameHash.Elements(hash, _values, Entities);
        hash = FrameHash.Elements(hash, _gradientX, Entities);
        hash = FrameHash.Elements(hash, _gradientY, Entities);
        hash = FrameHash.Elements(hash, _whole, (Entities + 63) >> 6);
        return FrameHash.Elements(hash, _changed, (Entities + 63) >> 6);
    }

    public override bool VerifyState()
    {
        if (_last < 0) return false;
        var px = (_last & 1) == 1 ? NextX : X;
        var py = (_last & 1) == 1 ? NextY : Y;
        for (var i = 0; i < Entities; i++)
            if (!SourceAt(Ids[i], px[i], py[i])) return false;
        return true;
    }
}

internal sealed unsafe class FrameSparse(int entities, int power, ulong seed, int movers, bool cell = false) : FrameCanonical(entities, power, seed, cell)
{
    private static readonly string[] StageNames = ["move", "process"];
    private readonly int _movers = movers;
    private FrameRandom _rng = new(seed ^ 0x5851F42D4C957F2DUL);
    private byte* _phase;
    private Gi.World.SourceId* _moverIds;
    private float* _moverX;
    private float* _moverY;

    public override string Name => "B2";

    public override string Variant => "movers=" + _movers.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override int Movers => _movers;

    public override string[] Stages => StageNames;

    public override double? TargetMs => _movers <= 1 ? 0.002 : 0.02;

    public override string Layout
        => $"{Entities} boxes {Width}x{Width} on a {Size}x{Size} grid, {_movers} mover(s) shift 0.5 x 0.25 cells per frame, no reads";

    public override void Setup()
    {
        base.Setup();
        _phase = Arena.Alloc<byte>(Entities);
        _moverIds = Arena.Alloc<Gi.World.SourceId>(_movers);
        _moverX = Arena.Alloc<float>(_movers);
        _moverY = Arena.Alloc<float>(_movers);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void Step(int frame)
    {
        var start = (long)(_rng.Next() % (ulong)Entities);
        for (var i = 0; i < _movers; i++)
        {
            var index = (int)((start + i * 5003L) % Entities);
            var phase = (byte)(_phase[index] ^ 1);
            _phase[index] = phase;
            _moverIds[i] = Ids[index];
            _moverX[i] = phase == 0 ? X[index] : NextX[index];
            _moverY[i] = phase == 0 ? Y[index] : NextY[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override bool Run(int frame, long* marks)
    {
        marks[0] = Stopwatch.GetTimestamp();
        for (var i = 0; i < _movers; i++) Gi.World.Move(WorldId, _moverIds[i], _moverX[i], _moverY[i]);
        marks[1] = Stopwatch.GetTimestamp();
        Gi.World.Process(WorldId);
        marks[2] = Stopwatch.GetTimestamp();
        return true;
    }

    public override ulong Consume()
    {
        var hash = FrameHash.Seed;
        for (var i = 0; i < Math.Min(_movers, 16); i++)
            hash = FrameHash.Mix(hash, (ushort)Gi.World.Query(WorldId, GridId, BaseLayer, (int)_moverX[i], (int)_moverY[i]));
        return FrameHash.Mix(hash, (uint)Gi.World.Tick(WorldId));
    }

    public override bool VerifyState()
    {
        for (var i = 0; i < Entities; i++)
        {
            var moved = _phase[i] != 0;
            if (!SourceAt(Ids[i], moved ? NextX[i] : X[i], moved ? NextY[i] : Y[i])) return false;
        }

        return true;
    }
}

internal sealed unsafe class FrameIdle(int entities, int power, ulong seed, bool cell = false) : FrameCanonical(entities, power, seed, cell)
{
    private static readonly string[] StageNames = ["process"];

    public override string Name => "B4";

    public override int Movers => 0;

    public override string[] Stages => StageNames;

    public override double? TargetMs => 0.001;

    public override string Layout
        => $"{Entities} boxes {Width}x{Width} on a {Size}x{Size} grid, nothing changes, nothing is read";

    public override void ResetCounters()
    {
        CrossCell = 0;
        CrossQ8 = 0;
        MoverSamples = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override bool Run(int frame, long* marks)
    {
        marks[0] = Stopwatch.GetTimestamp();
        Gi.World.Process(WorldId);
        marks[1] = Stopwatch.GetTimestamp();
        return true;
    }

    public override ulong Consume() => FrameHash.Mix(FrameHash.Seed, (uint)Gi.World.Tick(WorldId));

    public override bool VerifyState()
    {
        for (var i = 0; i < Entities; i++)
            if (!SourceAt(Ids[i], X[i], Y[i])) return false;
        return true;
    }
}

internal sealed unsafe class FrameCrowd(int entities, int power, ulong seed, bool cell = false) : FrameScenario(entities, power, seed, cell)
{
    private const int Width = 4;
    private const int Interval = 128;
    private const int Disks = 1000;
    private const float Reach = 16f;
    private const float Speed = 0.1f;
    private static readonly string[] StageNames = ["sync", "process", "point", "gradient", "area"];
    private readonly long[] _resorts = new long[64];
    private int _resortCount;
    private byte _layer;
    private byte _stamp;
    private Gi.World.SourceId* _ids;
    private Gi.World.SourceId* _idsNext;
    private float* _x;
    private float* _xNext;
    private float* _y;
    private float* _yNext;
    private float* _vx;
    private float* _vxNext;
    private float* _vy;
    private float* _vyNext;
    private ulong* _pairs;
    private ulong* _scratch;
    private uint* _counts;
    private float* _gradientX;
    private float* _gradientY;
    private short* _values;
    private ulong* _whole;
    private ulong* _gradientWhole;
    private long _area;

    public override string Name => "C";

    public override int Movers => Entities;

    public override string[] Stages => StageNames;

    public override byte[] LayerIds => [_layer];

    public override double? TargetMs => 1.0;

    public override int ResortInterval => Interval;

    public override long[] ResortTicks => _resorts.AsSpan(0, _resortCount).ToArray();

    public override string Layout
        => $"{Entities} agents as persistent boxes {Width}x{Width} on a {Size}x{Size} grid, synced in Morton order every frame " +
           $"({Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)} cell/frame), re-sorted every {Interval} frames, " +
           $"value + gradient per agent, {Disks} disk sums at r={Reach.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public override void Setup()
    {
        WorldId = Gi.World.New();
        GridId = Gi.Grid.New(WorldId, Power, 0f, 0f, Size);
        _layer = ReadMode.Transient ? Gi.Layer.New(WorldId, Gi.Layer.Residency.Transient, LayerPrecision) : NewLayer();
        _stamp = Gi.Stamp.Box(Width, Width, 8);
        _ids = Arena.Alloc<Gi.World.SourceId>(Entities);
        _idsNext = Arena.Alloc<Gi.World.SourceId>(Entities);
        _x = Arena.Alloc<float>(Entities);
        _xNext = Arena.Alloc<float>(Entities);
        _y = Arena.Alloc<float>(Entities);
        _yNext = Arena.Alloc<float>(Entities);
        _vx = Arena.Alloc<float>(Entities);
        _vxNext = Arena.Alloc<float>(Entities);
        _vy = Arena.Alloc<float>(Entities);
        _vyNext = Arena.Alloc<float>(Entities);
        _pairs = Arena.Alloc<ulong>(Entities);
        _scratch = Arena.Alloc<ulong>(Entities);
        _counts = Arena.Alloc<uint>(2048);
        _gradientX = Arena.Alloc<float>(Entities);
        _gradientY = Arena.Alloc<float>(Entities);
        _values = Arena.Alloc<short>(Entities);
        _whole = Arena.Alloc<ulong>((Entities + 63) >> 6);
        _gradientWhole = Arena.Alloc<ulong>((Entities + 63) >> 6);

        var rng = new FrameRandom(Seed);
        var margin = Reach + 8f;
        var span = Size - 2f * margin;
        for (var i = 0; i < Entities; i++)
        {
            _x[i] = margin + rng.Unit() * span;
            _y[i] = margin + rng.Unit() * span;
            var vx = rng.Signed();
            var vy = rng.Signed();
            var length = MathF.Sqrt(vx * vx + vy * vy);
            if (length < 1e-3f)
            {
                vx = 1f;
                vy = 0f;
                length = 1f;
            }

            _vx[i] = vx / length * Speed;
            _vy[i] = vy / length * Speed;
        }

        Reorder();
        if (!ReadMode.Transient)
            for (var i = 0; i < Entities; i++) _ids[i] = Place(_layer, _x[i], _y[i], _stamp, 1);
        Gi.World.Process(WorldId);
        ResetCounters();
    }

    public override void Step(int frame)
    {
        if (frame % Interval == 0)
        {
            var started = Stopwatch.GetTimestamp();
            Reorder();
            var elapsed = Stopwatch.GetTimestamp() - started;
            if (_resortCount < _resorts.Length) _resorts[_resortCount++] = elapsed;
        }

        Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override bool Run(int frame, long* marks)
    {
        marks[0] = Stopwatch.GetTimestamp();
        if (ReadMode.Transient)
            Gi.World.Splat(WorldId, _layer, new ReadOnlySpan<float>(_x, Entities), new ReadOnlySpan<float>(_y, Entities), _stamp, 1);
        else
            Gi.World.Sync(WorldId, _ids, _x, _y, Entities);
        marks[1] = Stopwatch.GetTimestamp();
        Gi.World.Process(WorldId);
        marks[2] = Stopwatch.GetTimestamp();
        bool ok;
        if (ReadMode.View)
        {
            ok = ViewReads();
            marks[3] = Stopwatch.GetTimestamp();
            marks[4] = marks[3];
        }
        else if (ReadMode.Fused)
        {
            ok = (Gi.World.SenseGradientColumn(WorldId, _layer, _x, _y, null, Entities, _values, _gradientX, _gradientY, _whole) == Gi.World.ReadStatus.Complete);
            marks[3] = Stopwatch.GetTimestamp();
            marks[4] = marks[3];
        }
        else
        {
            ok = (Gi.World.SenseColumn(WorldId, _layer, _x, _y, Entities, _values, _whole) == Gi.World.ReadStatus.Complete);
            marks[3] = Stopwatch.GetTimestamp();
            ok &= (Gi.World.SenseGradientColumn(WorldId, _layer, _x, _y, null, Entities, _gradientX, _gradientY, _gradientWhole) == Gi.World.ReadStatus.Complete);
            marks[4] = Stopwatch.GetTimestamp();
        }

        var area = 0L;
        for (var i = 0; i < Disks; i++)
        {
            var index = (int)((i * 997L + frame * 13L) % Entities);
            ok &= Gi.World.TrySenseArea(WorldId, _layer, _x[index], _y[index], Reach, out var total);
            area += total;
        }

        _area = area;
        marks[5] = Stopwatch.GetTimestamp();
        return ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool ViewReads()
    {
        var view = Gi.World.View(WorldId, 0, _layer);
        var size = view.Size;
        var complete = true;
        new Span<ulong>(_whole, (Entities + 63) >> 6).Clear();
        for (var i = 0; i < Entities; i++)
        {
            if (!view.TryCell(_x[i], _y[i], out var cx, out var cy))
            {
                _values[i] = 0;
                _gradientX[i] = 0f;
                _gradientY[i] = 0f;
                complete = false;
                continue;
            }

            _values[i] = view[cx, cy];
            view.Gradient(cx, cy, out var gx, out var gy);
            _gradientX[i] = gx * 0.5f;
            _gradientY[i] = gy * 0.5f;
            if (cx - 1 >= 0 && cy - 1 >= 0 && cx + 1 < size && cy + 1 < size) _whole[i >> 6] |= 1ul << (i & 63);
            else complete = false;
        }

        return complete;
    }

    public override ulong Consume()
    {
        var hash = FrameHash.Mix(FrameHash.Seed, (ulong)_area);
        hash = FrameHash.Elements(hash, _values, Entities);
        hash = FrameHash.Elements(hash, _gradientX, Entities);
        hash = FrameHash.Elements(hash, _gradientY, Entities);
        return FrameHash.Elements(hash, _whole, (Entities + 63) >> 6);
    }

    public override bool VerifyState()
    {
        if (ReadMode.Transient) return true;
        for (var i = 0; i < Entities; i++)
            if (!SourceAt(_ids[i], _x[i], _y[i])) return false;
        return true;
    }

    public override void Dispose()
    {
        Gi.Stamp.Free(_stamp);
        base.Dispose();
    }

    private void Reorder()
    {
        var order = FrameOrder.Sort(_x, _y, Entities, Power, _pairs, _scratch, _counts);
        FrameOrder.Gather(_ids, _idsNext, order, Entities);
        FrameOrder.Gather(_x, _xNext, order, Entities);
        FrameOrder.Gather(_y, _yNext, order, Entities);
        FrameOrder.Gather(_vx, _vxNext, order, Entities);
        FrameOrder.Gather(_vy, _vyNext, order, Entities);
        FrameOrder.Swap(ref _ids, ref _idsNext);
        FrameOrder.Swap(ref _x, ref _xNext);
        FrameOrder.Swap(ref _y, ref _yNext);
        FrameOrder.Swap(ref _vx, ref _vxNext);
        FrameOrder.Swap(ref _vy, ref _vyNext);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Advance()
    {
        var low = Reach + 3f;
        var high = Size - low;
        long cell = 0, q8 = 0;
        for (var i = 0; i < Entities; i++)
        {
            var ox = _x[i];
            var oy = _y[i];
            var vx = _vx[i];
            var vy = _vy[i];
            var nx = ox + vx;
            var ny = oy + vy;
            if (nx < low || nx > high)
            {
                vx = -vx;
                _vx[i] = vx;
                nx = ox + vx;
            }

            if (ny < low || ny > high)
            {
                vy = -vy;
                _vy[i] = vy;
                ny = oy + vy;
            }

            _x[i] = nx;
            _y[i] = ny;
            if ((int)ox != (int)nx || (int)oy != (int)ny) cell++;
            if ((int)(ox * 256f) != (int)(nx * 256f) || (int)(oy * 256f) != (int)(ny * 256f)) q8++;
        }

        CrossCell += cell;
        CrossQ8 += q8;
        MoverSamples += Entities;
    }
}

internal sealed unsafe class FrameWorld(int entities, int power, ulong seed, bool cell = false) : FrameScenario(entities, power, seed, cell)
{
    private const int Lifetime = 100;
    private const float Speed = 0.1f;
    private const int FadeGain = 8;
    private const float Margin = 40f;
    private static readonly string[] StageNames = ["sync", "churn", "process", "point", "gradient", "area"];
    private readonly int _agents = (entities + 2) / 4;
    private readonly int _timed = entities / 100 / Lifetime * Lifetime;
    private readonly int _churn = Math.Max(entities / 1000, 1);
    private FrameRandom _rng = new(seed ^ 0x2545F4914F6CDD1DUL);
    private byte _boxes;
    private byte _tents;
    private byte _derived;
    private byte _boxStamp;
    private byte _tentStamp;
    private int _plain;
    private int _timedPerFrame;
    private int _plainPerFrame;
    private int _cursor;
    private Gi.World.SourceId* _agentIds;
    private float* _agentX;
    private float* _agentY;
    private float* _agentVx;
    private float* _agentVy;
    private Gi.World.SourceId* _plainIds;
    private byte* _plainKind;
    private Gi.World.SourceId* _timedIds;
    private float* _gradientX;
    private float* _gradientY;
    private short* _values;
    private ulong* _whole;
    private ulong* _gradientWhole;
    private long _area;

    public override string Name => "W";

    public override int Movers => _agents;

    public override string[] Stages => StageNames;

    public override byte[] LayerIds => [_boxes, _tents, _derived];

    public override double? TargetMs => 1.0;

    public override string Layout
    {
        get
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return $"{Entities} persistent sources on a {Size}x{Size} grid in Morton slot order: {_agents} agents (box 4x4, " +
                   $"{Speed.ToString(c)} cell/frame), {_plain} static (box 4x4 / tent 6x6), {_timed} timed (fade + expire, {Lifetime} frames); " +
                   $"layers: boxes, tents, derived Sum; per frame {_churn} spawns and {_churn} despawns " +
                   $"({_timedPerFrame} timed), {_agents} point + gradient senses on the derived layer, {_churn} disk sums r=8..32";
        }
    }

    public override void Setup()
    {
        _timedPerFrame = _timed / Lifetime;
        _plainPerFrame = _churn - _timedPerFrame;
        _plain = Entities - _agents - _timed;
        WorldId = Gi.World.New();
        GridId = Gi.Grid.New(WorldId, Power, 0f, 0f, Size);
        _boxes = NewLayer();
        _tents = NewLayer();
        _derived = Gi.Layer.Sum(WorldId, _boxes, 1, _tents, 1);
        _boxStamp = Gi.Stamp.Box(4, 4, 12);
        _tentStamp = Gi.Stamp.Tent(6, 6, 12);
        _agentIds = Arena.Alloc<Gi.World.SourceId>(_agents);
        _agentX = Arena.Alloc<float>(_agents);
        _agentY = Arena.Alloc<float>(_agents);
        _agentVx = Arena.Alloc<float>(_agents);
        _agentVy = Arena.Alloc<float>(_agents);
        _plainIds = Arena.Alloc<Gi.World.SourceId>(_plain);
        _plainKind = Arena.Alloc<byte>(_plain);
        _timedIds = Arena.Alloc<Gi.World.SourceId>(_timed);
        _gradientX = Arena.Alloc<float>(_agents);
        _gradientY = Arena.Alloc<float>(_agents);
        _values = Arena.Alloc<short>(_agents);
        _whole = Arena.Alloc<ulong>((_agents + 63) >> 6);
        _gradientWhole = Arena.Alloc<ulong>((_agents + 63) >> 6);

        var x = Arena.Alloc<float>(Entities);
        var y = Arena.Alloc<float>(Entities);
        var sortedX = Arena.Alloc<float>(Entities);
        var sortedY = Arena.Alloc<float>(Entities);
        var pairs = Arena.Alloc<ulong>(Entities);
        var scratch = Arena.Alloc<ulong>(Entities);
        var counts = Arena.Alloc<uint>(2048);
        var rng = new FrameRandom(Seed);
        var margin = Margin;
        var span = Size - 2f * margin;
        for (var i = 0; i < Entities; i++)
        {
            x[i] = margin + rng.Unit() * span;
            y[i] = margin + rng.Unit() * span;
        }

        var order = FrameOrder.Sort(x, y, Entities, Power, pairs, scratch, counts);
        FrameOrder.Gather(x, sortedX, order, Entities);
        FrameOrder.Gather(y, sortedY, order, Entities);

        var stride = TimedStride(_timed);
        var agent = 0;
        var plain = 0;
        var timedRank = 0;
        for (var j = 0; j < Entities; j++)
        {
            if (j % 4 == 1 && agent < _agents)
            {
                var vx = rng.Signed();
                var vy = rng.Signed();
                var length = MathF.Sqrt(vx * vx + vy * vy);
                if (length < 1e-3f)
                {
                    vx = 1f;
                    vy = 0f;
                    length = 1f;
                }

                _agentX[agent] = sortedX[j];
                _agentY[agent] = sortedY[j];
                _agentVx[agent] = vx / length * Speed;
                _agentVy[agent] = vy / length * Speed;
                _agentIds[agent] = Place(_boxes, sortedX[j], sortedY[j], _boxStamp, 1);
                agent++;
            }
            else if (j % 100 == 0 && timedRank < _timed)
            {
                var slot = (int)((long)timedRank * stride % _timed);
                var life = slot / _timedPerFrame + 1;
                var tent = (timedRank & 1) == 1;
                var id = Place(tent ? _tents : _boxes, sortedX[j], sortedY[j], tent ? _tentStamp : _boxStamp, FadeGain);
                Gi.World.Fade(WorldId, id, 0, life);
                Gi.World.Expire(WorldId, id, life);
                _timedIds[slot] = id;
                timedRank++;
            }
            else if (plain < _plain)
            {
                var tent = ((j >> 2) & 1) == 1;
                _plainKind[plain] = (byte)(tent ? 1 : 0);
                _plainIds[plain] = Place(tent ? _tents : _boxes, sortedX[j], sortedY[j], tent ? _tentStamp : _boxStamp, 1);
                plain++;
            }
        }

        if (agent != _agents || plain != _plain || timedRank != _timed)
            throw new InvalidOperationException("Frame W role assignment does not add up.");

        Gi.World.Process(WorldId);
        ResetCounters();
    }

    public override void Step(int frame)
    {
        Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override bool Run(int frame, long* marks)
    {
        marks[0] = Stopwatch.GetTimestamp();
        Gi.World.Sync(WorldId, _agentIds, _agentX, _agentY, _agents);
        marks[1] = Stopwatch.GetTimestamp();
        var ok = Churn(frame);
        marks[2] = Stopwatch.GetTimestamp();
        Gi.World.Process(WorldId);
        marks[3] = Stopwatch.GetTimestamp();
        ok &= (Gi.World.SenseColumn(WorldId, _derived, _agentX, _agentY, _agents, _values, _whole) == Gi.World.ReadStatus.Complete);
        marks[4] = Stopwatch.GetTimestamp();
        ok &= (Gi.World.SenseGradientColumn(WorldId, _derived, _agentX, _agentY, null, _agents, _gradientX, _gradientY, _gradientWhole) == Gi.World.ReadStatus.Complete);
        marks[5] = Stopwatch.GetTimestamp();
        var area = 0L;
        for (var i = 0; i < _churn; i++)
        {
            var index = (int)((i * 997L + frame * 13L) % _agents);
            ok &= Gi.World.TrySenseArea(WorldId, _derived, _agentX[index], _agentY[index], 8f + 8f * (i & 3), out var total);
            area += total;
        }

        _area = area;
        marks[6] = Stopwatch.GetTimestamp();
        return ok;
    }

    public override ulong Consume()
    {
        var hash = FrameHash.Mix(FrameHash.Seed, (ulong)_area);
        hash = FrameHash.Elements(hash, _values, _agents);
        hash = FrameHash.Elements(hash, _gradientX, _agents);
        hash = FrameHash.Elements(hash, _gradientY, _agents);
        return FrameHash.Elements(hash, _whole, (_agents + 63) >> 6);
    }

    public override bool VerifyState()
    {
        for (var i = 0; i < _agents; i++)
            if (!SourceAt(_agentIds[i], _agentX[i], _agentY[i])) return false;
        return LiveSources() == Entities;
    }

    public int LiveSources()
    {
        var context = Gi.World.GetContext(WorldId);
        if (context == null) return -1;
        var sources = &context->Sources;
        var live = 0;
        for (var slot = 0; slot < sources->Count; slot++)
            if (sources->Alive[slot] != 0) live++;
        return live;
    }

    public override void Dispose()
    {
        Gi.Stamp.Free(_boxStamp);
        Gi.Stamp.Free(_tentStamp);
        base.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Churn(int frame)
    {
        var ok = true;
        var margin = Margin;
        var span = Size - 2f * margin;
        for (var k = 0; k < _plainPerFrame; k++)
        {
            var victim = (int)((_cursor + (long)k) % _plain);
            Gi.World.Remove(WorldId, _plainIds[victim]);
            var tent = _plainKind[victim] != 0;
            var id = Gi.World.Place(WorldId, tent ? _tents : _boxes,
                margin + _rng.Unit() * span, margin + _rng.Unit() * span, tent ? _tentStamp : _boxStamp, 1);
            ok &= id != default;
            _plainIds[victim] = id;
        }

        _cursor = (int)((_cursor + (long)_plainPerFrame) % _plain);

        var group = frame % Lifetime;
        for (var k = 0; k < _timedPerFrame; k++)
        {
            var slot = group * _timedPerFrame + k;
            var tent = (slot & 1) == 1;
            var id = Gi.World.Place(WorldId, tent ? _tents : _boxes,
                margin + _rng.Unit() * span, margin + _rng.Unit() * span, tent ? _tentStamp : _boxStamp, FadeGain);
            ok &= id != default;
            Gi.World.Fade(WorldId, id, 0, Lifetime + 1);
            Gi.World.Expire(WorldId, id, Lifetime + 1);
            _timedIds[slot] = id;
        }

        return ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Advance()
    {
        var low = Margin - 4f;
        var high = Size - low;
        long cell = 0, q8 = 0;
        for (var i = 0; i < _agents; i++)
        {
            var ox = _agentX[i];
            var oy = _agentY[i];
            var vx = _agentVx[i];
            var vy = _agentVy[i];
            var nx = ox + vx;
            var ny = oy + vy;
            if (nx < low || nx > high)
            {
                vx = -vx;
                _agentVx[i] = vx;
                nx = ox + vx;
            }

            if (ny < low || ny > high)
            {
                vy = -vy;
                _agentVy[i] = vy;
                ny = oy + vy;
            }

            _agentX[i] = nx;
            _agentY[i] = ny;
            if ((int)ox != (int)nx || (int)oy != (int)ny) cell++;
            if ((int)(ox * 256f) != (int)(nx * 256f) || (int)(oy * 256f) != (int)(ny * 256f)) q8++;
        }

        CrossCell += cell;
        CrossQ8 += q8;
        MoverSamples += _agents;
    }

    private static int TimedStride(int count)
    {
        if (count <= 1) return 1;
        var stride = 7919;
        while (Gcd(stride, count) != 1) stride++;
        return stride;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
