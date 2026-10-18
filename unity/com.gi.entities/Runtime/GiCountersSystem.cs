using BovineLabs.Core;
using Unity.Entities;
using UnityEngine.Profiling;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GiSenseSystem))]
    public partial class GiCountersSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GiFrame>();
        }

        protected override void OnUpdate()
        {
            if (!Profiler.enabled) return;

            var frame = SystemAPI.GetSingleton<GiFrame>();
            var stats = SystemAPI.GetSingleton<GiStats>();
            GiCounters.Commands.Value = stats.Commands;
            GiCounters.Pulses.Value = stats.Pulses;
            GiCounters.Splats.Value = stats.Splats;
            GiCounters.ChangedTiles.Value = stats.ChangedTiles;
            GiCounters.ImpureReads.Value = 0;
            GiCounters.Faults.Value = GiFaultBits(frame.Faults.Raised);
            if (!frame.Alive) return;

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
