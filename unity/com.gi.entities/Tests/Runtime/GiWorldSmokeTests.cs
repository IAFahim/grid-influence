using GiWorld = Gi.World;
using NUnit.Framework;

namespace Gi.Entities.Tests
{
    public sealed class GiWorldSmokeTests
    {
        [Test]
        public void NewWorldReportsRevisionAndFrees()
        {
            var world = GiWorld.New();
            var revision = GiWorld.Revision(world);
            Assert.That(revision, Is.GreaterThan(0));
            Assert.That(GiWorld.Free(world), Is.True);
        }
    }
}
