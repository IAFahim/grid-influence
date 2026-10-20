namespace Gi.Stats;

internal readonly record struct Receipt(string Name, bool Passed, long Value);

internal static unsafe class Receipts
{
    private const long SourceChunkBytes = 778_240 + 196_608 + 8_192 + 65_536;

    public static Receipt[] Run()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var other = Layer.New(world);
        Grid.New(world, 5, 128f, 128f, 32f);
        var empty = Inspection.Read(world);
        var box = Stamp.Box(4, 4, 60);
        ReadOnlySpan<sbyte> samples = [-10, 0, 0, 20];
        var raster = Stamp.New(samples, 2, 2);
        var a = World.Place(world, layer, 16f, 16f, box, 2);
        var b = World.Place(world, other, 48f, 48f, raster, 2);
        var pending = Inspection.Read(world);
        var pendingOk = a == new World.SourceId(0, 1) && b == new World.SourceId(1, 1) && pending.LiveSources == 2 &&
                        pending.SourceSlots == 2 &&
                        pending.SourceCapacity == 16_384 && pending.LiveTiles == 0 && pending.DirtyTiles == 0 &&
                        pending.GridBytes == World.MaxGrids * sizeof(GridCtx) &&
                        pending.LayerBytes == 2 * World.MaxLayers * sizeof(LayerData) +
                        World.MaxLayers * sizeof(LayerRecipe) &&
                        pending.SourceBytes == SourceChunkBytes && pending.DirectoryBytes == 0 &&
                        pending.SlabBytes == 0 &&
                        pending.DirtyFlagBytes == 0 && pending.DirtyQueueBytes == 0 &&
                        pending.Int32Bytes == 0 && pending.TentBytes == 0 && pending.KernelBytes == 0 &&
                        pending.QueueBytes == 16 * sizeof(int) + 16 * sizeof(JournalEntry) &&
                        pending.RecordBytes == 2L * 16_384 * sizeof(DepositRecord) &&
                        pending.WorldRevisionBytes == World.MaxWorlds * sizeof(int) &&
                        pending.ZeroDenseBytes == World.PageBytes &&
                        pending.StampRasterBytes - empty.StampRasterBytes == 25 && pending.Stamps - empty.Stamps == 2;

        World.Process(world);
        var processed = Inspection.Read(world);
        var kernelBytes = 32L * sizeof(KernelEntry) + 32L * KernelCache.MaxCells * sizeof(long) +
                          32L * 128 * sizeof(int) +
                          16L * sizeof(long) + 16L * sizeof(int);
        var fieldOk = World.Query(world, grid, layer, 16, 16) == 120 &&
                      World.Query(world, grid, layer, 0, 0, 64, 64) == 1920 &&
                      World.Query(world, grid, other, 47, 47) == -20 &&
                      World.Query(world, grid, other, 48, 48) == 40 &&
                      World.Query(world, grid, other, 0, 0, 64, 64) == 20 &&
                      processed.LiveTiles == 2 && processed.DirtyTiles == 0 &&
                      processed.Directories == 2 && processed.DirectoryBytes == 32 &&
                      processed.SlabBytes == 2 * 16 * (PlaneStore.PageUnit + PlaneStore.Int32Unit + TileSummary.Bytes +
                                                       sizeof(TilePlanes)) &&
                      processed.PageBytes + processed.Int32Bytes + processed.SummaryBytes + processed.SetBytes ==
                      2 * (PlaneStore.PageUnit + PlaneStore.Int32Unit + TileSummary.Bytes + sizeof(TilePlanes)) &&
                      processed.DirtyFlagBytes == 64 &&
                      processed.DirtyQueueBytes == 2 * (16 * sizeof(int) + 16 * sizeof(TileRect)) &&
                      processed.PyramidBytes == 2 * (64 * 2 + 2 * 16 * 4) &&
                      processed.KernelBytes == kernelBytes &&
                      processed.NativeBytes == pending.NativeBytes + 32 + 2 * 16 * (PlaneStore.PageUnit +
                          PlaneStore.Int32Unit + TileSummary.Bytes + sizeof(TilePlanes)) +
                      64 + 2 * (16 * sizeof(int) + 16 * sizeof(TileRect)) + processed.PyramidBytes +
                      processed.EpochTreeBytes + kernelBytes;

        World.Remove(world, a);
        World.Process(world);
        var removed = Inspection.Read(world);
        var removalOk = removed.LiveSources == 1 && removed.SourceSlots == 2 && removed.LiveTiles == 1 &&
                        removed.SlabFreeUnits == 4 && removed.WorldBytes - processed.WorldBytes == 4 * 64 + 64 &&
                        World.Query(world, grid, layer, 0, 0, 64, 64) == 0;

