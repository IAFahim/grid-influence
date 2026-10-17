namespace Gi.Stats;

internal readonly struct Scene(byte world, byte grid, int cells, int layers, int sources, int moves, World.SourceId[] ids)
{
    public readonly byte World = world;
    public readonly byte Grid = grid;
    public readonly int Cells = cells;
    public readonly int Layers = layers;
    public readonly int Sources = sources;
    public readonly int Moves = moves;
    public readonly World.SourceId[] Ids = ids;

    public static Scene Create(Options options)
    {
        var cells = 1 << options.Power;
        var world = Gi.World.New();
        var grid = Gi.Grid.New(world, options.Power, 0f, 0f, cells);
        for (var i = 0; i < options.Layers; i++) Layer.New(world);

        var size = (ushort)options.StampSize;
        byte stamp;
        if (options.Raster)
        {
            Span<sbyte> samples = stackalloc sbyte[size * size];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 61 - 30);
            stamp = Stamp.New(samples, size, size);
        }
        else stamp = Stamp.Box(size, size, 60);

        var ids = new World.SourceId[options.Sources];
        var state = 17u;
        for (var i = 0; i < options.Sources; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var x = (state >> 8) % (uint)(cells * 4) * 0.25f;
            state = unchecked(state * 1664525u + 1013904223u);
            var y = (state >> 8) % (uint)(cells * 4) * 0.25f;
            if ((ids[i] = Gi.World.Place(world, (byte)(i % options.Layers), x, y, stamp, 8)).Slot != (uint)i)
                throw new InvalidOperationException("Scene placement failed.");
        }
        Gi.World.Process(world);
        return new Scene(world, grid, cells, options.Layers, options.Sources, Math.Min(options.Moves, options.Sources), ids);
    }

    public long Sum()
    {
        var sum = 0L;
        for (var layer = 0; layer < Layers; layer++)
            sum += Gi.World.Query(World, Grid, (byte)layer, 0, 0, Cells, Cells);
        return sum;
    }
}
