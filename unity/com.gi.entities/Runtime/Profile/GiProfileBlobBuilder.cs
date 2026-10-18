using System;
using Unity.Collections;
using Unity.Entities;

namespace Gi.Entities
{
    public static class GiProfileBlobBuilder
    {
        public static BlobAssetReference<GiProfileBlob> Create(GiProfileSpec spec, Allocator allocator)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            try
            {
                ref var root = ref builder.ConstructRoot<GiProfileBlob>();
                root.Hash = spec.Hash;
                root.Plane = spec.Plane;
                Fill(ref builder, ref root.Grids, spec.Grids);
                Fill(ref builder, ref root.Layers, spec.Layers);
                Fill(ref builder, ref root.Stamps, spec.Stamps);
                Fill(ref builder, ref root.Cells, spec.Cells);
                Fill(ref builder, ref root.Bindings, spec.Bindings);
                return builder.CreateBlobAssetReference<GiProfileBlob>(allocator);
            }
            finally
            {
                builder.Dispose();
            }
        }

        public static GiProfileSpec ToSpec(ref GiProfileBlob blob)
        {
            return new GiProfileSpec
            {
                Plane = blob.Plane,
                Grids = blob.Grids.ToArray(),
                Layers = blob.Layers.ToArray(),
                Stamps = blob.Stamps.ToArray(),
                Cells = blob.Cells.ToArray(),
                Bindings = blob.Bindings.ToArray(),
            };
        }

        private static void Fill<T>(ref BlobBuilder builder, ref BlobArray<T> array, T[] values)
            where T : unmanaged
        {
            var target = builder.Allocate(ref array, values.Length);
            for (var i = 0; i < values.Length; i++) target[i] = values[i];
        }
    }
}
