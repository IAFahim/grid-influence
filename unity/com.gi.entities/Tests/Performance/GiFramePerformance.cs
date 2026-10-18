using System.Globalization;
using System.Text.RegularExpressions;
using Gi.Frames;
using NUnit.Framework;
using Unity.PerformanceTesting;

namespace Gi.Entities.Performance
{
    public sealed class GiFramePerformance
    {
        private static readonly string[][] Frames =
        {
            new[] { "B1", "--precision", "exact" },
            new[] { "C", "--precision", "cell", "--layer", "transient", "--reads", "fused" },
            new[] { "W", "--precision", "cell" },
        };

        [Test, Performance]
        public void TheBenchmarkFramesRunInsideUnityWithZeroAllocation()
        {
            var entities = GiPerfLog.EnvInt("GI_PERF_ENTITIES", 200_000);
            foreach (var frame in Frames)
            {
                var args = new System.Collections.Generic.List<string> { "--frames" };
                args.AddRange(frame);
                args.AddRange(new[] { "--entities", entities.ToString(CultureInfo.InvariantCulture), "--measure", "60", "--warmup", "10" });
                var code = GiFrames.Run(args.ToArray(), out var output, out var error);
                Assert.That(code, Is.EqualTo(0), error);

                var line = output.Split('\n')[output.Split('\n').Length - 2];
                var name = Field(line, "frame") + "." + Field(line, "precision");
                var p50 = Number(line, "p50_ms");
                var p99 = Number(line, "p99_ms");
                var bytes = Number(line, "bytes_per_frame");
                Assert.That(line, Does.Contain("\"ok\":true"), name);
                Assert.That(bytes, Is.EqualTo(0), name + " allocated per frame");

                GiPerfLog.Record("frame." + name + ".p50_ms", p50);
                GiPerfLog.Record("frame." + name + ".p99_ms", p99);
                GiPerfLog.Record("frame." + name + ".entities", entities);
                Measure.Custom(new SampleGroup(name + " p50", SampleUnit.Millisecond), p50);
                Measure.Custom(new SampleGroup(name + " p99", SampleUnit.Millisecond), p99);
            }
        }

        private static string Field(string json, string name)
        {
            return Regex.Match(json, "\"" + name + "\":\"([^\"]*)\"").Groups[1].Value;
        }

        private static double Number(string json, string name)
        {
            return double.Parse(Regex.Match(json, "\"" + name + "\":([-0-9.eE+]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
