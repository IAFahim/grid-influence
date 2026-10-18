using System.Collections.Generic;
using System.Linq;
using Gi.Entities.Presentation;
using Gi.Entities.Tests;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor.Tests
{
    public sealed unsafe class GiFieldTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static GiEcsFixture Fixture(string name, int emitters = 30)
        {
            var fixture = new GiEcsFixture(name);
            fixture.SetStandardProfile();
            fixture.AddInspector();
            for (var i = 0; i < emitters; i++)
                fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), (sbyte)(3 + i % 5), 8f + (i * 7) % 48, 8f + (i * 11) % 48);
            fixture.Frame1();
            fixture.Frame1();
            return fixture;
        }

        [Test]
        public void ThePickersFollowGiWorldsAsTheyAreCreatedAndDestroyed()
        {
            var changes = 0;
            void Count() => changes++;
            GiInspectorBoard.WorldsChanged += Count;
            try
            {
                Assert.That(GiInspectorBoard.Find("FieldA"), Is.Null);
                var a = Fixture("FieldA");
                Assert.That(GiInspectorBoard.Find("FieldA"), Is.Not.Null);
                Assert.That(GiInspectorBoard.Find("FieldA").Alive, Is.True);
                Assert.That(GiInspectorBoard.Find("FieldA").Grids.Length, Is.EqualTo(2));
                Assert.That(GiInspectorBoard.Find("FieldA").LayerCount, Is.EqualTo(7));

                var b = Fixture("FieldB");
                Assert.That(GiInspectorBoard.All.Select(world => world.EcsWorld), Does.Contain("FieldA").And.Contain("FieldB"));

                a.Dispose();
                Assert.That(GiInspectorBoard.Find("FieldA"), Is.Null);
                Assert.That(GiInspectorBoard.Find("FieldB"), Is.Not.Null);
                b.Dispose();
                Assert.That(GiInspectorBoard.Find("FieldB"), Is.Null);
                Assert.That(changes, Is.GreaterThanOrEqualTo(4));
            }
            finally
            {
                GiInspectorBoard.WorldsChanged -= Count;
            }
        }

        [Test]
        public void ProbeValuesEqualTheScalarCallsMadeOnTheMainThread()
        {
            using var fixture = Fixture("FieldProbe");
            using var model = new GiFieldModel();
            var rng = new GiRandom(5);
            for (var layerKey = 0; layerKey < 4; layerKey++)
            {
                model.Select("FieldProbe", 0, layerKey);
                model.Reach = 6f + layerKey * 3;
                model.Threshold = (short)(1 + layerKey);
                for (var probe = 0; probe < 12; probe++)
                {
                    var world = new Vector2(rng.Next(64) + rng.Next(8) * 0.125f, rng.Next(64) + rng.Next(8) * 0.125f);
                    model.Hover(world);
                    fixture.Frame1();
                    Assert.That(model.Poll(), Is.EqualTo(5));

                    var frame = fixture.Frame;
                    frame.Tables.TryLayer(new GiLayerKey((byte)layerKey), out var layer);
                    Assert.That(model.TryLatest(GiProbeKind.Value, out var value), Is.True);
                    Assert.That(value.Ok, Is.EqualTo(GiWorld.TrySense(frame.World, layer, world.x, world.y, out var expected)));
                    Assert.That(value.Value, Is.EqualTo(expected));
                    model.TryLatest(GiProbeKind.Gradient, out var gradient);
                    GiWorld.TrySenseGradient(frame.World, layer, world.x, world.y, out var gx, out var gy);
                    Assert.That((gradient.GradientX, gradient.GradientY), Is.EqualTo((gx, gy)));
                    model.TryLatest(GiProbeKind.Area, out var area);
                    GiWorld.TrySenseArea(frame.World, layer, world.x, world.y, model.Reach, out var total);
                    Assert.That(area.Total, Is.EqualTo(total));
                    model.TryLatest(GiProbeKind.Max, out var max);
                    GiWorld.TrySenseMax(frame.World, layer, world.x, world.y, model.Reach, out var best, out var bx, out var by);
                    Assert.That((max.Value, max.PositionX, max.PositionY), Is.EqualTo((best, bx, by)));
                    model.TryLatest(GiProbeKind.Nearest, out var nearest);
                    GiWorld.TrySenseNearest(frame.World, layer, world.x, world.y, model.Reach, model.Threshold, out var near, out var nx, out var ny);
                    Assert.That((nearest.Value, nearest.PositionX, nearest.PositionY), Is.EqualTo((near, nx, ny)));
                    Assert.That(value.Tick, Is.EqualTo(frame.Tick));
                }
            }
        }

        [Test]
        public void ClosingTheWindowModelFreesItsMirrorAndTexture()
        {
            Assume.That(SystemInfo.supportsComputeShaders, "needs compute shaders");
            using var fixture = Fixture("FieldMirror");
            var baseline = GiMirror.Live;
            var model = new GiFieldModel();
            model.Select("FieldMirror", 1, 0);
            fixture.Frame1();
            Assert.That(model.View.Mirror, Is.Not.Null);
            Assert.That(model.View.Texture, Is.Not.Null);
            Assert.That(GiMirror.Live, Is.EqualTo(baseline + 1));
            Assert.That(GiInspectorBoard.OpenViews, Is.EqualTo(1));

            var view = model.View;
            model.Dispose();
            Assert.That(GiMirror.Live, Is.EqualTo(baseline));
            Assert.That(GiInspectorBoard.OpenViews, Is.EqualTo(0));
            Assert.That(view.Disposed, Is.True);
            Assert.That(view.Texture == null, Is.True);
            fixture.Frame1();
            Assert.That(GiMirror.Live, Is.EqualTo(baseline));
        }

        [Test]
        public void DestroyingTheEcsWorldFreesTheMirrorsOfItsOpenViews()
        {
            Assume.That(SystemInfo.supportsComputeShaders, "needs compute shaders");
            var baseline = GiMirror.Live;
            var fixture = Fixture("FieldGone");
            var model = new GiFieldModel();
            model.Select("FieldGone", 0, 3);
            fixture.Frame1();
            Assert.That(GiMirror.Live, Is.EqualTo(baseline + 1));

            fixture.Dispose();
            Assert.That(GiMirror.Live, Is.EqualTo(baseline));
            Assert.That(GiInspectorBoard.OpenViews, Is.EqualTo(0));
            model.Dispose();
        }

        [Test]
        public void WithTheWindowOpenTheSteadyTickStillAllocatesNothing()
        {
            Assume.That(SystemInfo.supportsComputeShaders, "needs compute shaders");
            using var fixture = Fixture("FieldAlloc", 200);
            using var model = new GiFieldModel();
            model.Select("FieldAlloc", 1, 0);
            for (var i = 0; i < 60; i++) fixture.Frame1();
            Assert.That(model.View.Mirror, Is.Not.Null);

            Assert.That(GiAllocation.SteadyZero(() =>
            {
                for (var i = 0; i < 30; i++) fixture.Frame1();
            }, 14, 3), Is.True);
        }

        [Test]
        public void PinsCollectOneValuePerTickIntoTheirHistory()
        {
            using var fixture = Fixture("FieldPins");
            using var model = new GiFieldModel();
            model.Select("FieldPins", 0, 0);
            model.Hover(new Vector2(20f, 20f));
            var pin = model.Pin();
            Assert.That(pin, Is.Not.Null);

            for (var i = 0; i < 6; i++)
            {
                model.Poll();
                fixture.Frame1();
                model.Poll();
            }

            Assert.That(pin.History.Count, Is.GreaterThanOrEqualTo(5));
            for (var i = 1; i < pin.History.Count; i++) Assert.That(pin.History.TickAt(i), Is.GreaterThan(pin.History.TickAt(i - 1)));

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(0), out var layer);
            GiWorld.TrySense(frame.World, layer, 20f, 20f, out var expected);
            Assert.That(pin.History[pin.History.Count - 1], Is.EqualTo(expected));
            model.Unpin(pin);
            Assert.That(model.Pins, Is.Empty);
        }

        [Test]
        public void TheOverlaysReportLiveAndChangedTiles()
        {
            Assume.That(SystemInfo.supportsComputeShaders, "needs compute shaders");
            using var fixture = Fixture("FieldTiles");
            var mover = fixture.CreateEmitter(0, 2, 6, 40f, 40f);
            using var model = new GiFieldModel();
            model.Select("FieldTiles", 1, 0);
            for (var i = 0; i < 4; i++) fixture.Frame1();

            var mirror = model.View.Mirror;
            var live = 0;
            for (var tile = 0; tile < mirror.TileCount; tile++)
                if (mirror.IsLive(tile))
                    live++;
            Assert.That(live, Is.GreaterThan(0));
            Assert.That(live, Is.LessThanOrEqualTo(mirror.TileCount));

            fixture.Manager.SetComponentData(mover, Unity.Transforms.LocalTransform.FromPosition(10f, 0f, 12f));
            fixture.Frame1();
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryGrid(1, out var grid);
            frame.Tables.TryLayer(new GiLayerKey(0), out var layer);
            var count = GiWorld.ChangedTiles(frame.World, grid, layer, (int*)null);
            var tiles = new int[count];
            fixed (int* p = tiles) GiWorld.ChangedTiles(frame.World, grid, layer, p);
            Assert.That(count, Is.GreaterThan(0));
            var expected = new HashSet<int>(tiles);
            for (var tile = 0; tile < mirror.TileCount; tile++) Assert.That(mirror.WasChanged(tile), Is.EqualTo(expected.Contains(tile)), $"tile {tile}");
        }

        [Test]
        public void TheRuntimePanelReportsWhatTheCardNamesAndNaWithoutAWorld()
        {
            var none = GiRuntimeFacts.Collect(null).ToDictionary(row => row.Key, row => row.Value);
            foreach (var name in new[] { "Runtime", "Gi plugin", "Gi code path", "Gi version", "CPU", "Live Gi worlds", "Stamps used" })
                Assert.That(none.ContainsKey(name), Is.True, name);
            Assert.That(none["Store bytes"], Is.EqualTo(GiRuntimeFacts.NotAvailable));
            Assert.That(none["Stamps used"], Does.EndWith(" of 255"));
            Assert.That(none["Gi plugin"], Does.Contain("Gi "));

            using var fixture = Fixture("FieldFacts");
            var info = GiInspectorBoard.Find("FieldFacts");
            var facts = GiRuntimeFacts.Collect(info).ToDictionary(row => row.Key, row => row.Value);
            GiWorld.Memory(info.GiWorld, out var bytes, out var tiles, out _, out _, out var sources);
            Assert.That(facts["Store bytes"], Is.EqualTo(bytes.ToString()));
            Assert.That(facts["Live tiles"], Is.EqualTo(tiles.ToString()));
            Assert.That(facts["Live sources"], Is.EqualTo(sources.ToString()));
            Assert.That(sources, Is.EqualTo(30));
        }

        [Test]
        public void TheWindowBuildsItsInterfaceAndClosesCleanly()
        {
            var window = EditorWindow.CreateInstance<GiFieldWindow>();
            try
            {
                window.CreateGUI();
                Assert.That(window.rootVisualElement.childCount, Is.GreaterThan(5));
                window.Tick();
                Assert.That(window.Model.View, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }

            Assert.That(GiInspectorBoard.OpenViews, Is.EqualTo(0));
        }
    }
}
