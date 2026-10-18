using System;

namespace Gi.Entities
{
    public enum GiBindingStatus : byte
    {
        Valid,
        LayerOutOfRange,
        MissingOutput,
        WrongFieldSize,
        UnexpectedField,
        DuplicateWriter,
        ExcludeUnavailable,
        Unsupported,
    }

    [Flags]
    public enum GiBindingSupport : byte
    {
        None = 0,
        Basic = 1,
        Extended = 2,
        Exclusion = 4,
        All = Basic | Extended | Exclusion,
    }

    public static class GiBindingTable
    {
        public const int ValueSize = 2;
        public const int VectorSize = 8;
        public const int TotalSize = 8;

        public static int Validate(ReadOnlySpan<GiBindingSpec> bindings, int layerCount, GiBindingSupport support,
            Span<GiBindingStatus> statuses)
        {
            var invalid = 0;
            for (var i = 0; i < bindings.Length; i++)
            {
                statuses[i] = Check(bindings[i], layerCount, support);
                if (statuses[i] == GiBindingStatus.Valid) statuses[i] = Duplicate(bindings, statuses, i);
                if (statuses[i] != GiBindingStatus.Valid) invalid++;
            }

            return invalid;
        }

        public static string Describe(GiBindingStatus status)
        {
            switch (status)
            {
                case GiBindingStatus.Valid:
                    return "The binding is valid.";
                case GiBindingStatus.LayerOutOfRange:
                    return "The binding reads a layer that the profile does not have.";
                case GiBindingStatus.MissingOutput:
                    return "The binding has no output field for a value its kind produces.";
                case GiBindingStatus.WrongFieldSize:
                    return "An output field has the wrong size for what the binding writes into it.";
                case GiBindingStatus.UnexpectedField:
                    return "The binding names an output field that its kind never writes.";
                case GiBindingStatus.DuplicateWriter:
                    return "Another binding writes the same output field.";
                case GiBindingStatus.ExcludeUnavailable:
                    return "Excluding the entity's own source is not possible for this binding kind.";
                default:
                    return "This binding kind is not supported by the running Gi adapter.";
            }
        }

        public static bool NeedsPosition(GiBindingKind kind)
        {
            return kind == GiBindingKind.Max || kind == GiBindingKind.Nearest;
        }

        private static GiBindingStatus Check(in GiBindingSpec binding, int layerCount, GiBindingSupport support)
        {
            if (!Supported(binding.Kind, support)) return GiBindingStatus.Unsupported;
            if (binding.Layer >= layerCount) return GiBindingStatus.LayerOutOfRange;

            var exclude = (binding.Flags & GiBindingSpec.ExcludeSelf) != 0;
            if (exclude && !ExcludeAllowed(binding.Kind)) return GiBindingStatus.ExcludeUnavailable;
            if (exclude && (support & GiBindingSupport.Exclusion) == 0) return GiBindingStatus.Unsupported;

            var wantsValue = binding.Kind == GiBindingKind.Value || binding.Kind == GiBindingKind.ValueGradient ||
                             binding.Kind == GiBindingKind.Max || binding.Kind == GiBindingKind.Nearest;
            var wantsGradient = binding.Kind == GiBindingKind.Gradient || binding.Kind == GiBindingKind.ValueGradient;
            var wantsTotal = binding.Kind == GiBindingKind.Area;
            var wantsPosition = NeedsPosition(binding.Kind);
            var wantsCoverage = binding.Kind == GiBindingKind.Changed;

            var status = Field(binding.Value, wantsValue, ValueSize);
            if (status == GiBindingStatus.Valid) status = Field(binding.Gradient, wantsGradient, VectorSize);
            if (status == GiBindingStatus.Valid) status = Field(binding.Total, wantsTotal, TotalSize);
            if (status == GiBindingStatus.Valid) status = Field(binding.Position, wantsPosition, VectorSize);
            if (status == GiBindingStatus.Valid && wantsCoverage && !Present(binding.Coverage)) status = GiBindingStatus.MissingOutput;
            return status;
        }

        private static bool Supported(GiBindingKind kind, GiBindingSupport support)
        {
            var basic = kind == GiBindingKind.Value || kind == GiBindingKind.Gradient || kind == GiBindingKind.ValueGradient;
            return basic ? (support & GiBindingSupport.Basic) != 0 : (support & GiBindingSupport.Extended) != 0;
        }

        private static bool ExcludeAllowed(GiBindingKind kind)
        {
            return kind == GiBindingKind.Value || kind == GiBindingKind.Gradient || kind == GiBindingKind.ValueGradient ||
                   kind == GiBindingKind.Area;
        }

        private static bool Present(in GiBindingField field)
        {
            return field.TypeHash != 0ul;
        }

        private static GiBindingStatus Field(in GiBindingField field, bool wanted, int size)
        {
            if (!wanted) return Present(field) ? GiBindingStatus.UnexpectedField : GiBindingStatus.Valid;
            if (!Present(field)) return GiBindingStatus.MissingOutput;
            return field.Size == size ? GiBindingStatus.Valid : GiBindingStatus.WrongFieldSize;
        }

        private static GiBindingStatus Duplicate(ReadOnlySpan<GiBindingSpec> bindings, Span<GiBindingStatus> statuses, int index)
        {
            for (var earlier = 0; earlier < index; earlier++)
            {
                if (statuses[earlier] != GiBindingStatus.Valid) continue;
                if (Collides(bindings[earlier], bindings[index])) return GiBindingStatus.DuplicateWriter;
            }

            return GiBindingStatus.Valid;
        }

        private static bool Collides(in GiBindingSpec a, in GiBindingSpec b)
        {
            return Collides(a.Value, b.Value) || Collides(a.Value, b.Gradient) || Collides(a.Value, b.Total) ||
                   Collides(a.Value, b.Position) || Collides(a.Value, b.Coverage) ||
                   Collides(a.Gradient, b.Value) || Collides(a.Gradient, b.Gradient) || Collides(a.Gradient, b.Total) ||
                   Collides(a.Gradient, b.Position) || Collides(a.Gradient, b.Coverage) ||
                   Collides(a.Total, b.Value) || Collides(a.Total, b.Gradient) || Collides(a.Total, b.Total) ||
                   Collides(a.Total, b.Position) || Collides(a.Total, b.Coverage) ||
                   Collides(a.Position, b.Value) || Collides(a.Position, b.Gradient) || Collides(a.Position, b.Total) ||
                   Collides(a.Position, b.Position) || Collides(a.Position, b.Coverage) ||
                   Collides(a.Coverage, b.Value) || Collides(a.Coverage, b.Gradient) || Collides(a.Coverage, b.Total) ||
                   Collides(a.Coverage, b.Position) || Collides(a.Coverage, b.Coverage);
        }

        private static bool Collides(in GiBindingField a, in GiBindingField b)
        {
            if (!Present(a) || !Present(b) || a.TypeHash != b.TypeHash) return false;
            if (a.Size == 0 || b.Size == 0) return true;
            return a.Offset < b.Offset + b.Size && b.Offset < a.Offset + a.Size;
        }
    }
}
