using System;
using System.Globalization;
using System.IO;
using System.Text;
using Gi.Corpus;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class CorpusDigestsTests
{
    private const string RegenerateVariable = "GI_REGENERATE_CORPUS_DIGESTS";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Gi.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static string Source(ulong[] digests)
    {
        var text = new StringBuilder();
        text.Append("namespace Gi.Corpus;\n\ninternal static class CorpusDigests\n{\n    internal static readonly ulong[] Groups =\n    [\n");
        for (var i = 0; i < digests.Length; i += 4)
        {
            text.Append("       ");
            for (var j = i; j < Math.Min(i + 4, digests.Length); j++)
                text.Append(" 0x").Append(digests[j].ToString("X16", CultureInfo.InvariantCulture)).Append("UL,");
            text.Append('\n');
        }

        text.Append("    ];\n}\n");
        return text.ToString();
    }

    [Fact]
    public void TheCommittedDigestsMatchTheGoldenCorpusFile()
    {
        var root = RepositoryRoot();
        var golden = File.ReadAllText(Path.Combine(root, "tests", "Gi.Tests", "Corpus", "golden-corpus.txt"));
        var digests = GiCorpus.Digest(golden, out _);
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            File.WriteAllText(Path.Combine(root, "unity", "tests", "Gi.Corpus", "CorpusDigests.cs"), Source(digests), new UTF8Encoding(false));
            return;
        }

        Assert.Equal(digests.Length, GiCorpus.Expected.Count);
        for (var i = 0; i < digests.Length; i++) Assert.True(digests[i] == GiCorpus.Expected[i], $"group {i}");
    }

    [Fact]
    public void ReplayingEveryStreamUnderDotnetMatchesTheCommittedCorpus()
    {
        Assert.Equal(string.Empty, GiCorpus.Replay());
    }

    [Fact]
    public void TheCorpusHasTheExpectedShape()
    {
        var root = RepositoryRoot();
        var golden = File.ReadAllText(Path.Combine(root, "tests", "Gi.Tests", "Corpus", "golden-corpus.txt"));
        GiCorpus.Digest(golden, out var heads);
        var streams = 0;
        var modes = 0;
        foreach (var head in heads)
        {
            if (head.StartsWith("stream ", StringComparison.Ordinal)) streams++;
            if (head.StartsWith("mode ", StringComparison.Ordinal)) modes++;
        }

        Assert.Equal(1200, streams);
        Assert.Equal(6, modes);
        Assert.Equal("gi-corpus 1", heads[0]);
    }
}
