using BovineLabs.Core.ConfigVars;
using Unity.Burst;

namespace Gi.Entities
{
    [Configurable]
    public static class GiSenseConfig
    {
        private struct BatchKey
        {
        }

        [ConfigVar("gi.sense.batch", 4096, "Rows per Gi column read batch, rounded down to a multiple of 64.")]
        public static readonly SharedStatic<int> Batch = SharedStatic<int>.GetOrCreate<BatchKey>();
    }
}
