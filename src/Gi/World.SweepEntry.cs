using System.Runtime.CompilerServices;

namespace Gi;

internal struct SweepEntry
{
    public int Slot;
    public float X;
    public float Y;
    public uint Meta;
    public uint Shape;
}

public static unsafe partial class World
{
    [MethodImpl(Jit.Inline)]
    private static SweepEntry EntryOf(SourceColumns* s, int slot)
    {
        return new SweepEntry
        {
            Slot = slot,
            X = s->X[slot],
            Y = s->Y[slot],
            Meta = s->Layer[slot] | ((uint)s->Stamp[slot] << 8) | ((uint)s->Gain[slot] << 16) |
                   ((uint)s->Alive[slot] << 24),
            Shape = s->Angle[slot] | ((uint)s->Scale[slot] << 16)
        };
    }
}
