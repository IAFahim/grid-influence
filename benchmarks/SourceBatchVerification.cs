using System.Diagnostics;
using Gi;

internal static partial class Verification
{
    private readonly record struct SourceBatchInput(int Source, float PositionX, float PositionY,
        float Heading, float Scale, sbyte Gain);

    private readonly record struct SourceBatchMeasurement(double SerialMicroseconds,
        double BatchMicroseconds, long AllocatedBytes, long Checksum, bool Matches);

    private static bool SourceBatchesMatchSerialAndAllocateNothing()
    {
        var measurement = MeasureSourceBatch(16384, 4, 8);
        return measurement.Matches && measurement.AllocatedBytes == 0 && measurement.Checksum != 0;
    }

    private static bool SourceBatchesRetractAcrossLayers()
    {
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        Grid.New(world, 5, 0f, 0f, 64f);
        Layer.New(world);
        Layer.New(world);
        Layer.Sum(world, 0, 1, 1, -1);
        var stamps = new[] { Stamp.Box(3, 3, 5), Stamp.Tent(5, 5, 10), Stamp.Bell(5, 5, 10) };
        try
        {
            const int count = 96;
            var commands = new World.SourceCommand[count * 2];
            var results = new World.SourceResult[count * 2];
            for (var index = 0; index < count; index++)
                commands[index] = World.SourceCommand.Place(0, 10.5f, 10.5f, stamps[index % stamps.Length], 2);
            if (World.ApplyBatch(world, commands.AsSpan(0, count), results) != World.BatchStatus.Applied) return false;
            World.Process(world);
            var initial = World.Query(world, 0, 0, 0, 0, 64, 64);
            World.Record(world);
            for (var index = 0; index < count; index++)
            {
                commands[index] = World.SourceCommand.Remove(results[index].SourceId);
                commands[count + index] = World.SourceCommand.Place(1, 40.5f, 40.5f, stamps[index % stamps.Length], 3);
            }

            if (World.ApplyBatch(world, commands, results) != World.BatchStatus.Applied) return false;
            foreach (var result in results)
                if (result.Status != World.SourceResultStatus.Applied) return false;
            World.Process(world);
            if (World.Query(world, 0, 0, 0, 0, 64, 64) != 0 || World.Query(world, 1, 0, 0, 0, 32, 32) != 0 ||
                World.Query(world, 0, 1, 0, 0, 64, 64) <= 0 || World.QueryAt(world, 0, 2, 40.5f, 40.5f) >= 0)
                return false;
            World.Rewind(world);
            World.Process(world);
            return initial > 0 && World.Query(world, 0, 0, 0, 0, 64, 64) == initial &&
                World.Query(world, 0, 1, 0, 0, 64, 64) == 0;
        }
        finally
        {
            World.Free(world);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    public static int SourceBatchTiming()
    {
        if (!SourceBatchesMatchSerialAndAllocateNothing())
        {
            Console.WriteLine("FAIL source-batch-serial-parity-and-warm-allocation");
            return 1;
        }

        Console.WriteLine("PASS source-batch-serial-parity-and-warm-allocation");
        foreach (var count in new[] { 64, 4096, 65536 })
        {
            var measurement = MeasureSourceBatch(count, 32, 80);
            if (!measurement.Matches || measurement.AllocatedBytes != 0)
            {
                Console.WriteLine($"FAIL source-batch-{count}: match {measurement.Matches}, allocation {measurement.AllocatedBytes}");
                return 1;
            }

            Console.WriteLine($"{count} full source updates: serial {measurement.SerialMicroseconds:F1} us, " +
                $"batch {measurement.BatchMicroseconds:F1} us, " +
                $"{count / measurement.BatchMicroseconds:F1} million updates/s, " +
                $"{measurement.AllocatedBytes} B, checksum {measurement.Checksum}");
        }

        return 0;
    }

    private static unsafe SourceBatchMeasurement MeasureSourceBatch(int count, int warmFrames, int measuredFrames)
    {
        var serialWorld = World.New();
        var batchWorld = World.New();
        Grid.New(serialWorld, 8, 0f, 0f, 256f);
        Grid.New(batchWorld, 8, 0f, 0f, 256f);
        Layer.New(serialWorld);
        Layer.New(batchWorld);
        var stamp = Stamp.Box(3, 3, 5);
        try
        {
            var creations = new World.SourceCommand[count];
            var results = new World.SourceResult[count];
            var firstInputs = new SourceBatchInput[count];
            var secondInputs = new SourceBatchInput[count];
            var firstCommands = new World.SourceCommand[count];
            var secondCommands = new World.SourceCommand[count];
            var matches = true;
            for (var index = 0; index < count; index++)
            {
                var positionX = index % 250 + 3.5f;
                var positionY = index / 250 % 250 + 3.5f;
                var source = World.Place(serialWorld, 0, positionX, positionY, stamp, 1);
                creations[index] = World.SourceCommand.Place(0, positionX, positionY, stamp, 1);
                var first = new SourceBatchInput(source, positionX + 0.25f, positionY + 0.25f, 0.25f, 0.75f, 2);
                var second = new SourceBatchInput(source, positionX, positionY, 0f, 1f, 1);
                firstInputs[index] = first;
                secondInputs[index] = second;
                firstCommands[index] = World.SourceCommand.Update(first.Source, first.PositionX, first.PositionY,
                    first.Heading, first.Scale, first.Gain);
                secondCommands[index] = World.SourceCommand.Update(second.Source, second.PositionX, second.PositionY,
                    second.Heading, second.Scale, second.Gain);
            }

            matches &= World.ApplyBatch(batchWorld, creations, results) == World.BatchStatus.Applied;
            for (var index = 0; index < count; index++)
                matches &= results[index].Status == World.SourceResultStatus.Applied && results[index].SourceId == firstInputs[index].Source;
            World.Process(serialWorld);
            World.Process(batchWorld);

            long serialTicks = 0, batchTicks = 0, checksum = 0;
            long allocated = 0;
            var before = 0L;
            for (var frame = 0; frame < warmFrames + measuredFrames; frame++)
            {
                if (frame == warmFrames) before = GC.GetAllocatedBytesForCurrentThread();
                var inputs = (frame & 1) == 0 ? firstInputs : secondInputs;
                var commands = (frame & 1) == 0 ? firstCommands : secondCommands;
                var start = Stopwatch.GetTimestamp();
                foreach (ref readonly var input in inputs.AsSpan())
                {
                    World.Move(serialWorld, input.Source, input.PositionX, input.PositionY);
                    World.Turn(serialWorld, input.Source, input.Heading);
                    World.Scale(serialWorld, input.Source, input.Scale);
                    World.SetGain(serialWorld, input.Source, input.Gain);
                }

                var end = Stopwatch.GetTimestamp();
                if (frame >= warmFrames) serialTicks += end - start;
                World.Process(serialWorld);
                start = Stopwatch.GetTimestamp();
                var status = World.ApplyBatch(batchWorld, commands, results);
                end = Stopwatch.GetTimestamp();
                if (frame >= warmFrames) batchTicks += end - start;
                matches &= status == World.BatchStatus.Applied;
                World.Process(batchWorld);
                var serialSum = World.Query(serialWorld, 0, 0, 0, 0, 256, 256);
                var batchSum = World.Query(batchWorld, 0, 0, 0, 0, 256, 256);
                matches &= serialSum == batchSum;
                checksum += serialSum + batchSum;
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            for (var index = 0; index < count; index++)
                matches &= results[index].Status == World.SourceResultStatus.Applied;
            var serialCells = new short[256 * 256];
            var batchCells = new short[256 * 256];
            fixed (short* serialPointer = serialCells)
            fixed (short* batchPointer = batchCells)
            {
                World.QueryRegion(serialWorld, 0, 0, 0, 0, 256, 256, serialPointer);
                World.QueryRegion(batchWorld, 0, 0, 0, 0, 256, 256, batchPointer);
            }

            matches &= serialCells.AsSpan().SequenceEqual(batchCells);
            var factor = 1e6 / Stopwatch.Frequency / measuredFrames;
            return new SourceBatchMeasurement(serialTicks * factor, batchTicks * factor, allocated, checksum, matches);
        }
        finally
        {
            World.Free(serialWorld);
            World.Free(batchWorld);
            Stamp.Free(stamp);
        }
    }
}
