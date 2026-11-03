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
    internal struct GiSyncCountJob : IJobChunk
    {
        public GiSyncView View;
        public uint LastVersion;
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!Frames[Frame].Alive || !View.Needs(chunk, LastVersion)) return;
            Counts[unfilteredChunkIndex * GiSyncPass.Categories + View.Category(chunk)] = View.Placed(chunk);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSyncPrefixJob : IJob
    {
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Counts;
        public NativeArray<int> Offsets;
        public NativeArray<int> Meta;
        public NativeList<GiWorld.SourceId> Sources;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<float> Headings;
        public NativeList<float> Scales;
        public NativeList<ulong> Invalid;

        public void Execute()
        {
            Context.Value = GiReconcileContext.Of(Frames[Frame]);
            var chunks = Counts.Length / GiSyncPass.Categories;
            var total = 0;
            var words = 0;
            for (var k = 0; k < GiSyncPass.Categories; k++)
            {
                var count = 0;
                Meta[k] = total;
                for (var c = 0; c < chunks; c++)
                {
                    Offsets[c * GiSyncPass.Categories + k] = total + count;
                    count += Counts[c * GiSyncPass.Categories + k];
                }

                Meta[GiSyncPass.Categories + k] = count;
                Meta[2 * GiSyncPass.Categories + k] = words;
                total += count;
                words += GiSyncPass.Words(count);
            }

            Sources.ResizeUninitialized(total);
            Xs.ResizeUninitialized(total);
            Ys.ResizeUninitialized(total);
            Headings.ResizeUninitialized(total);
            Scales.ResizeUninitialized(total);
            Invalid.ResizeUninitialized(words);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSyncWriteJob : IJobChunk
    {
        public GiSyncView View;
        public uint LastVersion;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<GiWorld.SourceId> Sources;
        [NativeDisableParallelForRestriction] public NativeArray<float> Xs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Ys;
        [NativeDisableParallelForRestriction] public NativeArray<float> Headings;
        [NativeDisableParallelForRestriction] public NativeArray<float> Scales;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var context = Context.Value;
            if (!context.Alive || !View.Needs(chunk, LastVersion)) return;

            var useMatrix = View.UsesMatrix(chunk);
            var sources = chunk.GetNativeArray(ref View.Sources);
            var transforms = !useMatrix ? chunk.GetNativeArray(ref View.Transforms) : default;
            var matrices = useMatrix && chunk.Has(ref View.Matrices) ? chunk.GetNativeArray(ref View.Matrices) : default;
            var row = Offsets[unfilteredChunkIndex * GiSyncPass.Categories + View.Category(chunk)];
            for (var i = 0; i < sources.Length; i++)
            {
                if (!sources[i].Placed) continue;

                GiPose.Read(context.Plane, useMatrix, transforms, matrices, i, out var x, out var y, out var heading, out var scale);
                Sources[row] = sources[i].Id;
                Xs[row] = x;
                Ys[row] = y;
                Headings[row] = heading;
                Scales[row] = scale;
                row++;
            }
        }
    }

#if !ENABLE_CORECLR || GI_BURST_SYNC
    [BurstCompile]
#endif
    internal unsafe struct GiSyncApplyJob : IJob
    {
        public Entity Frame;
        public ComponentLookup<GiFrame> Frames;
        public ComponentLookup<GiStats> Stats;
        public Entity StatsEntity;
        [ReadOnly] public NativeArray<int> Meta;
        public NativeList<GiWorld.SourceId> Sources;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<float> Headings;
        public NativeList<float> Scales;
        public NativeList<ulong> Invalid;
        [NativeDisableUnsafePtrRestriction] internal Gi.GiRoot* Root;
        private static readonly ProfilerMarker Marker = new ProfilerMarker(ProfilerCategory.Scripts, GiMarkers.SyncName);

        public GiSyncApplyJob()
        {
            fixed (Gi.GiRoot* root = &Gi.Runtime.Root) Root = root;
        }

        public void Execute()
        {
            var rows = 0;
            for (var k = 0; k < GiSyncPass.Categories; k++) rows += Meta[GiSyncPass.Categories + k];

            var stats = Stats[StatsEntity];
            stats.SyncRows = rows;
            Stats[StatsEntity] = stats;
            if (rows == 0) return;

            var frame = Frames[Frame];
            if (!frame.Alive) return;

            var token = frame.Token;
            Gi.GiRoot* root = Root;
            var starts = (int*)Meta.GetUnsafeReadOnlyPtr();
            var sources = (GiWorld.SourceId*)Sources.GetUnsafePtr();
            var xs = (float*)Xs.GetUnsafePtr();
            var ys = (float*)Ys.GetUnsafePtr();
            var headings = (float*)Headings.GetUnsafePtr();
            var scales = (float*)Scales.GetUnsafePtr();
            GuardBegin(frame.World, GiGuard.Sync, true);
            var scope = Marker.Auto();
            RecordSync(frame.World, sources, xs, ys, headings, scales, starts, starts + GiSyncPass.Categories);
            var status = Apply(ref token, frame.World, root, sources, xs, ys, headings, scales, (ulong*)Invalid.GetUnsafePtr(),
                starts, starts + GiSyncPass.Categories, starts + 2 * GiSyncPass.Categories);
            scope.Dispose();
            GuardEnd(frame.World, true);

            frame.Token = token;
            if (status == GiApplyStatus.Stale) frame.Faults.Raise(GiFaultKind.StaleToken, frame.Tick);
            if (status == GiApplyStatus.Adapter) frame.Faults.Raise(GiFaultKind.Adapter, frame.Tick);
            Frames[Frame] = frame;
        }

        private static GiApplyStatus Apply(ref GiWorld.Token token, byte world, Gi.GiRoot* root, GiWorld.SourceId* sources, float* xs, float* ys,
            float* headings, float* scales, ulong* invalid, int* categoryStart, int* categoryCount, int* wordStart)
        {
            var ranges = stackalloc GiWorld.TransformRange[GiSyncPass.Categories];
            var used = 0;
            for (var k = 0; k < GiSyncPass.Categories; k++)
            {
                var count = categoryCount[k];
                if (count == 0) continue;

                var start = categoryStart[k];
                var words = GiSyncPass.Words(count);
                var mask = invalid + wordStart[k];

                ranges[used++] = new GiWorld.TransformRange
                {
                    Sources = sources + start,
                    Xs = xs + start,
                    Ys = ys + start,
                    Headings = (k & GiSyncPass.Oriented) != 0 ? headings + start : null,
                    Scales = (k & GiSyncPass.Scaled) != 0 ? scales + start : null,
                    Invalid = mask,
                    Count = count,
                    InvalidWords = words,
                };
            }

            if (used == 0) return GiApplyStatus.Applied;

            var batch = GiWorld.SyncTransforms(root, token, new System.ReadOnlySpan<GiWorld.TransformRange>(ranges, used));
            if (batch == GiWorld.BatchStatus.StaleRevision)
            {
                token = Refresh(world, root);
                batch = GiWorld.SyncTransforms(root, token, new System.ReadOnlySpan<GiWorld.TransformRange>(ranges, used));
            }

            if (batch == GiWorld.BatchStatus.Applied) return GiApplyStatus.Applied;
            return batch == GiWorld.BatchStatus.StaleRevision ? GiApplyStatus.Stale : GiApplyStatus.Adapter;
        }

        private static GiWorld.Token Refresh(byte world, Gi.GiRoot* root)
        {
            return new GiWorld.Token(world, GiWorld.GetContext(root, world) == null ? 0 : root->WorldRevisions[world]);
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
        private static void RecordSync(byte world, GiWorld.SourceId* sources, float* xs, float* ys, float* headings, float* scales, int* categoryStart,
            int* categoryCount)
        {
            if (GiRecorderGate.Enabled) GiRecorderGate.Sync(world, sources, xs, ys, headings, scales, categoryStart, categoryCount);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSyncScatterJob : IJobChunk
    {
        public GiSyncView View;
        public uint LastVersion;
        public ComponentTypeHandle<GiFault> Faults;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [ReadOnly] public NativeArray<int> Meta;
        [ReadOnly] public NativeList<float> Xs;
        [ReadOnly] public NativeList<float> Ys;
        [ReadOnly] public NativeList<ulong> Invalid;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!Context.Value.Alive || !View.Needs(chunk, LastVersion) || !chunk.Has(ref Faults)) return;

            var category = View.Category(chunk);
            var sources = chunk.GetNativeArray(ref View.Sources);
            var faults = chunk.GetNativeArray(ref Faults);
            var enabled = chunk.GetEnabledMask(ref Faults);
            var row = Offsets[unfilteredChunkIndex * GiSyncPass.Categories + category];
            var first = Meta[category];
            var words = (ulong*)Invalid.GetUnsafeReadOnlyPtr();
            var wordStart = Meta[2 * GiSyncPass.Categories + category];
            for (var i = 0; i < sources.Length; i++)
            {
                if (!sources[i].Placed) continue;

                if (GiSyncPass.Rejected(words, wordStart, row - first))
                {
                    faults[i] = new GiFault { Code = GiSyncPass.Reason(Xs[row], Ys[row]) };
                    chunk.SetComponentEnabled(ref Faults, i, true);
                }
                else if (enabled[i])
                {
                    chunk.SetComponentEnabled(ref Faults, i, false);
                }

                row++;
            }
        }
    }
}
