using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class RecorderTests
{

    private static readonly int[] DiffLayers = new[] { 0, 3, 4 };
    private sealed class DeflateCodec : IGiCodec
    {
        public int Bound(int length) => length + length / 8 + 64;

        public int Compress(byte[] source, int length, byte[] destination)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(source, 0, length);
            var bytes = output.ToArray();
            if (bytes.Length > destination.Length) return -1;

            Array.Copy(bytes, destination, bytes.Length);
            return bytes.Length;
        }

        public bool Decompress(byte[] source, int length, byte[] destination, int rawLength)
        {
            using var input = new MemoryStream(source, 0, length);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < rawLength)
            {
                var got = deflate.Read(destination, read, rawLength - read);
                if (got <= 0) return false;

                read += got;
            }

            return true;
        }
    }

    private static readonly DeflateCodec Deflate = new();

    private static GiScenarioProfile WithoutRaster()
    {
        var standard = GiScenarioProfile.Standard();
        var stamps = new List<GiScenarioStamp>();
        foreach (var stamp in standard.Stamps)
            if (stamp.Shape != GiScenarioShape.Raster)
                stamps.Add(stamp);
        return new GiScenarioProfile(standard.Grids, standard.Layers, stamps.ToArray());
    }

    private static GiTickRecord RandomRecord(GiRandom rng, int tick)
    {
        var record = new GiTickRecord { Tick = tick };
        var commands = rng.Next(12);
        record.SizeCommands(commands);
        for (var i = 0; i < commands; i++)
        {
            record.Commands[i] = new GiCommand
            {
                Kind = (GiCommandKind)rng.Next(5),
                LayerKey = new GiLayerKey((byte)rng.Next(7)),
                StampKey = new GiStampKey((byte)rng.Next(6)),
                Gain = (sbyte)(rng.Next(33) - 16),
                TargetGain = (sbyte)(rng.Next(33) - 16),
                Ticks = (ushort)rng.Next(40),
                Target = rng.Chance(1, 3) ? rng.Next(Math.Max(1, commands)) : GiCommand.Existing,
                Owner = GiCommand.NoOwner,
                Source = new GiWorld.SourceId((uint)rng.Next(5000), (uint)(1 + rng.Next(9))),
                X = rng.Next(4000) * 0.25f - 100f,
                Y = rng.Next(4000) * 0.25f - 100f,
                Heading = rng.Next(100) * 0.0625f,
                Scale = 0.25f + rng.Next(16) * 0.25f,
            };
            record.Results[i] = new GiCommandResult
            {
                Code = (GiFaultCode)rng.Next(10),
                Source = new GiWorld.SourceId((uint)rng.Next(5000), (uint)rng.Next(9)),
            };
        }

        var total = 0;
        for (var k = 0; k < GiTickRecord.SyncCategories; k++)
        {
            record.SyncStart[k] = total;
            record.SyncCount[k] = rng.Next(6);
            total += record.SyncCount[k];
        }

        record.SizeSync(total);
        for (var k = 0; k < GiTickRecord.SyncCategories; k++)
        for (var i = 0; i < record.SyncCount[k]; i++)
        {
            var row = record.SyncStart[k] + i;
            record.SyncSources[row] = new GiWorld.SourceId((uint)rng.Next(5000), (uint)(1 + rng.Next(9)));
            record.SyncXs[row] = rng.Next(4000) * 0.25f;
            record.SyncYs[row] = rng.Next(4000) * 0.25f;
            record.SyncHeadings[row] = (k & GiSyncPass.Oriented) != 0 ? rng.Next(100) * 0.0625f : 0f;
            record.SyncScales[row] = (k & GiSyncPass.Scaled) != 0 ? 0.25f + rng.Next(16) * 0.25f : 1f;
        }

        total = 0;
        for (var k = 0; k < GiTickRecord.SplatKeys; k++)
        {
            record.SplatStart[k] = total;
            record.SplatCount[k] = rng.Chance(1, 4) ? rng.Next(5) : 0;
            total += record.SplatCount[k];
        }

        record.SizeSplats(total);
        for (var i = 0; i < total; i++)
        {
            record.SplatXs[i] = rng.Next(4000) * 0.25f;
            record.SplatYs[i] = rng.Next(4000) * 0.25f;
            record.SplatStamps[i] = (byte)rng.Next(6);
            record.SplatGains[i] = (sbyte)(rng.Next(33) - 16);
        }

        record.Hashes = new ulong[rng.Next(8)];
        for (var i = 0; i < record.Hashes.Length; i++) record.Hashes[i] = ((ulong)rng.Next(1 << 30) << 34) ^ (ulong)rng.Next(1 << 30);
        return record;
    }

    private static void AssertSame(GiTickRecord expected, GiTickRecord actual)
    {
        Assert.Equal(expected.Tick, actual.Tick);
        Assert.Equal(expected.CommandCount, actual.CommandCount);
        for (var i = 0; i < expected.CommandCount; i++)
        {
            var a = expected.Commands[i];
            var b = actual.Commands[i];
            Assert.Equal((a.Kind, a.LayerKey, a.StampKey, a.Gain, a.TargetGain, a.Ticks, a.Target), (b.Kind, b.LayerKey, b.StampKey, b.Gain, b.TargetGain, b.Ticks, b.Target));
            Assert.Equal((a.Source, a.X, a.Y, a.Heading, a.Scale), (b.Source, b.X, b.Y, b.Heading, b.Scale));
            Assert.Equal((expected.Results[i].Code, expected.Results[i].Source), (actual.Results[i].Code, actual.Results[i].Source));
        }

        Assert.Equal(expected.SyncCount, actual.SyncCount);
        Assert.Equal(expected.SyncStart, actual.SyncStart);
        for (var i = 0; i < expected.SyncTotal; i++)
            Assert.Equal((expected.SyncSources[i], expected.SyncXs[i], expected.SyncYs[i], expected.SyncHeadings[i], expected.SyncScales[i]),
                (actual.SyncSources[i], actual.SyncXs[i], actual.SyncYs[i], actual.SyncHeadings[i], actual.SyncScales[i]));

        Assert.Equal(expected.SplatCount, actual.SplatCount);
        Assert.Equal(expected.SplatStart, actual.SplatStart);
        for (var i = 0; i < expected.SplatTotal; i++)
            Assert.Equal((expected.SplatXs[i], expected.SplatYs[i], expected.SplatStamps[i], expected.SplatGains[i]),
                (actual.SplatXs[i], actual.SplatYs[i], actual.SplatStamps[i], actual.SplatGains[i]));

        Assert.Equal(expected.Hashes, actual.Hashes);
    }

    [Fact]
    public void EveryTickRecordRoundTripsThroughTheStreamAndTheCodecOverTwoHundredSeeds()
    {
        var read = new GiTickRecord();
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var rng = new GiRandom(seed);
            var record = RandomRecord(rng, 1 + rng.Next(100000));
            var writer = new GiByteWriter(16);
            GiRecordingCodec.WriteTick(writer, record);
            Assert.True(GiRecordingCodec.ReadTick(new GiByteReader(writer.Buffer, 0, writer.Length), read));
            AssertSame(record, read);

            var again = new GiByteWriter(16);
            GiRecordingCodec.WriteTick(again, read);
            Assert.Equal(writer.Span.ToArray(), again.Span.ToArray());

            var packed = new byte[Deflate.Bound(writer.Length)];
            var length = Deflate.Compress(writer.Buffer, writer.Length, packed);
            Assert.True(length > 0);
            var raw = new byte[writer.Length];
            Assert.True(Deflate.Decompress(packed, length, raw, writer.Length));
            Assert.Equal(writer.Span.ToArray(), raw);
        }
    }

    [Fact]
    public void AWholeRecordingRoundTripsAndEveryTruncationOfItIsRejected()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var rng = new GiRandom(seed);
            var data = new GiRecordingData { Profile = GiScenarioProfile.Standard().ToSpec() };
            var keyframes = rng.Next(3);
            for (var k = 0; k < keyframes; k++)
            {
                var keyframe = new GiKeyframe { Tick = k * 10, Sources = new GiSourceState[rng.Next(6)], Hashes = new ulong[rng.Next(8)] };
                for (var i = 0; i < keyframe.Sources.Length; i++)
                    keyframe.Sources[i] = new GiSourceState
                    {
                        Id = new GiWorld.SourceId((uint)rng.Next(100), (uint)(1 + rng.Next(5))),
                        LayerKey = (byte)rng.Next(7),
                        StampKey = (byte)rng.Next(6),
                        Gain = (sbyte)(rng.Next(33) - 16),
                        X = rng.Next(400) * 0.25f,
                        Y = rng.Next(400) * 0.25f,
                        Heading = rng.Next(40) * 0.0625f,
                        Scale = 0.25f + rng.Next(8) * 0.25f,
                        FadeFrom = (sbyte)rng.Next(8),
                        FadeTo = (sbyte)rng.Next(8),
                        FadeTicks = rng.Next(9),
                        FadeTicksLeft = rng.Next(9),
                        ExpireTicksLeft = rng.Next(9),
                    };
                for (var i = 0; i < keyframe.Hashes.Length; i++) keyframe.Hashes[i] = (ulong)rng.Next(1 << 30) * 4099ul;
                data.Keyframes.Add(keyframe);
            }

            var ticks = rng.Next(6);
            for (var t = 0; t < ticks; t++) data.Ticks.Add(RandomRecord(rng, t + 1));

            var bytes = GiRecordingCodec.Encode(data);
            Assert.True(GiRecordingCodec.TryDecode(bytes, out var decoded));
            Assert.Equal(data.Profile.Hash, decoded.Profile.Hash);
            Assert.Equal(bytes, GiRecordingCodec.Encode(decoded));
            Assert.Equal(data.Keyframes.Count, decoded.Keyframes.Count);
            for (var k = 0; k < data.Keyframes.Count; k++)
            {
                Assert.Equal(data.Keyframes[k].Tick, decoded.Keyframes[k].Tick);
                Assert.Equal(data.Keyframes[k].Hashes, decoded.Keyframes[k].Hashes);
                Assert.Equal(data.Keyframes[k].Sources, decoded.Keyframes[k].Sources);
            }

            for (var t = 0; t < data.Ticks.Count; t++) AssertSame(data.Ticks[t], decoded.Ticks[t]);

            var cut = rng.Next(bytes.Length);
            var truncated = new byte[cut];
            Array.Copy(bytes, truncated, cut);
            Assert.False(GiRecordingCodec.TryDecode(truncated, out _));
            var extra = new byte[bytes.Length + 1];
            Array.Copy(bytes, extra, bytes.Length);
            Assert.False(GiRecordingCodec.TryDecode(extra, out _));
        }

        Assert.False(GiRecordingCodec.TryDecode(new byte[] { 1, 2, 3, 4, 5 }, out _));
    }

    private static GiScenario Crowd(ulong seed, int ticks, GiScenarioProfile profile = null)
    {
        return GiScenarioGenerator.Create(seed, new GiScenarioOptions(ticks, 90, 220, 6, true, true, true, true, 4), profile ?? GiScenarioProfile.Standard());
    }

    [Fact]
    public void TheIncrementalHashEqualsAFullRecomputeAtEveryTick()
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var scenario = Crowd(seed, 80);
            using var session = new GiColumnsSession(scenario);
            var recorder = session.Record(100, 20, new GiStoredCodec());
            var grids = session.Setup.Grids;
            for (var t = 0; t < scenario.Ticks.Length; t++)
            {
                session.Step(t);
                for (var l = 0; l < session.Setup.Layers.Length; l++)
                    Assert.Equal(GiTileHashes.Full(session.Setup.World, grids, session.Setup.Layers[l]), recorder.Hashes.Layer(l));
            }
        }
    }

    [Fact]
    public void ReplayingAWholeRunReproducesEveryLiveHash()
    {
        var fades = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var scenario = Crowd(seed, 160);
            using var session = new GiColumnsSession(scenario);
            var recorder = session.Record(400, 40, Deflate);
            var live = new ulong[scenario.Ticks.Length][];
            for (var t = 0; t < scenario.Ticks.Length; t++) live[t] = session.Step(t);

            Assert.True(recorder.TryWindow(out var first, out var last));
            Assert.Equal(0, first);
            Assert.Equal(scenario.Ticks.Length, last);
            Assert.True(recorder.TryPlan(first, last, out var keyframe, out var ticks));
            Assert.True(GiReplay.TryCreate(recorder.Profile, out var replay, out _));
            using (replay)
            {
                Assert.True(replay.Restore(keyframe));
                foreach (var tick in ticks)
                {
                    Assert.True(replay.Step(tick), "seed " + seed + " tick " + tick.Tick);
                    for (var l = 0; l < session.Setup.Layers.Length; l++)
                    {
                        replay.Tables.TryLayer(new GiLayerKey((byte)l), out var id);
                        Assert.Equal(live[tick.Tick - 1][l], GiFieldHash.OfLayer(replay.World, id));
                    }
                }

                Assert.Equal(GiReplayStatus.Matching, replay.Status);
                Assert.Equal(0, replay.CommandMismatches);
            }

            foreach (var blob in recorder.Export().Keyframes)
                foreach (var source in blob.Sources)
                    if (source.Fading)
                        fades++;
        }

        Assert.True(fades > 0);
    }

    [Fact]
    public void ARollingRingKeepsAReplayableWindowWhoseEveryTickReplaysExactly()
    {
        var restoredFading = 0;
        for (ulong seed = 11; seed <= 16; seed++)
        {
            var scenario = Crowd(seed, 300);
            using var session = new GiColumnsSession(scenario);
            var recorder = session.Record(40, 13, Deflate);
            var live = new ulong[scenario.Ticks.Length][];
            for (var t = 0; t < scenario.Ticks.Length; t++)
            {
                live[t] = session.Step(t);
                if (t >= 60) Assert.True(recorder.Count <= 40);
            }

            Assert.True(recorder.TryWindow(out var first, out var last));
            Assert.Equal(300, last);
            Assert.True(first >= 300 - 40 - 1);
            Assert.True(recorder.KeyframeCount >= 1 && recorder.KeyframeCount <= 4);
            var rng = new GiRandom(seed);
            for (var probe = 0; probe < 8; probe++)
            {
                var target = first + 1 + rng.Next(last - first);
                Assert.True(recorder.TryPlan(target, target, out var keyframe, out var ticks));
                Assert.True(keyframe.Tick <= target);
                Assert.True(GiReplay.TryCreate(recorder.Profile, out var replay, out _));
                using (replay)
                {
                    Assert.True(replay.Restore(keyframe));
                    foreach (var source in keyframe.Sources)
                        if (source.Fading)
                            restoredFading++;
                    foreach (var tick in ticks) Assert.True(replay.Step(tick));
                    var end = ticks.Count == 0 ? keyframe.Tick : ticks[^1].Tick;
                    Assert.Equal(target, end);
                    for (var l = 0; l < session.Setup.Layers.Length; l++)
                    {
                        replay.Tables.TryLayer(new GiLayerKey((byte)l), out var id);
                        Assert.Equal(live[end - 1][l], GiFieldHash.OfLayer(replay.World, id));
                    }
                }
            }
        }

        Assert.True(restoredFading > 5, "keyframes with a fade in flight: " + restoredFading);
    }

    [Fact]
    public void TheRingIsBoundedAndTheCompressionShrinksIt()
    {
        var scenario = Crowd(3, 200);
        using var stored = new GiColumnsSession(scenario);
        var storedRecorder = stored.Record(50, 10, new GiStoredCodec());
        using var packed = new GiColumnsSession(scenario);
        var packedRecorder = packed.Record(50, 10, Deflate);
        for (var t = 0; t < scenario.Ticks.Length; t++)
        {
            stored.Step(t);
            packed.Step(t);
        }

        Assert.Equal(50, storedRecorder.Count);
        Assert.Equal(50, packedRecorder.Count);
        Assert.True(packedRecorder.Bytes < storedRecorder.Bytes);
        Assert.Equal(storedRecorder.Export().Ticks.Count, packedRecorder.Export().Ticks.Count);
        var left = storedRecorder.Export();
        var right = packedRecorder.Export();
        Assert.Equal(GiRecordingCodec.Encode(left), GiRecordingCodec.Encode(right));
    }

    [Fact]
    public void ADiffNamesTheTilesTheLiveWorldChangedBehindTheRecorder()
    {
        var scenario = Crowd(5, 90);
        using var session = new GiColumnsSession(scenario);
        var recorder = session.Record(200, 25, new GiStoredCodec());
        for (var t = 0; t < 60; t++) session.Step(t);

        var layer = session.Setup.Layers[0];
        var stamp = session.Setup.Stamps[0];
        GiWorld.Place(session.Setup.World, layer, 40.5f, 24.5f, stamp, 9);
        for (var t = 60; t < 90; t++) session.Step(t);

        Assert.True(recorder.TryPlan(0, 90, out var keyframe, out var ticks));
        Assert.True(GiReplay.TryCreate(recorder.Profile, out var replay, out _));
        using (replay)
        {
            replay.Restore(keyframe);
            foreach (var tick in ticks) replay.Step(tick);
            Assert.Equal(GiReplayStatus.Diverged, replay.Status);

            var diff = new List<GiTileDiff>();
            replay.Diff(recorder.Hashes, diff);
            Assert.NotEmpty(diff);
            Assert.All(diff, d => Assert.Contains(d.Layer, DiffLayers));
            var cellX = 40;
            var cellY = 24;
            Assert.Contains(diff, d => d.Grid == 0 && d.Layer == 0 && d.Tile == cellY / 32 * recorder.Hashes.TilesPerSide(0) + cellX / 32);
            Assert.True(diff.Count < 3 * 4 * recorder.Hashes.GridCount);
        }

        using var clean = new GiColumnsSession(scenario);
        var cleanRecorder = clean.Record(200, 25, new GiStoredCodec());
        for (var t = 0; t < 90; t++) clean.Step(t);
        Assert.True(cleanRecorder.TryPlan(0, 90, out var cleanKeyframe, out var cleanTicks));
        Assert.True(GiReplay.TryCreate(cleanRecorder.Profile, out var cleanReplay, out _));
        using (cleanReplay)
        {
            cleanReplay.Restore(cleanKeyframe);
            foreach (var tick in cleanTicks) cleanReplay.Step(tick);
            var none = new List<GiTileDiff>();
            cleanReplay.Diff(cleanRecorder.Hashes, none);
            Assert.Empty(none);
            Assert.Equal(GiReplayStatus.Matching, cleanReplay.Status);
        }
    }

    [Fact]
    public void ARecorderStartedMidRunFlagsAnIncompleteKeyframe()
    {
        var scenario = Crowd(7, 80);
        using var session = new GiColumnsSession(scenario);
        for (var t = 0; t < 40; t++) session.Step(t);

        var recorder = session.Record(100, 20, new GiStoredCodec());
        for (var t = 40; t < 80; t++) session.Step(t);

        Assert.True(recorder.TryPlan(40, 80, out var keyframe, out var ticks));
        Assert.True(GiReplay.TryCreate(recorder.Profile, out var replay, out _));
        using (replay)
        {
            Assert.False(replay.Restore(keyframe));
            Assert.Equal(GiReplayStatus.KeyframeDiffers, replay.Status);
            Assert.NotEmpty(ticks);
        }
    }

    [Fact]
    public void TenThousandTicksReplayToEveryLiveHash()
    {
        var scenario = GiScenarioGenerator.Create(42, new GiScenarioOptions(10000, 140, 260, 6, true, true, true, true, 6));
        using var session = new GiColumnsSession(scenario);
        var recorder = session.Record(10000, 500, Deflate);
        var sampled = new Dictionary<int, ulong[]>();
        for (var t = 0; t < scenario.Ticks.Length; t++)
        {
            var hashes = session.Step(t);
            if (t % 997 == 0 || t == scenario.Ticks.Length - 1) sampled[t + 1] = hashes;
        }

        Assert.Equal(10000, recorder.Count);
        var keyframes = recorder.Export().Keyframes;
        Assert.True(keyframes[^1].Sources.Length > 60);
        Assert.True(recorder.TryPlan(0, 10000, out var keyframe, out var ticks));
        Assert.Equal(0, keyframe.Tick);
        Assert.Equal(10000, ticks.Count);
        Assert.True(GiReplay.TryCreate(recorder.Profile, out var replay, out _));
        using (replay)
        {
            Assert.True(replay.Restore(keyframe));
            foreach (var tick in ticks)
            {
                Assert.True(replay.Step(tick), "tick " + tick.Tick);
                if (!sampled.TryGetValue(tick.Tick, out var expected)) continue;

                for (var l = 0; l < expected.Length; l++)
                {
                    replay.Tables.TryLayer(new GiLayerKey((byte)l), out var id);
                    Assert.Equal(expected[l], GiFieldHash.OfLayer(replay.World, id));
                }
            }

            Assert.Equal(GiReplayStatus.Matching, replay.Status);
        }
    }

    [Fact]
    public void AnExportedRangeIsDeterministicAndRefusesWhatTheStreamFormatCannotHold()
    {
        var profile = WithoutRaster();
        var scenario = Crowd(9, 60, profile);
        using var session = new GiColumnsSession(scenario);
        var recorder = session.Record(100, 20, new GiStoredCodec());
        for (var t = 0; t < 60; t++) session.Step(t);

        Assert.True(recorder.TryPlan(0, 60, out var keyframe, out var ticks));
        Assert.Equal(GiExportStatus.Exported, GiStreamExport.Write("Crowd_0_60", recorder.Profile, keyframe, ticks, out var first));
        Assert.Equal(GiExportStatus.Exported, GiStreamExport.Write("Crowd_0_60", recorder.Profile, keyframe, ticks, out var second));
        Assert.Equal(first, second);
        Assert.Contains("public class Recorded_Crowd_0_60", first);
        Assert.Contains("Assert.Null(StreamRunner.TryRun(setup, setup.Commands));", first);
        Assert.Contains("StreamOp.Process", first);
        Assert.DoesNotContain("\r", first);

        Assert.Equal(GiExportStatus.EmptyRange, GiStreamExport.Write("x", recorder.Profile, keyframe, new List<GiTickRecord>(), out _));
        var raster = GiScenarioProfile.Standard().ToSpec();
        Assert.Equal(GiExportStatus.RasterStamp, GiStreamExport.Write("x", raster, keyframe, ticks, out _));
        var single = recorder.Profile;
        var layers = (GiLayerSpec[])single.Layers.Clone();
        layers[3] = new GiLayerSpec { Kind = GiLayerKind.Sum, First = 0, Second = GiLayerSpec.NoLayer, FirstWeight = 1 };
        var altered = new GiProfileSpec { Plane = single.Plane, Grids = single.Grids, Layers = layers, Stamps = single.Stamps, Cells = single.Cells };
        Assert.Equal(GiExportStatus.SingleInputLayer, GiStreamExport.Write("x", altered, keyframe, ticks, out _));
        Assert.Equal("Recorded_a_b_1", GiStreamExport.Identifier("a-b 1"));
    }

    [Fact]
    public void ARangeIsWrittenToTheDirectoryNamedByTheEnvironmentWhenOneIsGiven()
    {
        var directory = Environment.GetEnvironmentVariable("GI_EXPORT_DIR");
        if (string.IsNullOrEmpty(directory)) return;

        var scenario = Crowd(21, 220, WithoutRaster());
        using var session = new GiColumnsSession(scenario);
        var recorder = session.Record(300, 37, Deflate);
        for (var t = 0; t < scenario.Ticks.Length; t++) session.Step(t);

        Assert.True(recorder.TryPlan(100, 190, out var keyframe, out var ticks));
        Assert.True(keyframe.Sources.Length > 10);
        Assert.Equal(GiExportStatus.Exported, GiStreamExport.Write("Crowd_" + keyframe.Tick + "_" + ticks[^1].Tick, recorder.Profile, keyframe, ticks, out var text));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, GiStreamExport.Identifier("Crowd_" + keyframe.Tick + "_" + ticks[^1].Tick) + ".cs"), text);
    }
}
