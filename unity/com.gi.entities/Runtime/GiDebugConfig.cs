using BovineLabs.Core.ConfigVars;
using Unity.Burst;

namespace Gi.Entities
{
    [Configurable]
    public static class GiDebugConfig
    {
        private struct GuardKey
        {
        }

        private struct HeatmapKey
        {
        }

        private struct RecordKey
        {
        }

        private struct RecordTicksKey
        {
        }

        private struct RecordKeyframeKey
        {
        }

        [ConfigVar("gi.guard", true, "In checked builds, report two Gi systems that read and write one Gi world at once.")]
        public static readonly SharedStatic<bool> Guard = SharedStatic<bool>.GetOrCreate<GuardKey>();

        [ConfigVar("gi.draw.heatmap", true, "Draw the Gi heatmap in the Scene view overlay and the Field window.")]
        public static readonly SharedStatic<bool> Heatmap = SharedStatic<bool>.GetOrCreate<HeatmapKey>();

        [ConfigVar("gi.record", false, "Record every Gi input of every tick into a ring buffer so a range can be replayed, diffed and exported.")]
        public static readonly SharedStatic<bool> Record = SharedStatic<bool>.GetOrCreate<RecordKey>();

        [ConfigVar("gi.record.ticks", 600, "Ticks the flight recorder keeps.")]
        public static readonly SharedStatic<int> RecordTicks = SharedStatic<int>.GetOrCreate<RecordTicksKey>();

        [ConfigVar("gi.record.keyframe", 120, "Ticks between flight recorder keyframes.")]
        public static readonly SharedStatic<int> RecordKeyframe = SharedStatic<int>.GetOrCreate<RecordKeyframeKey>();
    }
}
