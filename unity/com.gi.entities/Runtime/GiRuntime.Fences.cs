using Unity.Burst;
using Unity.Jobs;

namespace Gi.Entities
{
    public static partial class GiRuntime
    {
        private struct WritersKey
        {
        }

        private struct AllKey
        {
        }

        private static readonly SharedStatic<JobHandle> Writers = SharedStatic<JobHandle>.GetOrCreate<WritersKey>();
        private static readonly SharedStatic<JobHandle> All = SharedStatic<JobHandle>.GetOrCreate<AllKey>();

        public static JobHandle BeginWrite(JobHandle dependency)
        {
            return JobHandle.CombineDependencies(dependency, All.Data);
        }

        public static void EndWrite(JobHandle job)
        {
            Writers.Data = job;
            All.Data = job;
        }

        public static JobHandle BeginRead(JobHandle dependency)
        {
            return JobHandle.CombineDependencies(dependency, Writers.Data);
        }

        public static void EndRead(JobHandle job)
        {
            All.Data = JobHandle.CombineDependencies(All.Data, job);
        }

        public static void CompleteAll()
        {
            All.Data.Complete();
            Writers.Data = default;
            All.Data = default;
        }
    }
}
