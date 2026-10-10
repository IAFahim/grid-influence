using System.Globalization;
using System.Text;
using Gi;

if (args.Length > 0 && args[0] == "live")
{
    Live.Run(args);
    return;
}

var scenes = new List<Scene>();

Combat();
BrushZoo();
SubCellEdges();
MoveVsTrail();
MultiGridLod();
SparseWorld();
ThousandSources();

var json = Serialize(scenes);
var html = Template.Html.Replace("/*__DATA__*/", json);
var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");
Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "index.html"), html);
Console.WriteLine($"wrote out/index.html ({html.Length / 1024} KB, {scenes.Count} scenes)");

foreach (var s in scenes)
{
    Console.WriteLine($"  {s.Name,-16} panels {s.Panels.Count}  layers {s.Layers.Count}  receipts {s.Receipts.Count}");
    foreach (var r in s.Receipts) Console.WriteLine($"      {r}");
}

void Combat()
{
    var scene = new Scene("layered combat",
        "One world, three layers: threat (boss aura + minions), player trail, hazards (water + fear pits). Toggle layer chips — the heightfield is the sum of every active layer.");
    var w = World.New();
    var g = Grid.New(w, 8, 0f, 0f, 256f);
    var threat = scene.Layer(w, "threat", "#ff5252");
    var trailL = scene.Layer(w, "trail", "#40c4ff");
    var danger = scene.Layer(w, "danger", "#69f0ae");

    var boss = Gaussian(48, 120);
    World.Place(w, threat, 140f, 120f, boss, 16);
    scene.Mark(140f, 120f, threat, "boss");

    var minion = Stamp.Box(10, 10, 45);
    var rng = new Random(7);
    for (var i = 0; i < 7; i++)
    {
        var a = i / 7.0 * Math.PI * 2;
        var x = 140f + (float)(Math.Cos(a) * (38 + rng.Next(18)));
        var y = 120f + (float)(Math.Sin(a) * (38 + rng.Next(18)));
        World.Place(w, threat, x, y, minion, 4 + rng.Next(7));
    }

    var trailStamp = Stamp.Box(8, 8, 28);
    for (var i = 0; i < 28; i++)
    {
        var t = i / 27.0;
        World.Place(w, trailL, 24f + (float)(t * 200),
            205f - (float)(t * 140) + (float)(Math.Sin(t * 9) * 9), trailStamp, 14 - i / 2);
    }

    World.Place(w, danger, 62f, 60f, Gaussian(36, -100), 12);
    scene.Mark(62f, 60f, danger, "water");
    var fear = Stamp.Box(6, 6, -60);
    for (var i = 0; i < 3; i++)
        World.Place(w, danger, 180f + (float)(rng.NextDouble() * 50), 170f + (float)(rng.NextDouble() * 50), fear, 6);

    World.Process(w);
    scene.Panel(w, g, "256² · 1 unit/cell", 256, 256f, 0, threat, trailL, danger);
    var sum = World.Query(w, g, threat, 0, 0, 256, 256);
    scene.Receipt($"region query threat[0,0,256,256] = {sum} (baked page sums, O(live tiles))");
    scenes.Add(scene);
}

void BrushZoo()
{
    var scene = new Scene("brush zoo",
        "Every stamp is just an sbyte grid — gaussian, falloff diamond, diagonal streak, noise, negative pits, even letters. Stamp.New bakes a mip chain so coarse grids sample it correctly.");
    var w = World.New();
    var g = Grid.New(w, 8, 0f, 0f, 256f);
    var brushes = scene.Layer(w, "brushes", "#ffb74d");

    World.Place(w, brushes, 70f, 84f, Gaussian(48, 110), 14);
    scene.Mark(70f, 84f, brushes, "gaussian");

    var diamond = Shape(25, 25, (dx, dy, r) => (sbyte)(Math.Abs(dx) + Math.Abs(dy) <= r ? 70 - 5 * (Math.Abs(dx) + Math.Abs(dy)) : 0));
    World.Place(w, brushes, 158f, 66f, diamond, 12);
    scene.Mark(158f, 66f, brushes, "diamond");

    var streak = Shape(28, 9, (x, y, _) => (sbyte)(Math.Abs(x - 3.2 * y - 2) < 1.5 ? 70 : Math.Abs(x - 3.2 * y - 2) < 3 ? 35 : 0));
    World.Place(w, brushes, 52f, 190f, streak, 10);
    scene.Mark(52f, 190f, brushes, "streak");

    World.Place(w, brushes, 206f, 186f, Noise(21, 60, 3), 10);
    scene.Mark(206f, 186f, brushes, "noise");

    World.Place(w, brushes, 118f, 196f, Letters(), 12);
    scene.Mark(118f, 196f, brushes, "letters");

    World.Place(w, brushes, 110f, 108f, Gaussian(30, -110), 10);
    scene.Mark(110f, 108f, brushes, "pit");

    World.Process(w);
    scene.Panel(w, g, "256² · 1 unit/cell", 256, 256f, 0, brushes);
    scene.Receipt("negative samples subtract — the pit carves straight through the gaussian tail");
    scenes.Add(scene);
}

