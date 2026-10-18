using Gi.Entities.Editor;
using Gi.Entities.Presentation;
using Gi.Entities.Tests;
using NUnit.Framework;
using Unity.Entities;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiLockstepTests
    {
        private int _check;

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
            _check = GiNetConfig.Check.Data;
        }

        [TearDown]
        public void TearDown()
        {
            GiNetConfig.Check.Data = _check;
        }

        [Test]
        public void TheSystemRecordsOneHashPerTickThatEqualsAFullRecomputeAndStopsWhenSwitchedOff()
        {
            using var fixture = new GiEcsFixture("LockstepSystem");
            fixture.SetStandardProfile();
            for (var i = 0; i < 20; i++) fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i * 2f, 12f + i * 2f);
            fixture.Frame1();
            Assert.That(GiLockstepGate.Find(fixture.Frame.World), Is.Null);

            GiNetConfig.Check.Data = 5;
            for (var i = 0; i < 12; i++) fixture.Frame1();
            fixture.Complete();
            var frame = fixture.Frame;
            var track = GiLockstepGate.Find(frame.World);
            Assert.That(track, Is.Not.Null);
            Assert.That(track.Lockstep.Latest, Is.EqualTo(frame.Tick));
            var grids = new byte[frame.Tables.GridCount];
            for (var g = 0; g < grids.Length; g++) frame.Tables.TryGrid(g, out grids[g]);
            for (var l = 0; l < frame.Tables.LayerCount; l++)
            {
                frame.Tables.TryLayer(new GiLayerKey((byte)l), out var layer);
                Assert.That(track.Lockstep.TryGet(frame.Tick, l, out var recorded), Is.True);
                Assert.That(recorded, Is.EqualTo(GiTileHashes.Full(frame.World, grids, layer)));
            }

            GiNetConfig.Check.Data = 0;
            fixture.Frame1();
            Assert.That(GiLockstepGate.Find(frame.World), Is.Null);
        }

        [Test]
        public void ARaisedDesyncShowsInTheRuntimePanel()
        {
            using var fixture = new GiEcsFixture("LockstepPanel");
            fixture.SetStandardProfile();
            fixture.AddInspector();
            fixture.Frame1();
            fixture.Frame1();
            var info = GiInspectorBoard.Find("LockstepPanel");
            var facts = GiRuntimeFacts.Collect(info).ToDictionaryByKey();
            Assert.That(facts["Faults"], Is.EqualTo("none"));
            Assert.That(facts["Desync"], Is.EqualTo("none"));

            fixture.Complete();
            var frame = fixture.Frame;
            frame.Faults.Raise(GiFaultKind.Desync, 42);
            frame.Faults.Raise(GiFaultKind.Overlap, 7);
            fixture.Manager.SetComponentData(fixture.FrameEntity, frame);
            fixture.Frame1();
            info = GiInspectorBoard.Find("LockstepPanel");
            facts = GiRuntimeFacts.Collect(info).ToDictionaryByKey();
            Assert.That(facts["Desync"], Is.EqualTo("first at tick 42"));
            Assert.That(facts["Faults"], Is.EqualTo("Overlap, Desync"));
            Assert.That(GiRuntimeFacts.Faults(0), Is.EqualTo("none"));
            Assert.That(GiRuntimeFacts.Faults((ushort)GiFaultKind.Profile), Is.EqualTo("Profile"));
        }
    }

    internal static class FactsExtensions
    {
        public static System.Collections.Generic.Dictionary<string, string> ToDictionaryByKey(this System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> rows)
        {
            var result = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var row in rows) result[row.Key] = row.Value;
            return result;
        }
    }
}
