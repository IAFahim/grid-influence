using System;
using Gi.Entities.Tests.Support;
using Unity.Collections;
using Unity.Entities;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Tests
{
    public sealed class GiEcsFixture : IDisposable
    {
        public readonly EcsWorld World;
        public readonly FixedStepSimulationSystemGroup FixedStep;
        public readonly GiSystemGroup Group;
        public readonly SystemHandle Bootstrap;
        public readonly SystemHandle Process;
        private BlobAssetReference<GiProfileBlob> _profile;
        private Entity _profileEntity;
        private bool _disposed;

        public GiEcsFixture(string name = "GiFixture")
        {
            World = new EcsWorld(name, WorldFlags.Game);
            var simulation = World.GetOrCreateSystemManaged<SimulationSystemGroup>();
            FixedStep = World.GetOrCreateSystemManaged<FixedStepSimulationSystemGroup>();
            FixedStep.RateManager = new RateUtils.FixedRateSimpleManager(1f / 60f);
            simulation.AddSystemToUpdateList(FixedStep);
            Group = World.GetOrCreateSystemManaged<GiSystemGroup>();
            FixedStep.AddSystemToUpdateList(Group);
            Bootstrap = World.GetOrCreateSystem<GiBootstrapSystem>();
            Process = World.GetOrCreateSystem<GiProcessSystem>();
            Group.AddSystemToUpdateList(Bootstrap);
            Group.AddSystemToUpdateList(Process);
            Group.SortSystems();
            FixedStep.SortSystems();
            simulation.SortSystems();
        }

        public EntityManager Manager => World.EntityManager;

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
