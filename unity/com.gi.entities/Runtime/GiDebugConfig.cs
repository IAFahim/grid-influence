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

        [ConfigVar("gi.guard", true, "In checked builds, report two Gi systems that read and write one Gi world at once.")]
        public static readonly SharedStatic<bool> Guard = SharedStatic<bool>.GetOrCreate<GuardKey>();

        [ConfigVar("gi.draw.heatmap", true, "Draw the Gi heatmap in the Scene view overlay and the Field window.")]
        public static readonly SharedStatic<bool> Heatmap = SharedStatic<bool>.GetOrCreate<HeatmapKey>();
    }
}
