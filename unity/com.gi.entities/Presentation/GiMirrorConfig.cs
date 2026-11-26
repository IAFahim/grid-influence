using Unity.Burst;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities.Presentation
{
    [GiConfigurable]
    [NoAutoStaticsCleanup]
    public static class GiMirrorConfig
    {
        private struct BudgetKey
        {
        }

        [GiConfigVar("gi.mirror.budget", 128, "Tiles uploaded to a Gi GPU mirror per frame; the rest carries over to the next frame.")]
        public static readonly SharedStatic<int> Budget = SharedStatic<int>.GetOrCreate<BudgetKey>();
    }
}
