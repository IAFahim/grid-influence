namespace Gi.Tests;

internal sealed unsafe class StreamRunner
{
    private const ulong AuxSalt = StreamOperands.AuxSalt;
    private const int MaxTiles = 64;
    private const int MaxColumn = 8;
    private readonly int[] _actualTiles = new int[MaxTiles];
    private readonly List<World.SourceId> _attemptIds = [];
    private readonly BugInjection? _bug;
    private readonly short[] _columnActual = new short[MaxColumn];
    private readonly World.SourceId[] _columnExcludes = new World.SourceId[MaxColumn];
    private readonly short[] _columnExpected = new short[MaxColumn];
    private readonly float[] _columnXs = new float[MaxColumn];
    private readonly float[] _columnYs = new float[MaxColumn];
    private readonly List<StreamCommand> _commands;
    private readonly long _convolveCost;
    private readonly int[] _expectedTiles = new int[MaxTiles];
    private readonly (long I, long Q40)? _fold;
    private readonly float[] _gradientActualX = new float[MaxColumn];
    private readonly float[] _gradientActualY = new float[MaxColumn];
    private readonly float[] _gradientExpectedX = new float[MaxColumn];
    private readonly float[] _gradientExpectedY = new float[MaxColumn];
    private readonly Action<byte, int, byte, byte, short[]>? _onPage;

    private readonly StreamSetup _setup;
    private readonly bool _uncached;
    private readonly ulong[] _wordsActual = new ulong[(MaxColumn + 63) >> 6];
    private readonly ulong[] _wordsExpected = new ulong[(MaxColumn + 63) >> 6];
    private short[] _actualPage = [];
    private short[] _actualRect = [];
    private short[] _expectedPage = [];
    private short[] _expectedRect = [];
    private int _frames;
    private int _index;
    private ReferenceWorld _rw = null!;
    private byte[] _stamps = [];
    private byte _world;

    private StreamRunner(
        StreamSetup setup,
        List<StreamCommand> commands,
        BugInjection? bug,
        Action<byte, int, byte, byte, short[]>? onPage,
        (long I, long Q40)? fold,
        bool uncached,
        long convolveCost)
    {
        _setup = setup;
        _commands = commands;
        _bug = bug;
        _onPage = onPage;
        _fold = fold;
        _uncached = uncached;
        _convolveCost = convolveCost;
    }

    public static void Run(
        StreamSetup setup,
        List<StreamCommand> commands,
        BugInjection? bug = null,
        Action<byte, int, byte, byte, short[]>? onPage = null,
        (long I, long Q40)? fold = null,
        bool uncached = false,
        long convolveCost = 0)
    {
        new StreamRunner(setup, commands, bug, onPage, fold, uncached, convolveCost).Execute();
    }

    public static StreamMismatch? TryRun(
        StreamSetup setup,
        List<StreamCommand> commands,
        BugInjection? bug = null,
        Action<byte, int, byte, byte, short[]>? onPage = null,
        (long I, long Q40)? fold = null,
        bool uncached = false,
        long convolveCost = 0)
    {
        try
        {
            Run(setup, commands, bug, onPage, fold, uncached, convolveCost);
            return null;
        }
        catch (StreamMismatch mismatch)
        {
            return mismatch;
        }
    }

