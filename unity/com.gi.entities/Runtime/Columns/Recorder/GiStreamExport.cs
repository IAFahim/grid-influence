using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public enum GiExportStatus : byte
    {
        Exported,
        EmptyRange,
        RasterStamp,
        SingleInputLayer,
        UnsupportedLayer,
    }

    public static class GiStreamExport
    {
        public static GiExportStatus Write(string name, GiProfileSpec profile, GiKeyframe keyframe, IReadOnlyList<GiTickRecord> ticks, out string text)
        {
            text = null;
            if (ticks.Count == 0) return GiExportStatus.EmptyRange;

            foreach (var stamp in profile.Stamps)
                if (stamp.Shape == GiStampShape.Raster)
                    return GiExportStatus.RasterStamp;

            foreach (var layer in profile.Layers)
            {
                if (layer.Kind == GiLayerKind.Sum && layer.Second == GiLayerSpec.NoLayer) return GiExportStatus.SingleInputLayer;
                if (layer.Kind > GiLayerKind.Mask) return GiExportStatus.UnsupportedLayer;
            }

            var builder = new StringBuilder(1 << 14);
            builder.Append("using Xunit;\n\nnamespace Gi.Tests;\n\npublic class ").Append(Identifier(name)).Append('\n').Append("{\n");
            builder.Append("    private static Layer.Precision Precision(int phaseBits, int turnBits)\n    {\n");
            builder.Append("        Layer.Precision.TryCreate(phaseBits, turnBits, out var precision);\n        return precision;\n    }\n\n");
            builder.Append("    [Fact]\n    public void TheRecordedRangeMatchesTheReference()\n    {\n");
            builder.Append("        var setup = new StreamSetup\n        {\n            Seed = 0,\n");
            AppendGrids(builder, profile);
            AppendLayers(builder, profile);
            AppendStamps(builder, profile);
            builder.Append("            Commands = new List<StreamCommand>\n            {\n");
            var commands = new Commands(builder);
            commands.Range(keyframe, ticks);
            builder.Append("            },\n        };\n        Assert.Null(StreamRunner.TryRun(setup, setup.Commands));\n    }\n}\n");
            text = builder.ToString();
            return GiExportStatus.Exported;
        }

        public static string Identifier(string name)
        {
            var builder = new StringBuilder("Recorded_");
            foreach (var c in name)
                builder.Append(c >= '0' && c <= '9' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' ? c : '_');
            return builder.ToString();
        }

        private static string Bits(float value)
        {
            if (value == 0f && !float.IsNegative(value)) return "0f";

            var bits = BitConverter.SingleToInt32Bits(value);
            return "BitConverter.Int32BitsToSingle(unchecked((int)0x" + ((uint)bits).ToString("X8", CultureInfo.InvariantCulture) + "))";
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static void AppendGrids(StringBuilder builder, GiProfileSpec profile)
        {
            builder.Append("            Grids = new[]\n            {\n");
            foreach (var grid in profile.Grids)
                builder.Append("                new GridSpec { Power = ").Append(Number(grid.Power)).Append(", X = ").Append(Bits(grid.X)).Append(", Y = ")
                    .Append(Bits(grid.Y)).Append(", Size = ").Append(Bits(grid.Size)).Append(" },\n");
            builder.Append("            },\n");
        }

        private static void AppendLayers(StringBuilder builder, GiProfileSpec profile)
        {
            builder.Append("            Layers = new[]\n            {\n");
            foreach (var layer in profile.Layers)
            {
                builder.Append("                new LayerSpec { ");
                switch (layer.Kind)
                {
                    case GiLayerKind.Persistent:
                    case GiLayerKind.Transient:
                        builder.Append("Precision = Precision(").Append(Number(layer.PhaseBits)).Append(", ").Append(Number(layer.TurnBits)).Append(")");
                        if (layer.Kind == GiLayerKind.Transient) builder.Append(", Transient = true");
                        break;
                    case GiLayerKind.Sum:
                        builder.Append("Derived = true, Op = LayerOp.Sum, A = ").Append(Number(layer.First)).Append(", B = ").Append(Number(layer.Second))
                            .Append(", WeightA = ").Append(Number(layer.FirstWeight)).Append(", WeightB = ").Append(Number(layer.SecondWeight))
                            .Append(", Shift = ").Append(Number(layer.Shift));
                        break;
                    case GiLayerKind.Min:
                    case GiLayerKind.Max:
                        builder.Append("Derived = true, Op = LayerOp.").Append(layer.Kind).Append(", A = ").Append(Number(layer.First)).Append(", B = ")
                            .Append(Number(layer.Second));
                        break;
                    default:
                        builder.Append("Derived = true, Op = LayerOp.Mask, A = ").Append(Number(layer.First)).Append(", B = ").Append(Number(layer.Second))
                            .Append(", Low = ").Append(Number(layer.MaskLow)).Append(", High = ").Append(Number(layer.MaskHigh));
                        break;
                }

                builder.Append(" },\n");
            }

            builder.Append("            },\n");
        }

        private static void AppendStamps(StringBuilder builder, GiProfileSpec profile)
        {
            builder.Append("            Stamps = new[]\n            {\n");
            foreach (var stamp in profile.Stamps)
            {
                builder.Append("                new StampSpec { Kind = StampKind.").Append(stamp.Shape);
                if (GiFootprint.IsRound(stamp.Shape))
                    builder.Append(", Radius = ").Append(Number(stamp.Radius)).Append(", Arc = ").Append(Number(stamp.Arc));
                else
                    builder.Append(", Width = ").Append(Number(stamp.Width)).Append(", Height = ").Append(Number(stamp.Height));
                builder.Append(", Value = ").Append(Number(stamp.Value)).Append(" },\n");
            }

            builder.Append("            },\n");
        }

        private sealed class Commands
        {
            private readonly StringBuilder _builder;
            private readonly Dictionary<GiWorld.SourceId, int> _handles = new Dictionary<GiWorld.SourceId, int>();
            private readonly GiRestoreDriver _driver = new GiRestoreDriver();
            private int _places;

            public Commands(StringBuilder builder)
            {
                _builder = builder;
            }

            public void Range(GiKeyframe keyframe, IReadOnlyList<GiTickRecord> ticks)
            {
                var created = new int[keyframe.Sources.Length];
                for (var i = 0; i < keyframe.Sources.Length; i++)
                {
                    var state = keyframe.Sources[i];
                    created[i] = PlaceOp(state.LayerKey, state.StampKey, state.Gain, state.X, state.Y, state.Heading, state.Scale);
                    _handles[state.Id] = created[i];
                }

                Splats(keyframe.Splats);
                Emit("Op = StreamOp.Process");
                for (var i = 0; i < keyframe.Sources.Length; i++)
                {
                    var state = keyframe.Sources[i];
                    if (state.Expiring) Emit("Op = StreamOp.Expire, Handle = " + Number(created[i]) + ", Int0 = " + Number(state.ExpireTicksLeft));
                    if (state.Fading) _driver.Add(HandleId(created[i]), state.FadeFrom, state.FadeTo, state.FadeTicks, state.FadeTicksLeft);
                }

                foreach (var tick in ticks) Tick(tick);
            }

            private void Tick(GiTickRecord record)
            {
                var batch = new int[record.CommandCount];
                for (var i = 0; i < record.CommandCount; i++)
                {
                    var command = record.Commands[i];
                    if (command.Kind == GiCommandKind.Place)
                    {
                        batch[i] = PlaceOp(command.LayerKey.Index, command.StampKey.Index, command.Gain, command.X, command.Y, command.Heading, command.Scale);
                        var result = record.Results[i].Source;
                        if (result.Generation != 0u) _handles[result] = batch[i];
                        continue;
                    }

                    var handle = command.Target >= 0 ? batch[command.Target] : _handles.TryGetValue(command.Source, out var known) ? known : -1;
                    batch[i] = handle;
                    Target(command, handle);
                }

                for (var k = 0; k < GiTickRecord.SyncCategories; k++)
                for (var i = 0; i < record.SyncCount[k]; i++)
                {
                    var row = record.SyncStart[k] + i;
                    if (!_handles.TryGetValue(record.SyncSources[row], out var handle)) continue;

                    Emit("Op = StreamOp.Move, Handle = " + Number(handle) + ", X = " + Bits(record.SyncXs[row]) + ", Y = " + Bits(record.SyncYs[row]));
                    if ((k & GiSyncPass.Oriented) != 0)
                        Emit("Op = StreamOp.Turn, Handle = " + Number(handle) + ", X = " + Bits(record.SyncHeadings[row]));
                    if ((k & GiSyncPass.Scaled) != 0)
                        Emit("Op = StreamOp.Scale, Handle = " + Number(handle) + ", X = " + Bits(record.SyncScales[row]));
                }

                Splats(record);
                _driver.Step(0, (id, gain) =>
                {
                    Emit("Op = StreamOp.SetGain, Handle = " + Number((int)id.Slot) + ", Gain = " + Number(gain));
                    return true;
                });
                Emit("Op = StreamOp.Process");
            }

            private void Splats(GiTickRecord record)
            {
                for (var key = 0; key < GiTickRecord.SplatKeys; key++)
                for (var i = 0; i < record.SplatCount[key]; i++)
                {
                    var row = record.SplatStart[key] + i;
                    Emit("Op = StreamOp.Splat, Layer = " + Number(key) + ", Stamp = " + Number(record.SplatStamps[row]) + ", Gain = " +
                         Number(record.SplatGains[row]) + ", X = " + Bits(record.SplatXs[row]) + ", Y = " + Bits(record.SplatYs[row]));
                }
            }

            private void Target(GiCommand command, int handle)
            {
                if (handle < 0) return;

                var h = Number(handle);
                switch (command.Kind)
                {
                    case GiCommandKind.Remove:
                        _driver.Cancel(HandleId(handle));
                        Emit("Op = StreamOp.Remove, Handle = " + h);
                        break;
                    case GiCommandKind.SetGain:
                        _driver.Cancel(HandleId(handle));
                        Emit("Op = StreamOp.SetGain, Handle = " + h + ", Gain = " + Number(command.Gain));
                        break;
                    case GiCommandKind.Fade:
                        _driver.Cancel(HandleId(handle));
                        Emit("Op = StreamOp.Fade, Handle = " + h + ", Gain = " + Number(command.Gain) + ", Int0 = " + Number(command.Ticks));
                        break;
                    default:
                        Emit("Op = StreamOp.Expire, Handle = " + h + ", Int0 = " + Number(command.Ticks));
                        break;
                }
            }

            private static GiWorld.SourceId HandleId(int handle)
            {
                return new GiWorld.SourceId((uint)handle, 1u);
            }

            private int PlaceOp(byte layer, byte stamp, sbyte gain, float x, float y, float heading, float scale)
            {
                var handle = _places++;
                Emit("Op = StreamOp.Place, Layer = " + Number(layer) + ", Stamp = " + Number(stamp) + ", Handle = " + Number(handle) + ", Gain = " +
                     Number(gain) + ", X = " + Bits(x) + ", Y = " + Bits(y));
                if (Bits(heading) != "0f") Emit("Op = StreamOp.Turn, Handle = " + Number(handle) + ", X = " + Bits(heading));
                if (BitConverter.SingleToInt32Bits(scale) != BitConverter.SingleToInt32Bits(1f))
                    Emit("Op = StreamOp.Scale, Handle = " + Number(handle) + ", X = " + Bits(scale));
                return handle;
            }

            private void Emit(string fields)
            {
                _builder.Append("                new StreamCommand { ").Append(fields).Append(" },\n");
            }
        }
    }
}
