using System.Diagnostics;
using Gi;

internal static unsafe partial class Verification
{
    private readonly record struct SyncTransformMeasurement(
        double SerialMicroseconds, double BatchMicroseconds, double SyncMicroseconds,
        long AllocatedBytes, long Checksum, bool Matches);

    private static bool SyncTransformsMatchOrderedCallsAndAllocateNothing()
    {
        var measurement = MeasureSyncTransforms(16384, 4, 8);
        return measurement.Matches && measurement.AllocatedBytes == 0 && measurement.Checksum != 0;
    }

    public static int SyncTransformTiming()
    {
        if (!SyncTransformsMatchOrderedCallsAndAllocateNothing())
        {
            Console.WriteLine("FAIL sync-transforms-match-ordered-calls");
            return 1;
        }

        Console.WriteLine("PASS sync-transforms-match-ordered-calls");
        Console.WriteLine(
            $"payload: 12 B/row position-only, 20 B/row full pose, {sizeof(World.TransformRange)} B descriptor; " +
            "0 full input copies; 1 planning scan; 1 bit/row invalid mask");
        Console.WriteLine(
            $"scratch: 0 B under {ResolvePool.ColumnThreshold} rows; large path " +
            $"{sizeof(SourceBatchEntry)} B per unique source + 4 B/entry lookup + ops reuse; " +
            "no command links, no per-row results");
        foreach (var count in new[] { 64, 4096, 65536, 100000 })
        {
            var (warmFrames, measuredFrames) = count >= 65536 ? (8, 16) : (32, 80);
            var measurement = MeasureSyncTransforms(count, warmFrames, measuredFrames);
            if (!measurement.Matches || measurement.AllocatedBytes != 0)
            {
                Console.WriteLine(
                    $"FAIL sync-transforms-{count}: match {measurement.Matches}, allocation {measurement.AllocatedBytes}");
                return 1;
            }

            Console.WriteLine($"{count} borrowed transform rows: serial {measurement.SerialMicroseconds:F1} us, " +
                $"batch {measurement.BatchMicroseconds:F1} us, borrowed {measurement.SyncMicroseconds:F1} us, " +
                $"{count / measurement.SyncMicroseconds:F1} million rows/s, " +
                $"{measurement.AllocatedBytes} B, checksum {measurement.Checksum}");
        }

        return 0;
    }

