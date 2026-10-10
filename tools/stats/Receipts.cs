namespace Gi.Stats;

internal readonly record struct Receipt(string Name, bool Passed, long Value);

internal static unsafe class Receipts
{
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
        var pendingOk = a == (1 << 24) && b == ((1 << 24) | 1) && pending.LiveSources == 2 && pending.SourceSlots == 2 &&
            pending.SourceCapacity == 64 && pending.LiveTiles == 0 && pending.DirtyTiles == 0 &&
            pending.GridBytes == World.MaxGrids * sizeof(GridCtx) &&
            pending.LayerBytes == 2 * World.MaxLayers * sizeof(LayerData) &&
            pending.SourceBytes == 64 * 17 && pending.MapBytes == 0 &&
            pending.DirtyFlagBytes == 0 && pending.DirtyQueueBytes == 0 &&
            pending.DifferenceBytes == 0 && pending.DenseBytes == 0 && pending.RasterTiles == 0 &&
            pending.DepositQueueBytes == 16 * sizeof(DepositOp) + 64 * sizeof(int) &&
            pending.StampRasterBytes - empty.StampRasterBytes == 25 && pending.Stamps - empty.Stamps == 2;

        World.Process(world);
        var processed = Inspection.Read(world);
        var fieldOk = World.Query(world, grid, layer, 16, 16) == 120 &&
            World.Query(world, grid, layer, 0, 0, 64, 64) == 1920 &&
            World.Query(world, grid, other, 47, 47) == -20 &&
            World.Query(world, grid, other, 48, 48) == 40 &&
            World.Query(world, grid, other, 0, 0, 64, 64) == 20 &&
            processed.LiveTiles == 2 && processed.DirtyTiles == 0 &&
            processed.MapBytes == 32 * (sizeof(int) + sizeof(byte*) + 1) &&
            processed.DirtyFlagBytes == 8 && processed.DirtyQueueBytes == 128 &&
            processed.RasterTiles == 1 &&
            processed.DifferenceBytes + processed.DenseBytes + processed.DensePointerBytes +
                processed.PageBytes + processed.PageSumBytes == 2 * World.BlockBytes + World.DenseBytes &&
            processed.PyramidBytes == 2 * (64 * 2 + 2 * 16 * 4) &&
            processed.DepositQueueBytes == 16 * sizeof(DepositOp) + 64 * sizeof(int) + 16 * sizeof(DepositFragment) &&
            processed.NativeBytes == pending.NativeBytes + 2 * World.BlockBytes + World.DenseBytes +
                32 * (sizeof(int) + sizeof(byte*) + 1) + 8 + 128 + processed.PyramidBytes +
                16 * sizeof(DepositFragment);

        World.Remove(world, a);
        World.Process(world);
        var removed = Inspection.Read(world);
        var removalOk = removed.LiveSources == 1 && removed.SourceSlots == 2 && removed.LiveTiles == 1 &&
            removed.Tombstones == 1 && processed.WorldBytes - removed.WorldBytes == World.BlockBytes - 64 &&
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

        World.Clear(world);
        var cleared = Inspection.Read(world);
        var clearOk = cleared.LiveSources == 0 && cleared.SourceSlots == 0 && cleared.SourceCapacity == 64 &&
            cleared.LiveTiles == 0 && cleared.DirtyTiles == 0 && cleared.MapSlots == 0 &&
            cleared.MapBytes == 0 && cleared.PyramidBytes == 0 && cleared.RasterTiles == 0 &&
            cleared.DepositQueueBytes == 16 * sizeof(DepositOp) + 64 * sizeof(int) + 16 * sizeof(DepositFragment) &&
            cleared.WorldBytes == empty.WorldBytes + 64 * 17 + 8 + 256 + 16 * sizeof(DepositOp) +
                64 * sizeof(int) + 16 * sizeof(DepositFragment) &&
            World.Query(world, grid, other, 0, 0, 64, 64) == 0;

        return
        [
            new("invalid-world-is-safe", !Inspection.Read(255).Valid, 0),
            new("native-memory-matches-layout", pendingOk, pending.WorldBytes),
            new("box-and-raster-output-matches", fieldOk, 1940),
            new("remove-frees-tile-retains-source-slot", removalOk, processed.WorldBytes - removed.WorldBytes),
            new("move-and-gain-output-matches", moveOk, 80),
            new("inspection-allocates-0-bytes", inspectionBytes == 0 && consumed > 0, inspectionBytes),
            new("warm-process-allocates-0-bytes", processBytes == 0 && processOk, processBytes),
            new("warm-query-allocates-0-bytes", queryBytes == 0 && checksum == 100256L * 80, queryBytes),
            new("warm-move-process-allocates-0-bytes", mutationBytes == 0 && mutationOk, mutationBytes),
            new("clear-frees-pages-retains-capacity", clearOk, cleared.WorldBytes),
        ];
    }

    private static void MoveAndProcess(byte world, int source, int iteration)
    {
        var position = (iteration & 1) == 0 ? 48f : 16f;
        World.Move(world, source, position, position);
        World.Process(world);
    }
}