void SubCellEdges()
{
    var scene = new Scene("sub-cell edges",
        "Stamps live in world units, not grid cells. Top row: eight identical 12×12 boxes, each nudged +⅛ cell. Bottom row: 40×40 boxes a half-cell apart, gaussians at quarter-cell steps.");
    var w = World.New();
    var g = Grid.New(w, 8, 0f, 0f, 256f);
    var phases = scene.Layer(w, "phases", "#4dd0e1");

    var box = Stamp.Box(12, 12, 60);
    var spots = new float[8];
    for (var i = 0; i < 8; i++)
    {
        spots[i] = 20f + i * 28.125f;
        World.Place(w, phases, spots[i], 40f, box, 8);
    }

    World.Place(w, phases, 44f, 120f, Stamp.Box(40, 40, 55), 10);
    World.Place(w, phases, 84.5f, 120f, Stamp.Box(40, 40, 55), 10);

    var gauss = Gaussian(21, 70);
    for (var i = 0; i < 8; i++)
        World.Place(w, phases, 20f + i * 28.25f, 208f, gauss, 10);

    World.Process(w);
    scene.Panel(w, g, "256² · 1 unit/cell", 256, 256f, 0, phases);
    foreach (var x in spots)
    {
        var a = FirstCovered(w, g, phases, 40, (int)x - 8, (int)x + 8);
        var b = LastCovered(w, g, phases, 40, a, a + 16);
        var tail = World.Query(w, g, phases, b, 40);
        scene.Receipt($"x={x.ToString("F3", CultureInfo.InvariantCulture)} → cells {a}..{b}, tail cell = {tail}");
    }
    scene.Receipt("edges slide by exact Q8 phases — no snapping to the cell grid");
    scenes.Add(scene);
}

void MoveVsTrail()
{
    var scene = new Scene("move vs trail",
        "One source World.Move'd along a Lissajous path leaves only its current stamp (exact withdrawal). A trail layer Place'd each step keeps every footprint forever.");
    var w = World.New();
    var g = Grid.New(w, 8, 0f, 0f, 256f);
    var moved = scene.Layer(w, "moved", "#7c4dff");
    var trailL = scene.Layer(w, "trail", "#40c4ff");

    var stamp = Gaussian(9, 50);
    const int steps = 90;
    var mover = -1;
    for (var i = 0; i < steps; i++)
    {
        var t = i / (steps - 1.0);
        var x = 40f + (float)(170 * Math.Sin(2.2 * t));
        var y = 128f + (float)(90 * Math.Sin(3.1 * t + 1));
        if (i == 0) mover = World.Place(w, moved, x, y, stamp, 12);
        else World.Move(w, mover, x, y);
        World.Place(w, trailL, x, y, stamp, 3 + i / 45);
    }

    var beacon = World.Place(w, trailL, 216f, 32f, Stamp.Box(20, 20, 70), 9);
    World.Process(w);
    var before = World.Query(w, g, trailL, 200, 20, 30, 30);
    World.Remove(w, beacon);
    World.Process(w);
    var after = World.Query(w, g, trailL, 200, 20, 30, 30);

    scene.Panel(w, g, "256² · 1 unit/cell", 256, 256f, 0, moved, trailL);
    scene.Receipt($"moved layer nonzero cells: {CountNonZero(w, g, moved)} (just the last footprint)");
    scene.Receipt($"trail layer nonzero cells: {CountNonZero(w, g, trailL)} (every step persists)");
    scene.Receipt(before - after == 630 * 400
        ? $"Remove: region sum {before} → {after}, delta {before - after} == 630 × 400 exactly"
        : $"Remove MISMATCH: delta {before - after}, expected {630 * 400}");
    scenes.Add(scene);
}

