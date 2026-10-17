using Xunit;

namespace Gi.Tests;

public unsafe class StreamDifferentialTests
{
    private const int GateStreams = 10_500;
    private const int GateSeedBase = 900_000;
    private const int TransientStreams = 1200;
    private const int TransientSeedBase = 950_000;
    private const int HeavyStreams = 48;
    private const int HeavySeedBase = 500_000;
    private const int FoldSeedBase = 1_200_000;

    [Fact]
    public void GateStreamsPassAtExactCellAndQuarter()
    {
        for (var i = 0; i < GateStreams; i++)
        {
            var setup = StreamGenerator.Generate(GateSeedBase + i, (PrecisionMode)(i % 3), StreamProfile.Gate);
            var mismatch = StreamRunner.TryRun(setup, setup.Commands);
            if (mismatch == null) continue;

            var minimized = StreamMinimizer.Minimize(setup, setup.Commands, null);
            throw new Xunit.Sdk.XunitException(
                $"{mismatch.Message}\nminimized to {minimized.Count} commands\n{StreamMinimizer.PrintAsCSharpTest(setup, minimized)}");
        }
    }

    [Fact]
    public void TransientStreamsPassAtExactCellAndQuarter()
    {
        for (var i = 0; i < TransientStreams; i++)
        {
            var setup = StreamGenerator.Generate(TransientSeedBase + i, (PrecisionMode)(i % 3), StreamProfile.Transient);
            var mismatch = StreamRunner.TryRun(setup, setup.Commands);
            if (mismatch == null) continue;

            var minimized = StreamMinimizer.Minimize(setup, setup.Commands, null);
            throw new Xunit.Sdk.XunitException(
                $"{mismatch.Message}\nminimized to {minimized.Count} commands\n{StreamMinimizer.PrintAsCSharpTest(setup, minimized)}");
        }
    }

    [Fact]
    public void HeavyStreamsCoverArcsTurnsTimersRewindClearAndMixedPrecisions()
    {
        for (var i = 0; i < HeavyStreams; i++)
        {
            var mode = (PrecisionMode)(3 + i % 2);
            var setup = StreamGenerator.Generate(HeavySeedBase + i, mode, StreamProfile.Heavy);
            var mismatch = StreamRunner.TryRun(setup, setup.Commands);
            if (mismatch == null) continue;

            var minimized = StreamMinimizer.Minimize(setup, setup.Commands, null);
            throw new Xunit.Sdk.XunitException(
                $"{mismatch.Message}\nminimized to {minimized.Count} commands\n{StreamMinimizer.PrintAsCSharpTest(setup, minimized)}");
        }
    }

    [Theory]
    [InlineData(1L, 1L, 0)]
    [InlineData(4_100L, 1L << 42, 1)]
    [InlineData(70_000L, 1L << 47, 2)]
    public void FoldedPlanesStayExactAgainstTheInterpreter(long limitI, long limitQ40, int variant)
    {
        const int Streams = 250;
        for (var i = 0; i < Streams; i++)
        {
            var seed = FoldSeedBase + variant * 10_000 + i;
            var setup = StreamGenerator.Generate(seed, (PrecisionMode)(i % 3), StreamProfile.Gate);
            var mismatch = StreamRunner.TryRun(setup, setup.Commands, fold: (limitI, limitQ40));
            if (mismatch == null) continue;

            var minimized = StreamMinimizer.Minimize(setup, setup.Commands, null, (limitI, limitQ40));
            throw new Xunit.Sdk.XunitException(
                $"seed {seed} limits {limitI}/{limitQ40}: {mismatch.Message}\nminimized to {minimized.Count} commands\n{StreamMinimizer.PrintAsCSharpTest(setup, minimized)}");
        }

        for (var i = 0; i < 90; i++)
        {
            var seed = FoldSeedBase + 50_000 + variant * 1_000 + i;
            var setup = StreamGenerator.Generate(seed, (PrecisionMode)(i % 3), StreamProfile.Transient);
            var mismatch = StreamRunner.TryRun(setup, setup.Commands, fold: (limitI, limitQ40));
            if (mismatch == null) continue;

            var minimized = StreamMinimizer.Minimize(setup, setup.Commands, null, (limitI, limitQ40));
            throw new Xunit.Sdk.XunitException(
                $"transient seed {seed} limits {limitI}/{limitQ40}: {mismatch.Message}\nminimized to {minimized.Count} commands\n{StreamMinimizer.PrintAsCSharpTest(setup, minimized)}");
        }

        for (var i = 0; i < 12; i++)
        {
            var seed = FoldSeedBase + 90_000 + variant * 100 + i;
            var setup = StreamGenerator.Generate(seed, (PrecisionMode)(3 + i % 2), StreamProfile.Heavy);
            var mismatch = StreamRunner.TryRun(setup, setup.Commands, fold: (limitI, limitQ40));
            if (mismatch == null) continue;

            throw new Xunit.Sdk.XunitException($"heavy seed {seed} limits {limitI}/{limitQ40}: {mismatch.Message}");
        }
    }

    [Fact]
    public void SeededRoundingBugIsCaughtAndMinimizedBelowTenCommands()
    {
        for (var trial = 0; trial < 3; trial++)
        {
            var (setup, bug) = SetupWithNonzeroCell(700_000 + trial * 17, (PrecisionMode)(trial % 3));
            var caught = StreamRunner.TryRun(setup, setup.Commands, bug);
            Assert.NotNull(caught);

            var minimized = StreamMinimizer.Minimize(setup, setup.Commands, bug);
            Assert.True(minimized.Count < 10, $"trial {trial}: minimized to {minimized.Count} commands");

            var stillCaught = StreamRunner.TryRun(setup, minimized, bug);
            Assert.NotNull(stillCaught);

            var printed = StreamMinimizer.PrintAsCSharpTest(setup, minimized);
            Assert.Equal(printed, StreamMinimizer.PrintAsCSharpTest(setup, minimized));
            Assert.Contains("StreamRunner.TryRun", printed);
        }
    }

    private static (StreamSetup Setup, BugInjection Bug) SetupWithNonzeroCell(int startSeed, PrecisionMode mode)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var setup = StreamGenerator.Generate(startSeed + attempt * 101, mode, StreamProfile.Gate);
            Assert.Null(StreamRunner.TryRun(setup, setup.Commands));
            var found = FindNonzeroCell(setup);
            if (found == null) continue;
            var cell = found.Value;
            return (setup, new BugInjection
            {
                AfterProcess = cell.Process,
                Grid = cell.Grid,
                Layer = cell.Layer,
                Cell = cell.Cell,
                Delta = 1,
            });
        }

        throw new Xunit.Sdk.XunitException($"no stream with a nonzero page from seed {startSeed}");
    }

    private static (int Process, byte Grid, byte Layer, int Cell)? FindNonzeroCell(StreamSetup setup)
    {
        (int Process, byte Grid, byte Layer, int Cell)? found = null;
        StreamRunner.Run(setup, setup.Commands, null, (process, grid, layer, page) =>
        {
            if (found != null) return;
            for (var cell = 0; cell < page.Length; cell++)
            {
                if (page[cell] == 0) continue;
                found = (process, grid, layer, cell);
                break;
            }
        });

        return found;
    }
}
