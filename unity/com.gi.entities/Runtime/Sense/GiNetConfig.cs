using Unity.Burst;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    [GiConfigurable]
    [NoAutoStaticsCleanup]
    public static class GiNetConfig
    {
        private struct CheckKey
        {
        }

        [GiConfigVar("gi.net.check", 0, "Ticks between lockstep field hashes sent to clients; 0 turns the check off.")]
        public static readonly SharedStatic<int> Check = SharedStatic<int>.GetOrCreate<CheckKey>();
    }
}
