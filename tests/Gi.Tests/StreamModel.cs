namespace Gi.Tests;

internal enum StreamOp : byte
{
    Place,
    Move,
    Turn,
    Scale,
    SetGain,
    Remove,
    Fade,
    Expire,
    Sync,
    Record,
    StopRecording,
    Rewind,
    Clear,
    Process,
    Read,
    Splat,
}

internal enum ReadKind : byte
{
    QueryRect,
    QueryRegion,
    QueryAt,
    QueryMaxWhole,
    QueryMaxRect,
    QueryGradient,
    Sense,
    SenseEx,
    SenseArea,
    SenseAreaEx,
    SenseMax,
    SenseNearest,
    SenseGradient,
    SenseGradientEx,
    ChangedDisk,
    ChangedTilesSince,
    ChangedTilesAll,
    ChangedMask,
    SenseColumn,
    SenseColumnEx,
    SenseGradientColumn,
    Covers,
    LayerLive,
    SourceSpec,
}

internal enum StampKind : byte
{
    Box,
    Tent,
    Bell,
    Disk,
    Cone,
    Dome,
    Raster,
}

internal struct StreamCommand
{
    public StreamOp Op;
    public ReadKind Read;
    public byte Layer;
    public byte Grid;
    public byte Stamp;
    public int Handle;
    public sbyte Gain;
    public int Int0;
    public float X;
    public float Y;
    public ulong Aux;
}

internal struct GridSpec
{
    public int Power;
    public float X;
    public float Y;
    public float Size;
}

internal struct LayerSpec
{
    public bool Derived;
    public LayerOp Op;
    public byte A;
    public byte B;
    public int WeightA;
    public int WeightB;
    public int Shift;
    public short Low;
    public short High;
    public Layer.Precision Precision;
    public bool Transient;
}

internal struct StampSpec
{
    public StampKind Kind;
    public int Width;
    public int Height;
    public int Radius;
    public int Arc;
    public int Value;
    public ulong DataSeed;
}

internal sealed class StreamSetup
{
    public int Seed;
    public GridSpec[] Grids = [];
    public LayerSpec[] Layers = [];
    public StampSpec[] Stamps = [];
    public int MaxLive;
    public List<StreamCommand> Commands = [];
}
