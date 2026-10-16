using System.Globalization;
using System.Text;

namespace Gi.Tests;

internal sealed unsafe class CorpusRunner
{
    private const int MaxTiles = 64;
    private const int MaxColumn = 8;

    private readonly StreamSetup _setup;
    private readonly TextWriter _output;
    private readonly List<int> _attemptIds = [];
    private readonly int[] _tiles = new int[MaxTiles];
    private readonly float[] _columnXs = new float[MaxColumn];
    private readonly float[] _columnYs = new float[MaxColumn];
    private readonly int[] _columnExcludes = new int[MaxColumn];
    private readonly short[] _columnValues = new short[MaxColumn];
    private readonly float[] _gradientXs = new float[MaxColumn];
    private readonly float[] _gradientYs = new float[MaxColumn];
    private readonly ulong[] _words = new ulong[(MaxColumn + 63) >> 6];
    private byte _world;
    private byte[] _stamps = [];
    private short[] _page = [];
    private short[] _rect = [];
    private CorpusHash _frameReads;
    private int _frames;

    private CorpusRunner(StreamSetup setup, TextWriter output)
    {
        _setup = setup;
        _output = output;
        _frameReads = new CorpusHash(CorpusHash.Offset);
    }

    public static void Run(StreamSetup setup, string mode, TextWriter output)
    {
        var previous = ResolvePool.SerialOnly;
        ResolvePool.SerialOnly = true;
        try
        {
            new CorpusRunner(setup, output).Execute(mode);
        }
        finally
        {
            ResolvePool.SerialOnly = previous;
        }
    }

