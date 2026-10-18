using UnityEngine;

namespace Gi.Entities.Authoring
{
    public sealed class GiSensorAuthoring : MonoBehaviour
    {
        public GiProfile Profile;
        public GiLayerAsset Layer;
        public bool Moves = true;

        public bool Value;
        public bool ValueExcludeSelf;
        public bool Gradient;
        public bool GradientExcludeSelf;
        public bool Area;
        [Range(1f, 128f)] public float AreaReach = 16f;
        public bool AreaExcludeSelf;
        public bool Max;
        [Range(1f, 128f)] public float MaxReach = 16f;
        public bool Nearest;
        [Range(1f, 128f)] public float NearestReach = 16f;
        public short NearestThreshold;
    }
}
