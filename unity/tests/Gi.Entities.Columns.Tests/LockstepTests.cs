using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class LockstepTests
{
    [Fact]
    public void ARecordedTickIsComparedPerLayerAndOnlyWhileItIsInTheWindow()
    {
        var lockstep = new GiLockstep(3);
        Assert.Equal(GiLockstepVerdict.Unknown, lockstep.Check(5, 0, 1ul));
        Assert.True(lockstep.Ahead(0));
        lockstep.Record(5, new ulong[] { 10, 20, 30 });
        Assert.Equal(5, lockstep.Latest);
        Assert.Equal(GiLockstepVerdict.Match, lockstep.Check(5, 1, 20ul));
        Assert.Equal(GiLockstepVerdict.Mismatch, lockstep.Check(5, 2, 31ul));
        Assert.Equal(GiLockstepVerdict.Unknown, lockstep.Check(6, 0, 10ul));
        Assert.Equal(GiLockstepVerdict.Unknown, lockstep.Check(5, 3, 10ul));
        Assert.Equal((2, 1, 5), (lockstep.Checks, lockstep.Mismatches, lockstep.FirstMismatchTick));
        Assert.False(lockstep.Ahead(5));
        Assert.True(lockstep.Ahead(6));
    }

    [Fact]
    public void TheWindowOverwritesTheOldestTickAndReportsItExpired()
    {
        var lockstep = new GiLockstep(1);
        for (var tick = 1; tick <= GiLockstep.Window + 40; tick++) lockstep.Record(tick, new[] { (ulong)tick * 7 });

        Assert.Equal(GiLockstepVerdict.Unknown, lockstep.Check(3, 0, 21ul));
        Assert.True(lockstep.Expired(3));
        Assert.False(lockstep.Expired(GiLockstep.Window + 40 - 10));
        Assert.Equal(GiLockstepVerdict.Match, lockstep.Check(GiLockstep.Window + 40 - 10, 0, (ulong)(GiLockstep.Window + 30) * 7));
        Assert.True(lockstep.TryGet(GiLockstep.Window + 40, 0, out var newest));
        Assert.Equal((ulong)(GiLockstep.Window + 40) * 7, newest);
        Assert.False(lockstep.TryGet(-1, 0, out _));
    }

    [Fact]
    public void TheFirstMismatchKeepsTheEarliestTick()
    {
        var lockstep = new GiLockstep(1);
        for (var tick = 10; tick <= 14; tick++) lockstep.Record(tick, new[] { (ulong)tick });

        lockstep.Check(13, 0, 0ul);
        lockstep.Check(11, 0, 0ul);
        lockstep.Check(12, 0, 12ul);
        Assert.Equal(11, lockstep.FirstMismatchTick);
        Assert.Equal(2, lockstep.Mismatches);
        Assert.Equal(3, lockstep.Checks);
    }
}
