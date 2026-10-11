using Gi;

AggroRange();
MultiResolutionTraffic();
RasterStamp();
BestSpot();
VisionCone();
ScentTrail();
NearestFood();
return 0;

void AggroRange()
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

void MultiResolutionTraffic()
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

void RasterStamp()
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

void BestSpot()
{
    Console.WriteLine("== best spot: food minus twice the threat ==");
    var w = World.New();
    var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
    var food = Layer.New(w);
    var threat = Layer.New(w);
    var score = Layer.Sum(w, food, 1, threat, -2);
    var berries = Stamp.Dome(10, 60);
    var wolves = Stamp.Cone(24, 50);
    var rng = new Random(5);
    for (var i = 0; i < 40; i++)
        World.Place(w, food, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), berries, 4);
    for (var i = 0; i < 8; i++)
        World.Place(w, threat, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), wolves, 6);
    World.Process(w);

    var best = World.QueryMax(w, g, score, out var bx, out var by);
    Console.WriteLine($"best cell ({bx},{by}) scores {best}: food {World.Query(w, g, food, bx, by)}, threat {World.Query(w, g, threat, bx, by)}");
}

void VisionCone()
{
    Console.WriteLine("== a guard's vision cone turns ==");
    var w = World.New();
    var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
    var seen = Layer.New(w);
    var guard = World.Place(w, seen, 64f, 64f, Stamp.Cone(30, 100, arc: 70), 1);
    foreach (var (label, angle) in new[] { ("+x", 0f), ("+y", MathF.PI / 2f), ("-x", MathF.PI) })
    {
        World.Turn(w, guard, angle);
        World.Process(w);
        Console.WriteLine($"facing {label}: sees {World.QueryAt(w, g, seen, 84f, 64f)} at +x, " +
            $"{World.QueryAt(w, g, seen, 64f, 84f)} at +y, {World.QueryAt(w, g, seen, 44f, 64f)} at -x");
    }
}

void ScentTrail()
{
    Console.WriteLine("== a scent trail fades on its own ==");
    var w = World.New();
    var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
    var scent = Layer.New(w);
    var crumb = Stamp.Dome(3, 40);
    for (var step = 0; step < 40; step++)
    {
        var id = World.Place(w, scent, 10f + step * 2.5f, 64f, crumb, 16);
        World.Fade(w, id, 0, ticks: 30);
        World.Expire(w, id, ticks: 30);
        World.Process(w);
    }

    Console.WriteLine($"tick {World.Tick(w)}: scent {World.QueryAt(w, g, scent, 10f, 64f)} where it started, " +
        $"{World.QueryAt(w, g, scent, 60f, 64f)} midway, {World.QueryAt(w, g, scent, 107.5f, 64f)} at the head");
}

void NearestFood()
{
    Console.WriteLine("== nearest food worth walking to ==");
    var w = World.New();
    Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
    var food = Layer.New(w);
    World.Place(w, food, 30f, 70f, Stamp.Disk(3, 20), 1);
    World.Place(w, food, 90f, 60f, Stamp.Disk(3, 80), 1);
    World.Process(w);

    World.TrySenseNearest(w, food, 64f, 64f, 60f, 10, out var any, out var ax, out var ay);
    World.TrySenseNearest(w, food, 64f, 64f, 60f, 50, out var rich, out var rx, out var ry);
    Console.WriteLine($"nearest food >= 10: {any} at ({ax},{ay}); nearest food >= 50: {rich} at ({rx},{ry})");
}
