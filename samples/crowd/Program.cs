using Gi;

const int agents = 200_000;
const int frames = 200;
const int sortEvery = 64;
const float speed = 0.1f;

var world = World.New();
var grid = Grid.New(world, 10, 0f, 0f, 1024f);
var crowd = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
var body = Stamp.Box(4, 4, 1);

var rng = new Random(21);
var xs = new float[agents];
var ys = new float[agents];
var vx = new float[agents];
var vy = new float[agents];
for (var i = 0; i < agents; i++)
{
    xs[i] = 8f + (float)rng.NextDouble() * 1008f;
    ys[i] = 8f + (float)rng.NextDouble() * 1008f;
    var angle = rng.NextDouble() * Math.Tau;
    vx[i] = (float)Math.Cos(angle) * speed;
    vy[i] = (float)Math.Sin(angle) * speed;
}

var keys = new uint[agents];
var permutation = new int[agents];
var scratch = new ulong[2 * agents];
var gatherX = new float[agents];
var gatherY = new float[agents];
var gatherVx = new float[agents];
var gatherVy = new float[agents];

long checksum = 0;
for (var frame = 0; frame < frames; frame++)
{
    if (frame % sortEvery == 0)
    {
        Grid.SpatialKeys(world, grid, xs, ys, keys);
        var before = Grid.Disorder(keys);
        Grid.SortByKeys(keys, permutation, scratch);
        for (var i = 0; i < agents; i++)
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

    for (var i = 0; i < agents; i++)
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
    for (var i = 0; i < agents; i += 16)
        if (view.TryCell(xs[i], ys[i], out var cx, out var cy))
            busiest = Math.Max(busiest, view[cx, cy] - 1);
    checksum += busiest;
}

Console.WriteLine($"crowded agents seen: {checksum}");
return 0;
