using GridInfluence;

class Scenarios
{
    static int Main()
    {
        AggroRange();
        MultiResolutionTraffic();
        RasterStamp();
        return 0;
    }

    static void AggroRange()
    {
        Console.WriteLine("== enemy in range ==");
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 512f);
        var threat = Layer.New(w);
        var aggro = Stamp.Box(80, 80, 1);
        var rng = new Random(7);

        for (var i = 0; i < 300; i++)
            World.Place(w, threat, (float)(rng.NextDouble() * 512), (float)(rng.NextDouble() * 512), aggro, 8);
        World.Process(w);

        var px = (int)(256f * 256 / 512);
        Console.WriteLine($"player at center — enemies in range: {World.Query(w, g, threat, px, px)}");
    }

    static void MultiResolutionTraffic()
    {
        Console.WriteLine("== world with 256x / 1024x / 32x grids ==");
        var w = World.New();
        var fine = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var dense = Grid.New(w, power: 10, x: 256f, y: 0f, size: 1024f);
        var coarse = Grid.New(w, power: 5, x: 1280f, y: 0f, size: 256f);
        var traffic = Layer.New(w);
        var car = Stamp.Box(6, 6, 4);
        var rng = new Random(11);

        for (var i = 0; i < 3000; i++)
        {
            var x = i switch
            {
                < 1000 => (float)(rng.NextDouble() * 256),
                < 2400 => 256f + (float)(rng.NextDouble() * 1024),
                _ => 1280f + (float)(rng.NextDouble() * 256),
            };
            World.Place(w, traffic, x, (float)(rng.NextDouble() * 256), car, 4);
        }

        World.Process(w);
        Console.WriteLine($"fine 256-cell grid total: {World.Query(w, fine, traffic, 0, 0, 256, 256)}");
        Console.WriteLine($"dense 1024-cell grid total: {World.Query(w, dense, traffic, 0, 0, 1024, 1024)}");
        Console.WriteLine($"coarse 32-cell grid total: {World.Query(w, coarse, traffic, 0, 0, 32, 32)}");
    }

    static void RasterStamp()
    {
        Console.WriteLine("== baked raster stamp ==");
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
        var l = Layer.New(w);

        var samples = new sbyte[9 * 9];
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 9; x++)
        {
            var d = Math.Abs(x - 4) + Math.Abs(y - 4);
            samples[y * 9 + x] = (sbyte)Math.Max(0, 40 - d * 8);
        }

        var blob = Stamp.New(samples, 9, 9);
        for (var i = 0; i < 80; i++)
            World.Place(w, l, i * 1.4f + 4f, 64f + (i % 7) - 3f, blob, 6);
        World.Process(w);

        var peak = 0;
        for (var c = 0; c < 128; c++) peak = Math.Max(peak, World.Query(w, g, l, c, 64));
        Console.WriteLine($"blob row peak influence: {peak}");
    }
}
