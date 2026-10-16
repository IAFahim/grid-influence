using System.Runtime.InteropServices;

namespace Gi;

public static unsafe partial class World
{
    internal const int SourceBatchLayerStride = MaxLayers + 16;

    public static BatchStatus ApplyBatch(byte world, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var context = GetContext(world);
        return context == null ? BatchStatus.InvalidWorld : ApplyBatchCommands(context, commands, results);
    }

    internal static BatchStatus ApplyBatchCommands(
        WorldCtx* context, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        if (commands.Length > MaxSourceSlots) return BatchStatus.TooManyCommands;
        if (results.Length < commands.Length) return BatchStatus.InsufficientResults;
        if (MemoryMarshal.AsBytes(commands).Overlaps(MemoryMarshal.AsBytes(results.Slice(0, commands.Length))))
            return BatchStatus.OverlappingBuffers;

        var creationCount = 0;
        var onlyUpdates = true;
        for (var index = 0; index < commands.Length; index++)
        {
            ref readonly var command = ref commands[index];
            if (command.Kind < SourceCommandKind.Place || command.Kind > SourceCommandKind.Expire)
                return BatchStatus.InvalidCommand;
            if (command.Kind == SourceCommandKind.Place)
            {
                onlyUpdates = false;
                creationCount++;
                continue;
            }

            onlyUpdates &= (command.Kind == SourceCommandKind.Update ||
                            command.Kind == SourceCommandKind.UpdateTransform) && !command.Target.Pending;
            if (!command.Target.Pending) continue;
            var creationIndex = command.Target.Command;
            if ((uint)creationIndex >= (uint)index || commands[creationIndex].Kind != SourceCommandKind.Place)
                return BatchStatus.InvalidReference;
        }

        if (commands.Length == 0) return BatchStatus.Applied;
        if (onlyUpdates && commands.Length < LargeBatchThreshold)
        {
            ApplySmallSourceUpdates(context, commands, results);
            return BatchStatus.Applied;
        }

        fixed (SourceCommand* input = commands)
        fixed (SourceResult* output = results)
        {
            var sourceCount = context->Sources.Count;
            context->Sources.Free.Reserve(Math.Min(MaxSourceSlots, sourceCount + creationCount));
            var ring = context->Sources.Free;
            if (!onlyUpdates || !TryPlanSourceUpdates(context, input, output, commands.Length))
                PlanSourceBatch(context, input, output, commands.Length, creationCount, ref sourceCount, ref ring);
            context->Sources.Free = ring;
            if (context->BatchSources.Length == 0) return BatchStatus.Applied;
            EnsureSourceCapacity(&context->Sources, context, sourceCount);

            PrepareSourceBatchRange(context, input, 0, context->BatchSources.Length);

            ReserveSourceBatch(context, commands.Length);
            ApplySourceBatchRange(context, commands.Length, 0, context->BatchSources.Length);

            context->Sources.Count = sourceCount;
            FinishSourceBatch(context, commands.Length, 1);
        }

        return BatchStatus.Applied;
    }

