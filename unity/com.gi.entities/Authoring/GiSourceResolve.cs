using System.Collections.Generic;

namespace Gi.Entities.Authoring
{
    public static class GiSourceResolve
    {
        public static bool Emitter(GiEmitterAuthoring authoring, out GiEmitterSettings settings, List<string> errors)
        {
            settings = default;
            if (!Keys(authoring.Profile, authoring.Layer, authoring.Stamp, out var layer, out var stamp, errors)) return false;
            if (!GiSourceCheck.TryEmitter(authoring.Layer.Kind, authoring.Gain, out var error))
            {
                errors.Add(error);
                return false;
            }

            settings = new GiEmitterSettings
            {
                Layer = layer,
                Stamp = stamp,
                Gain = (sbyte)authoring.Gain,
                RampTicks = authoring.RampTicks,
                Oriented = authoring.Oriented,
                Scaled = authoring.Scaled,
            };
            return true;
        }

        public static bool Splat(GiSplatAuthoring authoring, out GiSplatSettings settings, List<string> errors)
        {
            settings = default;
            if (!Keys(authoring.Profile, authoring.Layer, authoring.Stamp, out var layer, out var stamp, errors)) return false;
            if (!GiSourceCheck.TrySplat(authoring.Layer.Kind, authoring.Gain, out var error))
            {
                errors.Add(error);
                return false;
            }

            settings = new GiSplatSettings { Layer = layer, Stamp = stamp, Gain = (sbyte)authoring.Gain };
            return true;
        }

        private static bool Keys(GiProfile profile, GiLayerAsset layerAsset, GiStampAsset stampAsset, out GiLayerKey layer,
            out GiStampKey stamp, List<string> errors)
        {
            layer = default;
            stamp = default;
            if (profile == null)
            {
                errors.Add("No profile is assigned.");
                return false;
            }

            var layerIndex = profile.LayerIndex(layerAsset);
            var stampIndex = profile.StampIndex(stampAsset);
            if (layerIndex < 0) errors.Add(layerAsset == null ? "No layer is assigned." : "The layer is not in the profile's stack.");
            if (stampIndex < 0) errors.Add(stampAsset == null ? "No stamp is assigned." : "The stamp is not in the profile's stamp list.");
            if (layerIndex < 0 || stampIndex < 0) return false;

            layer = new GiLayerKey((byte)layerIndex);
            stamp = new GiStampKey((byte)stampIndex);
            return true;
        }
    }
}
