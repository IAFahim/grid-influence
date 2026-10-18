using System.Collections;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Gi.Entities.Tests
{
    public sealed class GiCountersTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [UnityTest]
        public IEnumerator CountersFollowTheFrameWhileTheProfilerIsOn()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            for (var i = 0; i < 25; i++) fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i, 20f);
            for (var i = 0; i < 9; i++) fixture.CreateSplat(2, 0, 3, 30f + i, 30f);

            var wasEnabled = Profiler.enabled;
            Profiler.enabled = true;
            using var sources = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiCounters.SourcesName);
            using var splats = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiCounters.SplatsName);
            using var bytes = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiCounters.BytesName);
            try
            {
                fixture.Tick(3);
                fixture.Complete();
                yield return null;
                fixture.Tick(2);
                fixture.Complete();
                yield return null;

                Assert.That(sources.Valid, Is.True);
                Assert.That(sources.LastValue, Is.EqualTo(25));
                Assert.That(splats.LastValue, Is.EqualTo(9));
                Assert.That(bytes.LastValue, Is.GreaterThan(0));
            }
            finally
            {
                Profiler.enabled = wasEnabled;
            }
        }
    }
}
