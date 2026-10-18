using Unity.Jobs;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    public static partial class GiRuntime
    {
        [NoAutoStaticsCleanup]
        public static readonly GiStampRegistry Stamps = new GiStampRegistry();

        private static JobHandle _writers;
        private static JobHandle _all;

        public static JobHandle BeginWrite(JobHandle dependency)
        {
            return JobHandle.CombineDependencies(dependency, _all);
        }

        public static void EndWrite(JobHandle job)
        {
            _writers = job;
            _all = job;
        }

        public static JobHandle BeginRead(JobHandle dependency)
        {
            return JobHandle.CombineDependencies(dependency, _writers);
        }

        public static void EndRead(JobHandle job)
        {
            _all = JobHandle.CombineDependencies(_all, job);
        }

        public static void CompleteAll()
        {
            _all.Complete();
            _writers = default;
            _all = default;
        }

        [OnCodeUnloading]
        private static void Unload()
        {
            CompleteAll();
            GiOrphans.Reclaim(Stamps);
        }

        [OnCodeLoaded]
        private static void Load()
        {
            _writers = default;
            _all = default;
            if (!GiRuntimeConfig.KeepOrphans.Data) GiOrphans.Reclaim(Stamps);
        }
    }
}
