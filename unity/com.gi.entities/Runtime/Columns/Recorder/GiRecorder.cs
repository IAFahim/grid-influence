using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public interface IGiCodec
    {
        int Bound(int length);

        int Compress(byte[] source, int length, byte[] destination);

        bool Decompress(byte[] source, int length, byte[] destination, int rawLength);
    }

    public sealed class GiStoredCodec : IGiCodec
    {
        public int Bound(int length)
        {
            return length;
        }

        public int Compress(byte[] source, int length, byte[] destination)
        {
            Array.Copy(source, destination, length);
            return length;
        }

        public bool Decompress(byte[] source, int length, byte[] destination, int rawLength)
        {
            if (length != rawLength) return false;

            Array.Copy(source, destination, length);
            return true;
        }
    }

    public sealed class GiRecorder
    {
        private sealed class Blob
        {
            public int Tick;
            public int RawLength;
            public int Length;
            public byte[] Data = Array.Empty<byte>();
        }

        private readonly byte _world;
        private readonly GiTables _tables;
        private readonly GiProfileSpec _profile;
        private readonly IGiCodec _codec;
        private readonly GiTileHashes _hashes;
        private readonly int _capacity;
        private readonly int _interval;
        private readonly Blob[] _ring;
        private readonly List<Blob> _keyframes = new List<Blob>();
        private readonly HashSet<GiWorld.SourceId> _live = new HashSet<GiWorld.SourceId>();
        private readonly GiTickRecord _current = new GiTickRecord();
        private readonly GiByteWriter _writer = new GiByteWriter(1 << 12);
        private readonly ulong[] _layerHashes;
        private byte[] _compressed = new byte[256];
        private int _head;
        private int _count;
        private int _sinceKeyframe;
        private bool _started;

        public GiRecorder(byte world, in GiTables tables, GiProfileSpec profile, int capacity, int keyframeInterval, IGiCodec codec)
        {
            _world = world;
            _tables = tables;
            _profile = profile;
            _codec = codec ?? new GiStoredCodec();
            _capacity = Math.Max(2, capacity);
            _interval = Math.Max(1, Math.Min(keyframeInterval, _capacity - 1));
            _ring = new Blob[_capacity];
            var grids = new byte[tables.GridCount];
            for (var g = 0; g < grids.Length; g++) tables.TryGrid(g, out grids[g]);
            var layers = new byte[tables.LayerCount];
            for (var l = 0; l < layers.Length; l++) tables.TryLayer(new GiLayerKey((byte)l), out layers[l]);
            _hashes = new GiTileHashes(world, grids, layers);
            _layerHashes = new ulong[layers.Length];
        }

        public byte World => _world;

        public int Capacity => _capacity;

        public int Count => _count;

        public int KeyframeCount => _keyframes.Count;

        public int LiveSources => _live.Count;

        public GiTileHashes Hashes => _hashes;

        public GiProfileSpec Profile => _profile;

        public GiTables Tables => _tables;

        public bool Started => _started;

        public long Bytes
        {
            get
            {
                long total = 0;
                for (var i = 0; i < _count; i++) total += _ring[(_head + i) % _capacity].Length;
                foreach (var keyframe in _keyframes) total += keyframe.Length;
                return total;
            }
        }

        public void Adopt(ReadOnlySpan<GiWorld.SourceId> sources)
        {
            foreach (var source in sources) _live.Add(source);
        }

        public void Start()
        {
            if (_started) return;

            _started = true;
            _hashes.Update();
            CaptureKeyframe(null);
        }

        public void OnCommands(ReadOnlySpan<GiCommand> commands, ReadOnlySpan<GiCommandResult> results)
        {
            var first = _current.CommandCount;
            _current.SizeCommands(first + commands.Length);
            var all = _current.Commands;
            var allResults = _current.Results;

            for (var i = 0; i < commands.Length; i++)
            {
                var command = commands[i];
                if (command.Target >= 0) command.Target += first;
                all[first + i] = command;
                allResults[first + i] = results[i];
                if (command.Kind == GiCommandKind.Place && results[i].Code == GiFaultCode.None && results[i].Source.Generation != 0u)
                    _live.Add(results[i].Source);
            }
        }

        public unsafe void OnSync(GiWorld.SourceId* sources, float* xs, float* ys, float* headings, float* scales, int* categoryStart,
            int* categoryCount)
        {
            var total = 0;
            for (var k = 0; k < GiTickRecord.SyncCategories; k++) total += categoryCount[k];
            _current.SizeSync(total);
            var row = 0;
            for (var k = 0; k < GiTickRecord.SyncCategories; k++)
            {
                _current.SyncStart[k] = row;
                _current.SyncCount[k] = categoryCount[k];
                for (var i = 0; i < categoryCount[k]; i++)
                {
                    var from = categoryStart[k] + i;
                    _current.SyncSources[row] = sources[from];
                    _current.SyncXs[row] = xs[from];
                    _current.SyncYs[row] = ys[from];
                    _current.SyncHeadings[row] = (k & GiSyncPass.Oriented) != 0 ? headings[from] : 0f;
                    _current.SyncScales[row] = (k & GiSyncPass.Scaled) != 0 ? scales[from] : 1f;
                    row++;
                }
            }
        }

        public unsafe void OnSplat(int* layerStart, int* layerCount, float* xs, float* ys, byte* stamps, sbyte* gains)
        {
            var total = 0;
            for (var k = 0; k < GiTickRecord.SplatKeys; k++) total += layerCount[k];
            _current.SizeSplats(total);
            var row = 0;
            for (var k = 0; k < GiTickRecord.SplatKeys; k++)
            {
                _current.SplatStart[k] = row;
                _current.SplatCount[k] = layerCount[k];
                for (var i = 0; i < layerCount[k]; i++)
                {
                    var from = layerStart[k] + i;
                    _current.SplatXs[row] = xs[from];
                    _current.SplatYs[row] = ys[from];
                    _current.SplatStamps[row] = _tables.TryStampKey(stamps[from], out var key) ? key.Index : (byte)255;
                    _current.SplatGains[row] = gains[from];
                    row++;
                }
            }
        }

        public void OnProcessed()
        {
            if (!_started) return;

            _hashes.Update();
            _hashes.Layers(_layerHashes);
            _current.Tick = GiWorld.Tick(_world);
            _current.Hashes = _layerHashes;
            _writer.Clear();
            GiRecordingCodec.WriteTick(_writer, _current);
            Store(NextSlot(), _current.Tick);
            if (++_sinceKeyframe >= _interval) CaptureKeyframe(_current);
            _current.Clear();
            DropStaleKeyframes();
        }

        public bool TryWindow(out int firstTick, out int lastTick)
        {
            firstTick = 0;
            lastTick = 0;
            if (_keyframes.Count == 0) return false;

            firstTick = _keyframes[0].Tick;
            lastTick = _count == 0 ? firstTick : _ring[(_head + _count - 1) % _capacity].Tick;
            return true;
        }

        public bool TryPlan(int startTick, int endTick, out GiKeyframe keyframe, out List<GiTickRecord> ticks)
        {
            keyframe = null;
            ticks = null;
            Blob chosen = null;
            foreach (var candidate in _keyframes)
                if (candidate.Tick <= startTick)
                    chosen = candidate;
            if (chosen == null || !TryDecodeKeyframe(chosen, out keyframe)) return false;

            ticks = new List<GiTickRecord>();
            for (var i = 0; i < _count; i++)
            {
                var blob = _ring[(_head + i) % _capacity];
                if (blob.Tick <= keyframe.Tick) continue;
                if (blob.Tick > endTick) break;
                if (!TryDecodeTick(blob, out var record)) return false;

                ticks.Add(record);
            }

            return ticks.Count == 0 || ticks[0].Tick == keyframe.Tick + 1;
        }

        public GiRecordingData Export()
        {
            var data = new GiRecordingData { Profile = _profile };
            foreach (var blob in _keyframes)
                if (TryDecodeKeyframe(blob, out var keyframe))
                    data.Keyframes.Add(keyframe);
            for (var i = 0; i < _count; i++)
                if (TryDecodeTick(_ring[(_head + i) % _capacity], out var record))
                    data.Ticks.Add(record);
            return data;
        }

        private Blob NextSlot()
        {
            var index = (_head + _count) % _capacity;
            if (_count == _capacity)
            {
                index = _head;
                _head = (_head + 1) % _capacity;
            }
            else
            {
                _count++;
            }

            return _ring[index] ?? (_ring[index] = new Blob());
        }

        private void Store(Blob blob, int tick)
        {
            blob.Tick = tick;
            blob.RawLength = _writer.Length;
            var bound = Math.Max(_codec.Bound(_writer.Length), _writer.Length);
            if (_compressed.Length < bound) _compressed = new byte[bound];
            var length = _codec.Compress(_writer.Buffer, _writer.Length, _compressed);
            if (length <= 0 || length >= _writer.Length)
            {
                Array.Copy(_writer.Buffer, _compressed, _writer.Length);
                length = _writer.Length;
            }

            blob.Length = length;
            if (blob.Data.Length < length) blob.Data = new byte[Math.Max(length, blob.Data.Length * 2)];
            Array.Copy(_compressed, blob.Data, length);
        }

        private void CaptureKeyframe(GiTickRecord splats)
        {
            var states = new List<GiSourceState>(_live.Count);
            var dead = new List<GiWorld.SourceId>();
            foreach (var id in _live)
            {
                if (GiSourceCapture.TryCapture(_world, _tables, id, out var state)) states.Add(state);
                else dead.Add(id);
            }

            foreach (var id in dead) _live.Remove(id);
            states.Sort((a, b) => a.Id.Slot != b.Id.Slot ? a.Id.Slot.CompareTo(b.Id.Slot) : a.Id.Generation.CompareTo(b.Id.Generation));
            _hashes.Layers(_layerHashes);
            var keyframe = new GiKeyframe { Tick = GiWorld.Tick(_world), Sources = states.ToArray(), Hashes = (ulong[])_layerHashes.Clone() };
            if (splats != null) Copy(splats, keyframe.Splats);
            _writer.Clear();
            GiRecordingCodec.WriteKeyframe(_writer, keyframe);
            var blob = new Blob();
            Store(blob, keyframe.Tick);
            _keyframes.Add(blob);
            _sinceKeyframe = 0;
        }

        private static void Copy(GiTickRecord from, GiTickRecord to)
        {
            Array.Copy(from.SplatStart, to.SplatStart, from.SplatStart.Length);
            Array.Copy(from.SplatCount, to.SplatCount, from.SplatCount.Length);
            var total = from.SplatTotal;
            to.SizeSplats(total);
            Array.Copy(from.SplatXs, to.SplatXs, total);
            Array.Copy(from.SplatYs, to.SplatYs, total);
            Array.Copy(from.SplatStamps, to.SplatStamps, total);
            Array.Copy(from.SplatGains, to.SplatGains, total);
        }

        private void DropStaleKeyframes()
        {
            if (_count == 0) return;

            var oldest = _ring[_head].Tick;
            while (_keyframes.Count > 1 && _keyframes[0].Tick + 1 < oldest) _keyframes.RemoveAt(0);
        }

        private bool TryDecodeTick(Blob blob, out GiTickRecord record)
        {
            record = new GiTickRecord();
            var raw = new byte[blob.RawLength];
            if (!Expand(blob, raw)) return false;

            return GiRecordingCodec.ReadTick(new GiByteReader(raw, 0, raw.Length), record);
        }

        private bool TryDecodeKeyframe(Blob blob, out GiKeyframe keyframe)
        {
            keyframe = null;
            var raw = new byte[blob.RawLength];
            return Expand(blob, raw) && GiRecordingCodec.ReadKeyframe(new GiByteReader(raw, 0, raw.Length), out keyframe);
        }

        private bool Expand(Blob blob, byte[] raw)
        {
            if (blob.Length == blob.RawLength)
            {
                Array.Copy(blob.Data, raw, blob.RawLength);
                return true;
            }

            return _codec.Decompress(blob.Data, blob.Length, raw, blob.RawLength);
        }
    }
}