    private static SyncTransformMeasurement MeasureSyncTransforms(int count, int warmFrames, int measuredFrames)
    {
        var serialWorld = World.New();
        var batchWorld = World.New();
        var syncWorld = World.New();
        Grid.New(serialWorld, 8, 0f, 0f, 256f);
        Grid.New(batchWorld, 8, 0f, 0f, 256f);
        Grid.New(syncWorld, 8, 0f, 0f, 256f);
        Layer.New(serialWorld);
        Layer.New(batchWorld);
        Layer.New(syncWorld);
        var stamp = Stamp.Box(3, 3, 5);
        try
        {
            var ids = new int[count];
            var fades = new World.SourceCommand[count];
            var results = new World.SourceResult[count];
            var ax = new float[count];
            var ay = new float[count];
            var ah = new float[count];
            var aScale = new float[count];
            var bx = new float[count];
            var by = new float[count];
            var bh = new float[count];
            var bScale = new float[count];
            var firstCommands = new World.SourceCommand[count];
            var secondCommands = new World.SourceCommand[count];
            for (var index = 0; index < count; index++)
            {
                var x = index % 250 + 3.5f;
                var y = index / 250 % 250 + 3.5f;
                var source = World.Place(serialWorld, 0, x, y, stamp, 1);
                World.Place(batchWorld, 0, x, y, stamp, 1);
                World.Place(syncWorld, 0, x, y, stamp, 1);
                ids[index] = source;
                fades[index] = World.SourceCommand.Fade(source, 0, 50);
                var idle = index % 4 == 0;
                ax[index] = idle ? x : x + 0.25f;
                ay[index] = idle ? y : y + 0.25f;
                ah[index] = idle ? 0f : 0.25f * (index % 4);
                aScale[index] = idle ? 1f : 0.75f + (index % 5) * 0.25f;
                bx[index] = x;
                by[index] = y;
                bh[index] = 0f;
                bScale[index] = 1f;
                firstCommands[index] = World.SourceCommand.UpdateTransform(source, ax[index], ay[index], ah[index], aScale[index]);
                secondCommands[index] = World.SourceCommand.UpdateTransform(source, bx[index], by[index], 0f, 1f);
            }

            World.ApplyBatch(batchWorld, fades, results);
            World.ApplyBatch(syncWorld, fades, results);
            World.ApplyBatch(serialWorld, fades, results);
            World.Process(serialWorld);
            World.Process(batchWorld);
            World.Process(syncWorld);

            var totalFrames = warmFrames + measuredFrames;
            var events = new World.SourceCommand[totalFrames / 8][];
            for (var frame = 8; frame < totalFrames; frame += 8)
            {
                var removed = ids[frame / 8 * 37 % count];
                var fadeTarget = ids[frame / 8 * 53 % count];
                events[frame / 8 - 1] = new[]
                {
                    World.SourceCommand.Remove(removed),
                    World.SourceCommand.Fade(fadeTarget, 1, 60),
                };
            }

            var words = (count + 63) >> 6;
            var mask = new ulong[words];
            long serialTicks = 0, batchTicks = 0, syncTicks = 0, checksum = 0;
            long allocated = 0;
            var matches = true;
            var syncToken = World.Capture(syncWorld);
            fixed (int* idPointer = ids)
            fixed (float* axPointer = ax)
            fixed (float* ayPointer = ay)
            fixed (float* ahPointer = ah)
            fixed (float* aScalePointer = aScale)
            fixed (float* bxPointer = bx)
            fixed (float* byPointer = by)
            fixed (float* bhPointer = bh)
            fixed (float* bScalePointer = bScale)
            fixed (ulong* maskPointer = mask)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = axPointer, Ys = ayPointer,
                    Headings = ahPointer, Scales = aScalePointer,
                    Invalid = maskPointer, InvalidWords = words, Count = count
                };
                var before = 0L;
                for (var frame = 0; frame < totalFrames; frame++)
                {
                    if (frame == warmFrames) before = GC.GetAllocatedBytesForCurrentThread();
                    var first = (frame & 1) == 0;
                    range[0].Xs = first ? axPointer : bxPointer;
                    range[0].Ys = first ? ayPointer : byPointer;
                    range[0].Headings = first ? ahPointer : bhPointer;
                    range[0].Scales = first ? aScalePointer : bScalePointer;

                    var start = Stopwatch.GetTimestamp();
                    for (var index = 0; index < count; index++)
                    {
                        if (first)
                        {
                            World.Move(serialWorld, ids[index], ax[index], ay[index]);
                            World.Turn(serialWorld, ids[index], ah[index]);
                            World.Scale(serialWorld, ids[index], aScale[index]);
                        }
                        else
                        {
                            World.Move(serialWorld, ids[index], bx[index], by[index]);
                            World.Turn(serialWorld, ids[index], 0f);
                            World.Scale(serialWorld, ids[index], 1f);
                        }
                    }

                    var end = Stopwatch.GetTimestamp();
                    if (frame >= warmFrames) serialTicks += end - start;
                    start = Stopwatch.GetTimestamp();
                    matches &= World.ApplyBatch(batchWorld, first ? firstCommands : secondCommands, results) ==
                               World.BatchStatus.Applied;
                    end = Stopwatch.GetTimestamp();
                    if (frame >= warmFrames) batchTicks += end - start;
                    start = Stopwatch.GetTimestamp();
                    matches &= World.SyncTransforms(syncToken, new ReadOnlySpan<World.TransformRange>(range, 1)) ==
                               World.BatchStatus.Applied;
                    end = Stopwatch.GetTimestamp();
                    if (frame >= warmFrames) syncTicks += end - start;

                    if (frame >= 8 && frame % 8 == 0)
                    {
                        var lifecycle = events[frame / 8 - 1];
                        matches &= World.ApplyBatch(serialWorld, lifecycle, results) == World.BatchStatus.Applied;
                        matches &= World.ApplyBatch(batchWorld, lifecycle, results) == World.BatchStatus.Applied;
                        matches &= World.ApplyBatch(syncWorld, lifecycle, results) == World.BatchStatus.Applied;
                    }

                    World.Process(serialWorld);
                    World.Process(batchWorld);
                    World.Process(syncWorld);
                    var serialSum = World.Query(serialWorld, 0, 0, 0, 0, 256, 256);
                    matches &= serialSum == World.Query(batchWorld, 0, 0, 0, 0, 256, 256);
                    matches &= serialSum == World.Query(syncWorld, 0, 0, 0, 0, 256, 256);
                    checksum += serialSum;
                }

                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            var serialCells = new short[256 * 256];
            var batchCells = new short[256 * 256];
            var syncCells = new short[256 * 256];
            fixed (short* serialPointer = serialCells)
            fixed (short* batchPointer = batchCells)
            fixed (short* syncPointer = syncCells)
            {
                World.QueryRegion(serialWorld, 0, 0, 0, 0, 256, 256, serialPointer);
                World.QueryRegion(batchWorld, 0, 0, 0, 0, 256, 256, batchPointer);
                World.QueryRegion(syncWorld, 0, 0, 0, 0, 256, 256, syncPointer);
            }

            matches &= serialCells.AsSpan().SequenceEqual(batchCells);
            matches &= serialCells.AsSpan().SequenceEqual(syncCells);
            var factor = 1e6 / Stopwatch.Frequency / measuredFrames;
            return new SyncTransformMeasurement(
                serialTicks * factor, batchTicks * factor, syncTicks * factor, allocated, checksum, matches);
        }
        finally
        {
            World.Free(serialWorld);
            World.Free(batchWorld);
            World.Free(syncWorld);
            Stamp.Free(stamp);
        }
    }
}
