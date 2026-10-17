using System.Globalization;
using System.Text;
using Xunit;

namespace Gi.Tests;

public unsafe class GoldenCorpusTests
{
    private const string RegenerateVariable = "GI_REGENERATE_CORPUS";

    [Fact]
    public void CommittedCorpusMatchesRegeneration()
    {
        var text = CorpusBuilder.Build();
        var path = CorpusBuilder.CommittedPath();
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"committed corpus missing: {path}");
        var committed = File.ReadAllText(path);
        Assert.True(
            string.Equals(committed, text, StringComparison.Ordinal),
            FirstDifference(committed, text));
    }

    private static string FirstDifference(string committed, string regenerated)
    {
        var committedLines = committed.Split('\n');
        var regeneratedLines = regenerated.Split('\n');
        var count = Math.Min(committedLines.Length, regeneratedLines.Length);
        for (var i = 0; i < count; i++)
        {
            if (string.Equals(committedLines[i], regeneratedLines[i], StringComparison.Ordinal)) continue;
            return $"corpus differs at line {i + 1}:\ncommitted:    {committedLines[i]}\nregenerated:  {regeneratedLines[i]}";
        }

        return $"corpus length differs: committed {committedLines.Length} lines, regenerated {regeneratedLines.Length} lines";
    }
}

internal static class CorpusBuilder
{
    public const string Header = "gi-corpus 1";
    public const int StreamsPerMode = 300;
    public const int SeedBase = 2_000_000;
    public const int TransientStreamsPerMode = 100;
    public const int TransientSeedBase = 3_000_000;

    public static string Build()
    {
        var text = new StringBuilder(1 << 20);
        using var writer = new StringWriter(text, CultureInfo.InvariantCulture);
        writer.WriteLine(Header);
        foreach (var mode in new[] { PrecisionMode.Exact, PrecisionMode.Cell, PrecisionMode.Quarter })
        {
            writer.Write("mode ");
            writer.WriteLine(mode.ToString().ToLowerInvariant());
            for (var i = 0; i < StreamsPerMode; i++)
            {
                var setup = StreamGenerator.Generate(SeedBase + (int)mode * 100_000 + i, mode, StreamProfile.Corpus);
                CorpusRunner.Run(setup, mode.ToString().ToLowerInvariant(), writer);
            }
        }

        foreach (var mode in new[] { PrecisionMode.Exact, PrecisionMode.Cell, PrecisionMode.Quarter })
        {
            writer.Write("mode transient-");
            writer.WriteLine(mode.ToString().ToLowerInvariant());
            for (var i = 0; i < TransientStreamsPerMode; i++)
            {
                var setup = StreamGenerator.Generate(TransientSeedBase + (int)mode * 100_000 + i, mode, StreamProfile.Transient);
                CorpusRunner.Run(setup, "transient-" + mode.ToString().ToLowerInvariant(), writer);
            }
        }

        return text.ToString();
    }

    public static string CommittedPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Gi.Tests.csproj")))
            directory = directory.Parent;
        if (directory == null)
            throw new InvalidOperationException("Gi.Tests.csproj not found above the test assembly");

        return Path.Combine(directory.FullName, "Corpus", "golden-corpus.txt");
    }
}
