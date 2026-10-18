using System.Collections;
using System.Diagnostics;
using Gi.Entities.Presentation;
using Gi.Entities.Tests;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gi.Entities.Performance
{
    public struct PerfSample : IComponentData
    {
        public short Value;
        public float2 Gradient;
    }

    public struct PerfCovered : IComponentData, IEnableableComponent
    {
    }

    public sealed class GiEcsPerformance
    {
        private const int Warmup = 20;

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static GiBindingField Field<T>(string name)
            where T : unmanaged, IComponentData
        {
            var info = typeof(T).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return new GiBindingField
            {
                TypeHash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<T>()).StableTypeHash,
                Offset = (ushort)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.GetFieldOffset(info),
                Size = (ushort)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf(info.FieldType),
            };
        }

        private static GiBindingField Tag<T>()
            where T : unmanaged, IComponentData
        {
            return new GiBindingField { TypeHash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<T>()).StableTypeHash };
        }

        private static double Median(double[] values)
        {
            System.Array.Sort(values);
            return values[values.Length / 2];
        }

        private static void Populate(GiEcsFixture fixture, int emitters, int probes)
        {
            var side = (int)math.ceil(math.sqrt(emitters));
            var archetype = fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(GiEmitter), typeof(GiFault));
            using var entities = fixture.Manager.CreateEntity(archetype, emitters, Allocator.Temp);
            for (var i = 0; i < emitters; i++)
            {
                fixture.Manager.SetComponentData(entities[i], new GiEmitter { Layer = new GiLayerKey((byte)(i % 2)), Stamp = new GiStampKey((byte)(i % 6)), Gain = 3 });
                fixture.Manager.SetComponentData(entities[i], LocalTransform.FromPosition(2f + i % side * (60f / side), 0f, 2f + i / side * (60f / side)));
                fixture.Manager.SetComponentEnabled<GiFault>(entities[i], false);
            }

            if (probes == 0) return;

            var probe = fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(PerfSample), typeof(PerfCovered));
            using var probeEntities = fixture.Manager.CreateEntity(probe, probes, Allocator.Temp);
            for (var i = 0; i < probes; i++)
            {
                fixture.Manager.SetComponentData(probeEntities[i], LocalTransform.FromPosition(2f + i % side * (60f / side), 0f, 2f + i / side * (60f / side)));
                fixture.Manager.SetComponentEnabled<PerfCovered>(probeEntities[i], false);
            }
        }

        [Test, Performance]
        public void IdleCostWithAProfileAndNoEntities()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick(Warmup);
            fixture.Complete();

            const int ticks = 2000;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < ticks; i++) fixture.Tick();
            fixture.Complete();
            var microseconds = sw.Elapsed.TotalMilliseconds * 1000.0 / ticks;
            GiPerfLog.Record("idle.us_per_tick", microseconds);
            Measure.Custom(new SampleGroup("idle us per tick", SampleUnit.Microsecond), microseconds);
        }

        [Test, Performance]
        public void ReconcileAtOnePercentChurnAndTheEcsShareOfAFrame()
        {
            var sources = GiPerfLog.EnvInt("GI_PERF_SOURCES", 200_000);
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            Populate(fixture, sources, 0);
            fixture.Tick(Warmup);
            fixture.Complete();

            var quiet = new double[20];
            for (var f = 0; f < quiet.Length; f++)
            {
                var timer = Stopwatch.StartNew();
                fixture.Tick();
                fixture.Complete();
                quiet[f] = timer.Elapsed.TotalMilliseconds;
            }

            var churn = sources / 100;
            var query = fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiEmitter>());
            var archetype = fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(GiEmitter), typeof(GiFault));
            const int frames = 20;
            var walls = new double[frames];
            for (var f = 0; f < frames; f++)
            {
                using (var all = query.ToEntityArray(Allocator.Temp))
                {
                    for (var i = 0; i < churn; i++) fixture.Manager.DestroyEntity(all[(f * churn + i * 7) % all.Length]);
                }

                using (var created = fixture.Manager.CreateEntity(archetype, churn, Allocator.Temp))
                {
                    for (var i = 0; i < churn; i++)
                    {
                        fixture.Manager.SetComponentData(created[i], new GiEmitter { Layer = new GiLayerKey(0), Stamp = new GiStampKey((byte)(i % 6)), Gain = 3 });
                        fixture.Manager.SetComponentData(created[i], LocalTransform.FromPosition(2f + i % 100 * 0.5f, 0f, 2f + i / 100 * 0.5f));
                        fixture.Manager.SetComponentEnabled<GiFault>(created[i], false);
                    }
                }

                var sw = Stopwatch.StartNew();
                fixture.Tick();
                fixture.Complete();
                walls[f] = sw.Elapsed.TotalMilliseconds;
            }

            var median = Median(walls);
            var quietMedian = Median(quiet);
            var perOperation = System.Math.Max(0.0, median - quietMedian) * 1000.0 / (2 * churn);
            GiPerfLog.Record("reconcile.sources", sources);
            GiPerfLog.Record("reconcile.churn_each_way", churn);
            GiPerfLog.Record("reconcile.quiet_ms", quietMedian);
            GiPerfLog.Record("reconcile.frame_ms", median);
            GiPerfLog.Record("reconcile.us_per_command", perOperation);
            GiPerfLog.Record("reconcile.ms_for_10k_each_way", perOperation * 20000 / 1000.0);
            Measure.Custom(new SampleGroup("reconcile frame at 1% churn", SampleUnit.Millisecond), median);
        }

        [Test, Performance]
        public void ProfileEditFreesRebuildsAndReplacesEverySource()
        {
            var sources = GiPerfLog.EnvInt("GI_PERF_SOURCES", 200_000);
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            Populate(fixture, sources, 0);
            fixture.Tick(Warmup);
            fixture.Complete();

            var changed = GiScenarioProfile.Standard().ToSpec();
            changed.Layers[3].Shift = 2;
            fixture.SetProfile(changed);
            var sw = Stopwatch.StartNew();
            fixture.Tick();
            fixture.Complete();
            var first = sw.Elapsed.TotalMilliseconds;
            Assert.That(fixture.Frame.Generation, Is.EqualTo(2u));

            var standard = GiScenarioProfile.Standard().ToSpec();
            fixture.SetProfile(standard);
            sw.Restart();
            fixture.Tick();
            fixture.Complete();
            var second = sw.Elapsed.TotalMilliseconds;
            Assert.That(fixture.Frame.Generation, Is.EqualTo(3u));

            GiPerfLog.Record("profile_edit.sources", sources);
            GiPerfLog.Record("profile_edit.ms", System.Math.Min(first, second));
            Measure.Custom(new SampleGroup("profile edit rebuild", SampleUnit.Millisecond), System.Math.Min(first, second));
        }

        [Test, Performance]
        public void MirrorUploadOfFiveHundredAndTwelveChangedTiles()
        {
            Assume.That(SystemInfo.supportsComputeShaders, "needs compute shaders");
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var layer = setup.Layers[0];
            using var mirror = new GiMirror(setup.World, 1, layer, 7);
            var ids = new System.Collections.Generic.List<Gi.World.SourceId>();
            for (var i = 0; i < 4096; i++) ids.Add(Gi.World.Place(setup.World, layer, 1f + i % 64, 1f + i / 64, setup.Stamps[0], 3));
            Gi.World.Process(setup.World);
            mirror.Update(1 << 20);

            var samples = new double[30];
            for (var s = 0; s < samples.Length; s++)
            {
                for (var i = 0; i < ids.Count; i++) Gi.World.SetGain(setup.World, ids[i], (sbyte)(2 + (s + i) % 7));
                Gi.World.Process(setup.World);
                var sw = Stopwatch.StartNew();
                mirror.Update(512);
                samples[s] = sw.Elapsed.TotalMilliseconds;
            }

            var median = Median(samples);
            GiPerfLog.Record("mirror.upload_512_tiles_ms", median);
            Measure.Custom(new SampleGroup("mirror upload 512 tiles", SampleUnit.Millisecond), median);
        }

        [UnityTest, Performance]
        public IEnumerator EcsFrameWallTimeAgainstGiTimeWithAndWithoutWorkers()
        {
            var sources = GiPerfLog.EnvInt("GI_PERF_SOURCES", 200_000);
            var workers = JobsUtility.JobWorkerCount;
            foreach (var count in new[] { 0, workers })
            {
                JobsUtility.JobWorkerCount = count;
                var recorders = new ProfilerRecorder[GiMarkers.Names.Length];
                GiEcsFixture fixture = null;
                try
                {
                    fixture = new GiEcsFixture("Perf" + count);
                    fixture.SetProfileWith(new GiBindingSpec
                    {
                        Kind = GiBindingKind.ValueGradient,
                        Layer = 3,
                        Value = Field<PerfSample>("Value"),
                        Gradient = Field<PerfSample>("Gradient"),
                        Coverage = Tag<PerfCovered>(),
                    });
                    Populate(fixture, sources, sources);
                    fixture.Tick(Warmup);
                    fixture.Complete();
                    for (var r = 0; r < recorders.Length; r++)
                        recorders[r] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiMarkers.Names[r], 16, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);

                    var wall = new double[40];
                    var giTime = new double[40];
                    for (var f = 0; f < wall.Length + 2; f++)
                    {
                        var sw = Stopwatch.StartNew();
                        fixture.Tick();
                        fixture.Complete();
                        var elapsed = sw.Elapsed.TotalMilliseconds;
                        yield return null;
                        if (f < 2) continue;

                        wall[f - 2] = elapsed;
                        var nanoseconds = 0L;
                        foreach (var recorder in recorders) nanoseconds += recorder.LastValue;
                        giTime[f - 2] = nanoseconds / 1e6;
                    }

                    var label = count == 0 ? "serial" : "workers";
                    var medianWall = Median(wall);
                    var medianGi = Median(giTime);
                    GiPerfLog.Record("ecs." + label + ".sources", sources);
                    GiPerfLog.Record("ecs." + label + ".wall_ms", medianWall);
                    GiPerfLog.Record("ecs." + label + ".gi_ms", medianGi);
                    GiPerfLog.Record("ecs." + label + ".ecs_share_of_gi", medianGi > 0 ? (medianWall - medianGi) / medianGi : 0);
                    Measure.Custom(new SampleGroup("ecs frame wall " + label, SampleUnit.Millisecond), medianWall);
                }
                finally
                {
                    foreach (var recorder in recorders)
                        if (recorder.Valid)
                            recorder.Dispose();
                    fixture?.Dispose();
                    JobsUtility.JobWorkerCount = workers;
                }
            }
        }
    }
}
