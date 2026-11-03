using BovineLabs.Core.Collections;
using BovineLabs.Core.SingletonCollection;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Profiling;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [BurstCompile]
    internal unsafe struct GiDrainPulsesJob : IJob
    {
        public Entity Frame;
        public bool ForceAll;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public UnsafeList<NativeThreadStream>.ReadOnly Streams;
        public NativeList<GiPulse> Pulses;

        public void Execute()
        {
            var context = GiReconcileContext.Of(Frames[Frame]);
            context.Force = ForceAll;
            Context.Value = context;
            for (var s = 0; s < Streams.Length; s++)
            {
                var reader = Streams.Ptr[s].AsReader();
                for (var lane = 0; lane < reader.ForEachCount; lane++)
                {
                    var remaining = reader.BeginForEachIndex(lane);
                    for (var i = 0; i < remaining; i++) Pulses.Add(reader.Read<GiPulse>());
                    reader.EndForEachIndex();
                }
            }

            Pulses.Sort(new GiPulseOrder());
        }
    }

    [BurstCompile]
    internal unsafe struct GiCountJob : IJobChunk
    {
        public GiReconcileView View;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        public uint LastVersion;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Counts[unfilteredChunkIndex] = View.Visit(chunk, Context.Value, LastVersion, 0, null, null);
        }
    }

    [BurstCompile]
    internal unsafe struct GiPrefixJob : IJob
    {
        [ReadOnly] public NativeArray<int> Counts;
        [ReadOnly] public NativeList<GiPulse> Pulses;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        public int Base;
        public NativeArray<int> Offsets;
        public NativeList<GiCommand> Commands;
        public NativeList<Entity> Owners;
        public NativeList<GiCommandResult> Results;
        public NativeList<GiWorld.SourceCommand> Scratch;
        public NativeList<GiWorld.SourceResult> BatchResults;

        public void Execute()
        {
            var total = 0;
            for (var c = 0; c < Counts.Length; c++)
            {
                Offsets[c] = Base + total;
                total += Counts[c];
            }

            var context = Context.Value;
            var entityTotal = Base + total;
            for (var p = 0; p < Pulses.Length; p++)
                if (context.Alive)
                    total += GiPulseExpand.Plan(Pulses[p], context.Tables, 0).Count;

            Commands.ResizeUninitialized(Base + total);
            Owners.ResizeUninitialized(Base + total);
            Results.ResizeUninitialized(Base + total);
            Scratch.ResizeUninitialized(Base + total);
            BatchResults.ResizeUninitialized(Base + total);

            var cursor = entityTotal;
            for (var p = 0; p < Pulses.Length && context.Alive; p++)
            {
                var plan = GiPulseExpand.Plan(Pulses[p], context.Tables, cursor);
                for (var k = 0; k < plan.Count; k++)
                {
                    Commands[cursor + k] = plan[k];
                    Owners[cursor + k] = Entity.Null;
                }

                cursor += plan.Count;
            }
        }
    }

    [BurstCompile]
    internal unsafe struct GiWriteJob : IJobChunk
    {
        public GiReconcileView View;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        public uint LastVersion;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<GiCommand> Commands;
        [NativeDisableParallelForRestriction] public NativeArray<Entity> Owners;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            View.Visit(chunk, Context.Value, LastVersion, Offsets[unfilteredChunkIndex],
                (GiCommand*)Commands.GetUnsafePtr(), (Entity*)Owners.GetUnsafePtr());
        }
    }

#if !ENABLE_CORECLR || GI_BURST_APPLY
    [BurstCompile]
