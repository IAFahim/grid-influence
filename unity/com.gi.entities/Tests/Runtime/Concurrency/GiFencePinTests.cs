using System.Threading;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Scripting.LifecycleManagement;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Tests
{
    public struct FenceProbe : IComponentData
    {
        public int Value;
    }

    [NoAutoStaticsCleanup]
    public static class FenceGate
    {
        public static int Open;
        public static int Incomplete;
    }

    [DisableAutoCreation]
    public partial struct FenceSpinWriteSystem : ISystem
    {
        private ComponentLookup<FenceProbe> _lookup;

        public void OnCreate(ref SystemState state)
        {
            _lookup = state.GetComponentLookup<FenceProbe>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _lookup.Update(ref state);
            state.Dependency = new SpinWriteJob { Lookup = _lookup, Entity = SystemAPI.GetSingletonEntity<FenceProbe>() }.Schedule(state.Dependency);
        }

        private struct SpinWriteJob : IJob
        {
            public ComponentLookup<FenceProbe> Lookup;
            public Entity Entity;

            public void Execute()
            {
                while (Volatile.Read(ref FenceGate.Open) == 0) Thread.SpinWait(100);
                Lookup[Entity] = new FenceProbe { Value = 1 };
            }
        }
    }

    [DisableAutoCreation]
    public partial struct FenceSpinReadSystem : ISystem
    {
        private ComponentLookup<FenceProbe> _lookup;

        public void OnCreate(ref SystemState state)
        {
            _lookup = state.GetComponentLookup<FenceProbe>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            _lookup.Update(ref state);
            state.Dependency = new SpinReadJob { Lookup = _lookup, Entity = SystemAPI.GetSingletonEntity<FenceProbe>() }.Schedule(state.Dependency);
        }

        private struct SpinReadJob : IJob
        {
            [ReadOnly] public ComponentLookup<FenceProbe> Lookup;
            public Entity Entity;

            public void Execute()
            {
                while (Volatile.Read(ref FenceGate.Open) == 0) Thread.SpinWait(100);
                Lookup.HasComponent(Entity);
            }
        }
    }

    [DisableAutoCreation]
    [UpdateAfter(typeof(FenceSpinWriteSystem))]
    [UpdateAfter(typeof(FenceSpinReadSystem))]
    public partial struct FenceProbeReadSystem : ISystem
    {
        private ComponentLookup<FenceProbe> _lookup;

        public void OnCreate(ref SystemState state)
        {
            _lookup = state.GetComponentLookup<FenceProbe>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            Volatile.Write(ref FenceGate.Incomplete, state.Dependency.IsCompleted ? 0 : 1);
        }
    }

    [DisableAutoCreation]
    [UpdateAfter(typeof(FenceSpinWriteSystem))]
    [UpdateAfter(typeof(FenceSpinReadSystem))]
    public partial struct FenceProbeWriteSystem : ISystem
    {
        private ComponentLookup<FenceProbe> _lookup;

        public void OnCreate(ref SystemState state)
        {
            _lookup = state.GetComponentLookup<FenceProbe>();
        }

        public void OnUpdate(ref SystemState state)
        {
            Volatile.Write(ref FenceGate.Incomplete, state.Dependency.IsCompleted ? 0 : 1);
        }
    }

    public sealed class GiFencePinTests
    {
        private static int Observe<TFirst, TSecond>()
            where TFirst : unmanaged, ISystem
            where TSecond : unmanaged, ISystem
        {
            using var world = new EcsWorld("FencePin", WorldFlags.Game);
            world.EntityManager.CreateEntity(typeof(FenceProbe));
            var group = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            group.AddSystemToUpdateList(world.GetOrCreateSystem<TFirst>());
            group.AddSystemToUpdateList(world.GetOrCreateSystem<TSecond>());
            group.SortSystems();

            Volatile.Write(ref FenceGate.Open, 0);
            Volatile.Write(ref FenceGate.Incomplete, -1);
            try
            {
                group.Update();
                return Volatile.Read(ref FenceGate.Incomplete);
            }
            finally
            {
                Volatile.Write(ref FenceGate.Open, 1);
                world.EntityManager.CompleteAllTrackedJobs();
            }
        }

        [Test]
        public void E1_AReaderRegisteredInOnCreateWaitsForAnEarlierWriter()
        {
            Assert.That(Observe<FenceSpinWriteSystem, FenceProbeReadSystem>(), Is.EqualTo(1));
        }

        [Test]
        public void E1_AWriterRegisteredInOnCreateWaitsForAnEarlierReader()
        {
            Assert.That(Observe<FenceSpinReadSystem, FenceProbeWriteSystem>(), Is.EqualTo(1));
        }

        [Test]
        public void E1_AReaderDoesNotWaitForAnEarlierReader()
        {
            Assert.That(Observe<FenceSpinReadSystem, FenceProbeReadSystem>(), Is.EqualTo(0));
        }

        [Test]
        public void E6_TheFixedStepGroupAndTheGiGroupRunInAnEditorWorld()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
            var world = new EcsWorld("E6", WorldFlags.Editor);
            var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Editor);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);

            var spec = GiScenarioProfile.Standard().ToSpec();
            var blob = GiProfileBlobBuilder.Create(spec, Allocator.Persistent);
            try
            {
                Assert.That(world.GetExistingSystemManaged<FixedStepSimulationSystemGroup>(), Is.Not.Null, "no fixed-step group in the Editor world");
                Assert.That(world.GetExistingSystemManaged<GiSystemGroup>(), Is.Not.Null, "no Gi group in the Editor world");
                var entity = world.EntityManager.CreateEntity(typeof(GiProfileReference));
                world.EntityManager.SetComponentData(entity, new GiProfileReference { Value = blob });
                var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
                simulation.Update();
                simulation.Update();
                GiRuntime.CompleteAll();
                world.EntityManager.CompleteAllTrackedJobs();

                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
                var frame = world.EntityManager.GetComponentData<GiFrame>(query.GetSingletonEntity());
                Assert.That(frame.Alive, Is.True, "the Gi frame was never built");
                Assert.That(frame.Tick, Is.GreaterThan(0), "the Gi group never processed in the Editor world");
            }
            finally
            {
                GiRuntime.CompleteAll();
                if (world.IsCreated) world.Dispose();
                if (blob.IsCreated) blob.Dispose();
            }
        }
    }
}
