using System.Diagnostics;
using GridInfluence;

const int N = 4000, Q = 100_000;
var rng = new Random(17);
var w = World.New();
var g = Grid.New(w, 8, 0f, 0f, 1024f);
var l = Layer.New(w);
var stamp = Stamp.Box(60);
const float bound = 32f;

var qx = new int[Q]; var qy = new int[Q];
for (var i = 0; i < Q; i++) { qx[i] = rng.Next(256); qy[i] = rng.Next(256); }

var src = new int[N];
for (var i = 0; i < N; i++)
    src[i] = World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), bound, stamp);
World.Process(w);

double Best(Action f, int reps)
{
    var b = double.MaxValue;
    for (var i = 0; i < reps; i++) { var t = Stopwatch.GetTimestamp(); f(); var e = Stopwatch.GetElapsedTime(t).TotalNanoseconds; if (e < b) b = e; }
    return b;
}

Console.WriteLine($"unchanged process: {Best(() => World.Process(w), 30):F0} ns");

var frame = Best(() => {
    for (var i = 0; i < N; i++) World.Move(w, src[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
    World.Process(w);
}, 10);
Console.WriteLine($"frame (move-all + process): {frame / 1e3:F1} us");

var qbest = Best(() => { long s = 0; for (var i = 0; i < Q; i++) s += World.Query(w, g, l, qx[i], qy[i]); }, 20);
Console.WriteLine($"query: {qbest / Q:F1} ns/cell");

var fresh = Best(() => {
    World.Clear(w);
    for (var i = 0; i < N; i++) src[i] = World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), bound, stamp);
    World.Process(w);
}, 5);
Console.WriteLine($"clear+place-all+process: {fresh / 1e3:F1} us");

var partial = Best(() => {
    for (var i = 0; i < 200; i++) World.Move(w, src[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
    World.Process(w);
}, 10);
Console.WriteLine($"frame (200 moves + process): {partial / 1e3:F1} us");
