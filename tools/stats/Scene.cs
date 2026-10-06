namespace Gi.Stats;

internal readonly struct Scene(byte world, byte grid, int cells, int layers, int sources, int moves)
{
    public readonly byte World = world;
    public readonly byte Grid = grid;
    public readonly int Cells = cells;
    public readonly int Layers = layers;
    public readonly int Sources = sources;
    public readonly int Moves = moves;

    public static Scene Create(Options options)
    {
        var cells = 1 << options.Power;
        var world = Gi.World.New();
        var grid = Gi.Grid.New(world, options.Power, 0f, 0f, cells);
        for (var i = 0; i < options.Layers; i++) Layer.New(world);

        byte stamp;
        if (options.Raster)
        {
            Span<sbyte> samples = stackalloc sbyte[options.StampSize * options.StampSize];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 61 - 30);
            stamp = Stamp.New(samples, options.StampSize, options.StampSize);
        }
        else stamp = Stamp.Box(options.StampSize, options.StampSize, 60);

        var state = 17u;
        for (var i = 0; i < options.Sources; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var x = (state >> 8) % (uint)(cells * 4) * 0.25f;
            state = unchecked(state * 1664525u + 1013904223u);
            var y = (state >> 8) % (uint)(cells * 4) * 0.25f;
            if (Gi.World.Place(world, (byte)(i % options.Layers), x, y, stamp, 8) != i)
                throw new InvalidOperationException("Scene placement failed.");
        }
        Gi.World.Process(world);
        return new Scene(world, grid, cells, options.Layers, options.Sources, Math.Min(options.Moves, options.Sources));
    }

    public long Sum()
    {
        var sum = 0L;
        for (var layer = 0; layer < Layers; layer++)
            sum += Gi.World.Query(World, Grid, (byte)layer, 0, 0, Cells, Cells);
        return sum;
    }
}
