using Unity.Burst;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    [GiConfigurable]
    [NoAutoStaticsCleanup]
    public static class GiSenseConfig
    {
        private struct BatchKey
        {
        }

        [GiConfigVar("gi.sense.batch", 4096, "Rows per Gi column read batch, rounded down to a multiple of 64.")]
        public static readonly SharedStatic<int> Batch = SharedStatic<int>.GetOrCreate<BatchKey>();
    }
}
