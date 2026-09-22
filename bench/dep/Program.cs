using System.Diagnostics;
using GridInfluence;

var w = World.New();
var g = Grid.New(w, 8, 0f, 0f, 1024f);   // 256 cells -> 64 tiles
var l = Layer.New(w);
var stamp = Stamp.Box(16, 16, 60);
var rng = new Random(17);

// sources spread so all 64 tiles get touched
var src = new int[4000];
for (var i = 0; i < 4000; i++)
    src[i] = World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), stamp, 8);
World.Process(w);

double Best(Action f, int reps)
{
    var b = double.MaxValue;
    for (var i = 0; i < reps; i++) { var t = Stopwatch.GetTimestamp(); f(); var e = Stopwatch.GetElapsedTime(t).TotalNanoseconds; if (e < b) b = e; }
    return b;
}

// dirty exactly 1 tile: in-place gain toggle on a source well inside one tile
var s0 = src[0];
World.SetGain(w, s0, 8);
World.Process(w);
var t1 = Best(() => {
    World.SetGain(w, s0, 7);
    World.Process(w);
    World.SetGain(w, s0, 8);
    World.Process(w);
}, 30);
Console.WriteLine($"2x (setgain+process) 1-4 dirty tiles: {t1 / 1e3:F1} us");

// dirty all 64: move every source by a tiny jitter
var tAll = Best(() => {
    for (var i = 0; i < 4000; i++) World.Move(w, src[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
    World.Process(w);
}, 8);
Console.WriteLine($"all-move + process (<=64 dirty): {tAll / 1e3:F1} us");

var tPart = Best(() => {
    for (var i = 0; i < 200; i++) World.Move(w, src[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
    World.Process(w);
}, 10);
Console.WriteLine($"200-move + process: {tPart / 1e3:F1} us");

var tFresh = Best(() => {
    World.Clear(w);
    for (var i = 0; i < 4000; i++) src[i] = World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), stamp, 8);
    World.Process(w);
}, 5);
Console.WriteLine($"clear + place all + process: {tFresh / 1e3:F1} us");

var qx = new int[100_000]; var qy = new int[100_000];
for (var i = 0; i < qx.Length; i++) { qx[i] = rng.Next(256); qy[i] = rng.Next(256); }
long sink = 0;
var tQuery = Best(() => { for (var i = 0; i < qx.Length; i++) sink += World.Query(w, g, l, qx[i], qy[i]); }, 10);
Console.WriteLine($"query: {tQuery / qx.Length:F1} ns/cell (sink {sink})");
