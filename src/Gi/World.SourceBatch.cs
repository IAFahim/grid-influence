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
            var creationIndex = command.Target.Identifier;
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

            ReserveSourceBatchOperations(context, commands.Length);
            context->BatchLayerDeltas.ResizeUninitialized(SourceBatchLayerStride);
            context->BatchLayerDeltas.Span.Clear();
            ApplySourceBatchRange(context, 0, context->BatchSources.Length, 0);

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
            var identifier = command.Target.Identifier;
            var slot = identifier & SourceIndexMask;
            if (identifier < 0 || slot >= source->Count || source->Alive.Pointer[slot] == 0 ||
                (uint)identifier >> 24 != source->Gen.Pointer[slot])
            {
                results[index] = new SourceResult(-1, SourceResultStatus.InvalidSource);
                continue;
            }

            var oldX = source->X.Pointer[slot];
            var oldY = source->Y.Pointer[slot];
            var oldAngle = source->Angle.Pointer[slot];
            var oldScale = source->Scale.Pointer[slot];
            var angle = oldAngle;
            if (float.IsFinite(command.HeadingRadians)) angle = QuantizeSourceBatchAngle(command.HeadingRadians);
            var scale = oldScale;
            if (float.IsFinite(command.ScaleValue)) scale = QuantizeSourceBatchScale(command.ScaleValue);
            var finitePosition = float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY);
            var positionX = finitePosition ? command.PositionX : oldX;
            var positionY = finitePosition ? command.PositionY : oldY;
            var layer = source->Layer.Pointer[slot];
            var posChanged = (positionX != oldX || positionY != oldY) && PositionChanged(context, layer, oldX, oldY, positionX, positionY);
            var angChanged = angle != oldAngle && AngleChanged(context, layer, oldAngle, angle);
            var scaleChanged = scale != oldScale;
            if (command.Kind == SourceCommandKind.UpdateTransform)
            {
                var currentGain = (sbyte)source->Gain.Pointer[slot];
                if (posChanged) EnqueueMutation(context, source, slot, positionX, positionY, currentGain, oldAngle, oldScale, true);
                source->X.Pointer[slot] = positionX;
                source->Y.Pointer[slot] = positionY;
                if (angChanged) EnqueueMutation(context, source, slot, positionX, positionY, currentGain, angle, oldScale, true);
                source->Angle.Pointer[slot] = angle;
                if (scaleChanged) EnqueueMutation(context, source, slot, positionX, positionY, currentGain, angle, scale, true);
                source->Scale.Pointer[slot] = scale;
                results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
                continue;
            }

            var oldGain = source->Gain.Pointer[slot];
            var gain = (byte)Math.Clamp((int)command.Gain, -MaxGain, MaxGain);
            if ((source->Timed.Pointer[slot] & Fading) != 0)
            {
                JournalSchedule(context, source, slot);
                source->Timed.Pointer[slot] &= unchecked((byte)~Fading);
            }

            var signedOldGain = (sbyte)oldGain;
            if (posChanged) EnqueueMutation(context, source, slot, positionX, positionY, signedOldGain, oldAngle, oldScale, true);
            source->X.Pointer[slot] = positionX;
            source->Y.Pointer[slot] = positionY;
            if (angChanged) EnqueueMutation(context, source, slot, positionX, positionY, signedOldGain, angle, oldScale, true);
            source->Angle.Pointer[slot] = angle;
            if (scaleChanged) EnqueueMutation(context, source, slot, positionX, positionY, signedOldGain, angle, scale, true);
            source->Scale.Pointer[slot] = scale;
            if (gain != oldGain)
            {
                CountGain(context, layer, oldGain, gain);
                EnqueueMutation(context, source, slot, positionX, positionY, (sbyte)gain, angle, scale, true);
                source->Gain.Pointer[slot] = gain;
            }

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
            var identifier = commands[index].Target.Identifier;
            var slot = identifier & SourceIndexMask;
            var link = context->BatchCommands.Pointer + index;
            *link = new SourceBatchLink { Entry = -1, Next = -1 };
            if (identifier < 0 || slot >= source->Count || source->Alive.Pointer[slot] == 0 ||
                (uint)identifier >> 24 != source->Gen.Pointer[slot])
            {
                results[index] = new SourceResult(-1, SourceResultStatus.InvalidSource);
                continue;
            }

            var pending = context->Pending.Pointer[slot];
            if (pending < 0)
            {
                unique = false;
                break;
            }

            context->BatchSources.Pointer[entries] = new SourceBatchEntry
            {
                Slot = slot, FirstCommand = index, LastCommand = index,
                FirstDepositCommand = -1, FirstScheduleCommand = -1, OperationIndex = -1,
                OriginalPending = pending,
                PlannedGeneration = source->Gen.Pointer[slot], PlannedAlive = 1
            };
            link->Entry = entries;
            context->Pending.Pointer[slot] = -(entries + 1);
            entries++;
            results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
        }

        for (var index = 0; index < entries; index++)
        {
            var entry = context->BatchSources.Pointer + index;
            context->Pending.Pointer[entry->Slot] = entry->OriginalPending;
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
                    results[index] = new SourceResult(-1, SourceResultStatus.InvalidLayer);
                    continue;
                }

                if (!StampCatalog.Valid(command.Stamp))
                {
                    results[index] = new SourceResult(-1, SourceResultStatus.InvalidStamp);
                    continue;
                }

                if (!float.IsFinite(command.PositionX) || !float.IsFinite(command.PositionY))
                {
                    results[index] = new SourceResult(-1, SourceResultStatus.InvalidSource);
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
                        results[index] = new SourceResult(-1, SourceResultStatus.SourceLimitReached);
                        continue;
                    }

                    slot = sourceCount++;
                    entryIndex = GetSourceBatchEntry(context, slot, ref entryCount);
                }

                var entry = context->BatchSources.Pointer + entryIndex;
                entry->PlannedGeneration = (byte)((entry->PlannedGeneration + 1) & 127);
                entry->PlannedAlive = 1;
                results[index] = new SourceResult((entry->PlannedGeneration << 24) | slot, SourceResultStatus.Applied);
            }
            else
            {
                var sourceId = command.Target.Pending
                    ? results[command.Target.Identifier].SourceId
                    : command.Target.Identifier;
                slot = sourceId & SourceIndexMask;
                if (sourceId < 0 || slot >= sourceCount)
                {
                    results[index] = new SourceResult(-1, SourceResultStatus.InvalidSource);
                    continue;
                }

                entryIndex = FindSourceBatchEntry(context, slot);
                var generation = entryIndex >= 0
                    ? context->BatchSources.Pointer[entryIndex].PlannedGeneration
                    : context->Sources.Gen.Pointer[slot];
                var alive = entryIndex >= 0
                    ? context->BatchSources.Pointer[entryIndex].PlannedAlive
                    : context->Sources.Alive.Pointer[slot];
                if (alive == 0 || (uint)sourceId >> 24 != generation)
                {
                    results[index] = new SourceResult(-1, SourceResultStatus.InvalidSource);
                    continue;
                }

                if (entryIndex < 0) entryIndex = GetSourceBatchEntry(context, slot, ref entryCount);
                if (command.Kind == SourceCommandKind.Remove)
                {
                    var entry = context->BatchSources.Pointer + entryIndex;
                    entry->PlannedAlive = 0;
                    ring.Push(slot);
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
            OperationIndex = -1,
            OriginalPending = slot < source->X.Length ? context->Pending.Pointer[slot] : 0,
            PlannedGeneration = slot < source->X.Length ? source->Gen.Pointer[slot] : (byte)0,
            PlannedAlive = slot < source->Count ? source->Alive.Pointer[slot] : (byte)0
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
            var state = slot < source->Count ? ReadSourceBatchState(source, slot) : new SourceBatchState
            {
                Generation = source->Gen.Pointer[slot],
                Scale = UnitScale
            };
            entry->InitialAlive = state.Alive;
            entry->InitialLayer = state.Layer;
            entry->InitialGain = state.Gain;
            entry->ScheduleBefore = new ScheduleRecord
            {
                Slot = slot, FadeStart = state.FadeStart, FadeTicks = state.FadeTicks,
                ExpireAt = state.ExpireAt, Timed = state.Timed, FadeFrom = state.FadeFrom, FadeTo = state.FadeTo
            };
            var operation = entry->OriginalPending != 0
                ? context->Ops.Pointer[entry->OriginalPending - 1]
                : new DepositOp
                {
                    Slot = slot, FromX = state.PositionX, FromY = state.PositionY,
                    FromStamp = state.Stamp, FromLayer = state.Layer,
                    FromGain = state.Alive != 0 ? (sbyte)state.Gain : (sbyte)0,
                    FromAngle = state.Angle, FromScale = state.Scale,
                    ToX = state.PositionX, ToY = state.PositionY,
                    ToStamp = state.Stamp, ToLayer = state.Layer,
                    ToGain = state.Alive != 0 ? (sbyte)state.Gain : (sbyte)0,
                    ToAngle = state.Angle, ToScale = state.Scale,
                    AliveFrom = state.Alive, AliveTo = state.Alive, FromGen = state.Generation
                };

            for (var commandIndex = entry->FirstCommand; commandIndex >= 0;
                 commandIndex = context->BatchCommands.Pointer[commandIndex].Next)
                FoldSourceCommand(context, entry, ref state, ref operation, in commands[commandIndex], commandIndex);

            entry->State = state;
            entry->Operation = operation;
        }
    }

    private static SourceBatchState ReadSourceBatchState(SourceColumns* source, int slot) => new()
    {
        PositionX = source->X.Pointer[slot], PositionY = source->Y.Pointer[slot],
        Angle = source->Angle.Pointer[slot], Scale = source->Scale.Pointer[slot],
        Stamp = source->Stamp.Pointer[slot], Layer = source->Layer.Pointer[slot],
        Gain = source->Gain.Pointer[slot], Alive = source->Alive.Pointer[slot],
        Generation = source->Gen.Pointer[slot], Timed = source->Timed.Pointer[slot],
        FadeFrom = source->FadeFrom.Pointer[slot], FadeTo = source->FadeTo.Pointer[slot],
        FadeStart = source->FadeStart.Pointer[slot], FadeTicks = source->FadeTicks.Pointer[slot],
        ExpireAt = source->ExpireAt.Pointer[slot], Due = source->Due.Pointer[slot]
    };

    private static void FoldSourceCommand(
        WorldCtx* context, SourceBatchEntry* entry, ref SourceBatchState state, ref DepositOp operation,
        in SourceCommand command, int index)
    {
        switch (command.Kind)
        {
            case SourceCommandKind.Place:
                state.PositionX = command.PositionX;
                state.PositionY = command.PositionY;
                state.Layer = command.Layer;
                state.Stamp = command.Stamp;
                state.Gain = (byte)Math.Clamp((int)command.Gain, -MaxGain, MaxGain);
                state.Angle = 0;
                state.Scale = UnitScale;
                state.Timed = 0;
                state.Alive = 1;
                state.Generation = (byte)((state.Generation + 1) & 127);
                MarkSourceBatchSchedule(entry, index);
                if (entry->OriginalPending == 0 && entry->FirstDepositCommand < 0)
                {
                    operation.FromX = command.PositionX;
                    operation.FromY = command.PositionY;
                    operation.FromStamp = command.Stamp;
                    operation.FromLayer = command.Layer;
                    operation.FromAngle = 0;
                    operation.FromScale = UnitScale;
                    operation.FromGen = state.Generation;
                }

                if (entry->FirstDepositCommand < 0) entry->FirstDepositCommand = index;
                CommitSourceBatchDeposit(ref operation, in state);
                FoldSourceTurn(context, entry, ref state, ref operation, command.HeadingRadians, index);
                FoldSourceScale(entry, ref state, ref operation, command.ScaleValue, index);
                break;
            case SourceCommandKind.Update:
                FoldSourceMove(context, entry, ref state, ref operation, command.PositionX, command.PositionY, index);
                FoldSourceTurn(context, entry, ref state, ref operation, command.HeadingRadians, index);
                FoldSourceScale(entry, ref state, ref operation, command.ScaleValue, index);
                FoldSourceGain(entry, ref state, ref operation, command.Gain, index);
                break;
            case SourceCommandKind.UpdateTransform:
                FoldSourceMove(context, entry, ref state, ref operation, command.PositionX, command.PositionY, index);
                FoldSourceTurn(context, entry, ref state, ref operation, command.HeadingRadians, index);
                FoldSourceScale(entry, ref state, ref operation, command.ScaleValue, index);
                break;
            case SourceCommandKind.Move:
                FoldSourceMove(context, entry, ref state, ref operation, command.PositionX, command.PositionY, index);
                break;
            case SourceCommandKind.Turn:
                FoldSourceTurn(context, entry, ref state, ref operation, command.HeadingRadians, index);
                break;
            case SourceCommandKind.Scale:
                FoldSourceScale(entry, ref state, ref operation, command.ScaleValue, index);
                break;
            case SourceCommandKind.SetGain:
                FoldSourceGain(entry, ref state, ref operation, command.Gain, index);
                break;
            case SourceCommandKind.Remove:
                if (state.Timed != 0) MarkSourceBatchSchedule(entry, index);
                BeginSourceBatchDeposit(entry, ref operation, in state, index);
                state.Timed = 0;
                state.Alive = 0;
                CommitSourceBatchDeposit(ref operation, in state);
                break;
            case SourceCommandKind.Fade:
                MarkSourceBatchSchedule(entry, index);
                var target = (byte)Math.Clamp((int)command.Gain, -MaxGain, MaxGain);
                if (command.Ticks <= 0 || target == state.Gain)
                {
                    state.Timed &= unchecked((byte)~Fading);
                    if (target != state.Gain)
                    {
                        BeginSourceBatchDeposit(entry, ref operation, in state, index);
                        state.Gain = target;
                        CommitSourceBatchDeposit(ref operation, in state);
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

    private static void BeginSourceBatchDeposit(
        SourceBatchEntry* entry, ref DepositOp operation, in SourceBatchState state, int index)
    {
        if (entry->FirstDepositCommand >= 0) return;
        if (entry->OriginalPending == 0)
        {
            operation.FromX = state.PositionX;
            operation.FromY = state.PositionY;
            operation.FromStamp = state.Stamp;
            operation.FromLayer = state.Layer;
            operation.FromGain = state.Alive != 0 ? (sbyte)state.Gain : (sbyte)0;
            operation.FromAngle = state.Angle;
            operation.FromScale = state.Scale;
            operation.AliveFrom = state.Alive;
            operation.FromGen = state.Generation;
        }

        entry->FirstDepositCommand = index;
    }

    private static void CommitSourceBatchDeposit(ref DepositOp operation, in SourceBatchState state)
    {
        operation.ToX = state.PositionX;
        operation.ToY = state.PositionY;
        operation.ToStamp = state.Stamp;
        operation.ToLayer = state.Layer;
        operation.ToGain = state.Alive != 0 ? (sbyte)state.Gain : (sbyte)0;
        operation.ToAngle = state.Angle;
        operation.ToScale = state.Scale;
        operation.AliveTo = state.Alive;
    }

    private static void MarkSourceBatchSchedule(SourceBatchEntry* entry, int index)
    {
        if (entry->FirstScheduleCommand < 0) entry->FirstScheduleCommand = index;
    }

    private static void FoldSourceMove(
        WorldCtx* context, SourceBatchEntry* entry, ref SourceBatchState state, ref DepositOp operation,
        float positionX, float positionY, int index)
    {
        if (!float.IsFinite(positionX) || !float.IsFinite(positionY)) return;
        if (state.PositionX == positionX && state.PositionY == positionY) return;
        var changed = PositionChanged(context, state.Layer, state.PositionX, state.PositionY, positionX, positionY);
        if (changed) BeginSourceBatchDeposit(entry, ref operation, in state, index);
        state.PositionX = positionX;
        state.PositionY = positionY;
        if (changed) CommitSourceBatchDeposit(ref operation, in state);
    }

    private static void FoldSourceTurn(
        WorldCtx* context, SourceBatchEntry* entry, ref SourceBatchState state, ref DepositOp operation,
        float headingRadians, int index)
    {
        if (!float.IsFinite(headingRadians)) return;
        var angle = QuantizeSourceBatchAngle(headingRadians);
        if (angle == state.Angle) return;
        var changed = AngleChanged(context, state.Layer, state.Angle, angle);
        if (changed) BeginSourceBatchDeposit(entry, ref operation, in state, index);
        state.Angle = angle;
        if (changed) CommitSourceBatchDeposit(ref operation, in state);
    }

    private static void FoldSourceScale(
        SourceBatchEntry* entry, ref SourceBatchState state, ref DepositOp operation, float scale, int index)
    {
        if (!float.IsFinite(scale)) return;
        var next = QuantizeSourceBatchScale(scale);
        if (next == state.Scale) return;
        BeginSourceBatchDeposit(entry, ref operation, in state, index);
        state.Scale = next;
        CommitSourceBatchDeposit(ref operation, in state);
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
        SourceBatchEntry* entry, ref SourceBatchState state, ref DepositOp operation, sbyte gain, int index)
    {
        if ((state.Timed & Fading) != 0)
        {
            MarkSourceBatchSchedule(entry, index);
            state.Timed &= unchecked((byte)~Fading);
        }

        var next = (byte)Math.Clamp((int)gain, -MaxGain, MaxGain);
        if (next == state.Gain) return;
        BeginSourceBatchDeposit(entry, ref operation, in state, index);
        state.Gain = next;
        CommitSourceBatchDeposit(ref operation, in state);
    }

    private static void ReserveSourceBatchOperations(WorldCtx* context, int commandCount)
    {
        var operationCount = context->Ops.Length;
        var scheduleCount = 0;
        for (var index = 0; index < commandCount; index++)
        {
            var entryIndex = context->BatchCommands.Pointer[index].Entry;
            if (entryIndex < 0) continue;
            var entry = context->BatchSources.Pointer + entryIndex;
            if (entry->FirstDepositCommand == index)
                entry->OperationIndex = entry->OriginalPending != 0 ? entry->OriginalPending - 1 : operationCount++;
            if (entry->FirstScheduleCommand == index) scheduleCount++;
        }

        context->Ops.ResizeUninitialized(operationCount);
        if (context->Recording != 0) context->ScheduleJournal.Ensure(context->ScheduleJournal.Length + scheduleCount);
        context->Timers.Ensure(context->Timers.Length + scheduleCount);
    }

    internal static void ApplySourceBatchRange(WorldCtx* context, int first, int last, int partition)
    {
        var source = &context->Sources;
        var deltas = context->BatchLayerDeltas.Pointer + partition * SourceBatchLayerStride;
        for (var index = first; index < last; index++)
        {
            var entry = context->BatchSources.Pointer + index;
            ref readonly var state = ref entry->State;
            var slot = entry->Slot;
            var previousCounted = entry->InitialAlive != 0 && entry->InitialGain != 0;
            var nextCounted = state.Alive != 0 && state.Gain != 0;
            if (previousCounted != nextCounted || entry->InitialLayer != state.Layer)
            {
                if (previousCounted) deltas[entry->InitialLayer]--;
                if (nextCounted) deltas[state.Layer]++;
            }

            source->X.Pointer[slot] = state.PositionX;
            source->Y.Pointer[slot] = state.PositionY;
            source->Angle.Pointer[slot] = state.Angle;
            source->Scale.Pointer[slot] = state.Scale;
            source->Stamp.Pointer[slot] = state.Stamp;
            source->Layer.Pointer[slot] = state.Layer;
            source->Gain.Pointer[slot] = state.Gain;
            source->Alive.Pointer[slot] = state.Alive;
            source->Gen.Pointer[slot] = state.Generation;
            source->Timed.Pointer[slot] = state.Timed;
            source->FadeFrom.Pointer[slot] = state.FadeFrom;
            source->FadeTo.Pointer[slot] = state.FadeTo;
            source->FadeStart.Pointer[slot] = state.FadeStart;
            source->FadeTicks.Pointer[slot] = state.FadeTicks;
            source->ExpireAt.Pointer[slot] = state.ExpireAt;
            source->Due.Pointer[slot] = state.Due;
            if (entry->FirstDepositCommand < 0) continue;
            context->Ops.Pointer[entry->OperationIndex] = entry->Operation;
            context->Pending.Pointer[slot] = entry->OperationIndex + 1;
        }
    }

    private static void FinishSourceBatch(WorldCtx* context, int commandCount, int partitions)
    {
        for (var partition = 0; partition < partitions; partition++)
        {
            var deltas = context->BatchLayerDeltas.Pointer + partition * SourceBatchLayerStride;
            for (var layer = 0; layer < context->LayerCount; layer++) context->LayerLive[layer] += deltas[layer];
        }

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
