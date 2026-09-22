using System.Text;
using GridInfluence;

const int Cells = 256;
const float Size = 256f;

var w = World.New();
var g = Grid.New(w, 8, 0f, 0f, Size);   // 256x256 cells, 1 cell = 1 unit
var threat = Layer.New(w);
var trail = Layer.New(w);
var danger = Layer.New(w);

var markers = new List<(float x, float y, int layer, string label)>();

static byte GaussianStamp(int size, int peak)
{
    var s = new sbyte[size * size];
    var c = (size - 1) / 2.0;
    var sigma = size / 5.0;
    for (var y = 0; y < size; y++)
    for (var x = 0; x < size; x++)
    {
        var dx = x - c; var dy = y - c;
        var v = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
        s[y * size + x] = (sbyte)Math.Clamp(Math.Round(v), -128, 127);
    }
    return Stamp.New(s, size, size);
}

// boss aura: saturating gaussian cone
var boss = GaussianStamp(48, 120);
World.Place(w, threat, 140f, 120f, boss, 16);
markers.Add((140f, 120f, threat, "boss aura"));

// minions
var minion = Stamp.Box(10, 10, 45);
var rng = new Random(7);
for (var i = 0; i < 7; i++)
{
    var a = i / 7.0 * Math.PI * 2;
    var x = 140f + (float)(Math.Cos(a) * (38 + rng.Next(18)));
    var y = 120f + (float)(Math.Sin(a) * (38 + rng.Next(18)));
    World.Place(w, threat, x, y, minion, 4 + rng.Next(7));
    markers.Add((x, y, threat, "minion"));
}

// player trail: fading boxes along a sine path
var trailStamp = Stamp.Box(8, 8, 28);
for (var i = 0; i < 28; i++)
{
    var t = i / 27.0;
    var x = 24f + (float)(t * 200);
    var y = 205f - (float)(t * 140) + (float)(Math.Sin(t * 9) * 9);
    World.Place(w, trail, x, y, trailStamp, 14 - i / 2);
    markers.Add((x, y, trail, "trail"));
}

// danger: negative water body + fear kill spots
var water = GaussianStamp(36, -100);
World.Place(w, danger, 62f, 60f, water, 12);
markers.Add((62f, 60f, danger, "water"));

var fear = Stamp.Box(6, 6, -60);
for (var i = 0; i < 3; i++)
{
    var x = 180f + (float)(rng.NextDouble() * 50);
    var y = 170f + (float)(rng.NextDouble() * 50);
    World.Place(w, danger, x, y, fear, 6);
    markers.Add((x, y, danger, "fear"));
}

World.Process(w);

// dump layers
var layerDefs = new (byte id, string name, string hex)[]
{
    (threat, "threat", "#ff5252"),
    (trail, "trail", "#40c4ff"),
    (danger, "danger", "#69f0ae"),
};

var json = new StringBuilder();
json.Append("{\"cells\":").Append(Cells).Append(",\"size\":").Append((int)Size);
json.Append(",\"layers\":[");
for (var li = 0; li < layerDefs.Length; li++)
{
    var (id, name, hex) = layerDefs[li];
    if (li > 0) json.Append(',');
    json.Append("{\"name\":\"").Append(name).Append("\",\"color\":\"").Append(hex).Append("\",\"values\":[");
    for (var y = 0; y < Cells; y++)
    for (var x = 0; x < Cells; x++)
        json.Append(World.Query(w, g, id, x, y)).Append(x == Cells - 1 && y == Cells - 1 ? "" : ",");
    json.Append("]}");
}
json.Append("],\"sources\":[");
for (var i = 0; i < markers.Count; i++)
{
    var m = markers[i];
    if (i > 0) json.Append(',');
    json.Append("{\"x\":").Append(m.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
        .Append(",\"y\":").Append(m.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
        .Append(",\"layer\":").Append(m.layer)
        .Append(",\"label\":\"").Append(m.label).Append("\"}");
}
json.Append("]}");

var html = Template.Html.Replace("/*__DATA__*/", json.ToString());
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "index.html"), html);
// also drop a copy at the .bench/viz dir root for convenience
File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "index.html"), html);
Console.WriteLine($"wrote index.html ({html.Length / 1024} KB), {markers.Count} markers, 3 layers x {Cells}x{Cells}");
