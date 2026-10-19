using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace Gi.Stats;

internal static class Report
{
    public static void WriteError(Options options, string error)
    {
        if (!options.Json)
        {
            Console.Error.WriteLine(error);
            return;
        }

        using var output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteNumber("schema_version"u8, 1);
        writer.WriteBoolean("success"u8, false);
        writer.WriteString("command"u8, options.Command);
        writer.WriteString("error"u8, error);
        writer.WriteEndObject();
        writer.Flush();
        output.WriteByte((byte)'\n');
    }

    public static void Write(Options options, bool success, Receipt[] receipts, Snapshot snapshot = default,
        long fieldSum = 0, Measurement[]? measurements = null)
    {
        if (!options.Json)
        {
            WriteHuman(options, success, receipts, snapshot, fieldSum, measurements);
            return;
        }

        using var output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version"u8, 1);
        writer.WriteBoolean("success"u8, success);
        writer.WriteString("command"u8, options.Command);
        writer.WriteBoolean("hardware_intrinsics"u8, Vector128.IsHardwareAccelerated);
        writer.WriteString("resolver"u8, Avx2.IsSupported ? "avx2" :
            Sse2.IsSupported ? "sse2" :
            AdvSimd.IsSupported ? "advsimd" : "scalar");
        writer.WriteStartArray("receipts"u8);
        foreach (var receipt in receipts)
        {
            writer.WriteStartObject();
            writer.WriteString("name"u8, receipt.Name);
            writer.WriteBoolean("passed"u8, receipt.Passed);
            writer.WriteNumber("value"u8, receipt.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (snapshot.Valid)
        {
            writer.WriteStartObject("workload"u8);
            writer.WriteNumber("power"u8, options.Power);
            writer.WriteNumber("cells_per_side"u8, 1 << options.Power);
            writer.WriteString("stamp"u8, options.Raster ? "raster" : "box");
            writer.WriteNumber("stamp_size"u8, options.StampSize);
            writer.WriteNumber("iterations"u8, options.Iterations);
            writer.WriteNumber("queries_per_frame"u8, options.Queries);
            writer.WriteNumber("moves_per_frame"u8, Math.Min(options.Moves, options.Sources));
            writer.WriteEndObject();
            writer.WriteStartObject("stats"u8);
            writer.WriteNumber("grids"u8, snapshot.Grids);
            writer.WriteNumber("layers"u8, snapshot.Layers);
            writer.WriteNumber("live_sources"u8, snapshot.LiveSources);
            writer.WriteNumber("source_slots"u8, snapshot.SourceSlots);
            writer.WriteNumber("removed_sources"u8, snapshot.SourceSlots - snapshot.LiveSources);
            writer.WriteNumber("source_capacity"u8, snapshot.SourceCapacity);
            writer.WriteNumber("possible_tiles"u8, snapshot.PossibleTiles);
            writer.WriteNumber("live_tiles"u8, snapshot.LiveTiles);
            writer.WriteNumber("dirty_tiles"u8, snapshot.DirtyTiles);
            writer.WriteNumber("directories"u8, snapshot.Directories);
            writer.WriteNumber("slab_free_units"u8, snapshot.SlabFreeUnits);
            writer.WriteNumber("catalog_stamps"u8, snapshot.Stamps);
            writer.WriteNumber("catalog_raster_stamps"u8, snapshot.RasterStamps);
            writer.WriteNumber("field_sum"u8, fieldSum);
            writer.WriteEndObject();

            writer.WriteStartObject("memory"u8);
            writer.WriteString("accounting"u8,
                "requested native bytes; selected world plus process-wide arenas and stamps; excludes allocator overhead");
            writer.WriteNumber("world_bytes"u8, snapshot.WorldBytes);
            writer.WriteNumber("shared_bytes"u8, snapshot.SharedBytes);
            writer.WriteNumber("native_bytes"u8, snapshot.NativeBytes);
            writer.WriteStartObject("world"u8);
            writer.WriteNumber("grids_bytes"u8, snapshot.GridBytes);
            writer.WriteNumber("layers_bytes"u8, snapshot.LayerBytes);
            writer.WriteNumber("sources_bytes"u8, snapshot.SourceBytes);
            writer.WriteNumber("scratch_bytes"u8, snapshot.ScratchBytes);
            writer.WriteNumber("dirty_flags_bytes"u8, snapshot.DirtyFlagBytes);
            writer.WriteNumber("dirty_queues_bytes"u8, snapshot.DirtyQueueBytes);
            writer.WriteNumber("directories_bytes"u8, snapshot.DirectoryBytes);
            writer.WriteNumber("slabs_bytes"u8, snapshot.SlabBytes);
            writer.WriteNumber("int32_planes_bytes"u8, snapshot.Int32Bytes);
            writer.WriteNumber("tent_planes_bytes"u8, snapshot.TentBytes);
            writer.WriteNumber("bell_planes_bytes"u8, snapshot.BellBytes);
            writer.WriteNumber("derived_tiles"u8, snapshot.DerivedTiles);
            writer.WriteNumber("query_pages_bytes"u8, snapshot.PageBytes);
            writer.WriteNumber("summaries_bytes"u8, snapshot.SummaryBytes);
            writer.WriteNumber("tile_sets_bytes"u8, snapshot.SetBytes);
            writer.WriteNumber("deposit_records_bytes"u8, snapshot.RecordBytes);
            writer.WriteNumber("kernel_cache_bytes"u8, snapshot.KernelBytes);
            writer.WriteNumber("max_pyramids_bytes"u8, snapshot.PyramidBytes);
            writer.WriteNumber("epoch_trees_bytes"u8, snapshot.EpochTreeBytes);
            writer.WriteEndObject();
            writer.WriteStartObject("shared"u8);
            writer.WriteNumber("world_arena_bytes"u8, snapshot.WorldArenaBytes);
            writer.WriteNumber("world_revision_bytes"u8, snapshot.WorldRevisionBytes);
            writer.WriteNumber("zero_dense_bytes"u8, snapshot.ZeroDenseBytes);
            writer.WriteNumber("stamp_arena_bytes"u8, snapshot.StampArenaBytes);
            writer.WriteNumber("raster_samples_bytes"u8, snapshot.StampRasterBytes);
            writer.WriteEndObject();
            using var process = Process.GetCurrentProcess();
            writer.WriteStartObject("runtime"u8);
            writer.WriteNumber("managed_heap_bytes"u8, GC.GetTotalMemory(false));
            writer.WriteNumber("working_set_bytes"u8, process.WorkingSet64);
            writer.WriteNumber("private_process_bytes"u8, process.PrivateMemorySize64);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteStartArray("measurements"u8);
        if (measurements != null)
            foreach (var measurement in measurements)
            {
                writer.WriteStartObject();
                writer.WriteString("name"u8, measurement.Name);
                writer.WriteNumber("operations"u8, measurement.Operations);
                writer.WriteNumber("elapsed_ticks"u8, measurement.ElapsedTicks);
                writer.WriteNumber("nanoseconds_per_operation"u8, measurement.NanosecondsPerOperation);
                writer.WriteNumber("allocated_bytes"u8, measurement.AllocatedBytes);
                writer.WriteNumber("checksum"u8, measurement.Checksum);
                writer.WriteEndObject();
            }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        output.WriteByte((byte)'\n');
    }

    private static void WriteHuman(Options options, bool success, Receipt[] receipts, Snapshot snapshot,
        long fieldSum, Measurement[]? measurements)
    {
        Console.WriteLine($"Gi {options.Command}: {(success ? "PASS" : "FAIL")}");
        foreach (var receipt in receipts)
            Console.WriteLine($"{(receipt.Passed ? "PASS" : "FAIL")} {receipt.Name}: {receipt.Value}");
        if (snapshot.Valid)
        {
            Console.WriteLine(
                $"grids: {snapshot.Grids}; layers: {snapshot.Layers}; sources: {snapshot.LiveSources}/{snapshot.SourceCapacity}; field sum: {fieldSum}");
            Console.WriteLine(
                $"tiles: {snapshot.LiveTiles}/{snapshot.PossibleTiles}; dirty: {snapshot.DirtyTiles}; directories: {snapshot.Directories}; slab free units: {snapshot.SlabFreeUnits}");
            Console.WriteLine(
                $"native requested bytes: {snapshot.NativeBytes}; selected world: {snapshot.WorldBytes}; shared arenas + stamps: {snapshot.SharedBytes}");
            Console.WriteLine(
                $"grids: {snapshot.GridBytes}; layers: {snapshot.LayerBytes}; sources: {snapshot.SourceBytes}; scratch: {snapshot.ScratchBytes}");
            Console.WriteLine(
                $"dirty flags: {snapshot.DirtyFlagBytes}; queues: {snapshot.DirtyQueueBytes}; directories: {snapshot.DirectoryBytes}; slabs: {snapshot.SlabBytes}");
            Console.WriteLine(
                $"int32 planes: {snapshot.Int32Bytes}; tent: {snapshot.TentBytes} ({snapshot.TentTiles} tiles); bell: {snapshot.BellBytes} ({snapshot.BellTiles} tiles); query pages: {snapshot.PageBytes}; summaries: {snapshot.SummaryBytes}; records: {snapshot.RecordBytes}; kernels: {snapshot.KernelBytes}; max pyramids: {snapshot.PyramidBytes}");
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(
                $"managed heap: {GC.GetTotalMemory(false)}; working set: {process.WorkingSet64}; private process: {process.PrivateMemorySize64}");
        }

        if (measurements != null)
            foreach (var measurement in measurements)
                Console.WriteLine(
                    $"{measurement.Name}: {measurement.NanosecondsPerOperation:F2} ns/op; {measurement.AllocatedBytes} B; checksum: {measurement.Checksum}");
    }
}
