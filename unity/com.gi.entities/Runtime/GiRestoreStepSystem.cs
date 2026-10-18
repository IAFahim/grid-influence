using BovineLabs.Core;
using Unity.Entities;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiReconcileSystem))]
    [UpdateBefore(typeof(GiProcessSystem))]
    public partial struct GiRestoreStepSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var frame = state.EntityManager.GetComponentData<GiFrame>(SystemAPI.GetSingletonEntity<GiFrame>());
            if (!frame.Alive || GiRestoreGate.Pending(frame.World) == 0) return;

            state.CompleteDependency();
            GiRuntime.CompleteAll();
            Step(state.EntityManager, frame.World);
        }

        private static void Step(EntityManager manager, byte world)
        {
            var driver = GiRestoreGate.For(world);
            driver.CancelWhere((tag, applied) =>
            {
                var owner = GiSaveRestoreSystem.FromTag(tag);
                return !manager.Exists(owner) || !manager.HasComponent<GiSource>(owner) || manager.GetComponentData<GiSource>(owner).Gain != applied;
            });
            driver.Step(world);
        }
    }
}
