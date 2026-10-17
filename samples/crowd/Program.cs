using Gi;

const int Agents = 200_000;
const int Frames = 200;
const int SortEvery = 64;
const float Speed = 0.1f;

var world = World.New();
var grid = Grid.New(world, power: 10, x: 0f, y: 0f, size: 1024f);
var crowd = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
var body = Stamp.Box(4, 4, 1);

var rng = new Random(21);
var xs = new float[Agents];
var ys = new float[Agents];
var vx = new float[Agents];
var vy = new float[Agents];
for (var i = 0; i < Agents; i++)
{
    xs[i] = 8f + (float)rng.NextDouble() * 1008f;
    ys[i] = 8f + (float)rng.NextDouble() * 1008f;
    var angle = rng.NextDouble() * Math.Tau;
    vx[i] = (float)Math.Cos(angle) * Speed;
    vy[i] = (float)Math.Sin(angle) * Speed;
}

var keys = new uint[Agents];
var permutation = new int[Agents];
var scratch = new ulong[2 * Agents];
var gatherX = new float[Agents];
var gatherY = new float[Agents];
var gatherVx = new float[Agents];
var gatherVy = new float[Agents];

long checksum = 0;
for (var frame = 0; frame < Frames; frame++)
{
    if (frame % SortEvery == 0)
    {
        Grid.SpatialKeys(world, grid, xs, ys, keys);
        var before = Grid.Disorder(keys);
        Grid.SortByKeys(keys, permutation, scratch);
        for (var i = 0; i < Agents; i++)
        {
            var source = permutation[i];
            gatherX[i] = xs[source];
            gatherY[i] = ys[source];
            gatherVx[i] = vx[source];
            gatherVy[i] = vy[source];
        }

        (xs, gatherX) = (gatherX, xs);
        (ys, gatherY) = (gatherY, ys);
        (vx, gatherVx) = (gatherVx, vx);
        (vy, gatherVy) = (gatherVy, vy);
        Grid.SpatialKeys(world, grid, xs, ys, keys);
        Console.WriteLine($"frame {frame,3}: disorder {before:F3} -> {Grid.Disorder(keys):F3}");
    }

    for (var i = 0; i < Agents; i++)
    {
        var nx = xs[i] + vx[i];
        var ny = ys[i] + vy[i];
        if (nx < 8f || nx > 1016f) vx[i] = -vx[i];
        if (ny < 8f || ny > 1016f) vy[i] = -vy[i];
        xs[i] += vx[i];
        ys[i] += vy[i];
    }

    World.Splat(world, crowd, xs, ys, body, 1);
    World.Process(world);

    var view = World.View(world, grid, crowd);
    var busiest = 0;
    for (var i = 0; i < Agents; i += 16)
        if (view.TryCell(xs[i], ys[i], out var cx, out var cy))
            busiest = Math.Max(busiest, view[cx, cy] - 1);
    checksum += busiest;
}

Console.WriteLine($"crowded agents seen: {checksum}");
return 0;
