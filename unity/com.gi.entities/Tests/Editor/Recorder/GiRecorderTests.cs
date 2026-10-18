using System.Collections.Generic;
using System.IO;
using Gi.Entities.Presentation;
using Gi.Entities.Tests;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiRecorderTests
    {
        private bool _record;
        private int _ticks;
        private int _keyframe;

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
            _record = GiDebugConfig.Record.Data;
            _ticks = GiDebugConfig.RecordTicks.Data;
            _keyframe = GiDebugConfig.RecordKeyframe.Data;
        }

        [TearDown]
        public void TearDown()
        {
            GiDebugConfig.Record.Data = _record;
            GiDebugConfig.RecordTicks.Data = _ticks;
            GiDebugConfig.RecordKeyframe.Data = _keyframe;
        }

        private static GiEcsFixture Fixture(string name, int emitters = 30)
        {
            var fixture = new GiEcsFixture(name);
            fixture.SetStandardProfile();
            fixture.AddInspector();
            for (var i = 0; i < emitters; i++)
                fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), (sbyte)(3 + i % 5), 8f + i * 7 % 48, 8f + i * 11 % 48);
            return fixture;
        }

        private static GiRecorder Recorder(GiEcsFixture fixture)
        {
            fixture.Complete();
            return GiRecorderGate.Find(fixture.Frame.World);
        }

        [Test]
        public void TheConfigVarStartsAndStopsRecordingAndTheRecordingIsKept()
        {
            using var fixture = Fixture("RecordSwitch");
            fixture.Frame1();
            Assert.That(GiRecorderGate.Enabled, Is.False);
            Assert.That(Recorder(fixture), Is.Null);

            GiDebugConfig.RecordTicks.Data = 50;
            GiDebugConfig.RecordKeyframe.Data = 10;
            GiDebugConfig.Record.Data = true;
            for (var i = 0; i < 40; i++) fixture.Frame1();
            var world = fixture.Frame.World;
            Assert.That(GiRecorderGate.IsRecording(world), Is.True);
            var recorder = Recorder(fixture);
            Assert.That(recorder.Count, Is.EqualTo(39).Or.EqualTo(40));
            Assert.That(recorder.LiveSources, Is.EqualTo(30));

            GiDebugConfig.Record.Data = false;
            fixture.Frame1();
            Assert.That(GiRecorderGate.IsRecording(world), Is.False);
            Assert.That(GiRecorderGate.Find(world), Is.SameAs(recorder));
            Assert.That(GiRecorderGate.Enabled, Is.False);
            var kept = recorder.Count;
            for (var i = 0; i < 5; i++) fixture.Frame1();
            Assert.That(recorder.Count, Is.EqualTo(kept));
        }

        [Test]
        public void ReplayingTenThousandEcsTicksReproducesEveryLiveHash()
        {
            var scenario = GiScenarioGenerator.Create(77, new GiScenarioOptions(10000, 120, 240, 6, true, true, true, true, 5));
            GiDebugConfig.RecordTicks.Data = 10000;
            GiDebugConfig.RecordKeyframe.Data = 1000;
            GiDebugConfig.Record.Data = true;
            using var executor = new GiEcsExecutor(scenario, "RecordTenThousand");
            var live = new Dictionary<int, ulong[]>();
            for (var t = 0; t < scenario.Ticks.Length; t++)
            {
                var hashes = executor.Step(t);
                if (t % 499 == 0 || t == scenario.Ticks.Length - 1) live[t + 1] = hashes;
            }

            var world = executor.Fixture.Frame.World;
            var recorder = GiRecorderGate.Find(world);
            Assert.That(recorder, Is.Not.Null);
            Assert.That(recorder.Count, Is.EqualTo(10000));
            Assert.That(recorder.TryPlan(0, 10000, out var keyframe, out var ticks), Is.True);
            Assert.That(ticks.Count, Is.EqualTo(10000));
            Assert.That(GiReplay.TryCreate(recorder.Profile, out var replay, out _), Is.True);
            using (replay)
            {
                Assert.That(replay.Restore(keyframe), Is.True);
                foreach (var tick in ticks)
                {
                    Assert.That(replay.Step(tick), Is.True, "tick " + tick.Tick);
                    if (!live.TryGetValue(tick.Tick, out var expected)) continue;

                    for (var l = 0; l < expected.Length; l++)
                    {
                        replay.Tables.TryLayer(new GiLayerKey((byte)l), out var id);
                        Assert.That(GiFieldHash.OfLayer(replay.World, id), Is.EqualTo(expected[l]), "tick " + tick.Tick + " layer " + l);
                    }
                }

                Assert.That(replay.Status, Is.EqualTo(GiReplayStatus.Matching));
            }
        }

        [Test]
        public void TheModelScrubsComparesExportsAndSaves()
        {
            GiDebugConfig.RecordTicks.Data = 200;
            GiDebugConfig.RecordKeyframe.Data = 25;
            GiDebugConfig.Record.Data = true;
            using var fixture = Fixture("RecordModel", 40);
            for (var i = 0; i < 90; i++)
            {
                if (i % 7 == 0) fixture.CreateEmitter(0, 2, 5, 12f + i % 40, 14f + i % 30);
                fixture.Frame1();
            }

            using var model = new GiRecorderModel();
            model.Select("RecordModel");
            Assert.That(model.HasWorld && model.Recording, Is.True);
            Assert.That(model.TryStatus(out var ticks, out var first, out var last, out var bytes), Is.True);
            Assert.That(ticks, Is.GreaterThan(50));
            Assert.That(bytes, Is.GreaterThan(0));

            Assert.That(model.ReplayTo(first + 20), Is.True);
            Assert.That(model.ReplayTick, Is.EqualTo(first + 20));
            Assert.That(model.CompareWithLive(), Is.EqualTo(0));
            Assert.That(model.Replay.Status, Is.EqualTo(GiReplayStatus.Matching));

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(0), out var layer);
            frame.Tables.TryStamp(new GiStampKey(0), out var stamp);
            GiWorld.Place(frame.World, layer, 40.5f, 24.5f, stamp, 9);
            fixture.Frame1();
            fixture.Frame1();
            Assert.That(model.CompareWithLive(), Is.GreaterThan(0));
            Assert.That(model.Diff, Has.Some.Matches<GiTileDiff>(d => d.Grid == 0 && d.Layer == 0 && d.Tile == 1));
            var pixels = GiRecorderWindow.Render(model.Replay, 0, 0, model.Diff, out var side);
            Assert.That(side, Is.EqualTo(64));
            Assert.That(pixels.Length, Is.EqualTo(64 * 64));

            var folder = Path.Combine(Path.GetTempPath(), "gi-recorder-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                model.TryStatus(out _, out first, out last, out _);
                var status = model.Export(folder, first, first + 30, out var path);
                Assert.That(status, Is.EqualTo(GiExportStatus.RasterStamp));
                Assert.That(path, Is.Null);

                var saved = Path.Combine(folder, "recording.girec");
                Directory.CreateDirectory(folder);
                Assert.That(model.Save(saved), Is.True);
                Assert.That(GiRecorderModel.TryLoad(saved, out var data), Is.True);
                Assert.That(data.Ticks.Count, Is.EqualTo(ticks + 2).Or.EqualTo(ticks + 1).Or.EqualTo(ticks));
                Assert.That(GiRecorderModel.TryLoad(Path.Combine(folder, "missing.girec"), out _), Is.False);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void ARangeWithoutRasterStampsExportsAGiTest()
        {
            GiDebugConfig.RecordTicks.Data = 100;
            GiDebugConfig.RecordKeyframe.Data = 20;
            var scenario = GiScenarioGenerator.Create(5, new GiScenarioOptions(60, 40, 80, 4, true, true, true, true, 3), WithoutRaster());
            GiDebugConfig.Record.Data = true;
            using var executor = new GiEcsExecutor(scenario, "RecordExport");
            executor.Fixture.AddInspector();
            for (var t = 0; t < scenario.Ticks.Length; t++) executor.Step(t);
            executor.Fixture.Frame1();

            using var model = new GiRecorderModel();
            model.Select("RecordExport");
            Assert.That(model.TryStatus(out _, out var first, out var last, out _), Is.True);
            var folder = Path.Combine(Path.GetTempPath(), "gi-recorder-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Assert.That(model.Export(folder, first, last, out var path), Is.EqualTo(GiExportStatus.Exported));
                var text = File.ReadAllText(path);
                Assert.That(text, Does.Contain("StreamRunner.TryRun(setup, setup.Commands)"));
                Assert.That(text, Does.Not.Contain("\r"));
                Assert.That(Path.GetFileName(path), Does.StartWith("Recorded_Range_"));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static GiScenarioProfile WithoutRaster()
        {
            var standard = GiScenarioProfile.Standard();
            var stamps = new List<GiScenarioStamp>();
            foreach (var stamp in standard.Stamps)
                if (stamp.Shape != GiScenarioShape.Raster)
                    stamps.Add(stamp);
            return new GiScenarioProfile(standard.Grids, standard.Layers, stamps.ToArray());
        }

        [Test]
        public void WithRecordingOffTheSteadyTickAllocatesNothing()
        {
            using var fixture = Fixture("RecordOff", 100);
            for (var i = 0; i < 40; i++) fixture.Frame1();
            Assert.That(GiRecorderGate.Enabled, Is.False);
            Assert.That(GiAllocation.SteadyZero(() =>
            {
                for (var i = 0; i < 30; i++) fixture.Frame1();
            }, 14, 3), Is.True);
        }

        [Test]
        public void WithRecordingOnTheSteadyTickBetweenKeyframesAllocatesNothing()
        {
            using var fixture = Fixture("RecordOn", 100);
            GiDebugConfig.RecordTicks.Data = 100;
            GiDebugConfig.RecordKeyframe.Data = 90;
            GiDebugConfig.Record.Data = true;
            for (var i = 0; i < 260; i++) fixture.Frame1();
            Assert.That(GiRecorderGate.IsRecording(fixture.Frame.World), Is.True);
            Assert.That(GiAllocation.SteadyZero(() =>
            {
                for (var i = 0; i < 20; i++) fixture.Frame1();
            }, 14, 3), Is.True);
        }

        [Test]
        public void TheWindowBuildsItsInterfaceAndClosesCleanly()
        {
            using var fixture = Fixture("RecordWindow");
            GiDebugConfig.Record.Data = true;
            for (var i = 0; i < 10; i++) fixture.Frame1();

            var window = EditorWindow.CreateInstance<GiRecorderWindow>();
            try
            {
                window.CreateGUI();
                Assert.That(window.rootVisualElement.childCount, Is.GreaterThan(8));
                window.Tick();
                Assert.That(window.Model.HasWorld, Is.True);
                Assert.That(window.Model.ReplayTo(9), Is.True);
                window.Tick();
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
