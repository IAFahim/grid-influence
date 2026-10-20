using System.Globalization;
using System.Text;

namespace Gi.Tests;

internal static class CorpusBuilder
{
    private const string Header = "gi-corpus 1";
    private const int StreamsPerMode = 300;
    private const int SeedBase = 2_000_000;
    private const int TransientStreamsPerMode = 100;
    private const int TransientSeedBase = 3_000_000;

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
                var setup = StreamGenerator.Generate(TransientSeedBase + (int)mode * 100_000 + i, mode,
                    StreamProfile.Transient);
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
