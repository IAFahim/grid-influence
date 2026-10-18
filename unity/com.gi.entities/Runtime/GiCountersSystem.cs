using BovineLabs.Core;
using Unity.Entities;
using UnityEngine.Profiling;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GiSenseSystem))]
    public partial struct GiCountersSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!Profiler.enabled) return;

            var entity = SystemAPI.GetSingletonEntity<GiFrame>();
            var frame = state.EntityManager.GetComponentData<GiFrame>(entity);
            if (!frame.Alive) return;

            var stats = state.EntityManager.GetComponentData<GiStats>(entity);
            GiCounters.Commands.Value = stats.Commands;
            GiCounters.Pulses.Value = stats.Pulses;
            GiCounters.Splats.Value = stats.Splats;
            GiCounters.ChangedTiles.Value = stats.ChangedTiles;
            GiCounters.ImpureReads.Value = 0;
            GiCounters.Faults.Value = GiFaultBits(frame.Faults.Raised);
            GiWorld.Memory(frame.World, out var bytes, out _, out _, out _, out var sources);
            GiCounters.Sources.Value = sources;
            GiCounters.Bytes.Value = bytes;
        }

        private static int GiFaultBits(ushort raised)
        {
            var count = 0;
            for (var bits = (uint)raised; bits != 0u; bits &= bits - 1u) count++;
            return count;
        }
    }
}
