using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    public static partial class GiRuntime
    {
        [NoAutoStaticsCleanup]
        public static readonly GiStampRegistry Stamps = new GiStampRegistry();

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
