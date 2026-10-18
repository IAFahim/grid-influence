using Unity.Entities;

namespace Gi.Entities
{
    public struct GiProfileBlob
    {
        public ulong Hash;
        public GiPlaneSpec Plane;
        public BlobArray<GiGridSpec> Grids;
        public BlobArray<GiLayerSpec> Layers;
        public BlobArray<GiStampSpec> Stamps;
        public BlobArray<sbyte> Cells;
        public BlobArray<GiBindingSpec> Bindings;
    }

    public struct GiProfileReference : IComponentData
    {
        public BlobAssetReference<GiProfileBlob> Value;
    }
}