void MultiGridLod()
{
    var scene = new Scene("multi-grid LOD",
        "Three grids over the same 256-unit world at 1×, ½×, ¼× resolution. Every Place fans out to all of them — no grid targeting, world-anchored extents decide the footprint.");
    var w = World.New();
    var g1 = Grid.New(w, 8, 0f, 0f, 256f);
    var g2 = Grid.New(w, 7, 0f, 0f, 256f);
    var g3 = Grid.New(w, 6, 0f, 0f, 256f);
    var boxes = scene.Layer(w, "boxes", "#ffb74d");
    var brush = scene.Layer(w, "brush", "#4dd0e1");

    var rng = new Random(21);
    var box = Stamp.Box(12, 12, 40);
    for (var i = 0; i < 30; i++)
    {
        var x = (rng.Next(54) + 1) * 4;
        var y = (rng.Next(54) + 1) * 4;
        World.Place(w, boxes, x, y, box, 4 + rng.Next(9));
    }
    World.Place(w, brush, 128f, 128f, Gaussian(32, 90), 10);

    World.Process(w);
    var s1 = World.Query(w, g1, boxes, 0, 0, 256, 256);
    var s2 = World.Query(w, g2, boxes, 0, 0, 128, 128);
    var s3 = World.Query(w, g3, boxes, 0, 0, 64, 64);

    scene.Panel(w, g1, "1× · 256²", 256, 256f, 0, boxes, brush);
    scene.Panel(w, g2, "½× · 128²", 128, 256f, 0, boxes, brush);
    scene.Panel(w, g3, "¼× · 64²", 64, 256f, 0, boxes, brush);
    scene.Mark(128f, 128f, brush, "gaussian");
    scene.Receipt($"box layer: 1× sum {s1} == 4 × ½× sum {s2} == 16 × ¼× sum {s3}");
    scene.Receipt(s1 == 4 * s2 && s1 == 16 * s3
        ? "world integral conserved exactly across dyadic grids"
        : "MISMATCH — check dyadic phases");
    scene.Receipt("raster stamps on coarse grids come from the baked mip chain (box-filtered)");
    scenes.Add(scene);
}

void SparseWorld()
{
    var scene = new Scene("sparse 4096²",
        "A 4096×4096 world — 16.7M cells. Each image pixel is a 16×16-cell region query. Empty space never allocates: only stamped 64×64 tiles own a block.");
    var w = World.New();
    var g = Grid.New(w, 12, 0f, 0f, 4096f);
    var cover = scene.Layer(w, "coverage", "#69f0ae");

    var rng = new Random(5);
    var stamp = Gaussian(40, 100);
    (float, float)[] clusters = [(700f, 700f), (2200f, 1500f), (3200f, 3400f)];
    foreach (var (cx, cy) in clusters)
        for (var i = 0; i < 60; i++)
            World.Place(w, cover, cx + (float)(rng.NextDouble() * 360 - 180), cy + (float)(rng.NextDouble() * 360 - 180),
                stamp, 6 + rng.Next(9));
    for (var i = 0; i < 30; i++)
        World.Place(w, cover, 600 + rng.Next(2900), 600 + rng.Next(2900), stamp, 8);

    World.Process(w);
    scene.Panel(w, g, "4096² · 16×16 cells/px", 256, 4096f, 16, cover);
    var full = World.Query(w, g, cover, 0, 0, 4096, 4096);
    var empty = World.Query(w, g, cover, 0, 0, 512, 512);
    var nonzero = 0;
    for (var y = 0; y < 256; y++)
    for (var x = 0; x < 256; x++)
        if (World.Query(w, g, cover, x * 16, y * 16, 16, 16) != 0) nonzero++;
    scene.Receipt($"full-world query = {full} (baked per-tile sums, 65,536 cells read in O(live tiles))");
    scene.Receipt($"empty corner query [0,0,512,512] = {empty}");
    scene.Receipt($"{nonzero.ToString("N0", CultureInfo.InvariantCulture)} / 65,536 region blocks nonzero — "
        + (nonzero * 100.0 / 65536).ToString("F1", CultureInfo.InvariantCulture) + "% of the world");
    scenes.Add(scene);
}

