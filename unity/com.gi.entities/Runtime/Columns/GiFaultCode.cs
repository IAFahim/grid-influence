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
}
