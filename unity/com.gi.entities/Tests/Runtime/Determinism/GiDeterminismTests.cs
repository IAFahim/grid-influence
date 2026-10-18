using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiDeterminismTests
    {
        private static readonly GiBindingSpec Dormant = new GiBindingSpec
        {
            Kind = GiBindingKind.Value,
            Layer = 1,
            Value = GiBindingFields.Of<KindValue>("Value"),
        };

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void ShufflingTheSpawnOrderGivesTheSamePerTickHashes()
        {
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, GiScenarioOptions.Standard);
                var expected = GiReferenceExecutor.Run(scenario);
                using var plain = new GiEcsExecutor(scenario, "Plain");
                using var shuffled = new GiEcsExecutor(scenario.WithOpOrderShuffled(seed * 31), "Shuffled");
                var a = plain.Run();
                var b = shuffled.Run();
                Assert.That(a.Compare(b), Is.EqualTo(string.Empty), $"seed {seed} plain vs shuffled");
                Assert.That(expected.Compare(a), Is.EqualTo(string.Empty), $"seed {seed} oracle vs plain");
            }
        }

        [Test]
        public void AfterARebuildTheHashesEqualThoseOfAWorldStartedFreshWithTheSameEntities()
        {
            var options = new GiScenarioOptions(6, 24, 64, 4, false, false);
            for (ulong seed = 1; seed <= 6; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, options);
                var changed = scenario.Profile.ToSpec();
                changed.Bindings = new[] { Dormant };

                using var rebuilt = new GiEcsExecutor(scenario, "Rebuilt");
                for (var t = 0; t < scenario.Ticks.Length; t++) rebuilt.Step(t);
                var firstGeneration = rebuilt.Fixture.Frame.Generation;
                rebuilt.Fixture.SetProfile(changed);
                rebuilt.Fixture.Tick();
                Assert.That(rebuilt.Fixture.Frame.Generation, Is.EqualTo(firstGeneration + 1u), $"seed {seed} did not rebuild");

                using var fresh = new GiEcsExecutor(scenario, "Fresh");
                fresh.Fixture.SetProfile(changed);
                for (var t = 0; t < scenario.Ticks.Length; t++) fresh.Queue(t);
                fresh.Fixture.Tick();

                Assert.That(rebuilt.Hashes(), Is.EqualTo(fresh.Hashes()), $"seed {seed}");
            }
        }

        [Test]
        public void AFadeInFlightIsDroppedByARebuildAndTheFreshWorldAgrees()
        {
            using var rebuilt = new GiEcsFixture("FadeRebuilt");
            rebuilt.SetStandardProfile();
            var entity = rebuilt.CreateEmitter(0, 0, 20, 30f, 30f, 6);
            rebuilt.Tick(2);
            rebuilt.Complete();

            var changed = GiScenarioProfile.Standard().ToSpec();
            changed.Bindings = new[] { Dormant };
            rebuilt.SetProfile(changed);
            rebuilt.Tick();
            rebuilt.Complete();

            using var fresh = new GiEcsFixture("FadeFresh");
            fresh.SetProfile(changed);
            fresh.CreateEmitter(0, 0, 20, 30f, 30f, 6);
            fresh.Tick();
            fresh.Complete();

            var rebuiltFrame = rebuilt.Frame;
            var freshFrame = fresh.Frame;
            rebuiltFrame.Tables.TryLayer(new GiLayerKey(0), out var layerA);
            freshFrame.Tables.TryLayer(new GiLayerKey(0), out var layerB);
            Assert.That(GiFieldHash.OfLayer(rebuiltFrame.World, layerA), Is.EqualTo(GiFieldHash.OfLayer(freshFrame.World, layerB)));
            Assert.That(rebuilt.Manager.GetComponentData<GiSource>(entity).Placed, Is.True);
        }

        [Test]
        public void FramesThatRunSeveralFixedTicksGiveTheSameHashesAsFramesThatRunOne()
        {
            var sizes = new[] { 2, 1, 3, 1, 2 };
            var scenario = GiScenarioGenerator.Create(53, new GiScenarioOptions(sizes.Length, 24, 64, 4, true, true));
            using var batched = new GiEcsExecutor(scenario, "Batched");
            using var stepped = new GiEcsExecutor(scenario, "Stepped");
            batched.Fixture.UseCatchUp(0.25f);
            stepped.Fixture.UseCatchUp(0.25f);
            batched.Fixture.Advance(0f);
            stepped.Fixture.Advance(0f);

            var ticks = 1;
            for (var f = 0; f < sizes.Length; f++)
            {
                batched.Queue(f);
                stepped.Queue(f);
                batched.Fixture.Advance(0.25f * sizes[f]);
                for (var i = 0; i < sizes[f]; i++) stepped.Fixture.Advance(0.25f);
                ticks += sizes[f];

                Assert.That(batched.Fixture.Frame.Tick, Is.EqualTo(ticks), $"batched tick, frame {f}");
                Assert.That(stepped.Fixture.Frame.Tick, Is.EqualTo(ticks), $"stepped tick, frame {f}");
                Assert.That(batched.Hashes(), Is.EqualTo(stepped.Hashes()), $"hashes, frame {f}");
            }
        }
    }
}
