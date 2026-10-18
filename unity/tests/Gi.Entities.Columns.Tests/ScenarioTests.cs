using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Gi.Entities.Tests.Support;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class ScenarioTests
{
    private static readonly GiScenarioOptions Options = GiScenarioOptions.Standard;

    [Fact]
    public void RandomMatchesThePcg32ReferenceVector()
    {
        var rng = new GiRandom(42ul);
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        foreach (var value in expected) Assert.Equal(value, rng.NextUInt());
    }

    [Fact]
    public void BoundedDrawsStayInRangeAndCoverIt()
    {
        var rng = new GiRandom(9ul);
        var seen = new bool[10];
        for (var i = 0; i < 2000; i++)
        {
            var value = rng.Next(10);
            Assert.InRange(value, 0, 9);
            seen[value] = true;
        }

        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void ShuffleKeepsEveryElement()
    {
        var rng = new GiRandom(5ul);
        var values = new int[64];
        for (var i = 0; i < values.Length; i++) values[i] = i;
        rng.Shuffle(values);
        var sorted = (int[])values.Clone();
        Array.Sort(sorted);
        for (var i = 0; i < sorted.Length; i++) Assert.Equal(i, sorted[i]);
        Assert.NotEqual(Enumerable.Range(0, 64), values);
    }

    [Fact]
    public void QuartersPrintExactlyAndWithoutCulture()
    {
        Assert.Equal("12.25", GiScenario.Quarters(49));
        Assert.Equal("-0.50", GiScenario.Quarters(-2));
        Assert.Equal("0.00", GiScenario.Quarters(0));
        Assert.Equal("56.75", GiScenario.Quarters(227));
    }

    [Fact]
    public void OneSeedPrintsByteIdenticalTextOnEveryRun()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var first = GiScenarioGenerator.Create(seed, Options).ToText();
            var second = GiScenarioGenerator.Create(seed, Options).ToText();
            Assert.Equal(first, second);
            Assert.DoesNotContain('\r', first);
            Assert.Equal(first.Length, Encoding.UTF8.GetByteCount(first));
        }
    }

    [Fact]
    public void DistinctSeedsPrintDistinctText()
    {
        var texts = new HashSet<string>();
        for (ulong seed = 1; seed <= 20; seed++) texts.Add(GiScenarioGenerator.Create(seed, Options).ToText());
        Assert.Equal(20, texts.Count);
    }

    [Fact]
    public void SeedOneTextDigestIsPinned()
    {
        var text = GiScenarioGenerator.Create(1, Options).ToText();
        var hash = 14695981039346656037ul;
        foreach (var b in Encoding.UTF8.GetBytes(text))
            unchecked
            {
                hash ^= b;
                hash *= 1099511628211ul;
            }

        Assert.Equal(0x540E715AE2F015E1ul, hash);
    }

    [Fact]
    public void ScenariosExerciseEveryOperationKind()
    {
        var seen = new HashSet<GiScenarioOpKind>();
        var rampedSpawns = 0;
        var disabledSpawns = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            foreach (var op in AllOps(GiScenarioGenerator.Create(seed, Options)))
            {
                seen.Add(op.Kind);
                if (op.Kind != GiScenarioOpKind.Spawn) continue;
                if (op.Ticks > 0) rampedSpawns++;
                if ((op.Flags & GiScenarioFlags.Disabled) != 0) disabledSpawns++;
            }
        }

        foreach (var kind in Enum.GetValues<GiScenarioOpKind>()) Assert.Contains(kind, seen);
        Assert.True(rampedSpawns > 0);
        Assert.True(disabledSpawns > 0);
    }

    [Fact]
    public void EachEntityTouchesDisjointStatePerTick()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            foreach (var tick in GiScenarioGenerator.Create(seed, Options).Ticks)
            {
                var kinds = new Dictionary<int, HashSet<GiScenarioOpKind>>();
                foreach (var op in tick.Ops)
                {
                    if (op.Entity < 0) continue;
                    if (!kinds.TryGetValue(op.Entity, out var set)) kinds[op.Entity] = set = [];
                    Assert.True(set.Add(op.Kind), $"seed {seed}: entity {op.Entity} repeats {op.Kind}");
                }

                foreach (var set in kinds.Values)
                {
                    if (set.Contains(GiScenarioOpKind.Despawn)) Assert.Single(set);
                    Assert.False(set.Contains(GiScenarioOpKind.Disable) && set.Contains(GiScenarioOpKind.Enable));
                    Assert.False(set.Contains(GiScenarioOpKind.Spawn) && set.Count > 1);
                }
            }
        }
    }

    [Fact]
    public void ReferenceHashesAreIdenticalOnEveryRun()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, Options);
            var first = GiReferenceExecutor.Run(scenario);
            var second = GiReferenceExecutor.Run(scenario);
            Assert.Equal(string.Empty, first.Compare(second));
            Assert.Equal(first.Digest(), second.Digest());
            Assert.Equal(first.ToText(), second.ToText());
        }
    }

    [Fact]
    public void DistinctSeedsProduceDistinctFields()
    {
        var digests = new HashSet<ulong>();
        for (ulong seed = 1; seed <= 10; seed++)
            digests.Add(GiReferenceExecutor.Run(GiScenarioGenerator.Create(seed, Options)).Digest());
        Assert.Equal(10, digests.Count);
    }

    [Fact]
    public void ShufflingOperationOrderKeepsEveryHash()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, Options);
            var expected = GiReferenceExecutor.Run(scenario);
            for (ulong shuffle = 1; shuffle <= 3; shuffle++)
            {
                var actual = GiReferenceExecutor.Run(scenario.WithOpOrderShuffled(seed * 1000 + shuffle));
                Assert.Equal(string.Empty, expected.Compare(actual));
            }
        }
    }

    [Fact]
    public void ShufflingChangesOnlyTheOrder()
    {
        var scenario = GiScenarioGenerator.Create(3, Options);
        var shuffled = scenario.WithOpOrderShuffled(11);
        var moved = false;
        for (var t = 0; t < scenario.Ticks.Length; t++)
        {
            Assert.Equal(scenario.Ticks[t].Ops.Length, shuffled.Ticks[t].Ops.Length);
            var a = scenario.Ticks[t].Ops.Select(op => op.Kind.ToString() + op.Entity).Order().ToArray();
            var b = shuffled.Ticks[t].Ops.Select(op => op.Kind.ToString() + op.Entity).Order().ToArray();
            Assert.Equal(a, b);
            moved |= !scenario.Ticks[t].Ops.SequenceEqual(shuffled.Ticks[t].Ops);
        }

        Assert.True(moved);
    }

    [Fact]
    public void ExecutorLeavesNoWorldBehind()
    {
        var before = LiveWorlds();
        for (ulong seed = 1; seed <= 300; seed++)
            GiReferenceExecutor.Run(GiScenarioGenerator.Create(seed, new GiScenarioOptions(2, 6, 12, 2, true, true)));
        Assert.Equal(before, LiveWorlds());
    }

    private static IEnumerable<GiScenarioOp> AllOps(GiScenario scenario)
    {
        return scenario.Ticks.SelectMany(tick => tick.Ops);
    }

    private static int LiveWorlds()
    {
        Span<byte> ids = stackalloc byte[256];
        return World.CopyWorldIds(ids);
    }
}
