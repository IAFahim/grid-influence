using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Samples.Territory
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TerritorySpawnerSystem : ISystem
    {
        private EntityQuery _pending;

        public void OnCreate(ref SystemState state)
        {
            _pending = state.GetEntityQuery(ComponentType.ReadOnly<TerritorySpawner>());
            state.RequireForUpdate(_pending);
        }

        public void OnUpdate(ref SystemState state)
        {
            var manager = state.EntityManager;
            var spawners = _pending.ToEntityArray(state.WorldUpdateAllocator);
            for (var s = 0; s < spawners.Length; s++)
            {
                var spawner = manager.GetComponentData<TerritorySpawner>(spawners[s]);
                var rng = new Random(spawner.Seed == 0u ? 1u : spawner.Seed);
                for (var i = 0; i < spawner.Count; i++)
                {
                    var banner = manager.CreateEntity(typeof(LocalTransform), typeof(TerritoryBanner), typeof(TerritoryHeld));
                    var angle = rng.NextFloat(math.PI * 2f);
                    var distance = math.sqrt(rng.NextFloat()) * spawner.Radius;
                    manager.SetComponentData(banner,
                        LocalTransform.FromPosition(math.cos(angle) * distance, 0f, math.sin(angle) * distance));
                    manager.SetComponentEnabled<TerritoryHeld>(banner, false);
                }

                manager.DestroyEntity(spawners[s]);
            }

            spawners.Dispose();
        }
    }
}
