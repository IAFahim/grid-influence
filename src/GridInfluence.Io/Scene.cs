using System.Text.Json;
using System.Text.Json.Serialization;
using GridInfluence;

namespace GridInfluence.Io;

public sealed class SceneDocument
{
    [JsonPropertyName("frames")]
    public int Frames { get; set; } = 1;

    [JsonPropertyName("fields")]
    public List<FieldSpec> Fields { get; set; } = [];

    [JsonPropertyName("images")]
    public List<ImageLayer> Images { get; set; } = [];

    [JsonPropertyName("clips")]
    public List<ClipSpec> Clips { get; set; } = [];

    [JsonPropertyName("captures")]
    public List<CaptureSpec> Captures { get; set; } = [];

    [JsonPropertyName("baseDirectory")]
    public string? BaseDirectory { get; set; }
}

public sealed class FieldSpec
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("chunkPower")]
    public int ChunkPower { get; set; } = 5;

    [JsonPropertyName("retentionFrames")]
    public uint RetentionFrames { get; set; } = 256;

    [JsonPropertyName("decayPerMille")]
    public int DecayPerMille { get; set; }

    [JsonPropertyName("spreadDenominator")]
    public int SpreadDenominator { get; set; } = 1;

    [JsonPropertyName("doubleBuffered")]
    public bool DoubleBuffered { get; set; }
}

public sealed class ImageLayer
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("origin")]
    public List<int> Origin { get; set; } = [0, 0];

    [JsonPropertyName("fromFrame")]
    public int FromFrame { get; set; }

    [JsonPropertyName("toFrame")]
    public int ToFrame { get; set; } = int.MaxValue;

    [JsonPropertyName("everyFrame")]
    public bool EveryFrame { get; set; }

    [JsonPropertyName("signed")]
    public bool Signed { get; set; } = true;
}

public sealed class ClipSpec
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("fromFrame")]
    public int FromFrame { get; set; }

    [JsonPropertyName("toFrame")]
    public int ToFrame { get; set; }

    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 100;

    [JsonPropertyName("shape")]
    public ShapeSpec Shape { get; set; } = new();

    [JsonPropertyName("motion")]
    public MotionSpec Motion { get; set; } = new();
}

public sealed class ShapeSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "disc";

    [JsonPropertyName("min")]
    public List<int>? Min { get; set; }

    [JsonPropertyName("size")]
    public List<int>? Size { get; set; }

    [JsonPropertyName("radius")]
    public int Radius { get; set; }

    [JsonPropertyName("innerRadius")]
    public int InnerRadius { get; set; }

    [JsonPropertyName("thickness")]
    public int Thickness { get; set; }

    [JsonPropertyName("radii")]
    public List<int>? Radii { get; set; }

    [JsonPropertyName("start")]
    public List<int>? Start { get; set; }

    [JsonPropertyName("end")]
    public List<int>? End { get; set; }

    [JsonPropertyName("dir0")]
    public List<int>? Dir0 { get; set; }

    [JsonPropertyName("dir1")]
    public List<int>? Dir1 { get; set; }
}

public sealed class MotionSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "static";

    [JsonPropertyName("from")]
    public List<int> From { get; set; } = [0, 0];

    [JsonPropertyName("to")]
    public List<int>? To { get; set; }

    [JsonPropertyName("center")]
    public List<int>? Center { get; set; }

    [JsonPropertyName("radius")]
    public float Radius { get; set; }

    [JsonPropertyName("periodFrames")]
    public int PeriodFrames { get; set; } = 120;
}

public sealed class CaptureSpec
{
    [JsonPropertyName("frame")]
    public int Frame { get; set; }

    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("origin")]
    public List<int> Origin { get; set; } = [0, 0];

    [JsonPropertyName("size")]
    public List<int> Size { get; set; } = [128, 128];

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("color")]
    public bool Color { get; set; }
}