        World.Move(world, b, 16f, 16f);
        World.SetGain(world, b, 4);
        World.Process(world);
        var moveOk = World.Query(world, grid, other, 15, 15) == -40 &&
                     World.Query(world, grid, other, 16, 16) == 80 &&
                     World.Query(world, grid, other, 48, 48) == 0;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var consumed = 0L;
        for (var i = 0; i < 256; i++) consumed += Inspection.Read(world).NativeBytes;
        var inspectionBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        for (var i = 0; i < 256; i++) World.Process(world);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++) World.Process(world);
        var processBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var processOk = World.Query(world, grid, other, 16, 16) == 80;

        var checksum = 0L;
        for (var i = 0; i < 256; i++) checksum += World.Query(world, grid, other, 16, 16);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100000; i++) checksum += World.Query(world, grid, other, 16, 16);
        var queryBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        for (var i = 0; i < 256; i++) MoveAndProcess(world, b, i);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++) MoveAndProcess(world, b, i);
        var mutationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var mutationOk = World.Query(world, grid, other, 16, 16) == 80 &&
                         World.Query(world, grid, other, 48, 48) == 0;

        var t = World.Place(world, layer, 48f, 48f, Stamp.Tent(8, 8, 40), 3);
        World.Process(world);
        var tented = Inspection.Read(world);
        var tentOk = tented.TentTiles == 1 && tented.TentBytes == PlaneStore.Int64Unit &&
                     World.Query(world, grid, layer, 48, 48) > 0;
        World.Remove(world, t);
        World.Process(world);
        var tentGone = Inspection.Read(world);
        tentOk = tentOk && tentGone.TentTiles == 0 && tentGone.TentBytes == 0;

        var bell = World.Place(world, layer, 48f, 48f, Stamp.Bell(12, 12, 40), 3);
        World.Process(world);
        var belled = Inspection.Read(world);
        var bellOk = belled.BellTiles == 1 && belled.BellBytes == PlaneStore.Int64Unit &&
                     World.Query(world, grid, layer, 48, 48) > 0;
        World.Remove(world, bell);
        World.Process(world);
        var bellGone = Inspection.Read(world);
        bellOk = bellOk && bellGone.BellTiles == 0 && bellGone.BellBytes == 0;

        World.Clear(world);
        var cleared = Inspection.Read(world);
        var layerSlabBytes = 16 * (PlaneStore.PageUnit + PlaneStore.Int32Unit + TileSummary.Bytes + sizeof(TilePlanes));
        var clearOk = cleared.LiveSources == 0 && cleared.SourceSlots == 0 && cleared.SourceCapacity == 16_384 &&
                      cleared.LiveTiles == 0 && cleared.DirtyTiles == 0 && cleared.SlabFreeUnits == 13 &&
                      cleared.PyramidBytes == 0 && cleared.Int32Bytes == 0 &&
                      cleared.Directories == 2 && cleared.DirectoryBytes == 32 &&
                      cleared.SlabBytes == 2 * layerSlabBytes + 16 * PlaneStore.Int64Unit + 9 * 64 &&
                      cleared.QueueBytes == 16 * sizeof(int) + 16 * sizeof(JournalEntry) &&
                      cleared.RecordBytes == 2L * 16_384 * sizeof(DepositRecord) &&
                      cleared.DirtyQueueBytes == 2 * (16 * sizeof(int) + 16 * sizeof(TileRect) + 16 * sizeof(int)) &&
                      cleared.WorldBytes == empty.WorldBytes + SourceChunkBytes + 64 +
                      cleared.DirtyQueueBytes + cleared.EpochTreeBytes + 32 +
                      2 * layerSlabBytes + 16 * PlaneStore.Int64Unit + 9 * 64 +
                      16 * sizeof(int) + 16 * sizeof(JournalEntry) +
                      2L * 16_384 * sizeof(DepositRecord) + cleared.KernelBytes &&
                      World.Query(world, grid, other, 0, 0, 64, 64) == 0;


        return
        [
            new Receipt("invalid-world-is-safe", !Inspection.Read(255).Valid, 0),
            new Receipt("native-memory-matches-layout", pendingOk, pending.WorldBytes),
            new Receipt("box-and-raster-output-matches", fieldOk, 1940),
            new Receipt("remove-recycles-tile-retains-source-slot", removalOk,
                removed.WorldBytes - processed.WorldBytes),
            new Receipt("move-and-gain-output-matches", moveOk, 80),
            new Receipt("inspection-allocates-0-bytes", inspectionBytes == 0 && consumed > 0, inspectionBytes),
            new Receipt("warm-process-allocates-0-bytes", processBytes == 0 && processOk, processBytes),
            new Receipt("warm-query-allocates-0-bytes", queryBytes == 0 && checksum == 100256L * 80, queryBytes),
            new Receipt("warm-move-process-allocates-0-bytes", mutationBytes == 0 && mutationOk, mutationBytes),
            new Receipt("tent-buffer-accounted", tentOk, tented.TentBytes),
            new Receipt("bell-buffer-accounted", bellOk, belled.BellBytes),
            new Receipt("clear-frees-pages-retains-capacity", clearOk, cleared.WorldBytes)
        ];
    }

    private static void MoveAndProcess(byte world, World.SourceId source, int iteration)
    {
        var position = (iteration & 1) == 0 ? 48f : 16f;
        World.Move(world, source, position, position);
        World.Process(world);
    }
}
