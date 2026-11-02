namespace Gi.Entities
{
    public enum GiProfileEditKind : byte
    {
        Rebuild,
        BindingsOnly,
    }

    public static class GiProfileEdit
    {
        public static GiProfileEditKind Classify(ulong builtStates, ulong builtBindings, ulong states, ulong bindings)
        {
            if (states == builtStates && bindings != builtBindings) return GiProfileEditKind.BindingsOnly;
            return GiProfileEditKind.Rebuild;
        }
    }
}
