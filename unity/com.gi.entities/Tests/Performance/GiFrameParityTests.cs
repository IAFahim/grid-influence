using System.Text.RegularExpressions;
using Gi.Frames;
using NUnit.Framework;

namespace Gi.Entities.Performance
{
    public sealed class GiFrameParityTests
    {
        [Test]
        public void FrameReceiptsAreGreenOnThisVariant()
        {
            var code = GiFrames.Run(new[] { "--frames", "verify" }, out var output, out var error);
            TestContext.Out.WriteLine(output);
            Assert.That(code, Is.EqualTo(0), error + output);
        }

        [Test]
        public void FrameChecksumsMatchGoldensOnThisVariant()
        {
            Check("B1", new[] { "B1", "--precision", "exact" }, "6782ae93471d0dd1");
            Check("C", new[] { "C", "--precision", "cell", "--layer", "transient", "--reads", "fused" }, "3acb24c8a236bce8");
            Check("W", new[] { "W", "--precision", "cell" }, "eda5d2665ddfdbc5");
        }

        private static void Check(string name, string[] frame, string golden)
        {
            var args = new System.Collections.Generic.List<string> { "--frames" };
            args.AddRange(frame);
            args.AddRange(new[] { "--entities", "20000", "--measure", "5", "--warmup", "2" });
            var code = GiFrames.Run(args.ToArray(), out var output, out var error);
            Assert.That(code, Is.EqualTo(0), name + " " + error);
            var lines = output.Split('\n');
            var line = lines[lines.Length - 2];
            Assert.That(line, Does.Contain("\"ok\":true"), name);
            Assert.That(line, Does.Contain("\"bytes_per_frame\":0"), name);
            var checksum = Regex.Match(line, "\"checksum\":\"([0-9a-f]+)\"").Groups[1].Value;
            TestContext.Out.WriteLine("parity| frame=" + name + " checksum=" + checksum + " golden=" + golden);
            Assert.That(checksum, Is.EqualTo(golden), name);
        }

    }
}
