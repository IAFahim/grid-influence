using Gi.Entities.Presentation;
using Gi.Entities.Tests;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiExplainTests
    {
        private static readonly byte[] IntegerStamps = { 0, 2, 3, 4 };

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [TearDown]
        public void TearDown()
        {
            Selection.activeObject = null;
        }

        private static GiEcsFixture Fixture(string name, int emitters, byte[] stamps)
        {
            var fixture = new GiEcsFixture(name);
            fixture.SetStandardProfile();
            fixture.AddInspector();
            var rng = new GiRandom(11);
            for (var i = 0; i < emitters; i++)
                fixture.CreateEmitter((byte)(i % 2), stamps[rng.Next(stamps.Length)], (sbyte)(1 + rng.Next(6)), 10f + rng.Next(44), 10f + rng.Next(44));
            fixture.Frame1();
            fixture.Frame1();
            return fixture;
        }

        private static GiExplainResult Explain(GiEcsFixture fixture, GiExplainModel model, string world, int layerKey, float x, float y)
        {
            model.Explain(world, layerKey, new Vector2(x, y));
            fixture.Frame1();
            Assert.That(model.Poll(), Is.EqualTo(1));
            return model.Latest;
        }

        [Test]
        public void ContributionsSumToTheValueAndLinkToTheirEntities()
        {
            using var fixture = Fixture("ExplainSum", 24, IntegerStamps);
            var model = new GiExplainModel();
            var linked = 0;
            var withSources = 0;
            var emitters = fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiEmitter>(), ComponentType.ReadOnly<LocalTransform>());
            using var entities = emitters.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (var probe = 0; probe < 24; probe++)
            {
                var anchor = fixture.Manager.GetComponentData<LocalTransform>(entities[probe % entities.Length]).Position;
                var layerKey = probe % 2;
                var result = Explain(fixture, model, "ExplainSum", layerKey, anchor.x + 0.5f, anchor.z + 0.5f);
                Assert.That(result.Valid, Is.True);
                Assert.That(result.Explanation.Saturated, Is.False);
                Assert.That(result.Explanation.Sum, Is.EqualTo((long)result.Explanation.Total));
                Assert.That(result.Explanation.Rest, Is.EqualTo(0L));
                Assert.That(result.Tick, Is.EqualTo(fixture.Frame.Tick));
                if (result.Explanation.Contributors > 0) withSources++;

                for (var i = 0; i < result.Contributions.Length; i++)
                {
                    var entity = result.Entities[i];
                    Assert.That(entity, Is.Not.EqualTo(Entity.Null));
                    Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Id, Is.EqualTo(result.Contributions[i].Source));
                    Assert.That(fixture.Manager.GetComponentData<GiEmitter>(entity).Layer.Index, Is.EqualTo(layerKey));
                    linked++;
                }
            }

            Assert.That(withSources, Is.GreaterThan(18));
            Assert.That(linked, Is.GreaterThan(24));
        }

        [Test]
        public void FollowingAContributionSelectsThatEntity()
        {
            using var fixture = Fixture("ExplainLink", 12, IntegerStamps);
            var model = new GiExplainModel();
            var emitter = fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiEmitter>(), ComponentType.ReadOnly<LocalTransform>()).ToEntityArray(Unity.Collections.Allocator.Temp)[0];
            var position = fixture.Manager.GetComponentData<LocalTransform>(emitter).Position;
            var layerKey = fixture.Manager.GetComponentData<GiEmitter>(emitter).Layer.Index;
            var result = Explain(fixture, model, "ExplainLink", layerKey, position.x, position.z);
            Assert.That(result.Entities.Length, Is.GreaterThan(0));
            Assert.That(GiEntitySelection.Available, Is.True);

            var target = result.Entities[0];
            Assert.That(GiEntitySelection.Select(GiEntitySelection.Find("ExplainLink"), target), Is.True);
            Assert.That(GiEntitySelection.TryGetSelected(out var world, out var selected), Is.True);
            Assert.That(world, Is.SameAs(fixture.World));
            Assert.That(selected, Is.EqualTo(target));

            Assert.That(GiEntitySelection.Select(fixture.World, Entity.Null), Is.False);
            Assert.That(GiEntitySelection.Select(null, target), Is.False);
        }

        [Test]
        public void ASaturatedPileShowsTheFlag()
        {
            using var fixture = new GiEcsFixture("ExplainSaturated");
            fixture.SetStandardProfile();
            fixture.AddInspector();
            for (var i = 0; i < 90; i++) fixture.CreateEmitter(0, 0, 12, 32f, 32f);
            fixture.Frame1();
            fixture.Frame1();

            var model = new GiExplainModel();
            var result = Explain(fixture, model, "ExplainSaturated", 0, 32.5f, 32.5f);
            Assert.That(result.Explanation.Saturated, Is.True);
            Assert.That(result.Explanation.Total, Is.EqualTo((short)GiExplain.Clamp));
            Assert.That(result.Explanation.Rest, Is.Not.EqualTo(0L));
            Assert.That(GiExplainText.Summary(result.Explanation), Does.Contain("saturated"));

            var quiet = Explain(fixture, model, "ExplainSaturated", 0, 3.5f, 3.5f);
            Assert.That(quiet.Explanation.Saturated, Is.False);
            Assert.That(quiet.Contributions, Is.Empty);
        }

        [Test]
        public void ADerivedLayerShowsItsRecipeWithTheIntermediateValues()
        {
            using var fixture = Fixture("ExplainRecipe", 30, new byte[] { 0, 1, 2, 3, 4, 5 });
            var model = new GiExplainModel();
            var checkedSteps = 0;
            for (var probe = 0; probe < 12; probe++)
            {
                var x = 12f + probe * 3.25f;
                var y = 14f + probe * 2.5f;
                var sum = Explain(fixture, model, "ExplainRecipe", 3, x, y);
                Assert.That(sum.Steps.Length, Is.EqualTo(1));
                var step = sum.Steps[0];
                var frame = fixture.Frame;
                frame.Tables.TryLayer(new GiLayerKey(0), out var first);
                frame.Tables.TryLayer(new GiLayerKey(2), out var second);
                frame.Tables.TryLayer(new GiLayerKey(3), out var derived);
                GiWorld.TrySense(frame.World, first, x, y, out var a);
                GiWorld.TrySense(frame.World, second, x, y, out var b);
                GiWorld.TrySense(frame.World, derived, x, y, out var actual);
                Assert.That((step.Layer, step.Kind, step.First, step.Second), Is.EqualTo((3, GiLayerKind.Sum, 0, 2)));
                Assert.That((step.A, step.B, step.Actual), Is.EqualTo((a, b, actual)));
                Assert.That(step.Matches, Is.True);
                Assert.That(step.Raw, Is.EqualTo(a + b));
                Assert.That(GiExplainText.Step(step), Does.Contain("layer 3 = Sum").And.Contain("-> " + actual));

                var max = Explain(fixture, model, "ExplainRecipe", 4, x, y);
                Assert.That(max.Steps.Length, Is.EqualTo(1));
                Assert.That(max.Steps[0].Matches, Is.True);
                checkedSteps += 2;

                var raw = Explain(fixture, model, "ExplainRecipe", 0, x, y);
                Assert.That(raw.Steps, Is.Empty);
            }

            Assert.That(checkedSteps, Is.EqualTo(24));
        }

        [Test]
        public void TheEntityReportCarriesDesiredAppliedLiveFaultAndFieldState()
        {
            using var fixture = new GiEcsFixture("ExplainEntity");
            fixture.SetStandardProfile();
            fixture.AddInspector();
            var healthy = fixture.CreateEmitter(1, 0, 5, 20f, 22f);
            var broken = fixture.CreateEmitter(0, 0, 4, 30f, 30f);
            fixture.Frame1();
            fixture.Frame1();
            fixture.Manager.SetComponentData(broken, LocalTransform.FromPosition(float.NaN, 0f, 5f));
            fixture.Frame1();
            fixture.Complete();

            var model = new GiExplainModel();
            model.Inspect("ExplainEntity", healthy);
            fixture.Frame1();
            Assert.That(model.Poll(), Is.EqualTo(1));
            var report = model.Report;
            Assert.That(report.Exists && report.HasEmitter && report.Enabled && report.HasSource && report.Placed && report.Live, Is.True);
            Assert.That((report.DesiredLayer, report.DesiredStamp, report.DesiredGain), Is.EqualTo(((byte)1, (byte)0, (sbyte)5)));
            Assert.That((report.AppliedLayer, report.AppliedStamp, report.AppliedGain), Is.EqualTo(((byte)1, (byte)0, (sbyte)5)));
            Assert.That(report.Drift, Is.False);
            Assert.That(report.HasFault, Is.False);
            Assert.That(report.Spec.X, Is.EqualTo(20f));
            Assert.That(report.Spec.Y, Is.EqualTo(22f));
            Assert.That(report.Layers.Length, Is.EqualTo(7));
            var frame = fixture.Frame;
            for (var i = 0; i < report.Layers.Length; i++)
            {
                frame.Tables.TryLayer(new GiLayerKey((byte)i), out var layer);
                GiWorld.TrySense(frame.World, layer, 20f, 22f, out var value);
                GiWorld.TrySenseGradient(frame.World, layer, 20f, 22f, out var gx, out var gy);
                Assert.That((report.Layers[i].Value, report.Layers[i].GradientX, report.Layers[i].GradientY), Is.EqualTo((value, gx, gy)));
            }

            model.Inspect("ExplainEntity", broken);
            fixture.Frame1();
            model.Poll();
            Assert.That(model.Report.HasFault, Is.True);
            Assert.That(model.Report.Fault, Is.EqualTo(GiFaultCode.NonFinite));
            Assert.That(GiSourceInspector.Lines(model.Report), Has.Some.Contains("Fault: NonFinite"));
        }

        [Test]
        public void ADestroyedEntityReportsThatItIsGone()
        {
            using var fixture = new GiEcsFixture("ExplainGone");
            fixture.SetStandardProfile();
            fixture.AddInspector();
            var emitter = fixture.CreateEmitter(0, 0, 3, 20f, 20f);
            fixture.Frame1();
            fixture.Frame1();
            fixture.Manager.DestroyEntity(emitter);

            var model = new GiExplainModel();
            model.Inspect("ExplainGone", emitter);
            fixture.Frame1();
            model.Poll();
            Assert.That(model.Report.Exists, Is.True);
            Assert.That(model.Report.HasSource && !model.Report.HasEmitter, Is.True);

            fixture.Frame1();
            fixture.Frame1();
            model.Inspect("ExplainGone", emitter);
            fixture.Frame1();
            model.Poll();
            Assert.That(model.Report.Exists, Is.False);
            Assert.That(GiSourceInspector.Lines(model.Report), Is.EqualTo(new[] { "The entity no longer exists." }));
            Assert.That(GiSourceInspector.Lines(null), Is.EqualTo(new[] { GiSourceInspector.NothingSelected }));
        }

        [Test]
        public void ADriftingReportIsDetectedFromItsStates()
        {
            var report = new GiEntityReport { HasEmitter = true, HasSource = true, Placed = true, DesiredGain = 3, AppliedGain = 3 };
            Assert.That(report.Drift, Is.False);
            report.DesiredGain = 4;
            Assert.That(report.Drift, Is.True);
            report.Placed = false;
            Assert.That(report.Drift, Is.False);
            report.Placed = true;
            report.DesiredGain = 3;
            report.DesiredStamp = 2;
            Assert.That(report.Drift, Is.True);
            Assert.That(GiSourceInspector.Lines(new GiEntityReport { Exists = true, HasEmitter = true, HasSource = true, Placed = true, DesiredStamp = 2 }),
                Has.Some.Contains("differ"));
        }

        [Test]
        public void RequestsForAWorldThatGoesAwayAreDropped()
        {
            var fixture = new GiEcsFixture("ExplainRetire");
            fixture.SetStandardProfile();
            fixture.AddInspector();
            fixture.Frame1();
            var model = new GiExplainModel();
            model.Explain("ExplainRetire", 0, Vector2.one);
            model.Inspect("ExplainRetire", Entity.Null);
            fixture.Dispose();
            Assert.That(model.Poll(), Is.EqualTo(0));
            Assert.That(model.Waiting, Is.True);
            model.Clear();
            Assert.That(model.Waiting, Is.False);
            Assert.That(model.Latest, Is.Null);
        }

        [Test]
        public void TheWindowBuildsItsInterfaceExplainsACellAndClosesCleanly()
        {
            using var fixture = Fixture("ExplainWindow", 12, IntegerStamps);
            var window = EditorWindow.CreateInstance<GiExplainWindow>();
            try
            {
                window.CreateGUI();
                Assert.That(window.rootVisualElement.childCount, Is.GreaterThan(6));
                window.Query("ExplainWindow", 0, new Vector2(30f, 30f));
                fixture.Frame1();
                window.Tick();
                Assert.That(window.Model.Latest, Is.Not.Null);
                Assert.That(window.Model.Latest.Id, Is.GreaterThan(0));
                Assert.That(window.Model.EcsWorld, Is.EqualTo("ExplainWindow"));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }

            Assert.That(GiInspectorBoard.OpenViews, Is.EqualTo(0));
        }

        [Test]
        public void TheTextIsInvariantAndDeterministic()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                var contribution = new GiContribution { Value = -12, X = 1.5f, Y = 2.25f, Stamp = 3, Gain = -4 };
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.That(GiExplainText.Contribution(contribution), Is.EqualTo("-12  stamp 3 gain -4 at (1.5, 2.25)"));
                Assert.That(GiExplainText.Summary(new GiExplanation { Ok = true, Total = 7, Sum = 9, Contributors = 2 }),
                    Is.EqualTo("value 7 = 9 from 2 sources, -2 unattributed"));
                Assert.That(GiExplainText.Summary(default), Is.EqualTo("outside the grid"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
