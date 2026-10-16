using System.Runtime.CompilerServices;

namespace Gi;

internal struct TimedEntry
{
    public int Slot;
    public int Next;
}

internal struct ScheduleRecord
{
    public int Slot;
    public int FadeStart;
    public int FadeTicks;
    public int ExpireAt;
    public byte Timed;
    public sbyte FadeFrom;
    public sbyte FadeTo;
}

public static unsafe partial class World
{
    private const byte Fading = 1;
    private const byte Expiring = 2;

    public static int Tick(byte world)
    {
        var w = GetContext(world);
        return w == null ? 0 : w->Tick;
    }

    public static void Fade(byte world, SourceId source, sbyte gain, int ticks)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var target = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        var current = (sbyte)s->Gain[i];
        JournalSchedule(w, s, i);
        if (ticks <= 0 || target == current)
        {
            s->Timed.Pointer[i] &= unchecked((byte)~Fading);
            if (target != current)
            {
                BeforeWrite(w, s, i);
                s->Gain[i] = (byte)target;
                MarkChanged(w, s, i);
            }
        }
        else
        {
            s->FadeFrom.Pointer[i] = current;
            s->FadeTo.Pointer[i] = target;
            s->FadeStart.Pointer[i] = w->Tick;
            s->FadeTicks.Pointer[i] = ticks;
            s->Timed.Pointer[i] |= Fading;
        }

