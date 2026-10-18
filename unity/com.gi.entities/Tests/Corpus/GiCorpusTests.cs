using Gi.Corpus;
using NUnit.Framework;

namespace Gi.Entities.Tests.Corpus
{
    public sealed class GiCorpusTests
    {
        private const int Groups = 1207;

        [Test]
        public void TheCommittedCorpusHasAllItsStreams()
        {
            Assert.That(GiCorpus.Expected.Count, Is.EqualTo(Groups));
        }

        [Test]
        public void EveryGoldenStreamHashesLikeTheCommittedCorpus()
        {
            Assert.That(GiCorpus.Replay(), Is.EqualTo(string.Empty));
        }
    }
}
