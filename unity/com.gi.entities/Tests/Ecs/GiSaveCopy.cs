using System.Collections.Generic;
using Unity.Entities;
using Unity.Transforms;

namespace Gi.Entities.Tests
{
    public static class GiSaveCopy
    {
        public static Entity Emitter(EntityManager from, Entity entity, EntityManager to)
        {
            var types = new List<ComponentType> { typeof(GiEmitter), typeof(GiFault), typeof(LocalTransform) };
            var ramp = from.HasComponent<GiGainRamp>(entity);
            var oriented = from.HasComponent<GiOriented>(entity);
            var scaled = from.HasComponent<GiScaled>(entity);
            var saved = from.HasComponent<GiSavedSource>(entity);
            if (ramp) types.Add(typeof(GiGainRamp));
            if (oriented) types.Add(typeof(GiOriented));
            if (scaled) types.Add(typeof(GiScaled));
            if (saved) types.Add(typeof(GiSavedSource));

            var copy = to.CreateEntity(to.CreateArchetype(types.ToArray()));
            to.SetComponentData(copy, from.GetComponentData<GiEmitter>(entity));
            to.SetComponentEnabled<GiEmitter>(copy, from.IsComponentEnabled<GiEmitter>(entity));
            to.SetComponentEnabled<GiFault>(copy, false);
            to.SetComponentData(copy, from.GetComponentData<LocalTransform>(entity));
            if (ramp) to.SetComponentData(copy, from.GetComponentData<GiGainRamp>(entity));
            if (saved) to.SetComponentData(copy, from.GetComponentData<GiSavedSource>(entity));
            return copy;
        }

        public static Entity Splat(EntityManager from, Entity entity, EntityManager to)
        {
            var copy = to.CreateEntity(typeof(GiSplat), typeof(GiFault), typeof(LocalTransform));
            to.SetComponentData(copy, from.GetComponentData<GiSplat>(entity));
            to.SetComponentEnabled<GiSplat>(copy, from.IsComponentEnabled<GiSplat>(entity));
            to.SetComponentEnabled<GiFault>(copy, false);
            to.SetComponentData(copy, from.GetComponentData<LocalTransform>(entity));
            return copy;
        }
    }
}
