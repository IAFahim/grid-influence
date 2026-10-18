using UnityEngine;

namespace Gi.Entities.Authoring
{
    public sealed class GiSplatAuthoring : MonoBehaviour
    {
        public GiProfile Profile;
        public GiLayerAsset Layer;
        public GiStampAsset Stamp;
        [Range(-16, 16)] public int Gain = 4;
        public bool Moves = true;
    }
}
