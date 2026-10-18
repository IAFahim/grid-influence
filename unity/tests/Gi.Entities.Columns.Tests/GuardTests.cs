using System;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class GuardTests
{
    private const int Process = 1;
    private const int Sense = 2;
    private const int Rogue = 3;

    private static nint Allocate()
    {
        var address = Marshal.AllocHGlobal(sizeof(GiGuardTable));
        new Span<byte>((void*)address, sizeof(GiGuardTable)).Clear();
        return address;
    }

    private sealed class ReaderWorker
    {
        public nint Address;
        public Barrier Start;
        public int Failures;

        public void Run()
        {
            var table = (GiGuardTable*)Address;
            Start.SignalAndWait();
            for (var n = 0; n < 50_000; n++)
            {
                if (!table->Begin(3, Sense, false)) Interlocked.Increment(ref Failures);
                table->End(3, false);
            }
        }
    }

    private sealed class LockedWorker
    {
        public nint Address;
        public ReaderWriterLockSlim Gate;
        public bool Write;
        public int Failures;

        public void Run()
        {
            var table = (GiGuardTable*)Address;
            for (var n = 0; n < 20_000; n++)
            {
                if (Write) Gate.EnterWriteLock();
                else Gate.EnterReadLock();
                if (!table->Begin(9, Write ? Process : Sense, Write)) Interlocked.Increment(ref Failures);
                table->End(9, Write);
                if (Write) Gate.ExitWriteLock();
                else Gate.ExitReadLock();
            }
        }
    }

    private sealed class HoldingWorker
    {
        public nint Address;
        public ManualResetEventSlim Holding;
        public ManualResetEventSlim Release;

        public void Run()
        {
            var table = (GiGuardTable*)Address;
            table->Begin(0, Process, true);
            Holding.Set();
            Release.Wait();
            table->End(0, true);
        }
    }

    [Fact]
    public void ReadersShareAWorldWithoutAConflict()
    {
        var table = new GiGuardTable();
        var t = &table;
        Assert.True(t->Begin(0, Sense, false));
        Assert.True(t->Begin(0, Sense, false));
        Assert.False(t->Idle(0));
        t->End(0, false);
        t->End(0, false);
        Assert.True(t->Idle(0));
        Assert.Equal(0, t->Total());
        Assert.False(t->Take(out _));
    }

    [Fact]
    public void WriterAfterWriterIsReportedWithBothTags()
    {
        var table = new GiGuardTable();
        var t = &table;
        Assert.True(t->Begin(4, Process, true));
        Assert.False(t->Begin(4, Rogue, true));

        Assert.True(t->Take(out var report));
        Assert.Equal(4, report.World);
        Assert.Equal(Rogue, report.Intruder);
        Assert.Equal(Process, report.Holder);
        Assert.True(report.IntruderWrites);
        Assert.Equal(1, report.Count);
    }

    [Fact]
    public void ReaderDuringAWriteIsReported()
    {
        var table = new GiGuardTable();
        var t = &table;
        Assert.True(t->Begin(0, Process, true));
        Assert.False(t->Begin(0, Rogue, false));

        Assert.True(t->Take(out var report));
        Assert.Equal(Rogue, report.Intruder);
        Assert.Equal(Process, report.Holder);
        Assert.False(report.IntruderWrites);
    }

    [Fact]
    public void WriterDuringReadsIsReported()
    {
        var table = new GiGuardTable();
        var t = &table;
        Assert.True(t->Begin(0, Sense, false));
        Assert.False(t->Begin(0, Rogue, true));

        Assert.True(t->Take(out var report));
        Assert.Equal(Rogue, report.Intruder);
        Assert.Equal(Sense, report.Holder);
        Assert.True(report.IntruderWrites);
    }

    [Fact]
    public void EveryBeginIsBalancedByAnEndEvenAfterAConflict()
    {
        var table = new GiGuardTable();
        var t = &table;
        t->Begin(7, Process, true);
        t->Begin(7, Rogue, true);
        t->Begin(7, Sense, false);
        t->End(7, false);
        t->End(7, true);
        t->End(7, true);

        Assert.True(t->Idle(7));
        Assert.Equal(2, t->Total());
        Assert.True(t->Begin(7, Process, true));
    }

    [Fact]
    public void WorldsAreIndependent()
    {
        var table = new GiGuardTable();
        var t = &table;
        Assert.True(t->Begin(1, Process, true));
        Assert.True(t->Begin(2, Process, true));
        Assert.True(t->Begin(255, Sense, false));
        Assert.Equal(0, t->Total());
    }

    [Fact]
    public void OnlyTheFirstUntakenViolationKeepsItsDetails()
    {
        var table = new GiGuardTable();
        var t = &table;
        t->Begin(0, Process, true);
        t->Begin(0, Rogue, true);
        t->Begin(0, Sense, false);

        Assert.True(t->Take(out var first));
        Assert.Equal(Rogue, first.Intruder);
        Assert.Equal(2, first.Count);
        Assert.False(t->Take(out _));

        t->Begin(0, Sense, false);
        Assert.True(t->Take(out var second));
        Assert.Equal(Sense, second.Intruder);
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public void ManyReadersCountAtomicallyAndNeverConflict()
    {
        var address = Allocate();
        var start = new Barrier(8);
        var workers = new ReaderWorker[8];
        var threads = new Thread[8];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = new ReaderWorker { Address = address, Start = start };
            threads[i] = new Thread(workers[i].Run);
            threads[i].Start();
        }

        foreach (var thread in threads) thread.Join();
        start.Dispose();

        var t = (GiGuardTable*)address;
        foreach (var worker in workers) Assert.Equal(0, worker.Failures);
        Assert.Equal(0, t->Total());
        Assert.True(t->Idle(3));
        Marshal.FreeHGlobal(address);
    }

    [Fact]
    public void LockedReadersAndWritersNeverConflictAndEndIdle()
    {
        var address = Allocate();
        var gate = new ReaderWriterLockSlim();
        var workers = new LockedWorker[6];
        var threads = new Thread[6];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = new LockedWorker { Address = address, Gate = gate, Write = i % 3 == 0 };
            threads[i] = new Thread(workers[i].Run);
            threads[i].Start();
        }

        foreach (var thread in threads) thread.Join();
        gate.Dispose();

        var t = (GiGuardTable*)address;
        foreach (var worker in workers) Assert.Equal(0, worker.Failures);
        Assert.True(t->Idle(9));
        Marshal.FreeHGlobal(address);
    }

    [Fact]
    public void UnorderedWritersAreCaught()
    {
        var address = Allocate();
        var holding = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var worker = new HoldingWorker { Address = address, Holding = holding, Release = release };
        var holder = new Thread(worker.Run);
        holder.Start();
        holding.Wait();

        var t = (GiGuardTable*)address;
        Assert.False(t->Begin(0, Rogue, true));
        t->End(0, true);
        release.Set();
        holder.Join();
        release.Dispose();
        holding.Dispose();

        Assert.True(t->Take(out var report));
        Assert.Equal(Rogue, report.Intruder);
        Assert.Equal(Process, report.Holder);
        Assert.True(t->Idle(0));
        Marshal.FreeHGlobal(address);
    }

    [Fact]
    public void ResetClearsStateHoldersAndTheReport()
    {
        var table = new GiGuardTable();
        var t = &table;
        t->Begin(5, Process, true);
        t->Begin(5, Rogue, true);
        t->Reset();

        Assert.True(t->Idle(5));
        Assert.Equal(0, t->Total());
        Assert.False(t->Take(out _));
        Assert.True(t->Begin(5, Sense, true));
    }

    [Fact]
    public void TheOverlapFaultIsARealFaultKind()
    {
        var faults = new GiFaults();
        Assert.True(faults.Raise(GiFaultKind.Overlap, 9));
        Assert.True(faults.Has(GiFaultKind.Overlap));
        Assert.Equal(9, faults.FirstTick(GiFaultKind.Overlap));
        Assert.False(faults.Has(GiFaultKind.Reclaimed));
        Assert.Equal(6, GiFaults.KindCount);
    }
}
