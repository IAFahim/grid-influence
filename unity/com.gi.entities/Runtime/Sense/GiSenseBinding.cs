using Unity.Entities;

namespace Gi.Entities
{
    internal struct GiSenseField
    {
        public bool Active;
        public ushort Offset;
        public ushort Size;
    }

    internal sealed class GiSenseType
    {
        public TypeIndex Index;
        public int Size;
        public DynamicComponentTypeHandle Handle;
        public GiSenseField Value;
        public GiSenseField Gradient;
        public GiSenseField Total;
        public GiSenseField Position;
        public GiSenseField Coverage;
    }

    internal sealed class GiSenseBinding
    {
        public GiBindingSpec Spec;
        public EntityQuery Query;
        public GiSenseType[] Types;
    }
}
