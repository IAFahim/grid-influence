using Gi.Validation;

namespace Gi.Entities
{
    public static class GiSourceCheck
    {
        public const string EmitterResidency = "An emitter's layer must be a persistent base layer.";
        public const string SplatResidency = "A splat's layer must be a transient base layer.";

        public static bool TryEmitter(GiLayerKind layer, int gain, out string error)
        {
            if (layer != GiLayerKind.Persistent)
            {
                error = EmitterResidency;
                return false;
            }

            return GiValidation.TryGain(gain, out error);
        }

        public static bool TrySplat(GiLayerKind layer, int gain, out string error)
        {
            if (layer != GiLayerKind.Transient)
            {
                error = SplatResidency;
                return false;
            }

            return GiValidation.TryGain(gain, out error);
        }
    }
}
