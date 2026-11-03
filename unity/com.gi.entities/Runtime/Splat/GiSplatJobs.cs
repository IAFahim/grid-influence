using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [BurstCompile]
    internal unsafe struct GiSplatCountJob : IJobChunk
    {
        public GiSplatView View;
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            View.Visit(chunk, GiReconcileContext.Of(Frames[Frame]), (int*)Counts.GetUnsafePtr() + unfilteredChunkIndex * GiSplatPass.Layers, null, null, null,
                null, null);
        }
    }

    [BurstCompile]
    internal struct GiSplatPrefixJob : IJob
    {
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Counts;
        public NativeArray<int> Offsets;
        public NativeArray<int> Meta;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<byte> Stamps;
        public NativeList<sbyte> Gains;

        public void Execute()
        {
            Context.Value = GiReconcileContext.Of(Frames[Frame]);
            var chunks = Counts.Length / GiSplatPass.Layers;
            var total = 0;
            for (var k = 0; k < GiSplatPass.Layers; k++)
            {
                var count = 0;
                Meta[k] = total;
                for (var c = 0; c < chunks; c++)
                {
                    Offsets[c * GiSplatPass.Layers + k] = total + count;
                    count += Counts[c * GiSplatPass.Layers + k];
                }

                Meta[GiSplatPass.Layers + k] = count;
                total += count;
            }

            Xs.ResizeUninitialized(total);
            Ys.ResizeUninitialized(total);
            Stamps.ResizeUninitialized(total);
            Gains.ResizeUninitialized(total);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSplatWriteJob : IJobChunk
    {
        public GiSplatView View;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<float> Xs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Ys;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Stamps;
        [NativeDisableParallelForRestriction] public NativeArray<sbyte> Gains;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var cursor = stackalloc int[GiSplatPass.Layers];
            for (var k = 0; k < GiSplatPass.Layers; k++) cursor[k] = Offsets[unfilteredChunkIndex * GiSplatPass.Layers + k];

            View.Visit(chunk, Context.Value, null, cursor, (float*)Xs.GetUnsafePtr(), (float*)Ys.GetUnsafePtr(),
                (byte*)Stamps.GetUnsafePtr(), (sbyte*)Gains.GetUnsafePtr());
        }
    }

    [BurstCompile]
    internal struct GiSplatIdleJob : IJob
    {
        public Entity Frame;
        public ComponentLookup<GiStats> Stats;

        public void Execute()
        {
            var stats = Stats[Frame];
            stats.Splats = 0;
            Stats[Frame] = stats;
        }
    }

#if !ENABLE_CORECLR || GI_BURST_SPLAT
    [BurstCompile]
#endif
    internal unsafe struct GiSplatApplyJob : IJob
    {
        public Entity Frame;
        public ComponentLookup<GiFrame> Frames;
        public ComponentLookup<GiStats> Stats;
        [ReadOnly] public NativeArray<int> Meta;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<byte> Stamps;
        public NativeList<sbyte> Gains;
        internal Gi.GiRoot Root;

        public GiSplatApplyJob()
        {
            Root = Gi.Runtime.Root;
        }

        public void Execute()
        {
            var frame = Frames[Frame];
            var applied = 0;
            if (frame.Alive && Xs.Length > 0)
            {
                Gi.GiRoot captured = Root;
                Gi.GiRoot* root = &captured;
                var meta = (int*)Meta.GetUnsafeReadOnlyPtr();
                var xs = (float*)Xs.GetUnsafePtr();
                var ys = (float*)Ys.GetUnsafePtr();
                var stamps = (byte*)Stamps.GetUnsafePtr();
                var gains = (sbyte*)Gains.GetUnsafePtr();
                GuardBegin(frame.World, GiGuard.Splat, true);
                var scope = GiMarkers.Splat.Auto();
                RecordSplat(frame.World, meta, meta + GiSplatPass.Layers, xs, ys, stamps, gains);
                applied = Apply(root, frame.World, frame.Tables, meta, meta + GiSplatPass.Layers, xs, ys, stamps, gains);
                scope.Dispose();
                GuardEnd(frame.World, true);
            }

            var stats = Stats[Frame];
            stats.Splats = applied;
            Stats[Frame] = stats;
        }

        private static int Apply(Gi.GiRoot* root, byte world, GiTables tables, int* layerStart, int* layerCount, float* xs, float* ys, byte* stamps,
            sbyte* gains)
        {
            var applied = 0;
            for (var key = 0; key < GiSplatPass.Layers; key++)
            {
                var count = layerCount[key];
                if (count == 0 || !tables.TryLayer(new GiLayerKey((byte)key), out var layer)) continue;

                var start = layerStart[key];
                var columnsX = new System.ReadOnlySpan<float>(xs + start, count);
                var columnsY = new System.ReadOnlySpan<float>(ys + start, count);
                if (GiSplatPass.Uniform(stamps + start, gains + start, count))
                    applied += GiWorld.Splat(root, world, layer, columnsX, columnsY, stamps[start], gains[start]);
                else
                    applied += GiWorld.Splat(root, world, layer, columnsX, columnsY,
                        new System.ReadOnlySpan<byte>(stamps + start, count), new System.ReadOnlySpan<sbyte>(gains + start, count));
            }

            return applied;
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
        private static void RecordSplat(byte world, int* layerStart, int* layerCount, float* xs, float* ys, byte* stamps, sbyte* gains)
        {
            if (GiRecorderGate.Enabled) GiRecorderGate.Splat(world, layerStart, layerCount, xs, ys, stamps, gains);
        }
    }
}