    private void Execute()
    {
        _world = World.New();
        if (_fold is { } fold) World.ConfigureFoldLimits(_world, fold.I, fold.Q40);
        if (_uncached) World.DisableKernelInserts(_world);
        _rw = new ReferenceWorld();
        _stamps = new byte[_setup.Stamps.Length];
        var maxCells = 1;
        foreach (var grid in _setup.Grids) maxCells = Math.Max(maxCells, 1 << grid.Power);
        _actualPage = new short[maxCells * maxCells];
        _expectedPage = new short[maxCells * maxCells];
        var rectCells = (maxCells + 8) * (maxCells + 8);
        _actualRect = new short[rectCells];
        _expectedRect = new short[rectCells];

        try
        {
            for (var g = 0; g < _setup.Grids.Length; g++)
            {
                var spec = _setup.Grids[g];
                var grid = Grid.New(_world, spec.Power, spec.X, spec.Y, spec.Size);
                var referenceGrid = _rw.AddGrid(spec.Power, spec.X, spec.Y, spec.Size);
                Require(grid == referenceGrid, $"grid {g} id {grid} vs {referenceGrid}", -1);
            }

            for (var l = 0; l < _setup.Layers.Length; l++)
            {
                var spec = _setup.Layers[l];
                byte layer;
                byte referenceLayer;
                if (!spec.Derived)
                {
                    layer = Layer.New(_world, spec.Transient ? Layer.Residency.Transient : Layer.Residency.Persistent,
                        spec.Precision);
                    if (spec.Transient && _convolveCost != 0) World.ConfigureConvolveCost(_world, layer, _convolveCost);
                    referenceLayer = _rw.AddLayer(spec.Precision, spec.Transient);
                }
                else
                {
                    switch (spec.Op)
                    {
                        case LayerOp.Sum:
                            layer = Layer.Sum(_world, spec.A, spec.WeightA, spec.B, spec.WeightB, spec.Shift);
                            referenceLayer = _rw.AddDerived(
                                LayerOp.Sum, spec.A, spec.B, spec.WeightA, spec.WeightB, spec.Shift, 0, 0);
                            break;
                        case LayerOp.Min:
                            layer = Layer.Min(_world, spec.A, spec.B);
                            referenceLayer = _rw.AddDerived(LayerOp.Min, spec.A, spec.B, 0, 0, 0, 0, 0);
                            break;
                        case LayerOp.Max:
                            layer = Layer.Max(_world, spec.A, spec.B);
                            referenceLayer = _rw.AddDerived(LayerOp.Max, spec.A, spec.B, 0, 0, 0, 0, 0);
                            break;
                        default:
                            layer = Layer.Mask(_world, spec.A, spec.B, spec.Low, spec.High);
                            referenceLayer = _rw.AddDerived(LayerOp.Mask, spec.A, spec.B, 0, 0, 0, spec.Low, spec.High);
                            break;
                    }
                }

                Require(layer == referenceLayer, $"layer {l} id {layer} vs {referenceLayer}", -1);
            }

            for (var s = 0; s < _setup.Stamps.Length; s++)
            {
                var spec = _setup.Stamps[s];
                byte stamp;
                switch (spec.Kind)
                {
                    case StampKind.Box:
                        stamp = Stamp.Box((ushort)spec.Width, (ushort)spec.Height, (sbyte)spec.Value);
                        break;
                    case StampKind.Tent:
                        stamp = Stamp.Tent((ushort)spec.Width, (ushort)spec.Height, (sbyte)spec.Value);
                        break;
                    case StampKind.Bell:
                        stamp = Stamp.Bell((ushort)spec.Width, (ushort)spec.Height, (sbyte)spec.Value);
                        break;
                    case StampKind.Disk:
                        stamp = Stamp.Disk((ushort)spec.Radius, (sbyte)spec.Value, (ushort)spec.Arc);
                        break;
                    case StampKind.Cone:
                        stamp = Stamp.Cone((ushort)spec.Radius, (sbyte)spec.Value, (ushort)spec.Arc);
                        break;
                    case StampKind.Dome:
                        stamp = Stamp.Dome((ushort)spec.Radius, (sbyte)spec.Value, (ushort)spec.Arc);
                        break;
                    default:
                        var rng = new SplitMix64(spec.DataSeed);
                        var data = new sbyte[spec.Width * spec.Height];
                        for (var i = 0; i < data.Length; i++) data[i] = (sbyte)(rng.NextInt(0, 201) - 100);
                        stamp = Stamp.New(data, (ushort)spec.Width, (ushort)spec.Height);
                        break;
                }

                _stamps[s] = stamp;
            }

            for (_index = 0; _index < _commands.Count; _index++)
            {
                var command = _commands[_index];
                switch (command.Op)
                {
                    case StreamOp.Place:
                        var id = World.Place(_world, command.Layer, command.X, command.Y, _stamps[command.Stamp],
                            command.Gain);
                        var referenceId = _rw.Place(command.Layer, command.X, command.Y, _stamps[command.Stamp],
                            command.Gain);
                        Require(id == referenceId, $"place id {id} vs {referenceId}", _index);
                        _attemptIds.Add(id);
                        break;
                    case StreamOp.Move:
                        World.Move(_world, Id(command.Handle), command.X, command.Y);
                        _rw.Move(Id(command.Handle), command.X, command.Y);
                        break;
                    case StreamOp.Turn:
                        World.Turn(_world, Id(command.Handle), command.X);
                        _rw.Turn(Id(command.Handle), command.X);
                        break;
                    case StreamOp.Scale:
                        World.Scale(_world, Id(command.Handle), command.X);
                        _rw.Scale(Id(command.Handle), command.X);
                        break;
                    case StreamOp.SetGain:
                        World.SetGain(_world, Id(command.Handle), command.Gain);
                        _rw.SetGain(Id(command.Handle), command.Gain);
                        break;
                    case StreamOp.Remove:
                        World.Remove(_world, Id(command.Handle));
                        _rw.Remove(Id(command.Handle));
                        break;
                    case StreamOp.Fade:
                        World.Fade(_world, Id(command.Handle), command.Gain, command.Int0);
                        _rw.Fade(Id(command.Handle), command.Gain, command.Int0);
                        break;
                    case StreamOp.Expire:
                        World.Expire(_world, Id(command.Handle), command.Int0);
                        _rw.Expire(Id(command.Handle), command.Int0);
                        break;
                    case StreamOp.Sync:
                        ExecuteSync(command);
                        break;
                    case StreamOp.Record:
                        World.Record(_world);
                        _rw.Record();
                        break;
                    case StreamOp.StopRecording:
                        World.StopRecording(_world);
                        _rw.StopRecording();
                        break;
                    case StreamOp.Rewind:
                        World.Rewind(_world);
                        _rw.Rewind();
                        break;
                    case StreamOp.Clear:
                        World.Clear(_world);
                        _rw.Clear();
                        break;
                    case StreamOp.Splat:
                        var applied = World.Splat(
                            _world, command.Layer, new[] { command.X }, new[] { command.Y }, _stamps[command.Stamp],
                            command.Gain);
                        var expectedSplat = _rw.Splat(command.Layer, command.X, command.Y, _stamps[command.Stamp],
                            command.Gain)
                            ? 1
                            : 0;
                        Require(applied == expectedSplat, $"splat applied {applied} vs {expectedSplat}", _index);
                        break;
                    case StreamOp.Process:
                        World.Process(_world);
                        _rw.Process();
                        _frames++;
                        VerifyState(command);
                        break;
                    case StreamOp.Read:
                        ExecuteRead(command);
                        break;
                    default:
                        Require(false, $"unknown op {command.Op}", _index);
                        break;
                }
            }
        }
        finally
        {
            World.Free(_world);
            foreach (var stamp in _stamps) Stamp.Free(stamp);
        }
    }

