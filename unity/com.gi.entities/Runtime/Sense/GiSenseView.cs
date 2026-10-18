using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Gi.Entities
{
    public struct GiSenseView
    {
        [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> Matrices;
        [ReadOnly] public ComponentTypeHandle<Parent> Parents;
        [ReadOnly] public ComponentTypeHandle<GiSource> Sources;
        [ReadOnly] public EntityTypeHandle Entities;

        public bool Eligible(in ArchetypeChunk chunk)
        {
            return chunk.Has(ref Transforms) || chunk.Has(ref Matrices);
        }

        public bool UsesMatrix(in ArchetypeChunk chunk)
        {
            return chunk.Has(ref Parents) || !chunk.Has(ref Transforms);
        }

        public ChunkEntityEnumerator Rows(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            return new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
        }

        public NativeArray<Entity> EntitiesOf(in ArchetypeChunk chunk)
        {
            return chunk.GetNativeArray(Entities);
        }
    }
}
