using System.Diagnostics;
using GridInfluence;

class Scenarios
{
    static int Main()
    {
        AggroRange();
        FearCrowd();
        TrafficCongestion();
        return 0;
    }

    static void AggroRange()
    {
        Console.WriteLine("== enemy in range ==");
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 512f);
        var threat = Layer.New(w);
        var aggro = Stamp.Circle(1);
        var rng = new Random(7);

        var sources = new int[300];
        for (var i = 0; i < sources.Length; i++)
            sources[i] = World.Place(w, threat, (float)(rng.NextDouble() * 512), (float)(rng.NextDouble() * 512), 40f, aggro);
        World.Process(w);

        Console.WriteLine($"player at center — enemies in range: {World.QueryAt(w, g, threat, 256f, 256f)}");

        World.Move(w, sources[0], 260f, 250f);
        World.Process(w);
        Console.WriteLine($"after enemy[0] moves to (260,250): {World.QueryAt(w, g, threat, 256f, 256f)}");
    }

    static void FearCrowd()
    {
        Console.WriteLine("== fear meter ==");
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var fear = Layer.New(w);
        var aura = Stamp.Circle(20);
        var decay = Fade.Stamp(20);
        var rng = new Random(3);

        for (var i = 0; i < 12; i++)
            World.Place(w, fear, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), 30f, aura, decay);

        var villagers = new (float x, float y)[500];
        for (var i = 0; i < villagers.Length; i++)
            villagers[i] = ((float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256));

        for (var step = 0; step < 4; step++)
        {
            World.Process(w);
            var scared = 0;
            foreach (var (vx, vy) in villagers)
                if (World.QueryAt(w, g, fear, vx, vy) > 15) scared++;
            Console.WriteLine($"step {step}: {scared}/{villagers.Length} villagers scared");
        }
    }

    static void TrafficCongestion()
    {
        Console.WriteLine("== traffic congestion ==");
        var w = World.New();
        var downtown = Grid.New(w, power: 9, x: 0f, y: 0f, size: 1024f);
        var suburbs = Grid.New(w, power: 6, x: 1024f, y: 0f, size: 2048f);
        var traffic = Layer.New(w);
        var car = Stamp.Box(4);
        var rng = new Random(11);

        const int cars = 2000;
        for (var i = 0; i < cars; i++)
        {
            var downtownCar = i < 1600;
            World.Place(w, traffic,
                downtownCar ? (float)(rng.NextDouble() * 1024) : 1024f + (float)(rng.NextDouble() * 2048),
                downtownCar ? (float)(rng.NextDouble() * 1024) : (float)(rng.NextDouble() * 2048),
                12f, car);
        }

        for (var i = 0; i < 50; i++) World.Process(w);
        var bestSerial = double.MaxValue; var bestSliced = double.MaxValue;
        for (var r = 0; r < 100; r++)
        {
            var t = Stopwatch.GetTimestamp();
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalNanoseconds;
            if (el < bestSerial) bestSerial = el;
            t = Stopwatch.GetTimestamp();
            World.BeginProcess(w);
            var slice = cars / 4;
            Parallel.For(0, 4, sidx => World.ProcessSlice(w, sidx * slice, slice));
            el = Stopwatch.GetElapsedTime(t).TotalNanoseconds;
            if (el < bestSliced) bestSliced = el;
        }
        Console.WriteLine($"{cars} cars over 2 grids — serial {bestSerial:F0}ns ({bestSerial / cars:F1}ns/car), 4-way sliced {bestSliced:F0}ns");

        Console.WriteLine($"downtown junction congestion: {World.QueryAt(w, downtown, traffic, 512f, 512f)} cars nearby");
        Console.WriteLine($"suburb junction congestion:   {World.QueryAt(w, suburbs, traffic, 1536f, 1536f)} cars nearby");
    }
}