    private World.SourceId Id(int handle)
    {
        return (uint)handle < (uint)_attemptIds.Count ? _attemptIds[handle] : default;
    }

    private void ExecuteSync(StreamCommand command)
    {
        var count = Math.Min(command.Int0, MaxColumn);
        var rng = new SplitMix64(command.Aux ^ AuxSalt);
        var target = _setup.Grids[command.Grid];
        var ids = new World.SourceId[Math.Max(1, count)];
        var xs = new float[Math.Max(1, count)];
        var ys = new float[Math.Max(1, count)];
        for (var i = 0; i < count; i++) ids[i] = Id(command.Handle + i);
        StreamOperands.ColumnPositions(ref rng, target, xs, ys, count);

        fixed (World.SourceId* pIds = ids)
        fixed (float* pXs = xs, pYs = ys)
        {
            World.Sync(_world, pIds, pXs, pYs, count);
            _rw.Sync(pIds, pXs, pYs, count);
        }
    }

    private void VerifyState(StreamCommand process)
    {
        for (var g = 0; g < _setup.Grids.Length; g++)
        {
            var size = 1 << _setup.Grids[g].Power;
            for (var l = 0; l < _setup.Layers.Length; l++)
            {
                fixed (short* pActual = _actualPage, pExpected = _expectedPage)
                {
                    World.QueryRegion(_world, (byte)g, (byte)l, 0, 0, size, size, pActual);
                    _rw.QueryRegion((byte)g, (byte)l, 0, 0, size, size, pExpected);
                }

                _onPage?.Invoke(_world, _frames, (byte)g, (byte)l, _expectedPage);
                if (_bug is { } bug && bug.AfterProcess == _frames && bug.Grid == g && bug.Layer == l &&
                    _expectedPage[bug.Cell] != 0)
                    _expectedPage[bug.Cell] += bug.Delta;
                for (var cell = 0; cell < size * size; cell++)
                    Require(
                        _actualPage[cell] == _expectedPage[cell],
                        $"page g{g} l{l} f{_frames} cell {cell}: {_actualPage[cell]} vs {_expectedPage[cell]}",
                        _index);

                Require(
                    World.Query(_world, (byte)g, (byte)l, 0, 0, size, size) ==
                    _rw.Query((byte)g, (byte)l, 0, 0, size, size),
                    $"full sum g{g} l{l} f{_frames}", _index);

                foreach (var since in new[] { -1, _frames - 3, _frames - 2, _frames - 1 })
                {
                    int sinceActual;
                    int sinceExpected;
                    fixed (int* pActualTiles = _actualTiles, pExpectedTiles = _expectedTiles)
                    {
                        sinceActual = World.ChangedTiles(_world, (byte)g, (byte)l, since, pActualTiles);
                        sinceExpected = _rw.ChangedTiles((byte)g, (byte)l, since, pExpectedTiles);
                    }

                    Require(
                        sinceActual == sinceExpected && TilePrefixEqual(sinceActual),
                        $"changed tiles since {since} g{g} l{l} f{_frames}: {sinceActual} vs {sinceExpected}", _index);
                }

                var actualMax = World.QueryMax(_world, (byte)g, (byte)l, out var maxX, out var maxY);
                var expectedMax = _rw.QueryMax((byte)g, (byte)l, out var rMaxX, out var rMaxY);
                Require(
                    actualMax == expectedMax && (maxX, maxY) == (rMaxX, rMaxY),
                    $"max g{g} l{l} f{_frames}: {actualMax}@({maxX},{maxY}) vs {expectedMax}@({rMaxX},{rMaxY})",
                    _index);

                var rng = new SplitMix64(process.Aux ^ AuxSalt ^ ((ulong)_frames * 0x9E3779B97F4A7C15ul));
                for (var q = 0; q < 2; q++)
                {
                    var x = rng.NextInt(-4, size);
                    var y = rng.NextInt(-4, size);
                    var w = rng.NextInt(1, size + 2);
                    var h = rng.NextInt(1, size + 2);
                    Require(
                        World.Query(_world, (byte)g, (byte)l, x, y, w, h) == _rw.Query((byte)g, (byte)l, x, y, w, h),
                        $"rect sum g{g} l{l} f{_frames} ({x},{y},{w},{h})", _index);

                    var actualRectMax = World.QueryMax(_world, (byte)g, (byte)l, x, y, w, h, out var rX, out var rY);
                    var expectedRectMax = _rw.QueryMax((byte)g, (byte)l, x, y, w, h, out var erX, out var erY);
                    Require(
                        actualRectMax == expectedRectMax && (rX, rY) == (erX, erY),
                        $"rect max g{g} l{l} f{_frames} ({x},{y},{w},{h}): {actualRectMax} vs {expectedRectMax}",
                        _index);

                    fixed (short* pActual = _actualRect, pExpected = _expectedRect)
                    {
                        World.QueryRegion(_world, (byte)g, (byte)l, x, y, w, h, pActual);
                        _rw.QueryRegion((byte)g, (byte)l, x, y, w, h, pExpected);
                    }

                    for (var cell = 0; cell < w * h; cell++)
                        Require(
                            _actualRect[cell] == _expectedRect[cell],
                            $"region g{g} l{l} f{_frames} ({x},{y},{w},{h}) cell {cell}: {_actualRect[cell]} vs {_expectedRect[cell]}",
                            _index);
                }

                foreach (var since in new[] { _frames - 2, _frames - 1 })
                {
                    int actualCount;
                    int expectedCount;
                    fixed (int* pActual = _actualTiles, pExpected = _expectedTiles)
                    {
                        actualCount = World.ChangedTiles(_world, (byte)g, (byte)l, since, pActual);
                        expectedCount = _rw.ChangedTiles((byte)g, (byte)l, since, pExpected);
                    }

                    Require(
                        actualCount == expectedCount && TilePrefixEqual(actualCount),
                        $"changed tiles since {since} g{g} l{l} f{_frames}: {actualCount} vs {expectedCount}" +
                        (actualCount > expectedCount ? $" first={_actualTiles[0]}" : string.Empty),
                        _index);
                }

                int frameCount;
                int frameExpected;
                fixed (int* pActual = _actualTiles, pExpected = _expectedTiles)
                {
                    frameCount = World.ChangedTiles(_world, (byte)g, (byte)l, pActual);
                    frameExpected = _rw.ChangedTiles((byte)g, (byte)l, pExpected);
                }

                Array.Sort(_actualTiles, 0, frameCount);
                Array.Sort(_expectedTiles, 0, frameExpected);
                Require(
                    frameCount == frameExpected && TilePrefixEqual(frameCount),
                    $"changed tiles g{g} l{l} f{_frames}: {frameCount} vs {frameExpected}", _index);
            }
        }

        for (var l = 0; l < _setup.Layers.Length; l++)
        {
            var expected = _rw.LiveCount((byte)l);
            var actual = LiveCount(l);
            Require(actual == expected, $"layer live {l} f{_frames}: {actual} vs {expected}", _index);
        }
    }