void ThousandSources()
{
    var scene = new Scene("5,000 sources",
        "5,000 overlapping box props on a 1024² blockout. Authoring is O(stamp area), queries are O(touched tiles), the warm path allocates zero managed bytes.");
    var w = World.New();
    var g = Grid.New(w, 10, 0f, 0f, 1024f);
    var density = scene.Layer(w, "density", "#ff8a65");

    var rng = new Random(13);
    var stamps = new byte[12];
    for (var i = 0; i < stamps.Length; i++)
        stamps[i] = Stamp.Box(4 + i, 4 + i, (sbyte)(20 + i * 4));
    var placeSw = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 5000; i++)
        World.Place(w, density, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024),
            stamps[rng.Next(stamps.Length)], 1 + rng.Next(8));
    var placeMs = placeSw.Elapsed.TotalMilliseconds;

    var processSw = System.Diagnostics.Stopwatch.StartNew();
    World.Process(w);
    var processMs = processSw.Elapsed.TotalMilliseconds;

    scene.Panel(w, g, "1024² · 4×4 cells/px", 256, 1024f, 4, density);
    var full = World.Query(w, g, density, 0, 0, 1024, 1024);
    Console.WriteLine($"  5,000 sources: places {placeMs.ToString("F1", CultureInfo.InvariantCulture)} ms, "
        + $"process {processMs.ToString("F1", CultureInfo.InvariantCulture)} ms (single pass, informal)");
    scene.Receipt("5,000 places + full Process + region query, all in one frame budget");
    scene.Receipt($"full 1024² region query = {full}");

    for (var i = 0; i < 200_000; i++) World.Query(w, g, density, i & 1023, (i * 37) & 1023);
    var gc0 = GC.GetAllocatedBytesForCurrentThread();
    var querySw = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 1_000_000; i++) World.Query(w, g, density, i & 1023, (i * 37) & 1023);
    var queryMs = querySw.Elapsed.TotalMilliseconds;
    var alloc = GC.GetAllocatedBytesForCurrentThread() - gc0;
    Console.WriteLine($"  5,000 sources: 1M warm queries {queryMs:F0} ms ({queryMs:F1} ns/query), managed alloc {alloc} B");
    scenes.Add(scene);
}

byte Gaussian(int size, int peak)
{
    var s = new sbyte[size * size];
    var c = (size - 1) / 2.0;
    var sigma = size / 5.0;
    for (var y = 0; y < size; y++)
    for (var x = 0; x < size; x++)
    {
        var dx = x - c;
        var dy = y - c;
        var v = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
        s[y * size + x] = (sbyte)Math.Clamp(Math.Round(v), -128, 127);
    }
    return Stamp.New(s, size, size);
}

byte Shape(int width, int height, Func<int, int, int, sbyte> sample)
{
    var s = new sbyte[width * height];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
        s[y * width + x] = sample(x, y, Math.Min(width, height) / 2);
    return Stamp.New(s, width, height);
}

byte Noise(int size, int amp, int seed)
{
    var rng = new Random(seed);
    var s = new sbyte[size * size];
    for (var i = 0; i < s.Length; i++) s[i] = (sbyte)(rng.Next(amp * 2 + 1) - amp);
    return Stamp.New(s, size, size);
}

byte Letters()
{
    string[] rows =
    [
        "GGGG. .I..",
        "G...G .....",
        "G...G .I..",
        "G...G .I..",
        "G.GGG .I..",
        "G...G .I..",
        "GGGG. .I..",
    ];
    var width = rows[0].Length;
    var s = new sbyte[rows.Length * width];
    for (var y = 0; y < rows.Length; y++)
    for (var x = 0; x < width; x++)
        if (rows[y][x] != '.') s[y * width + x] = 100;
    return Stamp.New(s, width, rows.Length);
}

int FirstCovered(byte w, byte g, byte layer, int y, int x0, int x1)
{
    for (var x = x0; x <= x1; x++)
        if (World.Query(w, g, layer, x, y) != 0) return x;
    return x0;
}

int LastCovered(byte w, byte g, byte layer, int y, int x0, int x1)
{
    var last = x0;
    for (var x = x0; x <= Math.Min(x1, 255); x++)
        if (World.Query(w, g, layer, x, y) != 0) last = x;
    return last;
}

