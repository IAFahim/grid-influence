using Unity.Entities;

namespace Gi.Entities
{
    public enum GiFaultCode : byte
    {
        None,
        InvalidSource,
        InvalidLayer,
        InvalidStamp,
        SourceLimit,
        NonFinite,
        WrongResidency,
        Capacity,
        StaleSource,
        Unknown,
    }

    public struct GiFault : IComponentData, IEnableableComponent
    {
        public GiFaultCode Code;
    }
}
