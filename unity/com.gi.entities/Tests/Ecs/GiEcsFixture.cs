using System;
using Gi.Entities.Tests.Support;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Transforms;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Tests
{
    public sealed class GiEcsFixture : IDisposable
    {
        public readonly EcsWorld World;
        public readonly SimulationSystemGroup Simulation;
        public readonly FixedStepSimulationSystemGroup FixedStep;
        public readonly GiSystemGroup Group;
        public readonly SystemHandle Bootstrap;
        public readonly SystemHandle Reconcile;
        public readonly SystemHandle Sync;
        public readonly SystemHandle Splat;
        public readonly GiSenseSystem Sense;
        public readonly GiCountersSystem Counters;
        public Presentation.GiInspectorSystem Inspector;
        public readonly SystemHandle Process;
        public readonly SystemHandle Recorder;
        public readonly SystemHandle Restore;
        public readonly SystemHandle RestoreStep;
        public readonly SystemHandle Lockstep;
        private BlobAssetReference<GiProfileBlob> _profile;
        private Entity _profileEntity;
        private bool _disposed;
        private double _elapsed;

        public GiEcsFixture(string name = "GiFixture")
        {
            World = new EcsWorld(name, WorldFlags.Game);
            Simulation = World.GetOrCreateSystemManaged<SimulationSystemGroup>();
            var simulation = Simulation;
            FixedStep = World.GetOrCreateSystemManaged<FixedStepSimulationSystemGroup>();
            FixedStep.RateManager = new RateUtils.FixedRateSimpleManager(1f / 60f);
            simulation.AddSystemToUpdateList(FixedStep);
            Group = World.GetOrCreateSystemManaged<GiSystemGroup>();
            FixedStep.AddSystemToUpdateList(Group);
            Bootstrap = World.GetOrCreateSystem<GiBootstrapSystem>();
            Reconcile = World.GetOrCreateSystem<GiReconcileSystem>();
            Sync = World.GetOrCreateSystem<GiSyncSystem>();
            Splat = World.GetOrCreateSystem<GiSplatSystem>();
            Sense = World.GetOrCreateSystemManaged<GiSenseSystem>();
            Counters = World.GetOrCreateSystemManaged<GiCountersSystem>();
            Process = World.GetOrCreateSystem<GiProcessSystem>();
            Recorder = World.GetOrCreateSystem<GiRecorderSystem>();
            Restore = World.GetOrCreateSystem<GiSaveRestoreSystem>();
            RestoreStep = World.GetOrCreateSystem<GiRestoreStepSystem>();
            Lockstep = World.GetOrCreateSystem<GiLockstepSystem>();
            Group.AddSystemToUpdateList(Bootstrap);
            Group.AddSystemToUpdateList(Recorder);
            Group.AddSystemToUpdateList(Restore);
            Group.AddSystemToUpdateList(RestoreStep);
            Group.AddSystemToUpdateList(Lockstep);
            Group.AddSystemToUpdateList(Reconcile);
            Group.AddSystemToUpdateList(Sync);
            Group.AddSystemToUpdateList(Splat);
            Group.AddSystemToUpdateList(Sense);
            Group.AddSystemToUpdateList(Process);
            Group.AddSystemToUpdateList(Counters);
            Group.SortSystems();
            FixedStep.SortSystems();
            simulation.SortSystems();
        }

        public EntityManager Manager => World.EntityManager;

        public void AddInspector()
        {
            if (Inspector != null) return;

            var presentation = World.GetOrCreateSystemManaged<PresentationSystemGroup>();
            Simulation.AddSystemToUpdateList(presentation);
            Inspector = World.GetOrCreateSystemManaged<Presentation.GiInspectorSystem>();
            presentation.AddSystemToUpdateList(Inspector);
            presentation.SortSystems();
            Simulation.SortSystems();
        }

        public SystemHandle AddFieldDraw()
        {
            var presentation = World.GetOrCreateSystemManaged<PresentationSystemGroup>();
            Simulation.AddSystemToUpdateList(presentation);
            var debug = World.GetOrCreateSystemManaged<BovineLabs.Core.DebugSystemGroup>();
            presentation.AddSystemToUpdateList(debug);
            debug.AddSystemToUpdateList(World.GetOrCreateSystem<BovineLabs.Quill.DrawSystem>());
            var draw = World.GetOrCreateSystem<Presentation.GiFieldDrawSystem>();
            debug.AddSystemToUpdateList(draw);
            presentation.SortSystems();
            Simulation.SortSystems();
            return draw;
        }

        public void AddTransformSystems()
        {
            var transforms = World.GetOrCreateSystemManaged<TransformSystemGroup>();
            Simulation.AddSystemToUpdateList(transforms);
            transforms.AddSystemToUpdateList(World.GetOrCreateSystem<ParentSystem>());
            transforms.AddSystemToUpdateList(World.GetOrCreateSystem<LocalToWorldSystem>());
            transforms.SortSystems();
            Simulation.SortSystems();
        }

        public void Frame1()
        {
            Simulation.Update();
        }

        public void UseCatchUp(float timestep)
        {
            FixedStep.RateManager = new RateUtils.FixedRateCatchUpManager(timestep);
            World.MaximumDeltaTime = 1f;
        }

        public void Advance(float seconds)
        {
            _elapsed += seconds;
            World.SetTime(new TimeData(_elapsed, seconds));
            Simulation.Update();
        }

        public Entity FrameEntity
        {
            get
            {
                using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
                return query.GetSingletonEntity();
            }
        }

        public GiFrame Frame => Manager.GetComponentData<GiFrame>(FrameEntity);

        public void SetProfile(GiProfileSpec spec)
        {
            ClearProfile();
            _profile = GiProfileBlobBuilder.Create(spec, Allocator.Persistent);
            _profileEntity = Manager.CreateEntity(typeof(GiProfileReference));
            Manager.SetComponentData(_profileEntity, new GiProfileReference { Value = _profile });
        }

        public void ClearProfile()
        {
            if (_profileEntity != Entity.Null && Manager.Exists(_profileEntity)) Manager.DestroyEntity(_profileEntity);
            _profileEntity = Entity.Null;
            if (_profile.IsCreated) _profile.Dispose();
            _profile = default;
        }

        public void SetStandardProfile()
        {
            SetProfile(GiScenarioProfile.Standard().ToSpec());
        }

        public void SetProfileWith(params GiBindingSpec[] bindings)
        {
            var spec = GiScenarioProfile.Standard().ToSpec();
            spec.Bindings = bindings;
            SetProfile(spec);
        }

        public Entity CreateEmitter(byte layer, byte stamp, sbyte gain, float x, float z, ushort ramp = 0)
        {
            var types = ramp > 0
                ? new[]
                {
                    ComponentType.ReadWrite<GiEmitter>(), ComponentType.ReadWrite<GiFault>(), ComponentType.ReadWrite<LocalTransform>(),
                    ComponentType.ReadWrite<GiGainRamp>(),
                }
                : new[] { ComponentType.ReadWrite<GiEmitter>(), ComponentType.ReadWrite<GiFault>(), ComponentType.ReadWrite<LocalTransform>() };
            var entity = Manager.CreateEntity(Manager.CreateArchetype(types));
            Manager.SetComponentData(entity, new GiEmitter { Layer = new GiLayerKey(layer), Stamp = new GiStampKey(stamp), Gain = gain });
            Manager.SetComponentData(entity, LocalTransform.FromPosition(x, 0f, z));
            Manager.SetComponentEnabled<GiFault>(entity, false);
            if (ramp > 0) Manager.SetComponentData(entity, new GiGainRamp { Ticks = ramp });
            return entity;
        }

        public Entity CreateSplat(byte layer, byte stamp, sbyte gain, float x, float z)
        {
            var entity = Manager.CreateEntity(typeof(GiSplat), typeof(GiFault), typeof(LocalTransform));
            Manager.SetComponentData(entity, new GiSplat { Layer = new GiLayerKey(layer), Stamp = new GiStampKey(stamp), Gain = gain });
            Manager.SetComponentData(entity, LocalTransform.FromPosition(x, 0f, z));
            Manager.SetComponentEnabled<GiFault>(entity, false);
            return entity;
        }

        public void Complete()
        {
            GiRuntime.CompleteAll();
            Manager.CompleteAllTrackedJobs();
        }

        public void Tick(int count = 1)
        {
            for (var i = 0; i < count; i++) FixedStep.Update();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Group.Enabled = false;
            ClearProfile();
            World.Dispose();
        }
    }
}
