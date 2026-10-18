using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static class GiSourceCapture
    {
        public static bool TryCapture(byte world, in GiTables tables, GiWorld.SourceId id, out GiSourceState state)
        {
            state = default;
            if (!GiWorld.TryGetSourceSpec(world, id, out var spec)) return false;
            if (!tables.TryLayerKey(spec.Layer, out var layerKey) || !tables.TryStampKey(spec.Stamp, out var stampKey)) return false;

            state.Id = id;
            state.LayerKey = layerKey.Index;
            state.StampKey = stampKey.Index;
            state.Gain = spec.Gain;
            state.X = spec.X;
            state.Y = spec.Y;
            state.Heading = spec.HeadingRadians;
            state.Scale = spec.Scale;
            if (GiWorld.TryGetSchedule(world, id, out var gain, out var from, out var to, out var ticks, out var left, out var expire))
            {
                state.Gain = gain;
                state.FadeFrom = from;
                state.FadeTo = to;
                state.FadeTicks = ticks;
                state.FadeTicksLeft = left;
                state.ExpireTicksLeft = expire;
            }

            return true;
        }
    }

    public sealed class GiRestoreDriver
    {
        private struct Entry
        {
            public GiWorld.SourceId Id;
            public sbyte From;
            public sbyte To;
            public int Ticks;
            public int Elapsed;
            public sbyte Last;
            public long Tag;
            public sbyte Applied;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public int Count => _entries.Count;

        public static sbyte Staircase(sbyte from, sbyte to, int ticks, int step)
        {
            var delta = to - from;
            if (delta == 0 || ticks <= 0) return to;

            var magnitude = (Math.Abs(delta) * (long)step + ticks / 2) / ticks;
            return (sbyte)(from + Math.Sign(delta) * (int)magnitude);
        }

        public void Add(GiWorld.SourceId id, sbyte from, sbyte to, int ticks, int ticksLeft, long tag = -1, sbyte applied = 0)
        {
            var elapsed = Math.Max(0, ticks - ticksLeft);
            _entries.Add(new Entry
            {
                Id = id, From = from, To = to, Ticks = ticks, Elapsed = elapsed, Last = Staircase(from, to, ticks, elapsed), Tag = tag, Applied = applied,
            });
        }

        public void CancelWhere(Func<long, sbyte, bool> changed)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].Tag >= 0 && changed(_entries[i].Tag, _entries[i].Applied))
                    _entries.RemoveAt(i);
        }

        public void Cancel(GiWorld.SourceId id)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].Id.Equals(id))
                    _entries.RemoveAt(i);
        }

        public void Clear()
        {
            _entries.Clear();
        }

        public void Step(byte world)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (!GiWorld.TryGetSourceSpec(world, entry.Id, out var spec) || spec.Gain != entry.Last ||
                    GiWorld.TryGetSchedule(world, entry.Id, out _, out _, out _, out var fadeTicks, out _, out _) && fadeTicks > 0)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                entry.Elapsed++;
                entry.Last = Staircase(entry.From, entry.To, entry.Ticks, entry.Elapsed);
                if (GiWorld.SetGain(world, entry.Id, entry.Last) != GiWorld.WriteStatus.Applied || entry.Elapsed >= entry.Ticks)
                    _entries.RemoveAt(i);
                else
                    _entries[i] = entry;
            }
        }

        public void Step(byte world, Func<GiWorld.SourceId, sbyte, bool> setGain)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                entry.Elapsed++;
                var gain = Staircase(entry.From, entry.To, entry.Ticks, entry.Elapsed);
                if (!setGain(entry.Id, gain) || entry.Elapsed >= entry.Ticks)
                    _entries.RemoveAt(i);
                else
                    _entries[i] = entry;
            }
        }
    }

    public static class GiRestore
    {
        public const float HalfAngleStep = (float)(Math.PI / 65536.0);

        public static bool Place(byte world, in GiTables tables, in GiSourceState state, GiRestoreDriver driver, out GiWorld.SourceId id,
            long tag = -1, sbyte applied = 0)
        {
            id = default;
            if (!tables.TryLayer(new GiLayerKey(state.LayerKey), out var layer) || !tables.TryStamp(new GiStampKey(state.StampKey), out var stamp)) return false;

            id = GiWorld.Place(world, layer, state.X, state.Y, stamp, state.Gain);
            if (id.Generation == 0u) return false;

            GiWorld.Turn(world, id, state.Heading + HalfAngleStep);
            GiWorld.Scale(world, id, state.Scale);
            Schedule(world, id, state, driver, tag, applied);
            return true;
        }

        public static void Schedule(byte world, GiWorld.SourceId id, in GiSourceState state, GiRestoreDriver driver, long tag = -1, sbyte applied = 0)
        {
            GiWorld.SetGain(world, id, state.Gain);
            if (state.Expiring) GiWorld.Expire(world, id, state.ExpireTicksLeft);
            if (state.Fading) driver.Add(id, state.FadeFrom, state.FadeTo, state.FadeTicks, state.FadeTicksLeft, tag, applied);
        }

        public static int Sources(byte world, in GiTables tables, GiSourceState[] sources, Dictionary<GiWorld.SourceId, GiWorld.SourceId> map,
            GiRestoreDriver driver, Action beforeProcess = null)
        {
            var placed = 0;
            var created = new GiWorld.SourceId[sources.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                var state = sources[i];
                if (!tables.TryLayer(new GiLayerKey(state.LayerKey), out var layer) || !tables.TryStamp(new GiStampKey(state.StampKey), out var stamp))
                    continue;

                var id = GiWorld.Place(world, layer, state.X, state.Y, stamp, state.Gain);
                if (id.Generation == 0u) continue;

                GiWorld.Turn(world, id, state.Heading + HalfAngleStep);
                GiWorld.Scale(world, id, state.Scale);
                created[i] = id;
                map[state.Id] = id;
                placed++;
            }

            beforeProcess?.Invoke();
            GiWorld.Process(world);
            for (var i = 0; i < sources.Length; i++)
            {
                if (created[i].Generation == 0u) continue;

                if (sources[i].Expiring) GiWorld.Expire(world, created[i], sources[i].ExpireTicksLeft);
                if (sources[i].Fading) driver.Add(created[i], sources[i].FadeFrom, sources[i].FadeTo, sources[i].FadeTicks, sources[i].FadeTicksLeft);
            }

            return placed;
        }
    }
}
