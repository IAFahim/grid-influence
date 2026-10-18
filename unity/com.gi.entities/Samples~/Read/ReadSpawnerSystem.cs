using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Samples.Read
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ReadSpawnerSystem : ISystem
    {
        private EntityQuery _pending;

        public void OnCreate(ref SystemState state)
        {
            _pending = state.GetEntityQuery(ComponentType.ReadOnly<ReadSpawner>());
            state.RequireForUpdate(_pending);
        }

        public void OnUpdate(ref SystemState state)
        {
            var manager = state.EntityManager;
            var spawners = _pending.ToEntityArray(state.WorldUpdateAllocator);
            for (var s = 0; s < spawners.Length; s++)
            {
                var spawner = manager.GetComponentData<ReadSpawner>(spawners[s]);
                var rng = new Random(spawner.Seed == 0u ? 1u : spawner.Seed);
                for (var i = 0; i < spawner.Count; i++)
                {
                    var mote = manager.CreateEntity(typeof(LocalTransform), typeof(ReadMote));
                    var angle = rng.NextFloat(math.PI * 2f);
                    var distance = math.sqrt(rng.NextFloat()) * spawner.Radius;
                    manager.SetComponentData(mote,
                        LocalTransform.FromPosition(math.cos(angle) * distance, 0f, math.sin(angle) * distance));
                    manager.SetComponentData(mote, new ReadMote
                    {
                        Heading = rng.NextFloat(math.PI * 2f),
                        Speed = spawner.Speed * rng.NextFloat(0.5f, 1.5f),
                    });
                }

                manager.DestroyEntity(spawners[s]);
            }

            spawners.Dispose();
        }
    }
}
