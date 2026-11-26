using Unity.Burst;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    [GiConfigurable]
    [NoAutoStaticsCleanup]
    public static class GiRuntimeConfig
    {
        private struct KeepOrphansKey
        {
        }

        [GiConfigVar("gi.runtime.keep-orphans", false, "Do not free Gi worlds and stamps that no adapter instance owns when code loads.")]
        public static readonly SharedStatic<bool> KeepOrphans = SharedStatic<bool>.GetOrCreate<KeepOrphansKey>();
    }
}
