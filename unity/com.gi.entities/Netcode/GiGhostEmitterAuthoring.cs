using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Netcode
{
    [DisallowMultipleComponent]
    public sealed class GiGhostEmitterAuthoring : MonoBehaviour
    {
    }

    public sealed class GiGhostEmitterBaker : Baker<GiGhostEmitterAuthoring>
    {
        public override void Bake(GiGhostEmitterAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GiGhostEmitter { Enabled = true });
        }
    }

    [DisallowMultipleComponent]
    public sealed class GiGhostSplatAuthoring : MonoBehaviour
    {
    }

    public sealed class GiGhostSplatBaker : Baker<GiGhostSplatAuthoring>
    {
        public override void Bake(GiGhostSplatAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GiGhostSplat { Enabled = true });
        }
    }
}
