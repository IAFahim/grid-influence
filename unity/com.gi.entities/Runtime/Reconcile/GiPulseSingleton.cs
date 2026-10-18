using BovineLabs.Core.Collections;
using BovineLabs.Core.SingletonCollection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Gi.Entities
{
    public unsafe struct GiPulseSingleton : ISingletonCollection<NativeThreadStream>
    {
        public UnsafeList<NativeThreadStream>* Collections { get; set; }

        public Allocator Allocator { get; set; }
    }
}
