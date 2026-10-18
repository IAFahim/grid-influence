using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Gi.Entities.Samples.Stealth
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    public partial struct StealthPlayerSystem : ISystem
    {
        private EntityQuery _players;

        public void OnCreate(ref SystemState state)
        {
            _players = state.GetEntityQuery(ComponentType.ReadOnly<StealthPlayer>(), ComponentType.ReadWrite<LocalTransform>());
            state.RequireForUpdate(_players);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!Input.anyKey) return;

            var move = new float2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
            if (math.lengthsq(move) < 1e-6f) return;

            var manager = state.EntityManager;
            var players = _players.ToEntityArray(state.WorldUpdateAllocator);
            for (var i = 0; i < players.Length; i++)
            {
                var player = manager.GetComponentData<StealthPlayer>(players[i]);
                var transform = manager.GetComponentData<LocalTransform>(players[i]);
                var position = transform.Position + new float3(move.x, 0f, move.y) * (player.Speed * SystemAPI.Time.DeltaTime);
                transform.Position = math.clamp(position, new float3(-30f, 0f, -30f), new float3(30f, 0f, 30f));
                manager.SetComponentData(players[i], transform);
            }

            players.Dispose();
        }
    }
}
