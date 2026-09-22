using System.Diagnostics;
using System.Runtime.InteropServices;
using GridInfluence;

unsafe class Scenarios
{
    static int Main()
    {
        AggroRange();
        FearCrowd();
        TrafficCongestion();
        return 0;
    }

    static Float2* Arena(Random rng, int n, float size)
    {
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)(n * sizeof(Float2)), 64);
        for (var i = 0; i < n; i++)
            pos[i] = new Float2((float)(rng.NextDouble() * size), (float)(rng.NextDouble() * size));
        return pos;
    }

    static void AggroRange()
    {
        Console.WriteLine("== enemy in range ==");
        var w = World.New();
        var g = World.Grid(w, power: 8, x: 0f, y: 0f, size: 512f);
        var threat = World.Layer(w);
        var aggro = Stamps.Circle(1);
        var rng = new Random(7);

        var enemies = 300;
        var pos = Arena(rng, enemies, 512f);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)(enemies * sizeof(float)), 64);
        var stamps = (byte*)NativeMemory.Alloc((nuint)enemies);
        var fades = (byte*)NativeMemory.Alloc((nuint)enemies);
        for (var i = 0; i < enemies; i++) { bounds[i] = 40f; stamps[i] = aggro; fades[i] = 0; }

        World.Queue(w, threat, pos, bounds, stamps, fades, enemies);
        World.Apply(w);

        var player = new Float2(256f, 256f);
        var cell = ToCell(0f, 512f, 256, player.X, player.Y);
        var heat = World.Cell(w, g, threat, cell.x, cell.y);
        Console.WriteLine($"player at ({player.X},{player.Y}) — enemies in range: {heat}");

        pos[0] = new Float2(260f, 250f);
        World.Apply(w);
        heat = World.Cell(w, g, threat, cell.x, cell.y);
        Console.WriteLine($"after enemy[0] moves to (260,250): {heat}");
    }

    static void FearCrowd()
    {
        Console.WriteLine("== fear meter ==");
        var w = World.New();
        var g = World.Grid(w, power: 8, x: 0f, y: 0f, size: 256f);
        var fear = World.Layer(w);
        var aura = Stamps.Circle(20);
        var decay = Fade.Stamp(20);
        var rng = new Random(3);

        var monsters = 12;
        var pos = Arena(rng, monsters, 256f);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)(monsters * sizeof(float)), 64);
        var stamps = (byte*)NativeMemory.Alloc((nuint)monsters);
        var fades = (byte*)NativeMemory.Alloc((nuint)monsters);
        for (var i = 0; i < monsters; i++) { bounds[i] = 30f; stamps[i] = aura; fades[i] = decay; }

        World.Queue(w, fear, pos, bounds, stamps, fades, monsters);

        var villagers = 500;
        var vpos = Arena(rng, villagers, 256f);

        for (var step = 0; step < 4; step++)
        {
            World.Apply(w);
            var scared = 0;
            for (var i = 0; i < villagers; i++)
            {
                var c = ToCell(0f, 256f, 256, vpos[i].X, vpos[i].Y);
                if (World.Cell(w, g, fear, c.x, c.y) > 15) scared++;
            }
            Console.WriteLine($"step {step}: {scared}/{villagers} villagers scared");
        }
    }

    static void TrafficCongestion()
    {
        Console.WriteLine("== traffic congestion ==");
        var w = World.New();
        var downtown = World.Grid(w, power: 9, x: 0f, y: 0f, size: 1024f);
        var suburbs = World.Grid(w, power: 6, x: 1024f, y: 0f, size: 2048f);
        var traffic = World.Layer(w);
        var car = Stamps.Box(4);
        var rng = new Random(11);

        var cars = 2000;
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)(cars * sizeof(Float2)), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)(cars * sizeof(float)), 64);
        var stamps = (byte*)NativeMemory.Alloc((nuint)cars);
        var fades = (byte*)NativeMemory.Alloc((nuint)cars);
        for (var i = 0; i < cars; i++)
        {
            var downtownCar = i < 1600;
            pos[i] = downtownCar
                ? new Float2((float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024))
                : new Float2(1024f + (float)(rng.NextDouble() * 2048), (float)(rng.NextDouble() * 2048));
            bounds[i] = 12f; stamps[i] = car; fades[i] = 0;
        }

        World.Queue(w, traffic, pos, bounds, stamps, fades, cars);
        var t = Stopwatch.GetTimestamp();
        World.Apply(w);
        var applyNs = Stopwatch.GetElapsedTime(t).TotalNanoseconds;
        Console.WriteLine($"{cars} cars over 2 grids — apply {applyNs:F0}ns ({applyNs / cars:F1}ns/car)");

        var junction = new Float2(512f, 512f);
        var jc = ToCell(0f, 1024f, 512, junction.X, junction.Y);
        var level = World.Cell(w, downtown, traffic, jc.x, jc.y);
        Console.WriteLine($"downtown junction congestion: {level} cars nearby");
        var suburbJunction = ToCell(1024f, 2048f, 64, 1536f, 1536f);
        level = World.Cell(w, suburbs, traffic, suburbJunction.x, suburbJunction.y);
        Console.WriteLine($"suburb junction congestion:   {level} cars nearby");
    }

    static (int x, int y) ToCell(float origin, float size, int cells, float wx, float wy)
        => ((int)((wx - origin) * cells / size), (int)((wy - origin) * cells / size));
}