    private void Execute(string mode)
    {
        _world = World.New();
        _stamps = new byte[_setup.Stamps.Length];
        var maxCells = 1;
        foreach (var grid in _setup.Grids) maxCells = Math.Max(maxCells, 1 << grid.Power);
        _page = new short[maxCells * maxCells];
        _rect = new short[(maxCells + 8) * (maxCells + 8)];

        try
        {
            for (var g = 0; g < _setup.Grids.Length; g++)
            {
                var spec = _setup.Grids[g];
                Grid.New(_world, spec.Power, spec.X, spec.Y, spec.Size);
            }

            for (var l = 0; l < _setup.Layers.Length; l++)
            {
                var spec = _setup.Layers[l];
                if (!spec.Derived)
                {
                    Layer.New(_world, spec.Precision);
                    continue;
                }

                switch (spec.Op)
                {
                    case LayerOp.Sum:
                        Layer.Sum(_world, spec.A, spec.WeightA, spec.B, spec.WeightB, spec.Shift);
                        break;
                    case LayerOp.Min:
                        Layer.Min(_world, spec.A, spec.B);
                        break;
                    case LayerOp.Max:
                        Layer.Max(_world, spec.A, spec.B);
                        break;
                    default:
                        Layer.Mask(_world, spec.A, spec.B, spec.Low, spec.High);
                        break;
                }
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

            _output.Write("stream ");
            _output.Write(_setup.Seed.ToString(CultureInfo.InvariantCulture));
            _output.Write(' ');
            _output.WriteLine(mode);

            foreach (var command in _setup.Commands)
            {
                switch (command.Op)
                {
                    case StreamOp.Place:
                        _attemptIds.Add(World.Place(
                            _world, command.Layer, command.X, command.Y, _stamps[command.Stamp], command.Gain));
                        break;
                    case StreamOp.Move:
                        World.Move(_world, Id(command.Handle), command.X, command.Y);
                        break;
                    case StreamOp.Turn:
                        World.Turn(_world, Id(command.Handle), command.X);
                        break;
                    case StreamOp.Scale:
                        World.Scale(_world, Id(command.Handle), command.X);
                        break;
                    case StreamOp.SetGain:
                        World.SetGain(_world, Id(command.Handle), command.Gain);
                        break;
                    case StreamOp.Remove:
                        World.Remove(_world, Id(command.Handle));
                        break;
                    case StreamOp.Fade:
                        World.Fade(_world, Id(command.Handle), command.Gain, command.Int0);
                        break;
                    case StreamOp.Expire:
                        World.Expire(_world, Id(command.Handle), command.Int0);
                        break;
                    case StreamOp.Sync:
                        ExecuteSync(command);
                        break;
                    case StreamOp.Record:
                        World.Record(_world);
                        break;
                    case StreamOp.StopRecording:
                        World.StopRecording(_world);
                        break;
                    case StreamOp.Rewind:
                        World.Rewind(_world);
                        break;
                    case StreamOp.Clear:
                        World.Clear(_world);
                        break;
                    case StreamOp.Process:
                        World.Process(_world);
                        _frames++;
                        WriteFrameLine();
                        break;
                    case StreamOp.Read:
                        ExecuteRead(command);
                        break;
                }
            }

            _output.Write("post reads ");
            _output.WriteLine(_frameReads.Value.ToString("x16", CultureInfo.InvariantCulture));
        }
        finally
        {
            World.Free(_world);
            foreach (var stamp in _stamps) Stamp.Free(stamp);
        }
    }

    private int Id(int handle)
        => (uint)handle < (uint)_attemptIds.Count ? _attemptIds[handle] : -1;

    private void ExecuteSync(StreamCommand command)
    {
        var count = Math.Min(command.Int0, MaxColumn);
        var rng = new SplitMix64(command.Aux ^ StreamOperands.AuxSalt);
        var target = _setup.Grids[command.Grid];
        var ids = new int[Math.Max(1, count)];
        var xs = new float[Math.Max(1, count)];
        var ys = new float[Math.Max(1, count)];
        for (var i = 0; i < count; i++) ids[i] = Id(command.Handle + i);
        StreamOperands.ColumnPositions(ref rng, target, xs, ys, count);

        fixed (int* pIds = ids)
        fixed (float* pXs = xs, pYs = ys)
        {
            World.Sync(_world, pIds, pXs, pYs, count);
        }
    }

    private void WriteFrameLine()
    {
        _output.Write("frame ");
        _output.Write(_frames.ToString(CultureInfo.InvariantCulture));
        _output.Write(" pages");
        for (var g = 0; g < _setup.Grids.Length; g++)
        {
            var size = 1 << _setup.Grids[g].Power;
            for (var l = 0; l < _setup.Layers.Length; l++)
            {
                fixed (short* pPage = _page)
                {
                    World.QueryRegion(_world, (byte)g, (byte)l, 0, 0, size, size, pPage);
                }

                var hash = new CorpusHash(CorpusHash.Offset);
                hash.Mix(_page, size * size);
                _output.Write(' ');
                _output.Write(hash.Value.ToString("x16", CultureInfo.InvariantCulture));
            }
        }

        _output.Write(" reads ");
        _output.WriteLine(_frameReads.Value.ToString("x16", CultureInfo.InvariantCulture));
        _frameReads = new CorpusHash(CorpusHash.Offset);
    }

    private void ExecuteRead(StreamCommand command)
    {
        var size = 1 << _setup.Grids[command.Grid].Power;
        var rng = new SplitMix64(command.Aux ^ StreamOperands.AuxSalt);
        StreamOperands.Rect(ref rng, size, command.Int0, out var rectX, out var rectY, out var rectW, out var rectH);
        var reach = command.Int0 < 0 ? float.NaN : command.Int0;
        var since = rng.NextInt(-2, _frames + 2);
        var exclude = Id(command.Handle);
        var hash = _frameReads;
        hash.Mix((byte)command.Read);

        switch (command.Read)
        {
            case ReadKind.QueryRect:
                hash.Mix(World.Query(_world, command.Grid, command.Layer, rectX, rectY, rectW, rectH));
                break;
            case ReadKind.QueryRegion:
                fixed (short* pRect = _rect)
                {
                    World.QueryRegion(_world, command.Grid, command.Layer, rectX, rectY, rectW, rectH, pRect);
                }

                hash.Mix(_rect, rectW * rectH);
                break;
            case ReadKind.QueryAt:
                hash.Mix(World.QueryAt(_world, command.Grid, command.Layer, command.X, command.Y));
                break;
            case ReadKind.QueryMaxWhole:
                hash.Mix(World.QueryMax(_world, command.Grid, command.Layer, out var x, out var y));
                hash.Mix(x);
                hash.Mix(y);
                break;
            case ReadKind.QueryMaxRect:
                hash.Mix(World.QueryMax(
                    _world, command.Grid, command.Layer, rectX, rectY, rectW, rectH, out var rx, out var ry));
                hash.Mix(rx);
                hash.Mix(ry);
                break;
            case ReadKind.QueryGradient:
                World.QueryGradient(_world, command.Grid, command.Layer, command.X, command.Y, out var gx, out var gy);
                hash.Mix(gx);
                hash.Mix(gy);
                break;
            case ReadKind.Sense:
                hash.Mix(World.TrySense(_world, command.Layer, command.X, command.Y, out var value));
                hash.Mix(value);
                break;
            case ReadKind.SenseEx:
                hash.Mix(World.TrySense(_world, command.Layer, command.X, command.Y, exclude, out var exValue));
                hash.Mix(exValue);
                break;
            case ReadKind.SenseArea:
                hash.Mix(World.TrySenseArea(_world, command.Layer, command.X, command.Y, reach, out var total));
                hash.Mix(total);
                break;
            case ReadKind.SenseAreaEx:
                hash.Mix(World.TrySenseArea(
                    _world, command.Layer, command.X, command.Y, reach, exclude, out var exTotal));
                hash.Mix(exTotal);
                break;
            case ReadKind.SenseMax:
                hash.Mix(World.TrySenseMax(
                    _world, command.Layer, command.X, command.Y, reach, out var maxValue, out var maxX, out var maxY));
                hash.Mix(maxValue);
                hash.Mix(maxX);
                hash.Mix(maxY);
                break;
            case ReadKind.SenseNearest:
                hash.Mix(World.TrySenseNearest(
                    _world, command.Layer, command.X, command.Y, reach, command.Gain, out var nearValue,
                    out var nearX, out var nearY));
                hash.Mix(nearValue);
                hash.Mix(nearX);
                hash.Mix(nearY);
                break;
            case ReadKind.SenseGradient:
                hash.Mix(World.TrySenseGradient(_world, command.Layer, command.X, command.Y, out var gradientX, out var gradientY));
                hash.Mix(gradientX);
                hash.Mix(gradientY);
                break;
            case ReadKind.SenseGradientEx:
                hash.Mix(World.TrySenseGradient(
                    _world, command.Layer, command.X, command.Y, exclude, out var exGradientX, out var exGradientY));
                hash.Mix(exGradientX);
                hash.Mix(exGradientY);
                break;
            case ReadKind.ChangedDisk:
                hash.Mix(World.Changed(_world, command.Layer, command.X, command.Y, reach, since));
                hash.Mix(since);
                break;
            case ReadKind.ChangedTilesSince:
            {
                int count;
                fixed (int* pTiles = _tiles)
                {
                    count = World.ChangedTiles(_world, command.Grid, command.Layer, command.Int0, pTiles);
                }

                hash.Mix(_tiles, count);
                break;
            }
            case ReadKind.ChangedTilesAll:
            {
                int count;
                fixed (int* pTiles = _tiles)
                {
                    count = World.ChangedTiles(_world, command.Grid, command.Layer, pTiles);
                }

                hash.Mix(_tiles, count);
                break;
            }
            case ReadKind.ChangedMask:
            {
                rng = new SplitMix64(command.Aux ^ StreamOperands.AuxSalt ^ 0x51ED270Bul);
                var count = 1 + rng.NextInt(0, MaxColumn);
                StreamOperands.ColumnPositions(ref rng, _setup.Grids[command.Grid], _columnXs, _columnYs, count);
                int hits;
                Array.Clear(_words);
                fixed (float* pXs = _columnXs, pYs = _columnYs)
                fixed (ulong* pWords = _words)
                {
                    hits = World.ChangedMask(_world, command.Layer, pXs, pYs, reach, since, count, pWords);
                }

                hash.Mix(hits);
                hash.Mix(_words, (count + 63) >> 6);
                break;
            }
            case ReadKind.SenseColumn:
            case ReadKind.SenseColumnEx:
            {
                rng = new SplitMix64(command.Aux ^ StreamOperands.AuxSalt ^ 0x2545F4914F6CDD1Dul);
                var count = 1 + rng.NextInt(0, MaxColumn);
                StreamOperands.ColumnPositions(ref rng, _setup.Grids[command.Grid], _columnXs, _columnYs, count);
                if (command.Read == ReadKind.SenseColumnEx)
                    for (var i = 0; i < count; i++)
                        _columnExcludes[i] = i == 0 ? exclude : -1;
                bool complete;
                Array.Clear(_words);
                fixed (float* pXs = _columnXs, pYs = _columnYs)
                fixed (int* pExcludes = _columnExcludes)
                fixed (short* pValues = _columnValues)
                fixed (ulong* pWords = _words)
                {
                    complete = command.Read == ReadKind.SenseColumnEx
                        ? World.SenseColumn(_world, command.Layer, pXs, pYs, pExcludes, count, pValues, pWords)
                        : World.SenseColumn(_world, command.Layer, pXs, pYs, count, pValues, pWords);
                }

                hash.Mix(complete);
                hash.Mix(_columnValues, count);
                hash.Mix(_words, (count + 63) >> 6);
                break;
            }
            case ReadKind.SenseGradientColumn:
            {
                rng = new SplitMix64(command.Aux ^ StreamOperands.AuxSalt ^ 0x8B72F1E0A3D49B15ul);
                var count = 1 + rng.NextInt(0, MaxColumn);
                StreamOperands.ColumnPositions(ref rng, _setup.Grids[command.Grid], _columnXs, _columnYs, count);
                bool complete;
                Array.Clear(_words);
                fixed (float* pXs = _columnXs, pYs = _columnYs)
                fixed (float* pGx = _gradientXs, pGy = _gradientYs)
                fixed (ulong* pWords = _words)
                {
                    complete = World.SenseGradientColumn(
                        _world, command.Layer, pXs, pYs, null, count, pGx, pGy, pWords);
                }

                hash.Mix(complete);
                hash.Mix(_gradientXs, count);
                hash.Mix(_gradientYs, count);
                hash.Mix(_words, (count + 63) >> 6);
                break;
            }
            case ReadKind.Covers:
                hash.Mix(World.Covers(_world, command.X, command.Y));
                break;
            case ReadKind.LayerLive:
                hash.Mix(World.GetContext(_world)->LayerLive[command.Layer]);
                break;
            case ReadKind.SourceSpec:
            {
                var found = World.TryGetSourceSpec(_world, exclude, out var spec);
                hash.Mix(found);
                if (found)
                {
                    hash.Mix(spec.X);
                    hash.Mix(spec.Y);
                    hash.Mix(spec.HeadingRadians);
                    hash.Mix(spec.Scale);
                    hash.Mix(spec.Gain);
                    hash.Mix(spec.Layer);
                    hash.Mix(Array.IndexOf(_stamps, spec.Stamp));
                }

                break;
            }
        }

        _frameReads = hash;
    }
}
