using BovineLabs.Core.ConfigVars;
using Unity.Burst;

namespace Gi.Entities.Presentation
{
    [Configurable]
    public static class GiMirrorConfig
    {
        private struct BudgetKey
        {
        }

        [ConfigVar("gi.mirror.budget", 128, "Tiles uploaded to a Gi GPU mirror per frame; the rest carries over to the next frame.")]
        public static readonly SharedStatic<int> Budget = SharedStatic<int>.GetOrCreate<BudgetKey>();
    }
}
