namespace GridInfluence;

public readonly struct FieldId : IEquatable<FieldId>
{
    public readonly int Value;

    public FieldId(int value) => Value = value;

    public bool IsValid => Value >= 0;

    public static FieldId Invalid => new(-1);

    public bool Equals(FieldId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is FieldId other && Equals(other);

    public override int GetHashCode() => Value;

    public override string ToString() => Value.ToString();
}

public readonly struct FieldConfig
{
    public readonly ushort Key;
    public readonly int ChunkPower;
    public readonly uint RetentionFrames;
    public readonly int DecayPerMille;
    public readonly int SpreadDenominator;
    public readonly int StrideAlignment;
    public readonly bool DoubleBuffered;

    public FieldConfig(
        ushort key,
        int chunkPower,
        uint retentionFrames,
        int decayPerMille = 0,
        int spreadDenominator = 1,
        int strideAlignment = 8,
        bool doubleBuffered = false)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decayPerMille, 1000);
        Key = key;
        ChunkPower = chunkPower;
        RetentionFrames = retentionFrames;
        DecayPerMille = decayPerMille;
        SpreadDenominator = spreadDenominator;
        StrideAlignment = strideAlignment;
        DoubleBuffered = doubleBuffered;
    }

    public bool NeedsDoubleBuffer => DoubleBuffered || DecayPerMille > 0;
}

public sealed class FieldPair : IDisposable
{
    public FieldConfig Config { get; }
    public Field Front { get; private set; }
    public Field Back { get; private set; }
    public bool DoubleBuffered { get; }
    public uint Tick { get; private set; }

    internal FieldPair(FieldConfig config)
    {
        Config = config;
        var align = config.StrideAlignment == 0 ? 8 : config.StrideAlignment;
        var spec = GridSpec.FromPowerOfTwo(config.ChunkPower, config.RetentionFrames, align);
        Front = new Field(spec);
        DoubleBuffered = config.NeedsDoubleBuffer;
        Back = DoubleBuffered ? new Field(spec) : default;
    }

    public FieldStats Step(ReadOnlySpan<FieldStamp> stamps)
    {
        Tick++;
        var hasPending = stamps.Length > 0;
        var hasChunks = Front.ActiveSlotCount > 0;
        var needsStencil = DoubleBuffered && Config.DecayPerMille > 0 && hasChunks;
        if (!hasPending && !hasChunks && !needsStencil) return default;

        if (!Back.IsCreated) return Front.Tick(stamps, Tick);

        var stencil = Config.DecayPerMille > 0
            ? Stencil.Create(Front, Config.DecayPerMille, Config.SpreadDenominator)
            : default;
        var stats = Back.Tick(stamps, Tick, stencil);
        Swap();
        return stats;
    }

    public void Swap()
    {
        if (!Back.IsCreated) return;

        var front = Front;
        Front = Back;
        Back = front;
    }

    public void Dispose()
    {
        Front.Dispose();
        if (Back.IsCreated) Back.Dispose();
    }
}

public sealed class FieldRegistry : IDisposable
{
    private readonly List<FieldPair> _pairs = [];
    private readonly Dictionary<ushort, int> _keyToIndex = new();

    public int Count => _pairs.Count;

    public FieldId Register(FieldConfig config)
    {
        if (_keyToIndex.ContainsKey(config.Key)) return FieldId.Invalid;

        _keyToIndex.Add(config.Key, _pairs.Count);
        _pairs.Add(new FieldPair(config));
        return new FieldId(_pairs.Count - 1);
    }

    public FieldPair Pair(FieldId id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id.Value);
        if (id.Value >= _pairs.Count) throw new ArgumentOutOfRangeException(nameof(id));

        return _pairs[id.Value];
    }

    public bool TryPair(ushort key, out FieldPair pair)
    {
        if (_keyToIndex.TryGetValue(key, out var index))
        {
            pair = _pairs[index];
            return true;
        }

        pair = null!;
        return false;
    }

    public void Dispose()
    {
        foreach (var pair in _pairs) pair.Dispose();

        _pairs.Clear();
        _keyToIndex.Clear();
    }
}
