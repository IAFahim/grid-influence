using System.Runtime.CompilerServices;

namespace Gi;

internal struct Alarm
{
    public int Due;
    public int Slot;
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

    public static void Fade(byte world, int source, sbyte gain, int ticks)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var target = (sbyte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        var current = (sbyte)s->Gain.Pointer[i];
        JournalSchedule(w, s, i);
        if (ticks <= 0 || target == current)
        {
            s->Timed.Pointer[i] &= unchecked((byte)~Fading);
            if (target != current)
            {
                EnqueueMutation(w, s, i, s->X.Pointer[i], s->Y.Pointer[i], target, s->Angle.Pointer[i], s->Scale.Pointer[i], true);
                s->Gain.Pointer[i] = (byte)target;
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

    public static void Expire(byte world, int source, int ticks)
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
            if (s->Alive.Pointer[i] == 0 || s->Timed.Pointer[i] == 0) continue;
            s->FadeStart.Pointer[i] += delta;
            s->ExpireAt.Pointer[i] += delta;
        }
    }

    private static void EnsureTimerPositions(WorldCtx* w, int count)
    {
        var old = w->TimerPositions.Length;
        if (count <= old) return;
        w->TimerPositions.Resize(count);
        new Span<int>(w->TimerPositions.Pointer + old, count - old).Clear();
    }

    private static void Schedule(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (s->Alive.Pointer[slot] == 0 || !NextDue(s, slot, w->Tick, out var due))
        {
            if (slot < w->TimerPositions.Length) RemoveTimer(w, slot);
            return;
        }

        EnsureTimerPositions(w, s->Count);
        s->Due.Pointer[slot] = due;
        var index = w->TimerPositions.Pointer[slot] - 1;
        if (index < 0)
        {
            index = w->Timers.Length;
            w->Timers.Resize(index + 1);
            w->TimerPositions.Pointer[slot] = index + 1;
        }
        w->Timers.Pointer[index] = new Alarm { Due = due, Slot = slot };
    }

    private static void RemoveTimer(WorldCtx* w, int slot)
    {
        var index = w->TimerPositions.Pointer[slot] - 1;
        if (index < 0) return;
        var last = w->Timers.Length - 1;
        var tail = w->Timers.Pointer[last];
        w->Timers.Resize(last);
        w->TimerPositions.Pointer[slot] = 0;
        if (index == last) return;
        w->Timers.Pointer[index] = tail;
        w->TimerPositions.Pointer[tail.Slot] = index + 1;
    }

    private static void RebuildTimers(WorldCtx* w)
    {
        var s = &w->Sources;
        w->Timers.Resize(0);
        EnsureTimerPositions(w, s->Count);
        w->TimerPositions.Span.Clear();
        for (var i = 0; i < s->Count; i++)
        {
            if (s->Alive.Pointer[i] == 0 || s->Timed.Pointer[i] == 0) continue;
            Schedule(w, s, i);
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

    private static void RunTimers(WorldCtx* w)
    {
        var s = &w->Sources;
        var now = w->Tick;
        w->TimerExpired.Resize(0);
        var index = 0;
        while (index < w->Timers.Length)
        {
            var timer = w->Timers.Pointer[index];
            var slot = timer.Slot;
            if ((uint)slot >= (uint)s->Count || s->Alive.Pointer[slot] == 0 || s->Timed.Pointer[slot] == 0)
            {
                RemoveTimer(w, slot);
                continue;
            }
            if (timer.Due - now <= 0)
            {
                var count = w->TimerExpired.Length;
                w->TimerExpired.Resize(count + 1);
                w->TimerExpired.Pointer[count] = slot;
            }
            index++;
        }

        var dueSlots = OrderedTimerSlots(w);
        for (var i = 0; i < w->TimerExpired.Length; i++)
        {
            var slot = dueSlots[i];
            var timed = s->Timed.Pointer[slot];
            if ((timed & Expiring) != 0 && s->ExpireAt.Pointer[slot] - now <= 0)
            {
                EnqueueMutation(w, s, slot, s->X.Pointer[slot], s->Y.Pointer[slot], 0, s->Angle.Pointer[slot], s->Scale.Pointer[slot], false);
                s->Alive.Pointer[slot] = 0;
                EnqueueFree(w, s, slot);
                RemoveTimer(w, slot);
                continue;
            }

            if ((timed & Fading) != 0)
            {
                var gain = FadeGain(s, slot, now);
                var previous = (sbyte)s->Gain.Pointer[slot];
                if (gain != previous)
                {
                    EnqueueMutation(w, s, slot, s->X.Pointer[slot], s->Y.Pointer[slot], gain, s->Angle.Pointer[slot], s->Scale.Pointer[slot], true);
                    s->Gain.Pointer[slot] = (byte)gain;
                }
            }

            var timerIndex = w->TimerPositions.Pointer[slot] - 1;
            if (NextDue(s, slot, now, out var due))
            {
                s->Due.Pointer[slot] = due;
                w->Timers.Pointer[timerIndex].Due = due;
            }
            else RemoveTimer(w, slot);
        }
    }

    private static int* OrderedTimerSlots(WorldCtx* w)
    {
        var count = w->TimerExpired.Length;
        var source = w->TimerExpired.Pointer;
        var ordered = true;
        for (var i = 1; i < count; i++)
            if (source[i - 1] > source[i]) { ordered = false; break; }
        if (ordered) return source;
        w->TimerSort.Resize(count);
        var destination = w->TimerSort.Pointer;
        var offsets = stackalloc int[256];
        for (var shift = 0; shift < 24; shift += 8)
        {
            new Span<int>(offsets, 256).Clear();
            for (var i = 0; i < count; i++) offsets[(source[i] >> shift) & 255]++;
            var first = 0;
            for (var bucket = 0; bucket < 256; bucket++)
            {
                var size = offsets[bucket];
                offsets[bucket] = first;
                first += size;
            }
            for (var i = 0; i < count; i++)
            {
                var slot = source[i];
                destination[offsets[(slot >> shift) & 255]++] = slot;
            }
            var swap = source;
            source = destination;
            destination = swap;
        }
        return source;
    }
}
