using System.Diagnostics;
using GridInfluence;

var seconds = args.Length > 0 ? double.Parse(args[0]) : 8.0;
var mixed = args.Length > 1 && args[1] == "mix";
var w = World.New();
var g = Grid.New(w, 8, 0f, 0f, 1024f);
var l = Layer.New(w);
var box = Stamp.Box(16, 16, 60);

var samples = new sbyte[10 * 8];
uint state = 1;
for (var i = 0; i < samples.Length; i++)
{
    state = state * 1664525u + 1013904223u;
    samples[i] = (sbyte)(state % 61u - 30);
}

var noise = Stamp.New(samples, 10, 8);

var rng = new Random(17);
const int n = 4000;
var src = new int[n];
for (var i = 0; i < n; i++)
{
    var x = (float)(rng.NextDouble() * 1024);
    var y = (float)(rng.NextDouble() * 1024);
    src[i] = World.Place(w, l, x, y, mixed && (i & 1) != 0 ? noise : box, 8);
}

World.Process(w);

var sink = 0L;
var moves = 0L;
var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds)
{
    for (var i = 0; i < n; i++)
    {
        var x = (float)(rng.NextDouble() * 1024);
        var y = (float)(rng.NextDouble() * 1024);
        World.Move(w, src[i], x, y);
        moves++;
    }

    World.Process(w);
    for (var i = 0; i < 128; i++) sink += World.Query(w, g, l, i * 2 % 250, i * 5 % 250);
}

sw.Stop();
Console.WriteLine($"profile: {(int)sw.Elapsed.TotalMilliseconds} ms, {moves} moves, sink {sink}");
