using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Gi.Entities
{
    public unsafe struct GiPulseSingleton : IComponentData
    {
        public NativeQueue<GiPulse> Queue;
        [NativeDisableUnsafePtrRestriction] internal int* Pending;

        public NativeQueue<GiPulse>.ParallelWriter CreateWriter()
        {
            if (Pending != null) *Pending = 1;
            return Queue.AsParallelWriter();
        }
    }
}
