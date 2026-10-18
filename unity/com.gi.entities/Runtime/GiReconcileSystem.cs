using BovineLabs.Core;
using BovineLabs.Core.Collections;
using BovineLabs.Core.SingletonCollection;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    [UpdateBefore(typeof(GiProcessSystem))]
    public partial struct GiReconcileSystem : ISystem
    {
        private SingletonCollectionUtil<GiPulseSingleton, NativeThreadStream> _pulses;
        private EntityQuery _all;
        private EntityQuery _added;
        private EntityQuery _retire;
        private EntityCommandBuffer _buffer;
        private bool _playback;
        private GiReconcileView _view;
        private ComponentLookup<GiFrame> _frames;
        private ComponentLookup<GiSource> _sourceLookup;
        private ComponentLookup<GiFault> _faultLookup;
        private EntityTypeHandle _entities;
        private ComponentTypeHandle<GiSource> _sourceHandle;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _pulses = new SingletonCollectionUtil<GiPulseSingleton, NativeThreadStream>(ref state);

            const EntityQueryOptions every = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab;
            _all = new EntityQueryBuilder(Allocator.Temp).WithAll<GiSource>().WithOptions(every).Build(ref state);
            _added = new EntityQueryBuilder(Allocator.Temp).WithAll<GiEmitter>().WithNone<GiSource>().Build(ref state);
            _retire = new EntityQueryBuilder(Allocator.Temp).WithAll<GiSource>().WithNone<GiEmitter>().WithOptions(every).Build(ref state);

            _view = new GiReconcileView
            {
                Entities = state.GetEntityTypeHandle(),
                Sources = state.GetComponentTypeHandle<GiSource>(true),
                Emitters = state.GetComponentTypeHandle<GiEmitter>(true),
                Ramps = state.GetComponentTypeHandle<GiGainRamp>(true),
                Oriented = state.GetComponentTypeHandle<GiOriented>(true),
                Scaled = state.GetComponentTypeHandle<GiScaled>(true),
                Disabled = state.GetComponentTypeHandle<Disabled>(true),
                Transforms = state.GetComponentTypeHandle<LocalTransform>(true),
                Matrices = state.GetComponentTypeHandle<LocalToWorld>(true),
                Parents = state.GetComponentTypeHandle<Parent>(true),
                Faults = state.GetComponentTypeHandle<GiFault>(),
            };
            _frames = state.GetComponentLookup<GiFrame>();
            _sourceLookup = state.GetComponentLookup<GiSource>();
            _faultLookup = state.GetComponentLookup<GiFault>();
            _entities = state.GetEntityTypeHandle();
            _sourceHandle = state.GetComponentTypeHandle<GiSource>(true);
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            if (_playback) _buffer.Dispose();
            _pulses.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_playback)
            {
                state.Dependency.Complete();
                _buffer.Playback(state.EntityManager);
                _buffer.Dispose();
                _playback = false;
            }

            var added = !_added.IsEmptyIgnoreFilter;
            if (!added && _all.IsEmptyIgnoreFilter && _pulses.Containers.Length == 0)
            {
                _pulses.ClearRewind(state.Dependency);
                return;
            }

            if (added) state.EntityManager.AddComponent<GiSource>(_added);

            _view.Entities.Update(ref state);
            _view.Sources.Update(ref state);
            _view.Emitters.Update(ref state);
            _view.Ramps.Update(ref state);
            _view.Oriented.Update(ref state);
            _view.Scaled.Update(ref state);
            _view.Disabled.Update(ref state);
            _view.Transforms.Update(ref state);
            _view.Matrices.Update(ref state);
            _view.Parents.Update(ref state);
            _view.Faults.Update(ref state);
            _frames.Update(ref state);
            _sourceLookup.Update(ref state);
            _faultLookup.Update(ref state);
            _entities.Update(ref state);
            _sourceHandle.Update(ref state);

            var allocator = state.WorldUpdateAllocator;
            var chunkCount = _all.CalculateChunkCountWithoutFiltering();
            var context = new NativeReference<GiReconcileContext>(allocator);
            var counts = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var offsets = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var pulses = new NativeList<GiPulse>(allocator);
            var commands = new NativeList<GiCommand>(allocator);
            var owners = new NativeList<Entity>(allocator);
            var results = new NativeList<GiCommandResult>(allocator);
            var scratch = new NativeList<GiWorld.SourceCommand>(allocator);
            var batchResults = new NativeList<GiWorld.SourceResult>(allocator);

            var handle = new GiDrainPulsesJob
            {
                Frame = SystemAPI.GetSingletonEntity<GiFrame>(),
                Frames = _frames,
                Context = context,
                Streams = _pulses.Containers,
                Pulses = pulses,
            }.Schedule(state.Dependency);
            handle = new GiCountJob { View = _view, Context = context, LastVersion = state.LastSystemVersion, Counts = counts }
                .ScheduleParallel(_all, handle);
            handle = new GiPrefixJob
            {
                Counts = counts,
                Pulses = pulses,
                Context = context,
                Offsets = offsets,
                Commands = commands,
                Owners = owners,
                Results = results,
                Scratch = scratch,
                BatchResults = batchResults,
            }.Schedule(handle);
            handle = new GiWriteJob
            {
                View = _view,
                Context = context,
                LastVersion = state.LastSystemVersion,
                Offsets = offsets,
                Commands = commands.AsDeferredJobArray(),
                Owners = owners.AsDeferredJobArray(),
            }.ScheduleParallel(_all, handle);

            handle = new GiApplyJob
            {
                Frame = SystemAPI.GetSingletonEntity<GiFrame>(),
                Frames = _frames,
                Commands = commands,
                Results = results,
                Scratch = scratch,
                BatchResults = batchResults,
            }.Schedule(GiRuntime.BeginWrite(handle));
            GiRuntime.EndWrite(handle);

            handle = new GiWriteBackJob
            {
                Mirror = SystemAPI.GetSingleton<GiPulseMirror>(),
                Context = context,
                Commands = commands,
                Results = results,
                Owners = owners,
                Sources = _sourceLookup,
                Faults = _faultLookup,
            }.Schedule(handle);

            if (!_retire.IsEmptyIgnoreFilter)
            {
                _buffer = new EntityCommandBuffer(Allocator.TempJob);
                handle = new GiRetireJob { Entities = _entities, Sources = _sourceHandle, Buffer = _buffer }.Schedule(_retire, handle);
                _playback = true;
            }

            _pulses.ClearRewind(handle);
            state.Dependency = handle;
        }
    }
}
