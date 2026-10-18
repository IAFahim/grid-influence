using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Gi.Entities
{
    public unsafe struct GiSplatView
    {
        [ReadOnly] public ComponentTypeHandle<GiSplat> Splats;
        [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> Matrices;
        [ReadOnly] public ComponentTypeHandle<Parent> Parents;
        public ComponentTypeHandle<GiFault> Faults;

        public void Visit(in ArchetypeChunk chunk, in GiReconcileContext context, int* counts, int* cursor, float* xs, float* ys,
            byte* stamps, sbyte* gains)
        {
            if (!context.Alive) return;

            var splats = chunk.GetNativeArray(ref Splats);
            var enabled = chunk.GetEnabledMask(ref Splats);
            var useMatrix = chunk.Has(ref Parents) || !chunk.Has(ref Transforms);
            var transforms = !useMatrix ? chunk.GetNativeArray(ref Transforms) : default;
            var matrices = useMatrix && chunk.Has(ref Matrices) ? chunk.GetNativeArray(ref Matrices) : default;
            var writing = xs != null;
            var hasFault = writing && chunk.Has(ref Faults);
            var faults = hasFault ? chunk.GetNativeArray(ref Faults) : default;
            var faultMask = hasFault ? chunk.GetEnabledMask(ref Faults) : default;

            for (var i = 0; i < chunk.Count; i++)
            {
                if (!enabled[i]) continue;

                var splat = splats[i];
                var fault = GiSplatPass.Check(context.Tables, splat.Layer, splat.Stamp, out var stamp);
                if (fault != GiFaultCode.None)
                {
                    if (hasFault)
                    {
                        faults[i] = new GiFault { Code = fault };
                        chunk.SetComponentEnabled(ref Faults, i, true);
                    }

                    continue;
                }

                if (!writing)
                {
                    counts[splat.Layer.Index]++;
                    continue;
                }

                GiPose.Read(context.Plane, useMatrix, transforms, matrices, i, out var x, out var y, out _, out _);
                var row = cursor[splat.Layer.Index]++;
                xs[row] = x;
                ys[row] = y;
                stamps[row] = stamp;
                gains[row] = splat.Gain;
                if (hasFault && faultMask[i]) chunk.SetComponentEnabled(ref Faults, i, false);
            }
        }
    }
}
