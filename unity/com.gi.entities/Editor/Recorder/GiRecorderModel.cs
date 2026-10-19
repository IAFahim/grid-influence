using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gi.Entities.Presentation;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor
{
    public sealed class GiRecorderModel : IDisposable
    {
        private readonly List<GiTileDiff> _diff = new List<GiTileDiff>();
        private GiReplay _replay;
        private GiRecorder _recorder;
        private int _replayTick;

        public string EcsWorld { get; private set; }

        public byte GiWorldId { get; private set; }

        public bool HasWorld { get; private set; }

        public string Message { get; private set; } = string.Empty;

        public GiReplay Replay => _replay;

        public int ReplayTick => _replayTick;

        public IReadOnlyList<GiTileDiff> Diff => _diff;

        public bool Recording => HasWorld && GiRecorderGate.IsRecording(GiWorldId);

        public void Select(string ecsWorld)
        {
            DropReplay();
            _recorder = null;
            EcsWorld = ecsWorld;
            var info = ecsWorld == null ? null : GiInspectorBoard.Find(ecsWorld);
            HasWorld = info != null && info.Alive;
            GiWorldId = HasWorld ? info.GiWorld : (byte)0;
            Message = HasWorld ? string.Empty : "No Gi world is running.";
        }

        public void SetRecording(bool on)
        {
            GiDebugConfig.Record.Data = on;
        }

        public bool TryStatus(out int ticks, out int first, out int last, out long bytes)
        {
            ticks = 0;
            first = 0;
            last = 0;
            bytes = 0;
            if (!HasWorld) return false;

            GiRuntime.CompleteAll();
            _recorder = GiRecorderGate.Find(GiWorldId);
            if (_recorder == null || !_recorder.TryWindow(out first, out last)) return false;

            ticks = _recorder.Count;
            bytes = _recorder.Bytes;
            return true;
        }

        public bool ReplayTo(int tick)
        {
            DropReplay();
            if (!HasWorld) return false;

            GiRuntime.CompleteAll();
            _recorder = GiRecorderGate.Find(GiWorldId);
            if (_recorder == null || !_recorder.TryWindow(out var first, out var last))
            {
                Message = "Nothing is recorded.";
                return false;
            }

            tick = Math.Max(first, Math.Min(last, tick));
            if (!_recorder.TryPlan(tick, tick, out var keyframe, out var ticks))
            {
                Message = "The tick is outside the recorded window.";
                return false;
            }

            if (!GiReplay.TryCreate(_recorder.Profile, out _replay, out var status))
            {
                Message = "The profile does not build: " + status + ".";
                return false;
            }

            var restored = _replay.Restore(keyframe);
            var matched = restored;
            foreach (var record in ticks) matched &= _replay.Step(record);
            _replayTick = ticks.Count == 0 ? keyframe.Tick : ticks[ticks.Count - 1].Tick;
            Message = !restored
                ? "The keyframe at tick " + keyframe.Tick + " does not rebuild the field it was taken from."
                : matched ? "Replayed to tick " + _replayTick + "; every hash matches." : "The replay diverges from the recorded hashes.";
            return matched;
        }

        public int CompareWithLive()
        {
            _diff.Clear();
            if (!HasWorld) return 0;

            GiRuntime.CompleteAll();
            _recorder = GiRecorderGate.Find(GiWorldId);
            if (_recorder == null)
            {
                Message = "Nothing is recorded.";
                return 0;
            }

            if (!_recorder.TryWindow(out _, out var last)) return 0;

            if (_replay == null || _replayTick != last)
                if (!ReplayTo(last))
                    if (_replay == null)
                        return 0;

            GiRuntime.CompleteAll();
            _replay.Diff(_recorder.Hashes, _diff);
            Message = _diff.Count == 0 ? "The replay and the live field agree." : _diff.Count + " tiles differ between the replay and the live field.";
            return _diff.Count;
        }

        public GiExportStatus Export(string folder, int fromTick, int toTick, out string path)
        {
            path = null;
            if (!HasWorld) return GiExportStatus.EmptyRange;

            GiRuntime.CompleteAll();
            _recorder = GiRecorderGate.Find(GiWorldId);
            if (_recorder == null || !_recorder.TryPlan(fromTick, toTick, out var keyframe, out var ticks)) return GiExportStatus.EmptyRange;

            var name = "Range_" + keyframe.Tick + "_" + toTick;
            var status = GiStreamExport.Write(name, _recorder.Profile, keyframe, ticks, out var text);
            Message = status == GiExportStatus.Exported ? "Exported " + ticks.Count + " ticks." : "Cannot export: " + status + ".";
            if (status != GiExportStatus.Exported) return status;

            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, GiStreamExport.Identifier(name) + ".cs");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return status;
        }

        public bool Save(string path)
        {
            if (!HasWorld) return false;

            GiRuntime.CompleteAll();
            _recorder = GiRecorderGate.Find(GiWorldId);
            if (_recorder == null) return false;

            File.WriteAllBytes(path, GiRecordingCodec.Encode(_recorder.Export()));
            Message = "Saved " + _recorder.Count + " ticks.";
            return true;
        }

        public static bool TryLoad(string path, out GiRecordingData data)
        {
            data = null;
            return File.Exists(path) && GiRecordingCodec.TryDecode(File.ReadAllBytes(path), out data);
        }

        public bool ReplayFile(GiRecordingData data)
        {
            DropReplay();
            if (data.Keyframes.Count == 0 || data.Ticks.Count == 0 || !GiReplay.TryCreate(data.Profile, out _replay, out _)) return false;

            var keyframe = data.Keyframes[0];
            var matched = _replay.Restore(keyframe);
            foreach (var record in data.Ticks)
            {
                if (record.Tick <= keyframe.Tick) continue;

                matched &= _replay.Step(record);
                _replayTick = record.Tick;
            }

            Message = matched ? "The file replays to tick " + _replayTick + "; every hash matches." : "The file diverges when replayed.";
            return matched;
        }

        public void Dispose()
        {
            DropReplay();
        }

        private void DropReplay()
        {
            _replay?.Dispose();
            _replay = null;
            _diff.Clear();
            _replayTick = 0;
        }
    }
}
