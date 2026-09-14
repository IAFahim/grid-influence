using GridInfluence;
using GridInfluence.Io;

var scenePath = "assets/scene.json";
var outputDirectory = "frames";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--scene") scenePath = args[i + 1];
    if (args[i] == "--out") outputDirectory = args[i + 1];
}

var document = SceneDocumentLoader.Load(scenePath);
var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(scenePath))!;
outputDirectory = Path.GetFullPath(outputDirectory);
foreach (var capture in document.Captures)
    capture.Path = Path.GetFullPath(capture.Path, outputDirectory);

using var runner = new SceneRunner(document, baseDirectory);
var terrain = runner.IdOf("terrain");

Console.WriteLine("running scene...");
for (var frame = 0; frame < document.Frames; frame++)
{
    runner.Step(frame);
    var stats = runner.LastStats("threat");
    if (frame % 30 == 0 || frame == document.Frames - 1)
        Console.WriteLine(
            $"frame {frame,3}: threat stampsIn {stats.StampsIn,3} activeChunks {stats.ActiveSlots,3} " +
            $"activated {stats.ChunksActivated,3}");

    foreach (var capture in document.Captures.Where(c => c.Frame == frame)) runner.Capture(capture);
}

var threatField = runner.Registry.Pair(runner.IdOf("threat")).Front;
var terrainField = runner.Registry.Pair(terrain).Front;
var threatReader = threatField.AsReader();
var terrainReader = terrainField.AsReader();

Console.WriteLine();
Console.WriteLine($"threat at patrol end (24, 20)   = {threatReader.ReadCell(new Int2(24, 20))}");
Console.WriteLine($"threat at orbit (26, -6)        = {threatReader.ReadCell(new Int2(26, -6))}");
Console.WriteLine($"threat on negative wall (20,11) = {threatReader.ReadCell(new Int2(20, 11))}");
Console.WriteLine($"terrain at (-18, -18)           = {terrainReader.ReadCell(new Int2(-18, -18))}");
Console.WriteLine($"terrain controller (-18,-18)    = {Territory.Controller(terrainReader, new Int2(-18, -18))}");
Console.WriteLine($"threat gradient at (18, 14)     = {threatReader.Gradient(new Int2(18, 14))}");
Console.WriteLine($"steer away from (18, 14)        = {FlowSteering.Direction(threatReader, new Int2(18, 14))}");
Console.WriteLine($"threat score (18,16)+(10,10)    = {Capture.Score(threatReader, new Int2(18, 16), new Int2(10, 10))}");
Console.WriteLine($"safe to place at (-28, 28)?     = {Placement.IsValid(threatReader, new Int2(-28, 28), new Int2(4, 4), 10)}");
Console.WriteLine($"safe to place at (22, 18)?      = {Placement.IsValid(threatReader, new Int2(22, 18), new Int2(4, 4), 10)}");

var exported = new int[64 * 64];
threatField.ReadRegion(new Int2(-32, -32), new Int2(64, 64), exported);
Console.WriteLine();
Console.WriteLine($"exported 64x64 threat snapshot, max |value| = {exported.Max(Math.Abs)}");
Console.WriteLine($"frames written to {outputDirectory}");
