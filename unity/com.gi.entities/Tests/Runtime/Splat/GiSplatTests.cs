using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiSplatTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static ulong LayerHash(GiEcsFixture fixture, int layer)
        {
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey((byte)layer), out var id);
            return GiFieldHash.OfLayer(frame.World, id);
        }

        [Test]
        public void EcsWithSplatsMatchesTheReferenceOverFiftySeeds()
        {
            for (ulong seed = 1; seed <= 50; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, GiScenarioOptions.Standard);
                var expected = GiReferenceExecutor.Run(scenario);
                using var executor = new GiEcsExecutor(scenario);
                Assert.That(expected.Compare(executor.Run()), Is.EqualTo(string.Empty), $"seed {seed}");
            }
        }

        [Test]
        public void TheUniformOverloadMakesTheSameFieldAsThePerEntryOverload()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var rng = new GiRandom(9);
            var xs = new float[40];
            var ys = new float[40];
            for (var i = 0; i < xs.Length; i++)
            {
                xs[i] = 6 + rng.Next(50);
                ys[i] = 6 + rng.Next(50);
                fixture.CreateSplat(2, 4, 3, xs[i], ys[i]);
            }

            fixture.Tick();
            var actual = LayerHash(fixture, 2);

            using var reference = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var stamps = new byte[xs.Length];
            var gains = new sbyte[xs.Length];
            for (var i = 0; i < xs.Length; i++)
            {
                stamps[i] = reference.Stamps[4];
                gains[i] = 3;
            }

            Assert.That(GiWorld.Splat(reference.World, reference.Layers[2], xs, ys, stamps, gains), Is.EqualTo(xs.Length));
            GiWorld.Process(reference.World);
            Assert.That(actual, Is.EqualTo(GiFieldHash.OfLayer(reference.World, reference.Layers[2])));
        }

        [Test]
        public void ASplatLastsAsLongAsItsEntityAndDisabledOnesContributeNothing()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick();
            var empty = LayerHash(fixture, 2);

            var splat = fixture.CreateSplat(2, 0, 4, 30f, 30f);
            fixture.Tick();
            var present = LayerHash(fixture, 2);
            Assert.That(present, Is.Not.EqualTo(empty));
            fixture.Tick(3);
            Assert.That(LayerHash(fixture, 2), Is.EqualTo(present), "re-splatted every tick");
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Splats, Is.EqualTo(1));

            fixture.Manager.SetComponentEnabled<GiSplat>(splat, false);
            fixture.Tick();
            Assert.That(LayerHash(fixture, 2), Is.EqualTo(empty));
            fixture.Manager.SetComponentEnabled<GiSplat>(splat, true);
            fixture.Tick();
            Assert.That(LayerHash(fixture, 2), Is.EqualTo(present));

            fixture.Manager.DestroyEntity(splat);
            fixture.Tick();
            Assert.That(LayerHash(fixture, 2), Is.EqualTo(empty));
        }

        [Test]
        public void ASplatOnAPersistentOrUnknownLayerFaultsAndRecovers()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var persistent = fixture.CreateSplat(0, 0, 4, 30f, 30f);
            var unknown = fixture.CreateSplat(200, 0, 4, 31f, 31f);
            var badStamp = fixture.CreateSplat(2, 99, 4, 32f, 32f);
            var good = fixture.CreateSplat(2, 0, 4, 33f, 33f);

            Assert.DoesNotThrow(() => fixture.Tick(2));
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiFault>(persistent).Code, Is.EqualTo(GiFaultCode.WrongResidency));
            Assert.That(fixture.Manager.GetComponentData<GiFault>(unknown).Code, Is.EqualTo(GiFaultCode.InvalidLayer));
            Assert.That(fixture.Manager.GetComponentData<GiFault>(badStamp).Code, Is.EqualTo(GiFaultCode.InvalidStamp));
            foreach (var bad in new[] { persistent, unknown, badStamp }) Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(bad), Is.True);
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(good), Is.False);
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Splats, Is.EqualTo(1));

            fixture.Manager.SetComponentData(persistent, new GiSplat { Layer = new GiLayerKey(6), Stamp = new GiStampKey(0), Gain = 4 });
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(persistent), Is.False);
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Splats, Is.EqualTo(2));
        }

        [Test]
        public void ManySplatsAllocateNothingPerTick()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            for (var i = 0; i < 1000; i++) fixture.CreateSplat((byte)(i % 2 == 0 ? 2 : 6), (byte)(i % 5 == 0 ? 4 : 0), (sbyte)(1 + i % 3), 6f + i % 50, 6f + i / 50);
            fixture.Tick(100);
            Assert.That(GiAllocation.SteadyZero(() => fixture.Tick(50), 14, 3), Is.True);
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Splats, Is.EqualTo(1000));
        }
    }
}
