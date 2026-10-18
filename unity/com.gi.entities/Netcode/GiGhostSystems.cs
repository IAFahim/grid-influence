using Unity.Entities;
using Unity.NetCode;

namespace Gi.Entities.Netcode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial struct GiGhostServerSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (emitter, enabled, ghost) in SystemAPI.Query<RefRO<GiEmitter>, EnabledRefRO<GiEmitter>, RefRW<GiGhostEmitter>>()
                         .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
            {
                var next = new GiGhostEmitter { Layer = emitter.ValueRO.Layer.Index, Stamp = emitter.ValueRO.Stamp.Index, Gain = emitter.ValueRO.Gain, Enabled = enabled.ValueRO };
                if (ghost.ValueRO.Layer != next.Layer || ghost.ValueRO.Stamp != next.Stamp || ghost.ValueRO.Gain != next.Gain || ghost.ValueRO.Enabled != next.Enabled)
                    ghost.ValueRW = next;
            }

            foreach (var (splat, enabled, ghost) in SystemAPI.Query<RefRO<GiSplat>, EnabledRefRO<GiSplat>, RefRW<GiGhostSplat>>()
                         .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
            {
                var next = new GiGhostSplat { Layer = splat.ValueRO.Layer.Index, Stamp = splat.ValueRO.Stamp.Index, Gain = splat.ValueRO.Gain, Enabled = enabled.ValueRO };
                if (ghost.ValueRO.Layer != next.Layer || ghost.ValueRO.Stamp != next.Stamp || ghost.ValueRO.Gain != next.Gain || ghost.ValueRO.Enabled != next.Enabled)
                    ghost.ValueRW = next;
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    public partial struct GiGhostClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (emitter, enabled, ghost) in SystemAPI.Query<RefRW<GiEmitter>, EnabledRefRW<GiEmitter>, RefRO<GiGhostEmitter>>()
                         .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
            {
                var next = new GiEmitter { Layer = new GiLayerKey(ghost.ValueRO.Layer), Stamp = new GiStampKey(ghost.ValueRO.Stamp), Gain = ghost.ValueRO.Gain };
                if (emitter.ValueRO.Layer.Index != next.Layer.Index || emitter.ValueRO.Stamp.Index != next.Stamp.Index || emitter.ValueRO.Gain != next.Gain)
                    emitter.ValueRW = next;
                if (enabled.ValueRO != ghost.ValueRO.Enabled) enabled.ValueRW = ghost.ValueRO.Enabled;
            }

            foreach (var (splat, enabled, ghost) in SystemAPI.Query<RefRW<GiSplat>, EnabledRefRW<GiSplat>, RefRO<GiGhostSplat>>()
                         .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
            {
                var next = new GiSplat { Layer = new GiLayerKey(ghost.ValueRO.Layer), Stamp = new GiStampKey(ghost.ValueRO.Stamp), Gain = ghost.ValueRO.Gain };
                if (splat.ValueRO.Layer.Index != next.Layer.Index || splat.ValueRO.Stamp.Index != next.Stamp.Index || splat.ValueRO.Gain != next.Gain)
                    splat.ValueRW = next;
                if (enabled.ValueRO != ghost.ValueRO.Enabled) enabled.ValueRW = ghost.ValueRO.Enabled;
            }
        }
    }
}