    private bool TilePrefixEqual(int count)
    {
        for (var i = 0; i < count; i++)
            if (_actualTiles[i] != _expectedTiles[i])
                return false;
        return true;
    }

    private void ExecuteRead(StreamCommand command)
    {
        var size = 1 << _setup.Grids[command.Grid].Power;
        var rng = new SplitMix64(command.Aux ^ AuxSalt);
        StreamOperands.Rect(ref rng, size, command.Int0, out var rectX, out var rectY, out var rectW, out var rectH);
        var reach = command.Int0 < 0 ? float.NaN : command.Int0;
        var since = rng.NextInt(-2, _frames + 2);
        var exclude = Id(command.Handle);

        switch (command.Read)
        {
            case ReadKind.QueryRect:
                Require(
                    World.Query(_world, command.Grid, command.Layer, rectX, rectY, rectW, rectH) ==
                    _rw.Query(command.Grid, command.Layer, rectX, rectY, rectW, rectH),
                    $"query rect ({rectX},{rectY},{rectW},{rectH})", _index);
                break;
            case ReadKind.QueryRegion:
            {
                fixed (short* pActual = _actualRect, pExpected = _expectedRect)
                {
                    World.QueryRegion(_world, command.Grid, command.Layer, rectX, rectY, rectW, rectH, pActual);
                    _rw.QueryRegion(command.Grid, command.Layer, rectX, rectY, rectW, rectH, pExpected);
                }

                for (var cell = 0; cell < rectW * rectH; cell++)
                    Require(
                        _actualRect[cell] == _expectedRect[cell],
                        $"read region ({rectX},{rectY},{rectW},{rectH}) cell {cell}: {_actualRect[cell]} vs {_expectedRect[cell]}",
                        _index);
                break;
            }
            case ReadKind.QueryAt:
                Require(
                    World.QueryAt(_world, command.Grid, command.Layer, command.X, command.Y) ==
                    _rw.QueryAt(command.Grid, command.Layer, command.X, command.Y),
                    $"query at ({command.X},{command.Y})", _index);
                break;
            case ReadKind.QueryMaxWhole:
            {
                var actual = World.QueryMax(_world, command.Grid, command.Layer, out var x, out var y);
                var expected = _rw.QueryMax(command.Grid, command.Layer, out var rX, out var rY);
                Require(
                    actual == expected && (x, y) == (rX, rY),
                    $"query max whole: {actual}@({x},{y}) vs {expected}@({rX},{rY})", _index);
                break;
            }
            case ReadKind.QueryMaxRect:
            {
                var actual = World.QueryMax(_world, command.Grid, command.Layer, rectX, rectY, rectW, rectH, out var x,
                    out var y);
                var expected = _rw.QueryMax(command.Grid, command.Layer, rectX, rectY, rectW, rectH, out var rX,
                    out var rY);
                Require(
                    actual == expected && (x, y) == (rX, rY),
                    $"query max rect ({rectX},{rectY},{rectW},{rectH}): {actual} vs {expected}", _index);
                break;
            }
            case ReadKind.QueryGradient:
            {
                World.QueryGradient(_world, command.Grid, command.Layer, command.X, command.Y, out var gx, out var gy);
                _rw.QueryGradient(command.Grid, command.Layer, command.X, command.Y, out var rgx, out var rgy);
                Require((gx, gy) == (rgx, rgy), $"query gradient ({command.X},{command.Y}): {(gx, gy)} vs {(rgx, rgy)}",
                    _index);
                break;
            }
            case ReadKind.Sense:
            {
                var actual = World.TrySense(_world, command.Layer, command.X, command.Y, out var value);
                var expected = _rw.TrySense(command.Layer, command.X, command.Y, out var rValue);
                Require(actual == expected && value == rValue, $"sense ({command.X},{command.Y}): {value} vs {rValue}",
                    _index);
                break;
            }
            case ReadKind.SenseEx:
            {
                var actual = World.TrySense(_world, command.Layer, command.X, command.Y, exclude, out var value);
                var expected = _rw.TrySense(command.Layer, command.X, command.Y, exclude, out var rValue);
                Require(
                    actual == expected && value == rValue,
                    $"sense ex {exclude} ({command.X},{command.Y}): {value} vs {rValue}", _index);
                break;
            }
            case ReadKind.SenseArea:
            {
                var actual = World.TrySenseArea(_world, command.Layer, command.X, command.Y, reach, out var total);
                var expected = _rw.TrySenseArea(command.Layer, command.X, command.Y, reach, out var rTotal);
                Require(
                    actual == expected && total == rTotal,
                    $"sense area ({command.X},{command.Y},r{reach}): {total} vs {rTotal}", _index);
                break;
            }
            case ReadKind.SenseAreaEx:
            {
                var actual = World.TrySenseArea(_world, command.Layer, command.X, command.Y, reach, exclude,
                    out var total);
                var expected = _rw.TrySenseArea(command.Layer, command.X, command.Y, reach, exclude, out var rTotal);
                Require(
                    actual == expected && total == rTotal,
                    $"sense area ex {exclude} ({command.X},{command.Y},r{reach}): {total} vs {rTotal}", _index);
                break;
            }
            case ReadKind.SenseMax:
            {
                var actual = World.TrySenseMax(_world, command.Layer, command.X, command.Y, reach, out var value,
                    out var x, out var y);
                var expected = _rw.TrySenseMax(command.Layer, command.X, command.Y, reach, out var rValue, out var rX,
                    out var rY);
                Require(
                    actual == expected && value == rValue && (x, y) == (rX, rY),
                    $"sense max ({command.X},{command.Y},r{reach}): {value}@({x},{y}) vs {rValue}@({rX},{rY})", _index);
                break;
            }
            case ReadKind.SenseNearest:
            {
                var actual = World.TrySenseNearest(
                    _world, command.Layer, command.X, command.Y, reach, command.Gain, out var value, out var x,
                    out var y);
                var expected = _rw.TrySenseNearest(
                    command.Layer, command.X, command.Y, reach, command.Gain, out var rValue, out var rX, out var rY);
                Require(
                    actual == expected && value == rValue && (x, y) == (rX, rY),
                    $"sense nearest ({command.X},{command.Y},r{reach},t{command.Gain})", _index);
                break;
            }
            case ReadKind.SenseGradient:
            {
                var actual =
                    World.TrySenseGradient(_world, command.Layer, command.X, command.Y, out var gx, out var gy);
                var expected = _rw.TrySenseGradient(command.Layer, command.X, command.Y, out var rgx, out var rgy);
                Require(
                    actual == expected && (gx, gy) == (rgx, rgy),
                    $"sense gradient ({command.X},{command.Y}): {(gx, gy)} vs {(rgx, rgy)}", _index);
                break;
            }
            case ReadKind.SenseGradientEx:
            {
                var actual = World.TrySenseGradient(_world, command.Layer, command.X, command.Y, exclude, out var gx,
                    out var gy);
                var expected = _rw.TrySenseGradient(command.Layer, command.X, command.Y, exclude, out var rgx,
                    out var rgy);
                Require(
                    actual == expected && (gx, gy) == (rgx, rgy),
                    $"sense gradient ex {exclude} ({command.X},{command.Y})", _index);
                break;
            }
            case ReadKind.ChangedDisk:
                Require(
                    World.Changed(_world, command.Layer, command.X, command.Y, reach, since) ==
                    _rw.Changed(command.Layer, command.X, command.Y, reach, since),
                    $"changed disk ({command.X},{command.Y},r{reach},s{since})", _index);
                break;
            case ReadKind.ChangedTilesSince:
            {
                int actualCount;
                int expectedCount;
                fixed (int* pActual = _actualTiles, pExpected = _expectedTiles)
                {
                    actualCount = World.ChangedTiles(_world, command.Grid, command.Layer, command.Int0, pActual);
                    expectedCount = _rw.ChangedTiles(command.Grid, command.Layer, command.Int0, pExpected);
                }

                Require(
                    actualCount == expectedCount && TilePrefixEqual(actualCount),
                    $"changed tiles since {command.Int0} g{command.Grid} l{command.Layer}: {actualCount} vs {expectedCount}",
                    _index);
                break;
            }
            case ReadKind.ChangedTilesAll:
            {
                int actualCount;
                int expectedCount;
                fixed (int* pActual = _actualTiles, pExpected = _expectedTiles)
                {
                    actualCount = World.ChangedTiles(_world, command.Grid, command.Layer, pActual);
                    expectedCount = _rw.ChangedTiles(command.Grid, command.Layer, pExpected);
                }

                Array.Sort(_actualTiles, 0, actualCount);
                Array.Sort(_expectedTiles, 0, expectedCount);
                Require(
                    actualCount == expectedCount && TilePrefixEqual(actualCount),
                    $"changed tiles all g{command.Grid} l{command.Layer}: {actualCount} vs {expectedCount}", _index);
                break;
            }
            case ReadKind.ChangedMask:
                ExecuteChangedMask(command, reach, since);
                break;
            case ReadKind.SenseColumn:
                ExecuteSenseColumn(command, false, default);
                break;
            case ReadKind.SenseColumnEx:
                ExecuteSenseColumn(command, true, exclude);
                break;
            case ReadKind.SenseGradientColumn:
                ExecuteSenseGradientColumn(command);
                break;
            case ReadKind.Covers:
                Require(
                    World.Covers(_world, command.X, command.Y) == _rw.Covers(command.X, command.Y),
                    $"covers ({command.X},{command.Y})", _index);
                break;
            case ReadKind.LayerLive:
            {
                var actual = LiveCount(command.Layer);
                var expected = _rw.LiveCount(command.Layer);
                Require(actual == expected, $"layer live {command.Layer}: {actual} vs {expected}", _index);
                break;
            }
            case ReadKind.SourceSpec:
            {
                var actual = World.TryGetSourceSpec(_world, exclude, out var spec);
                var expected = _rw.TryGetSource(exclude, out var referenceSource);
                Require(actual == expected, $"source spec {exclude}: {actual} vs {expected}", _index);
                if (!actual) break;
                Require(
                    spec.X.Equals(referenceSource.X) && spec.Y.Equals(referenceSource.Y) &&
                    spec.Layer == referenceSource.Layer && spec.Stamp == referenceSource.Stamp &&
                    spec.Gain == referenceSource.Gain,
                    $"source spec {exclude} fields", _index);
                var heading = referenceSource.Angle * ((float)Math.PI * 2 / 65536);
                var scale = referenceSource.Scale / 256f;
                Require(
                    spec.HeadingRadians.Equals(heading) && spec.Scale.Equals(scale),
                    $"source spec {exclude} heading/scale: {spec.HeadingRadians}/{spec.Scale} vs {heading}/{scale}",
                    _index);
                break;
            }
            default:
                Require(false, $"unknown read {command.Read}", _index);
                break;
        }
    }

