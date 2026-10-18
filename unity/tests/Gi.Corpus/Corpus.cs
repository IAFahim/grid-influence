using System.Globalization;
using Gi.Tests;

namespace Gi.Corpus;

public static class GiCorpus
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static string Build() => CorpusBuilder.Build();

    public static IReadOnlyList<ulong> Expected => CorpusDigests.Groups;

    public static ulong[] Digest(string text, out string[] heads)
    {
        var digests = new List<ulong>();
        var names = new List<string>();
        var hash = Offset;
        var open = false;
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text.AsSpan(start, end - start);
            if (!open || line.StartsWith("stream ", StringComparison.Ordinal) || line.StartsWith("mode ", StringComparison.Ordinal))
            {
                if (open) digests.Add(hash);
                hash = Offset;
                open = true;
                names.Add(line.ToString());
            }

            foreach (var c in line) hash = (hash ^ (byte)c) * Prime;
            hash = (hash ^ (byte)'\n') * Prime;
            start = end + 1;
        }

        if (open) digests.Add(hash);
        heads = names.ToArray();
        return digests.ToArray();
    }

    public static string Replay()
    {
        var digests = Digest(Build(), out var heads);
        var expected = CorpusDigests.Groups;
        var shared = Math.Min(digests.Length, expected.Length);
        for (var i = 0; i < shared; i++)
        {
            if (digests[i] != expected[i])
                return "group " + i.ToString(CultureInfo.InvariantCulture) + " (" + heads[i] + ") differs from the committed corpus";
        }

        return digests.Length == expected.Length
            ? string.Empty
            : "group count " + digests.Length.ToString(CultureInfo.InvariantCulture) + " differs from the committed " +
              expected.Length.ToString(CultureInfo.InvariantCulture);
    }
}
