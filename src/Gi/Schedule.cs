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

    public static void Fade(byte world, int source, int gain, int ticks)
    {
        if (!TrySource(world, source, out var w, out var s, out var i)) return;

        var target = (sbyte)Math.Clamp(gain, -MaxGain, MaxGain);
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

    private static void Schedule(WorldCtx* w, SourceColumns* s, int slot)
    {
        if (!NextDue(s, slot, w->Tick, out var due)) return;

        s->Due.Pointer[slot] = due;
        if (w->Timers.Length >= 2 * s->Count + 64) RebuildTimers(w);
        else Push(w, due, slot);
    }

    private static void RebuildTimers(WorldCtx* w)
    {
        var s = &w->Sources;
        w->Timers.Resize(0);
        for (var i = 0; i < s->Count; i++)
        {
            if (s->Alive.Pointer[i] == 0 || s->Timed.Pointer[i] == 0 || !NextDue(s, i, w->Tick, out var due)) continue;
            s->Due.Pointer[i] = due;
            Push(w, due, i);
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
        while (w->Timers.Length > 0 && w->Timers.Pointer[0].Due - now <= 0)
        {
            var timer = Pop(w);
            var slot = timer.Slot;
            if ((uint)slot >= (uint)s->Count || s->Alive.Pointer[slot] == 0 || s->Timed.Pointer[slot] == 0 ||
                s->Due.Pointer[slot] != timer.Due) continue;

            var timed = s->Timed.Pointer[slot];
            if ((timed & Expiring) != 0 && s->ExpireAt.Pointer[slot] - now <= 0)
            {
                EnqueueMutation(w, s, slot, s->X.Pointer[slot], s->Y.Pointer[slot], 0, s->Angle.Pointer[slot], s->Scale.Pointer[slot], false);
                s->Alive.Pointer[slot] = 0;
                s->Free.Pointer[slot] = w->FreeHead;
                w->FreeHead = slot;
                continue;
            }

            if ((timed & Fading) != 0)
            {
                var gain = FadeGain(s, slot, now);
                if (gain != (sbyte)s->Gain.Pointer[slot])
                {
                    EnqueueMutation(w, s, slot, s->X.Pointer[slot], s->Y.Pointer[slot], gain, s->Angle.Pointer[slot], s->Scale.Pointer[slot], true);
                    s->Gain.Pointer[slot] = (byte)gain;
                }
            }

            Schedule(w, s, slot);
        }
    }

    private static void Push(WorldCtx* w, int due, int slot)
    {
        var n = w->Timers.Length;
        w->Timers.Resize(n + 1);
        var heap = w->Timers.Pointer;
        var i = n;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (!Before(due, slot, heap[parent])) break;
            heap[i] = heap[parent];
            i = parent;
        }

        heap[i] = new Alarm { Due = due, Slot = slot };
    }

    private static Alarm Pop(WorldCtx* w)
    {
        var heap = w->Timers.Pointer;
        var top = heap[0];
        var n = w->Timers.Length - 1;
        var last = heap[n];
        w->Timers.Resize(n);
        var i = 0;
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= n) break;
            if (child + 1 < n && Before(heap[child + 1].Due, heap[child + 1].Slot, heap[child])) child++;
            if (!Before(heap[child].Due, heap[child].Slot, last)) break;
            heap[i] = heap[child];
            i = child;
        }

        if (n > 0) heap[i] = last;
        return top;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Before(int due, int slot, Alarm other)
    {
        var order = due - other.Due;
        return order < 0 || (order == 0 && slot < other.Slot);
    }
}
