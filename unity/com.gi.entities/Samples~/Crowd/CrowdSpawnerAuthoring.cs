using Gi.Entities.Authoring;
using UnityEngine;

namespace Gi.Entities.Samples.Crowd
{
    public sealed class CrowdSpawnerAuthoring : MonoBehaviour
    {
        public GiProfile Profile;
        public GiLayerAsset Layer;
        public GiStampAsset Stamp;
        [Range(1, 1000000)] public int Count = 4000;
        public uint Seed = 7;
        public float Radius = 56f;
        [Range(1, 16)] public int Gain = 3;
        [Range(0f, 8f)] public float Speed = 2.5f;
    }
}
