using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Gi.Entities
{
    public struct GiSyncView
    {
        [ReadOnly] public ComponentTypeHandle<GiSource> Sources;
        [ReadOnly] public ComponentTypeHandle<GiOriented> Oriented;
        [ReadOnly] public ComponentTypeHandle<GiScaled> Scaled;
        [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> Matrices;
        [ReadOnly] public ComponentTypeHandle<Parent> Parents;

        public bool UsesMatrix(in ArchetypeChunk chunk)
        {
            return chunk.Has(ref Parents) || !chunk.Has(ref Transforms);
        }

        public bool Needs(in ArchetypeChunk chunk, uint lastVersion)
        {
            return UsesMatrix(chunk) ? chunk.DidChange(ref Matrices, lastVersion) : chunk.DidChange(ref Transforms, lastVersion);
        }

        public int Category(in ArchetypeChunk chunk)
        {
            return GiSyncPass.Category(chunk.Has(ref Oriented), chunk.Has(ref Scaled));
        }

        public int Placed(in ArchetypeChunk chunk)
        {
            var sources = chunk.GetNativeArray(ref Sources);
            var placed = 0;
            for (var i = 0; i < sources.Length; i++)
                if (sources[i].Placed)
                    placed++;
            return placed;
        }
    }
}
