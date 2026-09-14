using GridInfluence;

namespace GridInfluence.Io;

public sealed class SceneRunner : IDisposable
{
    private readonly SceneDocument _document;
    private readonly string _baseDirectory;
    private readonly FieldRegistry _registry = new();
    private readonly Dictionary<string, FieldId> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeightMap> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldStats> _lastStats = new(StringComparer.Ordinal);
    public SceneRunner(SceneDocument document, string baseDirectory)
    {
        _document = document;
        _baseDirectory = baseDirectory;
        foreach (var field in document.Fields)
        {
            var id = _registry.Register(new FieldConfig(
                StableKey(field.Key),
                field.ChunkPower,
                field.RetentionFrames,
                field.DecayPerMille,
                Math.Max(1, field.SpreadDenominator),
                8,
                field.DoubleBuffered));
            if (!id.IsValid) throw new InvalidOperationException($"Duplicate field key: {field.Key}");

            _ids.Add(field.Key, id);
        }

        foreach (var image in document.Images)
        {
            if (_images.ContainsKey(image.Path)) continue;

            _images.Add(image.Path, Pnm.LoadWeights(Resolve(image.Path), image.Signed));
        }
    }

    public FieldRegistry Registry => _registry;

    public FieldStats LastStats(string fieldKey)
        => _lastStats.TryGetValue(fieldKey, out var stats) ? stats : default;

    public int Frames => _document.Frames;

    public FieldId IdOf(string key) => _ids[key];

    private static ushort StableKey(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key) hash = (hash ^ c) * 16777619u;

        return (ushort)(hash & 0xFFFF);
    }

    private string Resolve(string relative) => Path.Combine(_baseDirectory, relative);

    public void Step(int frame)
    {
        foreach (var pair in EnumeratePairs()) ApplyImages(pair.Value, pair.Key, frame, preStep: true);

        foreach (var field in _document.Fields)
        {
            var pair = _registry.Pair(_ids[field.Key]);
            _lastStats[field.Key] = pair.Step(BuildStamps(field.Key, frame));
        }

        foreach (var pair in EnumeratePairs()) ApplyImages(pair.Value, pair.Key, frame, preStep: false);
    }

    private IEnumerable<KeyValuePair<int, FieldPair>> EnumeratePairs()
    {
        for (var i = 0; i < _registry.Count; i++) yield return new(i, _registry.Pair(new FieldId(i)));
    }

    private void ApplyImages(FieldPair pair, int index, int frame, bool preStep)
    {
        foreach (var image in _document.Images)
        {
            if (_ids[image.Field].Value != index) continue;
            if (image.EveryFrame)
            {
                if (!preStep) continue;
            }
            else
            {
                if (preStep || frame != image.FromFrame) continue;
            }

            var map = _images[image.Path];
            pair.Front.WriteRegion(new Int2(image.Origin[0], image.Origin[1]), new Int2(map.Width, map.Height), map.Samples);
        }
    }

    private Stamp[] BuildStamps(string fieldKey, int frame)
    {
        var stamps = new List<Stamp>();
        foreach (var clip in _document.Clips)
        {
            if (clip.Field != fieldKey) continue;
            if (frame < clip.FromFrame || frame > clip.ToFrame) continue;

            var origin = MotionFactory.Evaluate(clip.Motion, frame, clip.FromFrame, clip.ToFrame);
            var shape = ShapeFactory.Build(clip.Shape, clip.Weight);
            if (shape.TryScaleWeight(1f, out var scaled)) stamps.Add(new Stamp(scaled, origin));
        }

        return stamps.ToArray();
    }

    public void Capture(CaptureSpec capture)
    {
        var pair = _registry.Pair(_ids[capture.Field]);
        var size = new Int2(capture.Size[0], capture.Size[1]);
        var origin = new Int2(capture.Origin[0], capture.Origin[1]);
        var region = new int[size.X * size.Y];
        pair.Front.ReadRegion(origin, size, region);

        var path = Resolve(capture.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (capture.Color) Pnm.SaveColorRgb(path, region, size.X, size.Y);
        else Pnm.SaveGraySigned(path, region, size.X, size.Y, 65535);
    }

    public void Dispose()
    {
        foreach (var map in _images.Values) map.Dispose();

        _images.Clear();
        _registry.Dispose();
    }
}