    private static void ApplySmallSourceUpdates(
        WorldCtx* context, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var source = &context->Sources;
        for (var index = 0; index < commands.Length; index++)
        {
            ref readonly var command = ref commands[index];
            var identifier = command.Target.Source;
            var slot = (int)identifier.Slot;
            if ((uint)slot >= (uint)source->Count || source->Alive[slot] == 0 ||
                identifier.Generation != source->Gen[slot])
            {
                results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
                continue;
            }

            var oldX = source->X[slot];
            var oldY = source->Y[slot];
            var oldAngle = source->Angle[slot];
            var oldScale = source->Scale[slot];
            var angle = oldAngle;
            if (float.IsFinite(command.HeadingRadians)) angle = QuantizeSourceBatchAngle(command.HeadingRadians);
            var scale = oldScale;
            if (float.IsFinite(command.ScaleValue)) scale = QuantizeSourceBatchScale(command.ScaleValue);
            var finitePosition = float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY);
            var positionX = finitePosition ? command.PositionX : oldX;
            var positionY = finitePosition ? command.PositionY : oldY;
            var layer = source->Layer[slot];
            var posChanged = (positionX != oldX || positionY != oldY) && PositionChanged(context, layer, oldX, oldY, positionX, positionY);
            var angChanged = angle != oldAngle && AngleChanged(context, layer, oldAngle, angle);
            var scaleChanged = scale != oldScale;
            var journal = new SourceBatchState
            {
                PositionX = posChanged ? oldX : positionX,
                PositionY = posChanged ? oldY : positionY,
                Angle = posChanged || angChanged ? oldAngle : angle,
                Scale = posChanged || angChanged || scaleChanged ? oldScale : scale,
                Stamp = source->Stamp[slot],
                Layer = source->Layer[slot],
                Gain = source->Gain[slot],
                Alive = source->Alive[slot],
                Generation = source->Gen[slot]
            };
            if (command.Kind == SourceCommandKind.UpdateTransform)
            {
                var changed = posChanged || angChanged || scaleChanged;
                if (changed) BeforeWriteState(context, source, slot, in journal);
                source->X[slot] = positionX;
                source->Y[slot] = positionY;
                source->Angle[slot] = angle;
                source->Scale[slot] = scale;
                if (changed) MarkChanged(context, source, slot);
                results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
                continue;
            }

            var oldGain = source->Gain[slot];
            var gain = (byte)Math.Clamp((int)command.Gain, -MaxGain, MaxGain);
            if ((source->Timed.Pointer[slot] & Fading) != 0)
            {
                JournalSchedule(context, source, slot);
                source->Timed.Pointer[slot] &= unchecked((byte)~Fading);
            }

            var changedAny = posChanged || angChanged || scaleChanged || gain != oldGain;
            if (changedAny) BeforeWriteState(context, source, slot, in journal);
            source->X[slot] = positionX;
            source->Y[slot] = positionY;
            source->Angle[slot] = angle;
            source->Scale[slot] = scale;
            source->Gain[slot] = gain;
            if (changedAny) MarkChanged(context, source, slot);
            results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
        }
    }

    private static bool TryPlanSourceUpdates(WorldCtx* context, SourceCommand* commands, SourceResult* results, int count)
    {
        var source = &context->Sources;
        context->BatchSources.ResizeUninitialized(Math.Min(count, source->Count));
        context->BatchCommands.ResizeUninitialized(count);
        var entries = 0;
        var unique = true;
        for (var index = 0; index < count; index++)
        {
            var identifier = commands[index].Target.Source;
            var slot = (int)identifier.Slot;
            var link = context->BatchCommands.Pointer + index;
            *link = new SourceBatchLink { Entry = -1, Next = -1 };
            if ((uint)slot >= (uint)source->Count || source->Alive[slot] == 0 ||
                identifier.Generation != source->Gen[slot])
            {
                results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
                continue;
            }

            if (source->BatchTag.Pointer[slot] != 0)
            {
                unique = false;
                break;
            }

            context->BatchSources.Pointer[entries] = new SourceBatchEntry
            {
                Slot = slot, FirstCommand = index, LastCommand = index,
                FirstDepositCommand = -1, FirstScheduleCommand = -1,
                PlannedGeneration = source->Gen[slot], PlannedAlive = 1
            };
            link->Entry = entries;
            source->BatchTag.Pointer[slot] = entries + 1;
            entries++;
            results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
        }

        for (var index = 0; index < entries; index++)
        {
            var entry = context->BatchSources.Pointer + index;
            source->BatchTag.Pointer[entry->Slot] = 0;
        }

        context->BatchSources.Resize(entries);
        return unique;
    }

    private static void PlanSourceBatch(
        WorldCtx* context, SourceCommand* commands, SourceResult* results, int count, int creationCount,
        ref int sourceCount, ref FreeRing ring)
    {
        var entryCapacity = Math.Min(count, context->Sources.Count + creationCount);
        context->BatchSources.ResizeUninitialized(entryCapacity);
        context->BatchCommands.ResizeUninitialized(count);
        var lookupSize = 16;
        while (lookupSize < entryCapacity * 2) lookupSize <<= 1;
        context->BatchLookup.ResizeUninitialized(lookupSize);
        context->BatchLookup.Span.Clear();
        var entryCount = 0;

        for (var index = 0; index < count; index++)
        {
            ref readonly var command = ref commands[index];
            var link = context->BatchCommands.Pointer + index;
            *link = new SourceBatchLink { Entry = -1, Next = -1 };
            int slot;
            int entryIndex;

            if (command.Kind == SourceCommandKind.Place)
            {
                if (command.Layer >= context->LayerCount || IsDerived(context, command.Layer))
                {
                    results[index] = new SourceResult(default, SourceResultStatus.InvalidLayer);
                    continue;
                }

                if (!StampCatalog.Valid(command.Stamp))
                {
                    results[index] = new SourceResult(default, SourceResultStatus.InvalidStamp);
                    continue;
                }

                if (!float.IsFinite(command.PositionX) || !float.IsFinite(command.PositionY))
                {
                    results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
                    continue;
                }

                if (ring.TryPop(out slot))
                {
                    entryIndex = GetSourceBatchEntry(context, slot, ref entryCount);
                }
                else
                {
                    if (sourceCount >= MaxSourceSlots)
                    {
                        results[index] = new SourceResult(default, SourceResultStatus.SourceLimitReached);
                        continue;
                    }

                    slot = sourceCount++;
                    entryIndex = GetSourceBatchEntry(context, slot, ref entryCount);
                }

                var entry = context->BatchSources.Pointer + entryIndex;
                entry->PlannedGeneration++;
                entry->PlannedAlive = 1;
                results[index] = new SourceResult(new SourceId((uint)slot, entry->PlannedGeneration), SourceResultStatus.Applied);
            }
            else
            {
                var sourceId = command.Target.Pending
                    ? results[command.Target.Command].SourceId
                    : command.Target.Source;
                slot = (int)sourceId.Slot;
                if (sourceId.Generation == 0 || (uint)slot >= (uint)sourceCount)
                {
                    results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
                    continue;
                }

                entryIndex = FindSourceBatchEntry(context, slot);
                var generation = entryIndex >= 0
                    ? context->BatchSources.Pointer[entryIndex].PlannedGeneration
                    : context->Sources.Gen[slot];
                var alive = entryIndex >= 0
                    ? context->BatchSources.Pointer[entryIndex].PlannedAlive
                    : context->Sources.Alive[slot];
                if (alive == 0 || sourceId.Generation != generation)
                {
                    results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
                    continue;
                }

                if (entryIndex < 0) entryIndex = GetSourceBatchEntry(context, slot, ref entryCount);
                if (command.Kind == SourceCommandKind.Remove)
                {
                    var entry = context->BatchSources.Pointer + entryIndex;
                    entry->PlannedAlive = 0;
                    if (entry->PlannedGeneration != uint.MaxValue) ring.Push(slot);
                }

                results[index] = new SourceResult(sourceId, SourceResultStatus.Applied);
            }

            var assignment = context->BatchSources.Pointer + entryIndex;
            if (assignment->FirstCommand < 0) assignment->FirstCommand = index;
            else context->BatchCommands.Pointer[assignment->LastCommand].Next = index;
            assignment->LastCommand = index;
            link->Entry = entryIndex;
        }

        context->BatchSources.Resize(entryCount);
    }

    private static int SourceBatchLookupIndex(WorldCtx* context, int slot)
    {
        var mask = context->BatchLookup.Length - 1;
        var index = (int)((uint)slot * 2654435761u) & mask;
        while (context->BatchLookup.Pointer[index] != 0 &&
               context->BatchSources.Pointer[context->BatchLookup.Pointer[index] - 1].Slot != slot)
            index = (index + 1) & mask;
        return index;
    }

    private static int FindSourceBatchEntry(WorldCtx* context, int slot)
        => context->BatchLookup.Pointer[SourceBatchLookupIndex(context, slot)] - 1;

    private static int GetSourceBatchEntry(WorldCtx* context, int slot, ref int count)
    {
        var lookupIndex = SourceBatchLookupIndex(context, slot);
        var existing = context->BatchLookup.Pointer[lookupIndex] - 1;
        if (existing >= 0) return existing;

        var source = &context->Sources;
        var index = count++;
        context->BatchSources.Pointer[index] = new SourceBatchEntry
        {
            Slot = slot,
            FirstCommand = -1,
            LastCommand = -1,
            FirstDepositCommand = -1,
            FirstScheduleCommand = -1,
            PlannedGeneration = slot < source->Slots ? source->Gen[slot] : 0u,
            PlannedAlive = slot < source->Count ? source->Alive[slot] : (byte)0
        };
        context->BatchLookup.Pointer[lookupIndex] = index + 1;
        return index;
    }

    internal static void PrepareSourceBatchRange(WorldCtx* context, SourceCommand* commands, int first, int last)
    {
        var source = &context->Sources;
        for (var entryIndex = first; entryIndex < last; entryIndex++)
        {
            var entry = context->BatchSources.Pointer + entryIndex;
            var slot = entry->Slot;
            var state = ReadSourceBatchState(source, slot);
            entry->ScheduleBefore = new ScheduleRecord
            {
                Slot = slot, FadeStart = state.FadeStart, FadeTicks = state.FadeTicks,
                ExpireAt = state.ExpireAt, Timed = state.Timed, FadeFrom = state.FadeFrom, FadeTo = state.FadeTo
            };

            for (var commandIndex = entry->FirstCommand; commandIndex >= 0;
                 commandIndex = context->BatchCommands.Pointer[commandIndex].Next)
                FoldSourceCommand(context, entry, ref state, in commands[commandIndex], commandIndex);

            entry->State = state;
        }
    }

    private static SourceBatchState ReadSourceBatchState(SourceColumns* source, int slot) => new()
    {
        PositionX = source->X[slot], PositionY = source->Y[slot],
        Angle = source->Angle[slot], Scale = source->Scale[slot],
        Stamp = source->Stamp[slot], Layer = source->Layer[slot],
        Gain = source->Gain[slot], Alive = source->Alive[slot],
        Generation = source->Gen[slot], Timed = source->Timed.Pointer[slot],
        FadeFrom = source->FadeFrom.Pointer[slot], FadeTo = source->FadeTo.Pointer[slot],
        FadeStart = source->FadeStart.Pointer[slot], FadeTicks = source->FadeTicks.Pointer[slot],
        ExpireAt = source->ExpireAt.Pointer[slot], Due = source->Due.Pointer[slot]
    };

    private static void FoldSourceCommand(
        WorldCtx* context, SourceBatchEntry* entry, ref SourceBatchState state,
        in SourceCommand command, int index)
    {
        switch (command.Kind)
        {
            case SourceCommandKind.Place:
                MarkSourceBatchSchedule(entry, index);
                MarkSourceBatchDeposit(entry, index, in state);
                state.PositionX = command.PositionX;
                state.PositionY = command.PositionY;
                state.Layer = command.Layer;
                state.Stamp = command.Stamp;
                state.Gain = (byte)Math.Clamp((int)command.Gain, -MaxGain, MaxGain);
                state.Angle = 0;
                state.Scale = UnitScale;
                state.Timed = 0;
                state.Alive = 1;
                state.Generation += 1;
                FoldSourceTurn(context, entry, ref state, command.HeadingRadians, index);
                FoldSourceScale(entry, ref state, command.ScaleValue, index);
                break;
            case SourceCommandKind.Update:
                FoldSourceMove(context, entry, ref state, command.PositionX, command.PositionY, index);
                FoldSourceTurn(context, entry, ref state, command.HeadingRadians, index);
                FoldSourceScale(entry, ref state, command.ScaleValue, index);
                FoldSourceGain(entry, ref state, command.Gain, index);
                break;
            case SourceCommandKind.UpdateTransform:
                FoldSourceMove(context, entry, ref state, command.PositionX, command.PositionY, index);
                FoldSourceTurn(context, entry, ref state, command.HeadingRadians, index);
                FoldSourceScale(entry, ref state, command.ScaleValue, index);
                break;
            case SourceCommandKind.Move:
                FoldSourceMove(context, entry, ref state, command.PositionX, command.PositionY, index);
                break;
            case SourceCommandKind.Turn:
                FoldSourceTurn(context, entry, ref state, command.HeadingRadians, index);
                break;
            case SourceCommandKind.Scale:
                FoldSourceScale(entry, ref state, command.ScaleValue, index);
                break;
            case SourceCommandKind.SetGain:
                FoldSourceGain(entry, ref state, command.Gain, index);
                break;
            case SourceCommandKind.Remove:
                if (state.Timed != 0) MarkSourceBatchSchedule(entry, index);
                MarkSourceBatchDeposit(entry, index, in state);
                state.Timed = 0;
                state.Alive = 0;
                break;
            case SourceCommandKind.Fade:
                MarkSourceBatchSchedule(entry, index);
                var target = (byte)Math.Clamp((int)command.Gain, -MaxGain, MaxGain);
                if (command.Ticks <= 0 || target == state.Gain)
                {
                    state.Timed &= unchecked((byte)~Fading);
                    if (target != state.Gain)
                    {
                        MarkSourceBatchDeposit(entry, index, in state);
                        state.Gain = target;
                    }
                }
                else
                {
                    state.FadeFrom = (sbyte)state.Gain;
                    state.FadeTo = (sbyte)target;
                    state.FadeStart = context->Tick;
                    state.FadeTicks = command.Ticks;
                    state.Timed |= Fading;
                }

                break;
            case SourceCommandKind.Expire:
                MarkSourceBatchSchedule(entry, index);
                if (command.Ticks <= 0) state.Timed &= unchecked((byte)~Expiring);
                else
                {
                    state.ExpireAt = context->Tick + command.Ticks;
                    state.Timed |= Expiring;
                }

                break;
        }
    }

    private static void MarkSourceBatchDeposit(SourceBatchEntry* entry, int index, in SourceBatchState state)
    {
        if (entry->FirstDepositCommand < 0)
        {
            entry->FirstDepositCommand = index;
            entry->PendingState = state;
        }
    }

    private static void MarkSourceBatchSchedule(SourceBatchEntry* entry, int index)
    {
        if (entry->FirstScheduleCommand < 0) entry->FirstScheduleCommand = index;
    }

    private static void FoldSourceMove(
        WorldCtx* context, SourceBatchEntry* entry, ref SourceBatchState state,
        float positionX, float positionY, int index)
    {
        if (!float.IsFinite(positionX) || !float.IsFinite(positionY)) return;
        if (state.PositionX == positionX && state.PositionY == positionY) return;
        if (PositionChanged(context, state.Layer, state.PositionX, state.PositionY, positionX, positionY))
            MarkSourceBatchDeposit(entry, index, in state);
        state.PositionX = positionX;
        state.PositionY = positionY;
    }

    private static void FoldSourceTurn(
        WorldCtx* context, SourceBatchEntry* entry, ref SourceBatchState state,
        float headingRadians, int index)
    {
        if (!float.IsFinite(headingRadians)) return;
        var angle = QuantizeSourceBatchAngle(headingRadians);
        if (angle == state.Angle) return;
        if (AngleChanged(context, state.Layer, state.Angle, angle)) MarkSourceBatchDeposit(entry, index, in state);
        state.Angle = angle;
    }

    private static void FoldSourceScale(
        SourceBatchEntry* entry, ref SourceBatchState state, float scale, int index)
    {
        if (!float.IsFinite(scale)) return;
        var next = QuantizeSourceBatchScale(scale);
        if (next == state.Scale) return;
        MarkSourceBatchDeposit(entry, index, in state);
        state.Scale = next;
    }

    private static ushort QuantizeSourceBatchAngle(float headingRadians)
    {
        var turns = headingRadians * (1.0 / (2 * Math.PI));
        turns -= Math.Floor(turns);
        return (ushort)((int)(turns * 65536.0) & 0xFFFF);
    }

    private static ushort QuantizeSourceBatchScale(float scale)
    {
        var quantized = Math.Floor(scale * 256.0);
        return (ushort)(quantized < 1 ? 1 : quantized > ushort.MaxValue ? ushort.MaxValue : quantized);
    }

    private static void FoldSourceGain(
        SourceBatchEntry* entry, ref SourceBatchState state, sbyte gain, int index)
    {
        if ((state.Timed & Fading) != 0)
        {
            MarkSourceBatchSchedule(entry, index);
            state.Timed &= unchecked((byte)~Fading);
        }

        var next = (byte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        if (next == state.Gain) return;
        MarkSourceBatchDeposit(entry, index, in state);
        state.Gain = next;
    }

    private static void ReserveSourceBatch(WorldCtx* context, int commandCount)
    {
        var scheduleCount = 0;
        for (var index = 0; index < commandCount; index++)
        {
            var entryIndex = context->BatchCommands.Pointer[index].Entry;
            if (entryIndex < 0) continue;
            var entry = context->BatchSources.Pointer + entryIndex;
            if (entry->FirstScheduleCommand == index) scheduleCount++;
        }

        if (context->Recording != 0) context->ScheduleJournal.Ensure(context->ScheduleJournal.Length + scheduleCount);
        context->Timers.Ensure(context->Timers.Length + scheduleCount);
    }

    internal static void ApplySourceBatchRange(WorldCtx* context, int commandCount, int first, int last)
    {
        var source = &context->Sources;
        var entries = context->BatchSources.Pointer;
        for (var index = 0; index < commandCount; index++)
        {
            var entryIndex = context->BatchCommands.Pointer[index].Entry;
            if ((uint)(entryIndex - first) >= (uint)(last - first)) continue;
            var entry = entries + entryIndex;
            if (entry->FirstDepositCommand == index) BeforeWriteState(context, source, entry->Slot, in entry->PendingState);
        }

        for (var index = first; index < last; index++)
        {
            var entry = entries + index;
            ref readonly var state = ref entry->State;
            var slot = entry->Slot;
            source->X[slot] = state.PositionX;
            source->Y[slot] = state.PositionY;
            source->Angle[slot] = state.Angle;
            source->Scale[slot] = state.Scale;
            source->Stamp[slot] = state.Stamp;
            source->Layer[slot] = state.Layer;
            source->Gain[slot] = state.Gain;
            source->Alive[slot] = state.Alive;
            source->Gen[slot] = state.Generation;
            source->Timed.Pointer[slot] = state.Timed;
            source->FadeFrom.Pointer[slot] = state.FadeFrom;
            source->FadeTo.Pointer[slot] = state.FadeTo;
            source->FadeStart.Pointer[slot] = state.FadeStart;
            source->FadeTicks.Pointer[slot] = state.FadeTicks;
            source->ExpireAt.Pointer[slot] = state.ExpireAt;
            source->Due.Pointer[slot] = state.Due;
        }

        for (var index = 0; index < commandCount; index++)
        {
            var entryIndex = context->BatchCommands.Pointer[index].Entry;
            if ((uint)(entryIndex - first) >= (uint)(last - first)) continue;
            var entry = entries + entryIndex;
            if (entry->FirstDepositCommand == index) MarkChanged(context, source, entry->Slot);
        }
    }

    private static void FinishSourceBatch(WorldCtx* context, int commandCount, int partitions)
    {
        for (var index = 0; index < commandCount; index++)
        {
            var entryIndex = context->BatchCommands.Pointer[index].Entry;
            if (entryIndex < 0) continue;
            var entry = context->BatchSources.Pointer + entryIndex;
            if (entry->FirstScheduleCommand != index) continue;
            if (context->Recording != 0)
            {
                var journalIndex = context->ScheduleJournal.Length;
                context->ScheduleJournal.Resize(journalIndex + 1);
                context->ScheduleJournal.Pointer[journalIndex] = entry->ScheduleBefore;
            }

            if (entry->State.Alive != 0 && entry->State.Timed != 0)
                Schedule(context, &context->Sources, entry->Slot);
        }
    }
}
