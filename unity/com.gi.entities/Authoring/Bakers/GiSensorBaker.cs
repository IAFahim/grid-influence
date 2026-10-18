using System.Collections.Generic;
using BovineLabs.Core.Authoring.EntityCommands;
using Unity.Entities;

namespace Gi.Entities.Authoring
{
    public sealed class GiSensorBaker : Baker<GiSensorAuthoring>
    {
        public override void Bake(GiSensorAuthoring authoring)
        {
            DependsOn(authoring.Profile);
            DependsOn(authoring.Layer);

            var errors = new List<string>();
            var settings = Resolve(authoring, errors);
            if (errors.Count > 0)
            {
                GiBakerLog.Errors(authoring, "sensor", errors);
                return;
            }

            var entity = GetEntity(authoring.Moves ? TransformUsageFlags.Dynamic : TransformUsageFlags.Renderable);
            var commands = new BakerCommands(this, entity);
            GiSensorBuilder.Apply(ref commands, settings);
        }

        private static GiSensorSettings Resolve(GiSensorAuthoring authoring, List<string> errors)
        {
            var settings = new GiSensorSettings
            {
                Value = authoring.Value,
                ValueExcludeSelf = authoring.Value && authoring.ValueExcludeSelf,
                Gradient = authoring.Gradient,
                GradientExcludeSelf = authoring.Gradient && authoring.GradientExcludeSelf,
                Area = authoring.Area,
                AreaReach = authoring.AreaReach,
                AreaExcludeSelf = authoring.Area && authoring.AreaExcludeSelf,
                Max = authoring.Max,
                MaxReach = authoring.MaxReach,
                Nearest = authoring.Nearest,
                NearestReach = authoring.NearestReach,
                NearestThreshold = authoring.NearestThreshold,
            };

            if (authoring.Profile == null)
            {
                errors.Add("No profile is assigned.");
                return settings;
            }

            if (authoring.Profile.LayerIndex(authoring.Layer) < 0)
            {
                errors.Add(authoring.Layer == null ? "No layer is assigned." : "The layer is not in the profile's stack.");
                return settings;
            }

            Reach(settings.Area, settings.AreaReach, "area", errors);
            Reach(settings.Max, settings.MaxReach, "max", errors);
            Reach(settings.Nearest, settings.NearestReach, "nearest", errors);

            var transient = authoring.Layer.Kind == GiLayerKind.Transient;
            if (transient && settings.ValueExcludeSelf) errors.Add(Exclude());
            if (transient && settings.GradientExcludeSelf) errors.Add(Exclude());
            if (transient && settings.AreaExcludeSelf) errors.Add(Exclude());
            return settings;
        }

        private static void Reach(bool enabled, float reach, string what, List<string> errors)
        {
            if (enabled && !(reach > 0f && float.IsFinite(reach)))
                errors.Add("The " + what + " sensor needs a positive reach.");
        }

        private static string Exclude()
        {
            return GiBindingTable.Describe(GiBindingStatus.ExcludeOnTransient);
        }
    }
}
