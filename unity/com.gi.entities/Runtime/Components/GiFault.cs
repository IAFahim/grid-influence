using Unity.Entities;

namespace Gi.Entities
{
    public struct GiFault : IComponentData, IEnableableComponent
    {
        public GiFaultCode Code;
    }
}
