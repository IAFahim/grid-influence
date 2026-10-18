using BovineLabs.Core.ConfigVars;
using Unity.Burst;

namespace Gi.Entities
{
    [Configurable]
    public static class GiRuntimeConfig
    {
        private struct KeepOrphansKey
        {
        }

        [ConfigVar("gi.runtime.keep-orphans", false, "Do not free Gi worlds and stamps that no adapter instance owns when code loads.")]
        public static readonly SharedStatic<bool> KeepOrphans = SharedStatic<bool>.GetOrCreate<KeepOrphansKey>();
    }
}
