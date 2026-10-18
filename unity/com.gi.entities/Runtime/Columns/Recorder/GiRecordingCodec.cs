using System;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static class GiRecordingCodec
    {
        public const int Version = 1;
        public const int Limit = 1 << 26;
        private static readonly byte[] Magic = { (byte)'G', (byte)'I', (byte)'R', (byte)'C' };

        public static void WriteTick(GiByteWriter writer, GiTickRecord record)
        {
            writer.Varint((ulong)record.Tick);
            writer.Varint((ulong)record.CommandCount);
            for (var i = 0; i < record.CommandCount; i++)
            {
                var command = record.Commands[i];
                writer.Byte((byte)command.Kind);
                writer.Byte(command.LayerKey.Index);
                writer.Byte(command.StampKey.Index);
                writer.SByte(command.Gain);
                writer.SByte(command.TargetGain);
                writer.Varint(command.Ticks);
                writer.Int(command.Target);
                writer.Varint(command.Source.Slot);
                writer.Varint(command.Source.Generation);
                writer.Float(command.X);
                writer.Float(command.Y);
                writer.Float(command.Heading);
                writer.Float(command.Scale);
                var result = record.Results[i];
                writer.Byte((byte)result.Code);
                writer.Varint(result.Source.Slot);
                writer.Varint(result.Source.Generation);
            }

            for (var k = 0; k < GiTickRecord.SyncCategories; k++) writer.Varint((ulong)record.SyncCount[k]);
            for (var k = 0; k < GiTickRecord.SyncCategories; k++)
            for (var i = 0; i < record.SyncCount[k]; i++)
            {
                var row = record.SyncStart[k] + i;
                writer.Varint(record.SyncSources[row].Slot);
                writer.Varint(record.SyncSources[row].Generation);
                writer.Float(record.SyncXs[row]);
                writer.Float(record.SyncYs[row]);
                if ((k & GiSyncPass.Oriented) != 0) writer.Float(record.SyncHeadings[row]);
                if ((k & GiSyncPass.Scaled) != 0) writer.Float(record.SyncScales[row]);
            }

            WriteSplats(writer, record);

            writer.Varint((ulong)record.Hashes.Length);
            foreach (var hash in record.Hashes) writer.Fixed64(hash);
        }

        public static void WriteSplats(GiByteWriter writer, GiTickRecord record)
        {
            for (var k = 0; k < GiTickRecord.SplatKeys; k++) writer.Varint((ulong)record.SplatCount[k]);
            for (var k = 0; k < GiTickRecord.SplatKeys; k++)
            for (var i = 0; i < record.SplatCount[k]; i++)
            {
                var row = record.SplatStart[k] + i;
                writer.Float(record.SplatXs[row]);
                writer.Float(record.SplatYs[row]);
                writer.Byte(record.SplatStamps[row]);
                writer.SByte(record.SplatGains[row]);
            }
        }

        public static bool ReadTick(GiByteReader reader, GiTickRecord record)
        {
            record.Clear();
            record.Tick = (int)reader.Varint();
            var commands = reader.Count(Limit);
            if (reader.Failed) return false;

            record.SizeCommands(commands);
            for (var i = 0; i < commands; i++)
            {
                var command = new GiCommand { Owner = GiCommand.NoOwner, Target = GiCommand.Existing };
                command.Kind = (GiCommandKind)reader.Byte();
                command.LayerKey = new GiLayerKey(reader.Byte());
                command.StampKey = new GiStampKey(reader.Byte());
                command.Gain = reader.SByte();
                command.TargetGain = reader.SByte();
                command.Ticks = (ushort)reader.Varint();
                command.Target = reader.Int();
                var slot = (uint)reader.Varint();
                var generation = (uint)reader.Varint();
                command.Source = new GiWorld.SourceId(slot, generation);
                command.X = reader.Float();
                command.Y = reader.Float();
                command.Heading = reader.Float();
                command.Scale = reader.Float();
                record.Commands[i] = command;
                var code = (GiFaultCode)reader.Byte();
                slot = (uint)reader.Varint();
                generation = (uint)reader.Varint();
                record.Results[i] = new GiCommandResult { Code = code, Source = new GiWorld.SourceId(slot, generation) };
            }

            var syncTotal = 0;
            for (var k = 0; k < GiTickRecord.SyncCategories; k++)
            {
                record.SyncStart[k] = syncTotal;
                record.SyncCount[k] = reader.Count(Limit);
                syncTotal += record.SyncCount[k];
                if (syncTotal > Limit) return false;
            }

            if (reader.Failed) return false;

            record.SizeSync(syncTotal);
            for (var k = 0; k < GiTickRecord.SyncCategories; k++)
            for (var i = 0; i < record.SyncCount[k]; i++)
            {
                var row = record.SyncStart[k] + i;
                var slot = (uint)reader.Varint();
                var generation = (uint)reader.Varint();
                record.SyncSources[row] = new GiWorld.SourceId(slot, generation);
                record.SyncXs[row] = reader.Float();
                record.SyncYs[row] = reader.Float();
                record.SyncHeadings[row] = (k & GiSyncPass.Oriented) != 0 ? reader.Float() : 0f;
                record.SyncScales[row] = (k & GiSyncPass.Scaled) != 0 ? reader.Float() : 1f;
            }

            if (!ReadSplats(reader, record)) return false;

            var hashes = reader.Count(GiTables.MaxLayers);
            record.Hashes = new ulong[hashes];
            for (var i = 0; i < hashes; i++) record.Hashes[i] = reader.Fixed64();
            return !reader.Failed;
        }

        public static bool ReadSplats(GiByteReader reader, GiTickRecord record)
        {
            var splatTotal = 0;
            for (var k = 0; k < GiTickRecord.SplatKeys; k++)
            {
                record.SplatStart[k] = splatTotal;
                record.SplatCount[k] = reader.Count(Limit);
                splatTotal += record.SplatCount[k];
                if (splatTotal > Limit) return false;
            }

            if (reader.Failed) return false;

            record.SizeSplats(splatTotal);
            for (var k = 0; k < GiTickRecord.SplatKeys; k++)
            for (var i = 0; i < record.SplatCount[k]; i++)
            {
                var row = record.SplatStart[k] + i;
                record.SplatXs[row] = reader.Float();
                record.SplatYs[row] = reader.Float();
                record.SplatStamps[row] = reader.Byte();
                record.SplatGains[row] = reader.SByte();
            }

            return !reader.Failed;
        }

        public static void WriteKeyframe(GiByteWriter writer, GiKeyframe keyframe)
        {
            writer.Varint((ulong)keyframe.Tick);
            writer.Varint((ulong)keyframe.Sources.Length);
            foreach (var source in keyframe.Sources)
            {
                writer.Varint(source.Id.Slot);
                writer.Varint(source.Id.Generation);
                writer.Byte(source.LayerKey);
                writer.Byte(source.StampKey);
                writer.SByte(source.Gain);
                writer.Float(source.X);
                writer.Float(source.Y);
                writer.Float(source.Heading);
                writer.Float(source.Scale);
                writer.SByte(source.FadeFrom);
                writer.SByte(source.FadeTo);
                writer.Int(source.FadeTicks);
                writer.Int(source.FadeTicksLeft);
                writer.Int(source.ExpireTicksLeft);
            }

            WriteSplats(writer, keyframe.Splats);
            writer.Varint((ulong)keyframe.Hashes.Length);
            foreach (var hash in keyframe.Hashes) writer.Fixed64(hash);
        }

        public static bool ReadKeyframe(GiByteReader reader, out GiKeyframe keyframe)
        {
            keyframe = new GiKeyframe { Tick = (int)reader.Varint() };
            var count = reader.Count(Limit);
            if (reader.Failed) return false;

            keyframe.Sources = new GiSourceState[count];
            for (var i = 0; i < count; i++)
            {
                var slot = (uint)reader.Varint();
                var generation = (uint)reader.Varint();
                keyframe.Sources[i] = new GiSourceState
                {
                    Id = new GiWorld.SourceId(slot, generation),
                    LayerKey = reader.Byte(),
                    StampKey = reader.Byte(),
                    Gain = reader.SByte(),
                    X = reader.Float(),
                    Y = reader.Float(),
                    Heading = reader.Float(),
                    Scale = reader.Float(),
                    FadeFrom = reader.SByte(),
                    FadeTo = reader.SByte(),
                    FadeTicks = reader.Int(),
                    FadeTicksLeft = reader.Int(),
                    ExpireTicksLeft = reader.Int(),
                };
            }

            if (!ReadSplats(reader, keyframe.Splats)) return false;

            var hashes = reader.Count(GiTables.MaxLayers);
            keyframe.Hashes = new ulong[hashes];
            for (var i = 0; i < hashes; i++) keyframe.Hashes[i] = reader.Fixed64();
            return !reader.Failed;
        }

        public static void WriteProfile(GiByteWriter writer, GiProfileSpec profile)
        {
            writer.Byte((byte)profile.Plane.Axes);
            writer.Float(profile.Plane.OriginX);
            writer.Float(profile.Plane.OriginY);
            writer.Varint((ulong)profile.Grids.Length);
            foreach (var grid in profile.Grids)
            {
                writer.Int(grid.Power);
                writer.Float(grid.X);
                writer.Float(grid.Y);
                writer.Float(grid.Size);
            }

            writer.Varint((ulong)profile.Layers.Length);
            foreach (var layer in profile.Layers)
            {
                writer.Byte((byte)layer.Kind);
                writer.Byte(layer.PhaseBits);
                writer.Byte(layer.TurnBits);
                writer.Byte(layer.First);
                writer.Byte(layer.Second);
                writer.Int(layer.FirstWeight);
                writer.Int(layer.SecondWeight);
                writer.Int(layer.Shift);
                writer.Int(layer.MaskLow);
                writer.Int(layer.MaskHigh);
            }

            writer.Varint((ulong)profile.Stamps.Length);
            foreach (var stamp in profile.Stamps)
            {
                writer.Byte((byte)stamp.Shape);
                writer.Varint(stamp.Width);
                writer.Varint(stamp.Height);
                writer.Varint(stamp.Radius);
                writer.Varint(stamp.Arc);
                writer.SByte(stamp.Value);
                writer.Int(stamp.CellOffset);
                writer.Int(stamp.CellCount);
            }

            writer.Varint((ulong)profile.Cells.Length);
            foreach (var cell in profile.Cells) writer.SByte(cell);
        }

        public static bool ReadProfile(GiByteReader reader, out GiProfileSpec profile)
        {
            profile = new GiProfileSpec();
            profile.Plane = new GiPlaneSpec { Axes = (GiPlaneAxes)reader.Byte(), OriginX = reader.Float(), OriginY = reader.Float() };
            var grids = reader.Count(GiTables.MaxGrids);
            if (reader.Failed) return false;

            profile.Grids = new GiGridSpec[grids];
            for (var i = 0; i < grids; i++)
                profile.Grids[i] = new GiGridSpec { Power = reader.Int(), X = reader.Float(), Y = reader.Float(), Size = reader.Float() };

            var layers = reader.Count(GiTables.MaxLayers);
            if (reader.Failed) return false;

            profile.Layers = new GiLayerSpec[layers];
            for (var i = 0; i < layers; i++)
                profile.Layers[i] = new GiLayerSpec
                {
                    Kind = (GiLayerKind)reader.Byte(),
                    PhaseBits = reader.Byte(),
                    TurnBits = reader.Byte(),
                    First = reader.Byte(),
                    Second = reader.Byte(),
                    FirstWeight = reader.Int(),
                    SecondWeight = reader.Int(),
                    Shift = reader.Int(),
                    MaskLow = (short)reader.Int(),
                    MaskHigh = (short)reader.Int(),
                };

            var stamps = reader.Count(GiTables.MaxStamps);
            if (reader.Failed) return false;

            profile.Stamps = new GiStampSpec[stamps];
            for (var i = 0; i < stamps; i++)
                profile.Stamps[i] = new GiStampSpec
                {
                    Shape = (GiStampShape)reader.Byte(),
                    Width = (ushort)reader.Varint(),
                    Height = (ushort)reader.Varint(),
                    Radius = (ushort)reader.Varint(),
                    Arc = (ushort)reader.Varint(),
                    Value = reader.SByte(),
                    CellOffset = reader.Int(),
                    CellCount = reader.Int(),
                };

            var cells = reader.Count(Limit);
            if (reader.Failed) return false;

            profile.Cells = new sbyte[cells];
            for (var i = 0; i < cells; i++) profile.Cells[i] = reader.SByte();
            return !reader.Failed;
        }

        public static byte[] Encode(GiRecordingData data)
        {
            var writer = new GiByteWriter(1 << 12);
            foreach (var b in Magic) writer.Byte(b);
            writer.Varint(Version);
            WriteProfile(writer, data.Profile);
            writer.Varint((ulong)data.Keyframes.Count);
            foreach (var keyframe in data.Keyframes) WriteKeyframe(writer, keyframe);
            writer.Varint((ulong)data.Ticks.Count);
            foreach (var tick in data.Ticks) WriteTick(writer, tick);
            var result = new byte[writer.Length];
            Array.Copy(writer.Buffer, result, writer.Length);
            return result;
        }

        public static bool TryDecode(byte[] bytes, out GiRecordingData data)
        {
            data = null;
            var reader = new GiByteReader(bytes, 0, bytes.Length);
            foreach (var b in Magic)
                if (reader.Byte() != b)
                    return false;

            if (reader.Varint() != Version || reader.Failed) return false;

            var result = new GiRecordingData();
            if (!ReadProfile(reader, out result.Profile)) return false;

            var keyframes = reader.Count(Limit);
            if (reader.Failed) return false;

            for (var i = 0; i < keyframes; i++)
            {
                if (!ReadKeyframe(reader, out var keyframe)) return false;

                result.Keyframes.Add(keyframe);
            }

            var ticks = reader.Count(Limit);
            if (reader.Failed) return false;

            for (var i = 0; i < ticks; i++)
            {
                var record = new GiTickRecord();
                if (!ReadTick(reader, record)) return false;

                result.Ticks.Add(record);
            }

            if (!reader.AtEnd) return false;

            data = result;
            return true;
        }
    }
}