    private void FillColumnPositions(ref SplitMix64 rng, byte grid, int count)
    {
        StreamOperands.ColumnPositions(ref rng, _setup.Grids[grid], _columnXs, _columnYs, count);
    }

    private void ExecuteChangedMask(StreamCommand command, float reach, int since)
    {
        var rng = new SplitMix64(command.Aux ^ AuxSalt ^ 0x51ED270Bul);
        var count = 1 + rng.NextInt(0, MaxColumn);
        FillColumnPositions(ref rng, command.Grid, count);
        int actual;
        int expected;
        fixed (float* pXs = _columnXs, pYs = _columnYs)
        fixed (ulong* pActual = _wordsActual, pExpected = _wordsExpected)
        {
            Array.Clear(_wordsActual);
            Array.Clear(_wordsExpected);
            actual = World.ChangedMask(_world, command.Layer, pXs, pYs, reach, since, count, pActual);
            expected = _rw.ChangedMask(command.Layer, pXs, pYs, reach, since, count, pExpected);
        }

        Require(
            actual == expected && _wordsActual.AsSpan().SequenceEqual(_wordsExpected),
            $"changed mask count {actual} vs {expected}", _index);
    }

    private void ExecuteSenseColumn(StreamCommand command, bool withExcludes, World.SourceId exclude)
    {
        var rng = new SplitMix64(command.Aux ^ AuxSalt ^ 0x2545F4914F6CDD1Dul);
        var count = 1 + rng.NextInt(0, MaxColumn);
        FillColumnPositions(ref rng, command.Grid, count);
        if (withExcludes)
            for (var i = 0; i < count; i++)
                _columnExcludes[i] = i == 0 ? exclude : default;
        bool actual;
        bool expected;
        fixed (float* pXs = _columnXs, pYs = _columnYs)
        fixed (World.SourceId* pExcludes = _columnExcludes)
        fixed (short* pActual = _columnActual, pExpected = _columnExpected)
        fixed (ulong* pActualWords = _wordsActual, pExpectedWords = _wordsExpected)
        {
            Array.Clear(_wordsActual);
            Array.Clear(_wordsExpected);
            actual = withExcludes
                ? World.SenseColumn(_world, command.Layer, pXs, pYs, pExcludes, count, pActual, pActualWords) ==
                  World.ReadStatus.Complete
                : World.SenseColumn(_world, command.Layer, pXs, pYs, count, pActual, pActualWords) ==
                  World.ReadStatus.Complete;
            expected = withExcludes
                ? _rw.SenseColumn(command.Layer, pXs, pYs, pExcludes, count, pExpected, pExpectedWords)
                : _rw.SenseColumn(command.Layer, pXs, pYs, count, pExpected, pExpectedWords);
        }

        Require(
            actual == expected &&
            _columnActual.AsSpan(0, count).SequenceEqual(_columnExpected.AsSpan(0, count)) &&
            _wordsActual.AsSpan().SequenceEqual(_wordsExpected),
            $"sense column {actual} vs {expected}", _index);
    }