#endif
    internal unsafe struct GiApplyJob : IJob
    {
        public Entity Frame;
        public ComponentLookup<GiFrame> Frames;
        [ReadOnly] public NativeList<GiCommand> Commands;
        public NativeList<GiCommandResult> Results;
        public NativeList<GiWorld.SourceCommand> Scratch;
        public NativeList<GiWorld.SourceResult> BatchResults;
        [NativeDisableUnsafePtrRestriction] internal Gi.GiRoot* Root;
        private static readonly ProfilerMarker Marker = new ProfilerMarker(ProfilerCategory.Scripts, GiMarkers.ReconcileName);

        public GiApplyJob()
        {
            fixed (Gi.GiRoot* root = &Gi.Runtime.Root) Root = root;
        }

        public void Execute()
        {
            var count = Commands.Length;
            if (count == 0) return;

            var frame = Frames[Frame];
            if (!frame.Alive)
            {
                for (var i = 0; i < count; i++) Results[i] = new GiCommandResult { Code = GiFaultCode.Unknown };
                return;
            }

            var token = frame.Token;
            Gi.GiRoot* root = Root;
            GuardBegin(frame.World, GiGuard.Reconcile, true);
            var scope = Marker.Auto();
            var status = Apply(ref token, frame.World, root,
                new System.ReadOnlySpan<GiCommand>(Commands.GetUnsafeReadOnlyPtr(), count),
                new System.Span<GiCommandResult>(Results.GetUnsafePtr(), count),
                new System.Span<GiWorld.SourceCommand>(Scratch.GetUnsafePtr(), count),
                new System.Span<GiWorld.SourceResult>(BatchResults.GetUnsafePtr(), count), GiApplyPass.BatchLimit);
            scope.Dispose();
            GuardEnd(frame.World, true);
            RecordCommands(frame.World,
                new System.ReadOnlySpan<GiCommand>(Commands.GetUnsafeReadOnlyPtr(), count),
                new System.ReadOnlySpan<GiCommandResult>(Results.GetUnsafePtr(), count));

            frame.Token = token;
            if (status == GiApplyStatus.Stale) frame.Faults.Raise(GiFaultKind.StaleToken, frame.Tick);
            if (status == GiApplyStatus.Adapter) frame.Faults.Raise(GiFaultKind.Adapter, frame.Tick);
            Frames[Frame] = frame;
        }

        private static GiApplyStatus Apply(ref GiWorld.Token token, byte world, Gi.GiRoot* root, System.ReadOnlySpan<GiCommand> commands,
            System.Span<GiCommandResult> results, System.Span<GiWorld.SourceCommand> scratch, System.Span<GiWorld.SourceResult> batchResults,
            int limit)
        {
            var status = GiApplyStatus.Applied;
            var start = 0;
            while (start < commands.Length)
            {
                var stop = GiApplyPass.SliceEnd(commands, start, limit);
                var sliced = ApplySlice(ref token, world, root, commands, results, scratch, batchResults, start, stop);
                if (sliced != GiApplyStatus.Applied) status = sliced;
                start = stop;
            }

            return status;
        }

        private static GiApplyStatus ApplySlice(ref GiWorld.Token token, byte world, Gi.GiRoot* root, System.ReadOnlySpan<GiCommand> commands,
            System.Span<GiCommandResult> results, System.Span<GiWorld.SourceCommand> scratch, System.Span<GiWorld.SourceResult> batchResults,
            int start, int stop)
        {
            var count = stop - start;
            for (var i = 0; i < count; i++) scratch[i] = Convert(commands[start + i], start);

            var batch = GiWorld.ApplyBatch(root, token, scratch.Slice(0, count), batchResults.Slice(0, count));
            if (batch == GiWorld.BatchStatus.StaleRevision)
            {
                token = Refresh(world, root);
                batch = GiWorld.ApplyBatch(root, token, scratch.Slice(0, count), batchResults.Slice(0, count));
            }

            if (batch != GiWorld.BatchStatus.Applied)
            {
                var code = batch == GiWorld.BatchStatus.StaleRevision ? GiFaultCode.StaleSource : GiFaultCode.Unknown;
                for (var i = 0; i < count; i++) results[start + i] = new GiCommandResult { Code = code };
                return batch == GiWorld.BatchStatus.StaleRevision ? GiApplyStatus.Stale : GiApplyStatus.Adapter;
            }

            for (var i = 0; i < count; i++)
                results[start + i] = new GiCommandResult { Code = Map(batchResults[i].Status), Source = batchResults[i].SourceId };
            return GiApplyStatus.Applied;
        }

        private static GiWorld.Token Refresh(byte world, Gi.GiRoot* root)
        {
            return new GiWorld.Token(world, GiWorld.GetContext(root, world) == null ? 0 : root->WorldRevisions[world]);
        }

        private static GiWorld.SourceTarget TargetOf(in GiCommand command, int start)
        {
            return command.Target >= 0
                ? GiWorld.SourceTarget.CreatedBy(command.Target - start)
                : GiWorld.SourceTarget.Existing(command.Source);
        }

        private static GiWorld.SourceCommand Convert(in GiCommand command, int start)
        {
            switch (command.Kind)
            {
                case GiCommandKind.Place:
                    return GiWorld.SourceCommand.Place(command.Layer, command.X, command.Y, command.Stamp, command.Gain,
                        command.Heading, command.Scale);
                case GiCommandKind.Remove:
                    return GiWorld.SourceCommand.Remove(TargetOf(command, start));
                case GiCommandKind.SetGain:
                    return GiWorld.SourceCommand.SetGain(TargetOf(command, start), command.Gain);
                case GiCommandKind.Fade:
                    return GiWorld.SourceCommand.Fade(TargetOf(command, start), command.Gain, command.Ticks);
                default:
                    return GiWorld.SourceCommand.Expire(TargetOf(command, start), command.Ticks);
            }
        }

        private static GiFaultCode Map(GiWorld.SourceResultStatus status)
        {
            switch (status)
            {
                case GiWorld.SourceResultStatus.Applied:
                    return GiFaultCode.None;
                case GiWorld.SourceResultStatus.InvalidSource:
                    return GiFaultCode.InvalidSource;
                case GiWorld.SourceResultStatus.InvalidLayer:
                    return GiFaultCode.InvalidLayer;
                case GiWorld.SourceResultStatus.InvalidStamp:
                    return GiFaultCode.InvalidStamp;
                case GiWorld.SourceResultStatus.SourceLimitReached:
                    return GiFaultCode.SourceLimit;
                default:
                    return GiFaultCode.Unknown;
            }
        }

        [BurstDiscard]
        private static void GuardBegin(byte world, int tag, bool write)
        {
            GiGuard.Begin(world, tag, write);
        }

        [BurstDiscard]
        private static void GuardEnd(byte world, bool write)
        {
            GiGuard.End(world, write);
        }

        [BurstDiscard]
        private static void RecordCommands(byte world, System.ReadOnlySpan<GiCommand> commands, System.ReadOnlySpan<GiCommandResult> results)
        {
            if (GiRecorderGate.Enabled) GiRecorderGate.Commands(world, commands, results);
        }
    }

    [BurstCompile]
    internal struct GiWriteBackJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public GiPulseMirror Mirror;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeList<GiCommand> Commands;
        [ReadOnly] public NativeList<GiCommandResult> Results;
        [ReadOnly] public NativeList<Entity> Owners;
        public ComponentLookup<GiSource> Sources;
        public ComponentLookup<GiFault> Faults;

        public void Execute()
        {
            for (var i = 0; i < Commands.Length; i++)
            {
                var command = Commands[i];
                if (command.Owner < 0)
                {
                    Track(command, i);
                    continue;
                }

                var entity = Owners[i];
                if (!Sources.HasComponent(entity)) continue;

                var source = Sources[entity];
                var applied = new GiApplied
                {
                    Placed = source.Placed,
                    Layer = source.Layer,
                    Stamp = source.Stamp,
                    Gain = source.Gain,
                    Source = source.Id,
                };

                var fault = GiWriteBack.Fold(command, Results[i], ref applied);
                Sources[entity] = new GiSource { Id = applied.Source, Layer = applied.Layer, Stamp = applied.Stamp, Gain = applied.Gain };

                if (!Faults.HasComponent(entity)) continue;
                if (fault != GiFaultCode.None)
                {
                    Faults[entity] = new GiFault { Code = fault };
                    Faults.SetComponentEnabled(entity, true);
                }
                else if (command.Kind != GiCommandKind.Remove && Faults.IsComponentEnabled(entity))
                {
                    Faults.SetComponentEnabled(entity, false);
                }
            }

            Mirror.Prune(Context.Value.Tick);
        }

        private void Track(in GiCommand command, int index)
        {
            if (command.Kind == GiCommandKind.Place && Results[index].Code == GiFaultCode.None && Results[index].Source.Generation != 0u)
                Mirror.Place(Results[index].Source);
            else if (command.Kind == GiCommandKind.Expire && command.Target >= 0 && command.Target < Results.Length)
                Mirror.Expire(Results[command.Target].Source, Context.Value.Tick + command.Ticks);
        }
    }

    [BurstCompile]
    internal struct GiRetireJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<GiSource> Sources;
        public EntityCommandBuffer Buffer;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var sources = chunk.GetNativeArray(ref Sources);
            var entities = chunk.GetNativeArray(Entities);
            for (var i = 0; i < sources.Length; i++)
                if (!sources[i].Placed)
                    Buffer.RemoveComponent<GiSource>(entities[i]);
        }
    }
}
