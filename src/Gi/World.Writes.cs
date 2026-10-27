using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    private static ushort QuantizeAngle(float radians)
    {
        var turns = radians * (1.0 / (2 * Math.PI));
        turns -= Math.Floor(turns);
        return (ushort)((int)(turns * 65536.0) & 0xFFFF);
    }

    [MethodImpl(Jit.Hot)]
    private static bool FootprintFits(WorldCtx* w, byte stamp, long scale)
    {
        var v = StampCatalog.Get(stamp);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var scaleQ8 = w->Grids[gi].ScaleQ8;
            if (scaleQ8 == 0) continue;

            var sampling = (scaleQ8 * scale) >> 8;
            if (!ValidationRules.IsFootprint((v->Width * sampling + 255) >> 8) ||
                !ValidationRules.IsFootprint((v->Height * sampling + 255) >> 8))
                return false;
        }

        return true;
    }

    [MethodImpl(Jit.Hot)]
    private static ushort QuantizeScale(float scale)
    {
        var quantized = Math.Floor(scale * 256.0);
        return (ushort)(quantized < 1 ? 1 : quantized > ushort.MaxValue ? ushort.MaxValue : quantized);
    }

    [MethodImpl(Jit.Hot)]
    private static SourceResultStatus PlaceSlot(
        WorldCtx* w, byte layer, float x, float y, byte stamp, sbyte gain, ushort scale, out SourceId id)
    {
        id = default;
        if (layer >= w->LayerCount || IsDerived(w, layer) || IsTransient(w, layer))
            return SourceResultStatus.InvalidLayer;
        if (!StampCatalog.Valid(stamp) || !FootprintFits(w, stamp, UnitScale) ||
            (scale != UnitScale && !FootprintFits(w, stamp, scale)))
            return SourceResultStatus.InvalidStamp;
        if (!float.IsFinite(x) || !float.IsFinite(y)) return SourceResultStatus.InvalidSource;

        var s = &w->Sources;
        if (!s->Free.TryPop(out var i))
        {
            if (s->Count >= MaxSourceSlots) return SourceResultStatus.SourceLimitReached;
            if (s->Count == s->Slots) EnsureSourceCapacity(s, w, s->Count + 1);
            i = s->Count++;
        }

        var g = (byte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        BeforeWrite(w, s, i);
        var gen = s->Gen[i] + 1;
        s->Gen[i] = gen;
        s->X[i] = x;
        s->Y[i] = y;
        s->Stamp[i] = stamp;
        s->Layer[i] = layer;
        s->Gain[i] = g;
        s->Angle[i] = 0;
        s->Scale[i] = scale;
        JournalSchedule(w, s, i);
        s->Timed.Pointer[i] = 0;
        s->Alive[i] = 1;
        MarkChanged(w, s, i);
        id = new SourceId((uint)i, gen);
        return SourceResultStatus.Applied;
    }

    [MethodImpl(Jit.Inline)]
    private static void WriteMove(WorldCtx* w, SourceColumns* s, int i, float x, float y)
    {
        var oldX = s->X[i];
        var oldY = s->Y[i];
        if (oldX.Equals(x) && oldY.Equals(y)) return;

        var changed = PositionChanged(w, s->Layer[i], oldX, oldY, x, y);
        if (changed) BeforeWrite(w, s, i);
        s->X[i] = x;
        s->Y[i] = y;
        if (changed) MarkChanged(w, s, i);
    }

    [MethodImpl(Jit.Inline)]
    private static void WriteMoveExact(WorldCtx* w, SourceColumns* s, int i, float x, float y)
    {
        var oldX = s->X[i];
        var oldY = s->Y[i];
        if (*(int*)&x == *(int*)&oldX && *(int*)&y == *(int*)&oldY) return;

        var changed = PositionChanged(w, s->Layer[i], oldX, oldY, x, y);
        if (changed) BeforeWrite(w, s, i);
        s->X[i] = x;
        s->Y[i] = y;
        if (changed) MarkChanged(w, s, i);
    }

    [MethodImpl(Jit.Inline)]
    private static void WriteTurn(WorldCtx* w, SourceColumns* s, int i, ushort next)
    {
        var old = s->Angle[i];
        if (next == old) return;

        var changed = AngleChanged(w, s->Layer[i], old, next);
        if (changed) BeforeWrite(w, s, i);
        s->Angle[i] = next;
        if (changed) MarkChanged(w, s, i);
    }

    [MethodImpl(Jit.Inline)]
    private static void WriteScale(WorldCtx* w, SourceColumns* s, int i, ushort next)
    {
        if (next == s->Scale[i] || !FootprintFits(w, s->Stamp[i], next)) return;

        BeforeWrite(w, s, i);
        s->Scale[i] = next;
        MarkChanged(w, s, i);
    }

    [MethodImpl(Jit.Hot)]
    private static void WriteGain(WorldCtx* w, SourceColumns* s, int i, sbyte gain)
    {
        var next = (byte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        if ((s->Timed.Pointer[i] & Fading) != 0)
        {
            JournalSchedule(w, s, i);
            s->Timed.Pointer[i] &= unchecked((byte)~Fading);
        }

        if (next == s->Gain[i]) return;

        BeforeWrite(w, s, i);
        s->Gain[i] = next;
        MarkChanged(w, s, i);
    }

    [MethodImpl(Jit.Hot)]
    private static void WriteRemove(WorldCtx* w, SourceColumns* s, int i)
    {
        BeforeWrite(w, s, i);
        if (s->Timed.Pointer[i] != 0)
        {
            JournalSchedule(w, s, i);
            s->Timed.Pointer[i] = 0;
        }

        s->Alive[i] = 0;
        if (s->Gen[i] != uint.MaxValue) s->Free.Push(i);
        MarkChanged(w, s, i);
    }
}