        Schedule(w, s, i);
    }

    public static void Expire(byte world, SourceId source, int ticks)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        JournalSchedule(w, s, i);
        if (ticks <= 0)
        {
            s->Timed.Pointer[i] &= unchecked((byte)~Expiring);
        }
        else
        {
            s->ExpireAt.Pointer[i] = w->Tick + ticks;
            s->Timed.Pointer[i] |= Expiring;
        }

        Schedule(w, s, i);
    }

    private static void JournalSchedule(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (w->Recording == 0) return;

        var n = w->ScheduleJournal.Length;
        w->ScheduleJournal.Resize(n + 1);
        w->ScheduleJournal.Pointer[n] = new ScheduleRecord
        {
            Slot = slot,
            FadeStart = s->FadeStart.Pointer[slot],
            FadeTicks = s->FadeTicks.Pointer[slot],
            ExpireAt = s->ExpireAt.Pointer[slot],
            Timed = s->Timed.Pointer[slot],
            FadeFrom = s->FadeFrom.Pointer[slot],
            FadeTo = s->FadeTo.Pointer[slot],
        };
    }

    private static void RestoreSchedules(WorldCtx* w, SourceColumns* s)
    {
        var records = w->ScheduleJournal.Pointer;
        for (var j = w->ScheduleJournal.Length - 1; j >= 0; j--)
        {
            var r = records + j;
            s->FadeStart.Pointer[r->Slot] = r->FadeStart;
            s->FadeTicks.Pointer[r->Slot] = r->FadeTicks;
            s->ExpireAt.Pointer[r->Slot] = r->ExpireAt;
            s->Timed.Pointer[r->Slot] = r->Timed;
            s->FadeFrom.Pointer[r->Slot] = r->FadeFrom;
            s->FadeTo.Pointer[r->Slot] = r->FadeTo;
        }

        w->ScheduleJournal.Resize(0);
    }

    private static void ShiftSchedules(SourceColumns* s, int delta)
    {
        for (var i = 0; i < s->Count; i++)
        {
            if (s->Alive[i] == 0 || s->Timed.Pointer[i] == 0) continue;
            s->FadeStart.Pointer[i] += delta;
            s->ExpireAt.Pointer[i] += delta;
        }
    }

    private static void Schedule(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (!NextDue(s, slot, w->Tick, out var next)) return;

        var at = s->TimedAt.Pointer[slot];
        if (at != 0)
        {
            w->TimedList.Pointer[at - 1].Next = next;
            return;
        }

        var n = w->TimedList.Length;
        w->TimedList.Resize(n + 1);
        w->TimedList.Pointer[n] = new TimedEntry { Slot = slot, Next = next };
        s->TimedAt.Pointer[slot] = n + 1;
    }

    private static void Unlist(WorldCtx* w, SourceColumns* s, int index)
    {
        var list = w->TimedList.Pointer;
        var last = w->TimedList.Length - 1;
        s->TimedAt.Pointer[list[index].Slot] = 0;
        if (index != last)
        {
            list[index] = list[last];
            s->TimedAt.Pointer[list[index].Slot] = index + 1;
        }

        w->TimedList.Resize(last);
    }

    private static void RebuildTimed(WorldCtx* w)
    {
        var s = &w->Sources;
        w->TimedList.Resize(0);
        for (var i = 0; i < s->Count; i++)
        {
            s->TimedAt.Pointer[i] = 0;
            if (s->Alive[i] == 0 || s->Timed.Pointer[i] == 0 || !NextDue(s, i, w->Tick, out var next)) continue;

            var n = w->TimedList.Length;
            w->TimedList.Resize(n + 1);
            w->TimedList.Pointer[n] = new TimedEntry { Slot = i, Next = next };
            s->TimedAt.Pointer[i] = n + 1;
        }
    }

    private static bool NextDue(SourceColumns* s, int slot, int now, out int due)
    {
        due = 0;
        var found = false;
        var timed = s->Timed.Pointer[slot];
        if ((timed & Fading) != 0 && NextStep(s, slot, now, out var step))
        {
            due = step;
            found = true;
        }

        if ((timed & Expiring) != 0)
        {
            var at = s->ExpireAt.Pointer[slot];
            if (at - now <= 0) at = now + 1;
            if (!found || at - due < 0) due = at;
            found = true;
        }

        return found;
    }

    private static bool NextStep(SourceColumns* s, int slot, int now, out int due)
    {
        due = 0;
        var ticks = s->FadeTicks.Pointer[slot];
        var elapsed = now - s->FadeStart.Pointer[slot];
        if (elapsed >= ticks) return false;

        var delta = Math.Abs(s->FadeTo.Pointer[slot] - s->FadeFrom.Pointer[slot]);
        var step = ((long)delta * Math.Max(elapsed, 0) + ticks / 2) / ticks;
        if (step >= delta) return false;

        var next = ((step + 1) * ticks - ticks / 2 + delta - 1) / delta;
        due = s->FadeStart.Pointer[slot] + (int)next;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte FadeGain(SourceColumns* s, int slot, int now)
    {
        var from = s->FadeFrom.Pointer[slot];
        var to = s->FadeTo.Pointer[slot];
        var ticks = s->FadeTicks.Pointer[slot];
        var elapsed = Math.Clamp(now - s->FadeStart.Pointer[slot], 0, ticks);
        var delta = to - from;
        var step = (int)(((long)Math.Abs(delta) * elapsed + ticks / 2) / ticks);
        return (sbyte)(from + (delta < 0 ? -step : step));
    }

    private static void SweepTimed(WorldCtx* w)
    {
        var s = &w->Sources;
        var now = w->Tick;
        w->TimedDue.Resize(0);
        var list = w->TimedList.Pointer;
        for (var i = 0; i < w->TimedList.Length; i++)
        {
            if (list[i].Next - now > 0) continue;

            var n = w->TimedDue.Length;
            w->TimedDue.Resize(n + 1);
            w->TimedDue.Pointer[n] = list[i].Slot;
        }

        var due = w->TimedDue.Pointer;
        var count = w->TimedDue.Length;
        SortSlots(due, count);
        for (var k = 0; k < count; k++)
        {
            var slot = due[k];
            var index = s->TimedAt.Pointer[slot] - 1;
            var timed = s->Timed.Pointer[slot];
            if (s->Alive[slot] == 0 || timed == 0)
            {
                Unlist(w, s, index);
                continue;
            }

            if ((timed & Expiring) != 0 && s->ExpireAt.Pointer[slot] - now <= 0)
            {
                BeforeWrite(w, s, slot);
                s->Alive[slot] = 0;
                if (s->Gen[slot] != uint.MaxValue) s->Free.Push(slot);
                MarkChanged(w, s, slot);
                Unlist(w, s, index);
                continue;
            }

            if ((timed & Fading) != 0)
            {
                var gain = FadeGain(s, slot, now);
                if (gain != (sbyte)s->Gain[slot])
                {
                    BeforeWrite(w, s, slot);
                    s->Gain[slot] = (byte)gain;
                    MarkChanged(w, s, slot);
                }
            }

            if (NextDue(s, slot, now, out var next)) w->TimedList.Pointer[index].Next = next;
            else Unlist(w, s, index);
        }
    }

    private static void SortSlots(int* slots, int count)
    {
#if NET
        new Span<int>(slots, count).Sort();
#else
        for (var i = 1; i < count; i++)
        {
            var value = slots[i];
            var j = i - 1;
            while (j >= 0 && slots[j] > value)
            {
                slots[j + 1] = slots[j];
                j--;
            }

            slots[j + 1] = value;
        }
#endif
    }
}