public static class SceneDocumentLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static SceneDocument Load(string path)
    {
        var document = JsonSerializer.Deserialize<SceneDocument>(File.ReadAllText(path), Options)
                       ?? throw new InvalidDataException($"{path}: empty scene document.");

        foreach (var field in document.Fields)
        {
            if (field.Key.Length == 0) throw new InvalidDataException($"{path}: a field spec has no key.");
            if (field.ChunkPower is < 1 or > 8)
                throw new InvalidDataException($"{path}: field {field.Key} chunkPower must be in 1..8.");
            if (field.DecayPerMille is < 0 or > 1000)
                throw new InvalidDataException($"{path}: field {field.Key} decayPerMille must be in 0..1000.");
        }

        var keys = document.Fields.Select(f => f.Key).ToHashSet();
        foreach (var clip in document.Clips)
        {
            if (!keys.Contains(clip.Field)) throw new InvalidDataException($"{path}: clip references unknown field {clip.Field}.");
            if (clip.FromFrame > clip.ToFrame) throw new InvalidDataException($"{path}: a clip has fromFrame > toFrame.");
        }

        foreach (var image in document.Images)
        {
            if (!keys.Contains(image.Field)) throw new InvalidDataException($"{path}: image references unknown field {image.Field}.");
        }

        foreach (var capture in document.Captures)
        {
            if (capture.Origin.Count != 2 || capture.Size.Count != 2)
                throw new InvalidDataException($"{path}: capture origin/size must be [x, y].");
        }

        return document;
    }
}

public static class ShapeFactory
{
    public static InfluenceShape Build(ShapeSpec spec, int weight)
    {
        var bounded = (sbyte)Math.Clamp(weight, sbyte.MinValue, sbyte.MaxValue);
        var kind = spec.Kind.ToLowerInvariant();
        return kind switch
        {
            "solidrect" => InfluenceShape.SolidRect(List2(spec.Min), List2(spec.Size), bounded),
            "rectshell" => InfluenceShape.RectShell(List2(spec.Min), List2(spec.Size), spec.Thickness, bounded),
            "disc" => InfluenceShape.Disc(Int2.Zero, spec.Radius, bounded),
            "annulus" => InfluenceShape.Annulus(Int2.Zero, spec.Radius, spec.InnerRadius, bounded),
            "capsule" => InfluenceShape.Capsule(List2(spec.Start), List2(spec.End), spec.Radius, bounded),
            "ellipse" => InfluenceShape.Ellipse(Int2.Zero, List2(spec.Radii), bounded),
            "roundedrect" => InfluenceShape.RoundedRect(List2(spec.Min), List2(spec.Size), spec.Radius, bounded),
            "thickline" => InfluenceShape.ThickLine(List2(spec.Start), List2(spec.End), spec.Radius, bounded),
            "sector" => InfluenceShape.Sector(Int2.Zero, spec.Radius, List2(spec.Dir0), List2(spec.Dir1), bounded),
            _ => throw new InvalidDataException($"Unknown shape kind: {spec.Kind}")
        };
    }

    private static Int2 List2(List<int>? values)
    {
        if (values is null || values.Count != 2) throw new InvalidDataException("Shape geometry expects [x, y] lists.");

        return new Int2(values[0], values[1]);
    }
}

public static class MotionFactory
{
    public static Int2 Evaluate(MotionSpec motion, int frame, int clipFrom, int clipTo)
    {
        return motion.Kind.ToLowerInvariant() switch
        {
            "static" => new Int2(motion.From[0], motion.From[1]),
            "line" => Line(motion, frame, clipFrom, clipTo),
            "circle" => Circle(motion, frame),
            _ => throw new InvalidDataException($"Unknown motion kind: {motion.Kind}")
        };
    }

    private static Int2 Line(MotionSpec motion, int frame, int clipFrom, int clipTo)
    {
        if (motion.To is null) throw new InvalidDataException("Line motion expects a to list.");

        var duration = Math.Max(1, clipTo - clipFrom);
        var progress = Math.Clamp((float)(frame - clipFrom) / duration, 0f, 1f);
        var from = new Int2(motion.From[0], motion.From[1]);
        var to = new Int2(motion.To[0], motion.To[1]);
        return new Int2(
            (int)MathF.Round(from.X + (to.X - from.X) * progress, MidpointRounding.AwayFromZero),
            (int)MathF.Round(from.Y + (to.Y - from.Y) * progress, MidpointRounding.AwayFromZero));
    }

    private static Int2 Circle(MotionSpec motion, int frame)
    {
        if (motion.Center is null) throw new InvalidDataException("Circle motion expects a center list.");

        var period = Math.Max(1, motion.PeriodFrames);
        var angle = 2.0 * Math.PI * (frame % period) / period;
        return new Int2(
            (int)MathF.Round(motion.Center[0] + motion.Radius * (float)Math.Cos(angle), MidpointRounding.AwayFromZero),
            (int)MathF.Round(motion.Center[1] + motion.Radius * (float)Math.Sin(angle), MidpointRounding.AwayFromZero));
    }
}
