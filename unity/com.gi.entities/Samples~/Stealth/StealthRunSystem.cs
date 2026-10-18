using BovineLabs.Core.SingletonCollection;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace Gi.Entities.Samples.Stealth
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    public partial struct StealthRunSystem : ISystem
    {
        private EntityQuery _players;
        private EntityQuery _pulses;
        private ushort _tick;

        public void OnCreate(ref SystemState state)
        {
            _players = state.GetEntityQuery(ComponentType.ReadOnly<StealthPlayer>(), ComponentType.ReadOnly<LocalTransform>());
            _pulses = state.GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<GiPulseSingleton>() },
                Options = EntityQueryOptions.IncludeSystems,
            });
            state.RequireForUpdate(_players);
        }

        public void OnUpdate(ref SystemState state)
        {
            _tick++;
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) return;

            var manager = state.EntityManager;
            var players = _players.ToEntityArray(state.WorldUpdateAllocator);
            for (var i = 0; i < players.Length; i++)
            {
                var player = manager.GetComponentData<StealthPlayer>(players[i]);
                if (_tick % player.RunEveryTicks != 0) continue;

                var position = manager.GetComponentData<LocalTransform>(players[i]).Position;
                var writer = _pulses.GetSingleton<GiPulseSingleton>().CreateThreadStream();
                writer.Write(new GiPulse
                {
                    Layer = player.Layer,
                    Stamp = player.Clash,
                    X = position.x,
                    Y = position.z,
                    Scale = 1f,
                    Gain = player.RunGain,
                    Ticks = 1,
                });
            }

            players.Dispose();
        }
    }
}
