using GiWorld = Gi.World;
using NUnit.Framework;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiWorldEditorSmokeTests
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