    private void ExecuteSenseGradientColumn(StreamCommand command)
    {
        var rng = new SplitMix64(command.Aux ^ AuxSalt ^ 0x8B72F1E0A3D49B15ul);
        var count = 1 + rng.NextInt(0, MaxColumn);
        FillColumnPositions(ref rng, command.Grid, count);
        bool actual;
        fixed (float* pXs = _columnXs, pYs = _columnYs)
        fixed (float* pActualX = _gradientActualX, pActualY = _gradientActualY)
        fixed (ulong* pActualWords = _wordsActual)
        {
            Array.Clear(_wordsActual);
            actual = World.SenseGradientColumn(
                         _world, command.Layer, pXs, pYs, null, count, pActualX, pActualY, pActualWords) ==
                     World.ReadStatus.Complete;
        }

        var expected = true;
        Array.Clear(_wordsExpected);
        for (var i = 0; i < count; i++)
        {
            var filled = _rw.TrySenseGradient(command.Layer, _columnXs[i], _columnYs[i], out var gx, out var gy);
            _gradientExpectedX[i] = gx;
            _gradientExpectedY[i] = gy;
            expected &= filled;
            if (filled) _wordsExpected[i >> 6] |= 1ul << (i & 63);
        }

        Require(
            actual == expected &&
            _gradientActualX.AsSpan(0, count).SequenceEqual(_gradientExpectedX.AsSpan(0, count)) &&
            _gradientActualY.AsSpan(0, count).SequenceEqual(_gradientExpectedY.AsSpan(0, count)) &&
            _wordsActual.AsSpan().SequenceEqual(_wordsExpected),
            $"sense gradient column {actual} vs {expected}", _index);
    }

    private void Require(bool condition, string detail, int index)
    {
        if (condition) return;
        var op = (uint)index < (uint)_commands.Count ? _commands[index].Op : 0;
        throw new StreamMismatch(
            $"seed {_setup.Seed} command {index} ({op}): {detail}",
            index,
            (int)op);
    }

    private int LiveCount(int layer)
    {
        var s = &World.GetContext(_world)->Sources;
        var count = 0;
        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] != 0 && s->Gain[i] != 0 && s->Layer[i] == layer)
                count++;
        return count;
    }
}

internal struct BugInjection
{
    public int AfterProcess;
    public byte Grid;
    public byte Layer;
    public int Cell;
    public short Delta;
}

internal sealed class StreamMismatch : Exception
{
    public int CommandIndex;
    public int OpIndex;

    public StreamMismatch(string detail, int commandIndex, int opIndex) : base(detail)
    {
        CommandIndex = commandIndex;
        OpIndex = opIndex;
    }
}
