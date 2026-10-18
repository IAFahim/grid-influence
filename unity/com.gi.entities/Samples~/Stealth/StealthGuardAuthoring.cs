using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Samples.Stealth
{
    public sealed class StealthGuardAuthoring : MonoBehaviour
    {
        [Range(4f, 60f)] public float Hearing = 26f;
        [Range(-90f, 90f)] public float SweepRate = 40f;

        public sealed class StealthGuardBaker : Baker<StealthGuardAuthoring>
        {
            public override void Bake(StealthGuardAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new StealthGuard
                {
                    Hearing = authoring.Hearing,
                    SweepRate = authoring.SweepRate,
                    SweepPhase = authoring.transform.position.x * 0.37f + authoring.transform.position.z,
                });
                AddComponent<StealthAlert>(entity);
                SetComponentEnabled<StealthAlert>(false);
            }
        }
    }
}
