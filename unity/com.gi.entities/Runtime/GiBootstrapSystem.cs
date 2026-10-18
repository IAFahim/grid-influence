using System;
using BovineLabs.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup), OrderFirst = true)]
    public unsafe partial struct GiBootstrapSystem : ISystem
    {
        private Entity _frame;
        private EntityQuery _profiles;
        private EntityQuery _sources;
        private ComponentTypeHandle<GiSource> _sourceHandle;
        private ulong _builtHash;
        private ulong _failedHash;
        private uint _generation;

        public void OnCreate(ref SystemState state)
        {
            _frame = state.EntityManager.CreateEntity(typeof(GiFrame), typeof(GiStats));
            _profiles = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            _sources = state.GetEntityQuery(ComponentType.ReadWrite<GiSource>());
            _sourceHandle = state.GetComponentTypeHandle<GiSource>();
        }

        public void OnDestroy(ref SystemState state)
        {
            GiRuntime.CompleteAll();
            state.EntityManager.CompleteAllTrackedJobs();
            Release(ref state);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_profiles.IsEmptyIgnoreFilter)
            {
                if (_builtHash != 0ul) Release(ref state);
                return;
            }

            var reference = _profiles.GetSingleton<GiProfileReference>();
            if (!reference.Value.IsCreated) return;

            ref var blob = ref reference.Value.Value;
            if (blob.Hash == _builtHash || blob.Hash == _failedHash) return;

            Rebuild(ref state, ref blob);
        }

        private void Rebuild(ref SystemState state, ref GiProfileBlob blob)
        {
            GiRuntime.CompleteAll();
            state.EntityManager.CompleteAllTrackedJobs();
            var frame = state.EntityManager.GetComponentData<GiFrame>(_frame);
            if (frame.Alive) GiWorldBuilder.Release(frame.World, frame.Tables, GiRuntime.Stamps);
            frame.Generation = 0u;
            _builtHash = 0ul;

            var grids = new ReadOnlySpan<GiGridSpec>(blob.Grids.GetUnsafePtr(), blob.Grids.Length);
            var layers = new ReadOnlySpan<GiLayerSpec>(blob.Layers.GetUnsafePtr(), blob.Layers.Length);
            var stamps = new ReadOnlySpan<GiStampSpec>(blob.Stamps.GetUnsafePtr(), blob.Stamps.Length);
            var cells = new ReadOnlySpan<sbyte>(blob.Cells.GetUnsafePtr(), blob.Cells.Length);
            var bindings = new ReadOnlySpan<GiBindingSpec>(blob.Bindings.GetUnsafePtr(), blob.Bindings.Length);

            var intact = GiProfileHash.Compute(blob.Plane, grids, layers, stamps, cells, bindings) == blob.Hash;
            byte world = 0;
            var tables = default(GiTables);
            var status = intact
                ? GiWorldBuilder.TryBuild(grids, layers, stamps, cells, GiRuntime.Stamps, out world, out tables)
                : GiBuildStatus.Unexpected;
            if (!intact || status != GiBuildStatus.Built)
            {
                _failedHash = blob.Hash;
                RaiseProfileFault(ref frame, intact ? status.ToString() : "hash mismatch");
                state.EntityManager.SetComponentData(_frame, frame);
                return;
            }

            _failedHash = 0ul;
            _builtHash = blob.Hash;
            _generation++;
            frame.World = world;
            frame.Token = GiWorld.Capture(world);
            frame.Tick = GiWorld.Tick(world);
            frame.Generation = _generation;
            frame.ProfileHash = blob.Hash;
            frame.Plane = new GiPlane(blob.Plane);
            frame.Tables = tables;
            frame.Faults.Clear(GiFaultKind.Profile);
            state.EntityManager.SetComponentData(_frame, frame);
            ResetSources(ref state);
        }

        private void Release(ref SystemState state)
        {
            var frame = state.EntityManager.GetComponentData<GiFrame>(_frame);
            if (frame.Alive) GiWorldBuilder.Release(frame.World, frame.Tables, GiRuntime.Stamps);
            frame.Generation = 0u;
            frame.ProfileHash = 0ul;
            state.EntityManager.SetComponentData(_frame, frame);
            _builtHash = 0ul;
            _failedHash = 0ul;
            ResetSources(ref state);
        }

        private void ResetSources(ref SystemState state)
        {
            _sourceHandle.Update(ref state);
            state.Dependency = new ResetSourcesJob { Sources = _sourceHandle }.ScheduleParallel(_sources, state.Dependency);
            state.Dependency.Complete();
        }

        private static void RaiseProfileFault(ref GiFrame frame, string reason)
        {
            if (frame.Faults.Raise(GiFaultKind.Profile, frame.Tick))
                BLGlobalLogger.LogErrorString("Gi profile rejected: " + reason);
        }

        [BurstCompile]
        private struct ResetSourcesJob : IJobChunk
        {
            public ComponentTypeHandle<GiSource> Sources;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var sources = chunk.GetNativeArray(ref Sources);
                for (var i = 0; i < sources.Length; i++)
                {
                    var source = sources[i];
                    source.Id = default;
                    sources[i] = source;
                }
            }
        }
    }
}
