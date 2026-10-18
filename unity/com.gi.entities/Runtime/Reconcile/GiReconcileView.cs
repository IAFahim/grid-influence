using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;

namespace Gi.Entities
{
    public unsafe struct GiReconcileView
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<GiSource> Sources;
        [ReadOnly] public ComponentTypeHandle<GiEmitter> Emitters;
        [ReadOnly] public ComponentTypeHandle<GiGainRamp> Ramps;
        [ReadOnly] public ComponentTypeHandle<GiOriented> Oriented;
        [ReadOnly] public ComponentTypeHandle<GiScaled> Scaled;
        [ReadOnly] public ComponentTypeHandle<Disabled> Disabled;
        [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> Matrices;
        [ReadOnly] public ComponentTypeHandle<Parent> Parents;
        public ComponentTypeHandle<GiFault> Faults;

        public int Visit(in ArchetypeChunk chunk, in GiReconcileContext context, uint lastVersion, int baseIndex,
            GiCommand* commands, Entity* owners)
        {
            if (!context.Alive) return 0;

            var hasEmitter = chunk.Has(ref Emitters);
            if (hasEmitter && !chunk.DidChange(ref Emitters, lastVersion) && !chunk.DidChange(ref Sources, lastVersion) &&
                !chunk.DidOrderChange(lastVersion))
                return 0;

            var sources = chunk.GetNativeArray(ref Sources);
            var emitters = hasEmitter ? chunk.GetNativeArray(ref Emitters) : default;
            var enabled = hasEmitter ? chunk.GetEnabledMask(ref Emitters) : default;
            var disabled = chunk.Has(ref Disabled);
            var ramps = chunk.Has(ref Ramps) ? chunk.GetNativeArray(ref Ramps) : default;
            var oriented = chunk.Has(ref Oriented);
            var scaled = chunk.Has(ref Scaled);
            var useMatrix = chunk.Has(ref Parents) || !chunk.Has(ref Transforms);
            var transforms = !useMatrix ? chunk.GetNativeArray(ref Transforms) : default;
            var matrices = useMatrix && chunk.Has(ref Matrices) ? chunk.GetNativeArray(ref Matrices) : default;
            var entities = chunk.GetNativeArray(Entities);
            var hasFault = commands != null && chunk.Has(ref Faults);
            var faults = hasFault ? chunk.GetNativeArray(ref Faults) : default;

            var count = 0;
            for (var i = 0; i < chunk.Count; i++)
            {
                var desired = default(GiDesired);
                desired.Want = hasEmitter && !disabled && enabled[i];
                if (desired.Want)
                {
                    desired.Layer = emitters[i].Layer;
                    desired.Stamp = emitters[i].Stamp;
                    desired.Gain = emitters[i].Gain;
                    desired.Ramp = ramps.IsCreated ? ramps[i].Ticks : (ushort)0;
                    desired.Oriented = oriented;
                    desired.Scaled = scaled;
                    GiPose.Read(context.Plane, useMatrix, transforms, matrices, i, out desired.X, out desired.Y, out desired.Heading, out desired.Scale);
                }

                var source = sources[i];
                var applied = new GiApplied
                {
                    Placed = source.Placed,
                    Layer = source.Layer,
                    Stamp = source.Stamp,
                    Gain = source.Gain,
                    Source = source.Id,
                };

                var plan = GiDiff.Plan(desired, applied, context.Tables, 0, baseIndex + count);
                if (commands != null)
                {
                    for (var k = 0; k < plan.Count; k++)
                    {
                        commands[baseIndex + count + k] = plan[k];
                        owners[baseIndex + count + k] = entities[i];
                    }

                    if (hasFault && plan.Fault != GiFaultCode.None)
                    {
                        faults[i] = new GiFault { Code = plan.Fault };
                        chunk.SetComponentEnabled(ref Faults, i, true);
                    }
                }

                count += plan.Count;
            }

            return count;
        }
    }
}
