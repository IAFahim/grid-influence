using System.Text;
using Xunit;

namespace Gi.Tests;

public class GoldenCorpusTests
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
