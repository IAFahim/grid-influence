using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public enum GiReplayStatus : byte
    {
        Ready,
        Matching,
        KeyframeDiffers,
        Diverged,
    }

    public readonly struct GiTileDiff
    {
        public readonly int Grid;
        public readonly int Layer;
        public readonly int Tile;

        public GiTileDiff(int grid, int layer, int tile)
        {
            Grid = grid;
            Layer = layer;
            Tile = tile;
        }
    }

    public sealed unsafe class GiReplay : IDisposable
    {
        private readonly GiStampRegistry _registry;
        private readonly Dictionary<GiWorld.SourceId, GiWorld.SourceId> _map = new Dictionary<GiWorld.SourceId, GiWorld.SourceId>();
        private readonly GiRestoreDriver _driver = new GiRestoreDriver();
        private readonly GiTileHashes _hashes;
        private readonly byte _world;
        private readonly GiTables _tables;
        private readonly int _layerCount;
        private readonly ulong[] _layerHashes;
        private GiWorld.Token _token;
        private GiCommand[] _commands = Array.Empty<GiCommand>();
        private GiCommandResult[] _results = Array.Empty<GiCommandResult>();
        private GiWorld.SourceCommand[] _scratch = Array.Empty<GiWorld.SourceCommand>();
        private GiWorld.SourceResult[] _batchResults = Array.Empty<GiWorld.SourceResult>();
        private bool _disposed;

        private GiReplay(byte world, in GiTables tables, GiStampRegistry registry)
        {
            _world = world;
            _tables = tables;
            _registry = registry;
            var grids = new byte[tables.GridCount];
            for (var g = 0; g < grids.Length; g++) tables.TryGrid(g, out grids[g]);
            var layers = new byte[tables.LayerCount];
            for (var l = 0; l < layers.Length; l++) tables.TryLayer(new GiLayerKey((byte)l), out layers[l]);
            _layerCount = layers.Length;
            _layerHashes = new ulong[layers.Length];
            _hashes = new GiTileHashes(world, grids, layers);
            _token = GiWorld.Capture(world);
        }

        public byte World => _world;

        public GiTables Tables => _tables;

        public GiTileHashes Hashes => _hashes;

        public GiReplayStatus Status { get; private set; }

        public int Tick => GiWorld.Tick(_world);

        public int CommandMismatches { get; private set; }

        public static bool TryCreate(GiProfileSpec profile, out GiReplay replay, out GiBuildStatus status)
        {
            replay = null;
            var registry = new GiStampRegistry();
            status = GiWorldBuilder.TryBuild(profile.Grids, profile.Layers, profile.Stamps, profile.Cells, registry, out var world, out var tables);
            if (status != GiBuildStatus.Built) return false;

            replay = new GiReplay(world, tables, registry);
            return true;
        }

        public bool Restore(GiKeyframe keyframe)
        {
            _map.Clear();
            _driver.Clear();
            GiRestore.Sources(_world, _tables, keyframe.Sources, _map, _driver, () => ApplySplats(keyframe.Splats));
            _token = GiWorld.Capture(_world);
            _hashes.Update();
            _hashes.Layers(_layerHashes);
            var same = keyframe.Hashes.Length == _layerCount && Equal(keyframe.Hashes, _layerHashes);
            Status = same ? GiReplayStatus.Ready : GiReplayStatus.KeyframeDiffers;
            return same;
        }

        public bool Step(GiTickRecord record)
        {
            CommandMismatches += ApplyCommands(record);
            ApplySync(record);
            ApplySplats(record);
            _driver.Step(_world);
            GiWorld.Process(_world);
            _hashes.Update();
            _hashes.Layers(_layerHashes);
            var same = record.Hashes.Length == _layerCount && Equal(record.Hashes, _layerHashes);
            if (!same) Status = GiReplayStatus.Diverged;
            else if (Status == GiReplayStatus.Ready) Status = GiReplayStatus.Matching;
            return same;
        }

        public void Hash(Span<ulong> destination)
        {
            for (var l = 0; l < _layerCount && l < destination.Length; l++) destination[l] = _layerHashes[l];
        }

        public void Diff(GiTileHashes live, List<GiTileDiff> destination)
        {
            destination.Clear();
            var grids = Math.Min(live.GridCount, _hashes.GridCount);
            var layers = Math.Min(live.LayerCount, _hashes.LayerCount);
            for (var g = 0; g < grids; g++)
            {
                var tiles = Math.Min(live.TilesPerSide(g), _hashes.TilesPerSide(g));
                tiles *= tiles;
                for (var l = 0; l < layers; l++)
                for (var tile = 0; tile < tiles; tile++)
                    if (live.Tile(g, l, tile) != _hashes.Tile(g, l, tile))
                        destination.Add(new GiTileDiff(g, l, tile));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            GiWorldBuilder.Release(_world, _tables, _registry);
        }

        private int ApplyCommands(GiTickRecord record)
        {
            var count = record.CommandCount;
            if (count == 0) return 0;

            if (_commands.Length < count)
            {
                _commands = new GiCommand[count];
                _results = new GiCommandResult[count];
                _scratch = new GiWorld.SourceCommand[count];
                _batchResults = new GiWorld.SourceResult[count];
            }

            for (var i = 0; i < count; i++)
            {
                var command = record.Commands[i];
                command.Owner = GiCommand.NoOwner;
                command.Layer = _tables.TryLayer(command.LayerKey, out var layer) ? layer : (byte)255;
                command.Stamp = _tables.TryStamp(command.StampKey, out var stamp) ? stamp : (byte)255;
                if (command.Target < 0 && command.Kind != GiCommandKind.Place)
                    command.Source = _map.TryGetValue(command.Source, out var mapped) ? mapped : default;

                _commands[i] = command;
                if (command.Kind == GiCommandKind.SetGain || command.Kind == GiCommandKind.Fade || command.Kind == GiCommandKind.Remove)
                    if (command.Target < 0)
                        _driver.Cancel(command.Source);
            }

            GiApplyPass.Apply(ref _token, _world, new ReadOnlySpan<GiCommand>(_commands, 0, count),
                new Span<GiCommandResult>(_results, 0, count), new Span<GiWorld.SourceCommand>(_scratch, 0, count),
                new Span<GiWorld.SourceResult>(_batchResults, 0, count), GiApplyPass.BatchLimit);

            var mismatches = 0;
            for (var i = 0; i < count; i++)
            {
                if (_results[i].Code != record.Results[i].Code) mismatches++;
                if (_commands[i].Kind != GiCommandKind.Place) continue;

                var recorded = record.Results[i].Source;
                if (recorded.Generation != 0u && _results[i].Source.Generation != 0u) _map[recorded] = _results[i].Source;
            }

            return mismatches;
        }

        private void ApplySync(GiTickRecord record)
        {
            var total = record.SyncTotal;
            if (total == 0) return;

            var sources = new GiWorld.SourceId[total];
            var words = 0;
            var wordStarts = new int[GiSyncPass.Categories];
            for (var k = 0; k < GiSyncPass.Categories; k++)
            {
                wordStarts[k] = words;
                words += GiSyncPass.Words(record.SyncCount[k]);
            }

            for (var i = 0; i < total; i++)
                sources[i] = _map.TryGetValue(record.SyncSources[i], out var mapped) ? mapped : default;

            var invalid = new ulong[Math.Max(1, words)];
            fixed (GiWorld.SourceId* sourcePointer = sources)
            fixed (float* xs = record.SyncXs, ys = record.SyncYs, headings = record.SyncHeadings, scales = record.SyncScales)
            fixed (ulong* invalidPointer = invalid)
            fixed (int* starts = record.SyncStart, counts = record.SyncCount, wordPointer = wordStarts)
                GiSyncPass.Apply(ref _token, _world, sourcePointer, xs, ys, headings, scales, invalidPointer, starts, counts, wordPointer);
        }

        private void ApplySplats(GiTickRecord record)
        {
            var total = record.SplatTotal;
            if (total == 0) return;

            var stamps = new byte[total];
            for (var i = 0; i < total; i++) stamps[i] = _tables.TryStamp(new GiStampKey(record.SplatStamps[i]), out var stamp) ? stamp : (byte)0;

            fixed (float* xs = record.SplatXs, ys = record.SplatYs)
            fixed (byte* stampPointer = stamps)
            fixed (sbyte* gains = record.SplatGains)
            fixed (int* starts = record.SplatStart, counts = record.SplatCount)
                GiSplatPass.Apply(_world, _tables, starts, counts, xs, ys, stampPointer, gains);
        }

        private static bool Equal(ulong[] left, ulong[] right)
        {
            if (left.Length != right.Length) return false;

            for (var i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }
    }
}
