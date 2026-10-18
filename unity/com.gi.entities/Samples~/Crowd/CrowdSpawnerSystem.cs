using BovineLabs.Core.EntityCommands;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Samples.Crowd
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct CrowdSpawnerSystem : ISystem
    {
        private EntityQuery _pending;

        public void OnCreate(ref SystemState state)
        {
            _pending = state.GetEntityQuery(ComponentType.ReadOnly<CrowdSpawner>());
            state.RequireForUpdate(_pending);
        }

        public void OnUpdate(ref SystemState state)
        {
            var manager = state.EntityManager;
            var spawners = _pending.ToEntityArray(state.WorldUpdateAllocator);
            for (var s = 0; s < spawners.Length; s++)
            {
                var spawner = manager.GetComponentData<CrowdSpawner>(spawners[s]);
                var rng = new Random(spawner.Seed == 0u ? 1u : spawner.Seed);
                for (var i = 0; i < spawner.Count; i++)
                {
                    var angle = rng.NextFloat(math.PI * 2f);
                    var distance = math.sqrt(rng.NextFloat()) * spawner.Radius;
                    var agent = manager.CreateEntity(typeof(LocalTransform), typeof(CrowdAgent), typeof(CrowdSteering));
                    var commands = new EntityManagerCommands(manager, agent);
                    GiSplatBuilder.Apply(ref commands, new GiSplatSettings
                    {
                        Layer = spawner.Layer,
                        Stamp = spawner.Stamp,
                        Gain = spawner.Gain,
                    });
                    manager.SetComponentData(agent,
                        LocalTransform.FromPosition(math.cos(angle) * distance, 0f, math.sin(angle) * distance));
                    manager.SetComponentData(agent, new CrowdAgent { Rng = rng.NextUInt(), Speed = spawner.Speed * rng.NextFloat(0.7f, 1.3f) });
                }

                manager.DestroyEntity(spawners[s]);
            }

            spawners.Dispose();
        }
    }
}
