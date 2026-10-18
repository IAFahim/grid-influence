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
        public GiSenseField Reach;
        public GiSenseField Threshold;

        public bool HasOutput => Value.Active || Gradient.Active || Total.Active || Position.Active;

        public bool HasInput => Reach.Active || Threshold.Active;
    }

    internal sealed class GiSenseBinding
    {
        public GiBindingSpec Spec;
        public GiSenseNeeds Needs;
        public int Period;
        public EntityQuery Query;
        public GiSenseType[] Types;
    }
}