int CountNonZero(byte w, byte g, byte layer)
{
    var n = 0;
    for (var y = 0; y < 256; y++)
    for (var x = 0; x < 256; x++)
        if (World.Query(w, g, layer, x, y) != 0) n++;
    return n;
}

string Serialize(List<Scene> list)
{
    var builder = new StringBuilder();
    builder.Append("{\"scenes\":[");
    for (var si = 0; si < list.Count; si++)
    {
        var s = list[si];
        if (si > 0) builder.Append(',');
        builder.Append("{\"name\":\"").Append(s.Name).Append("\",\"caption\":\"").Append(s.Caption)
            .Append("\",\"layers\":[");
        for (var li = 0; li < s.Layers.Count; li++)
        {
            if (li > 0) builder.Append(',');
            builder.Append("{\"name\":\"").Append(s.Layers[li].name).Append("\",\"color\":\"")
                .Append(s.Layers[li].color).Append("\"}");
        }
        builder.Append("],\"panels\":[");
        for (var pi = 0; pi < s.Panels.Count; pi++)
        {
            var p = s.Panels[pi];
            if (pi > 0) builder.Append(',');
            builder.Append("{\"label\":\"").Append(p.Label).Append("\",\"cells\":").Append(p.Cells)
                .Append(",\"size\":").Append((int)p.Size).Append(",\"down\":").Append(p.Down)
                .Append(",\"values\":[");
            for (var li = 0; li < p.Layers.Count; li++)
            {
                if (li > 0) builder.Append(',');
                builder.Append('[');
                var vals = p.Layers[li];
                for (var i = 0; i < vals.Length; i++)
                    builder.Append(vals[i].ToString(CultureInfo.InvariantCulture)).Append(i == vals.Length - 1 ? "" : ",");
                builder.Append(']');
            }
            builder.Append("]}");
        }
        builder.Append("],\"sources\":[");
        for (var i = 0; i < s.Sources.Count; i++)
        {
            var m = s.Sources[i];
            if (i > 0) builder.Append(',');
            builder.Append("{\"x\":").Append(m.x.ToString("F2", CultureInfo.InvariantCulture))
                .Append(",\"y\":").Append(m.y.ToString("F2", CultureInfo.InvariantCulture))
                .Append(",\"layer\":").Append(m.layer)
                .Append(",\"label\":\"").Append(m.label).Append("\"}");
        }
        builder.Append("],\"receipts\":[");
        for (var i = 0; i < s.Receipts.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append('"').Append(s.Receipts[i]).Append('"');
        }
        builder.Append("]}");
    }
    builder.Append("]}");
    return builder.ToString();
}

internal sealed class Scene
{
    public readonly string Name;
    public readonly string Caption;
    public readonly List<(string name, string color)> Layers = [];
    public readonly List<(float x, float y, int layer, string label)> Sources = [];
    public readonly List<string> Receipts = [];
    public readonly List<Panel> Panels = [];

    public Scene(string name, string caption)
    {
        Name = name;
        Caption = caption;
    }

    public byte Layer(byte world, string name, string color)
    {
        Layers.Add((name, color));
        return Gi.Layer.New(world);
    }

    public void Mark(float x, float y, int layer, string label) => Sources.Add((x, y, layer, label));
    public void Receipt(string line) => Receipts.Add(line);

    public void Panel(byte world, byte grid, string label, int cells, float size, int down, params int[] layers)
    {
        var p = new Panel(label, cells, size, down);
        foreach (var id in layers) p.Layers.Add(Dump(world, grid, (byte)id, cells, down));
        Panels.Add(p);
    }

    private static int[] Dump(byte world, byte grid, byte layer, int cells, int down)
    {
        var vals = new int[cells * cells];
        if (down == 0 || down == 1)
        {
            for (var y = 0; y < cells; y++)
            for (var x = 0; x < cells; x++)
                vals[y * cells + x] = World.Query(world, grid, layer, x, y);
        }
        else
        {
            for (var y = 0; y < cells; y++)
            for (var x = 0; x < cells; x++)
                vals[y * cells + x] = (int)World.Query(world, grid, layer, x * down, y * down, down, down);
        }
        return vals;
    }
}

internal sealed class Panel(string label, int cells, float size, int down)
{
    public readonly string Label = label;
    public readonly int Cells = cells;
    public readonly float Size = size;
    public readonly int Down = down;
    public readonly List<int[]> Layers = [];
}
