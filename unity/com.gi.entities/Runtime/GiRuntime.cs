using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    public static partial class GiRuntime
    {
        [NoAutoStaticsCleanup]
        public static readonly GiStampRegistry Stamps = new GiStampRegistry();

        internal static unsafe Gi.GiRoot* RootPtr
        {
            get
            {
                fixed (Gi.GiRoot* root = &Gi.Runtime.Root) return root;
            }
        }

        [OnCodeUnloading]
        private static void Unload()
        {
            CompleteAll();
            GiOrphans.Reclaim(Stamps);
            GiGuard.Destroy();
        }

        [OnCodeLoaded]
        private static void Load()
        {
            Writers.Data = default;
            All.Data = default;
            GiGuard.Create();
            if (!GiRuntimeConfig.KeepOrphans.Data) GiOrphans.Reclaim(Stamps);
        }
    }
}
