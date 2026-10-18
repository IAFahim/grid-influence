using BovineLabs.Core.EntityCommands;

namespace Gi.Entities
{
    public struct GiSensorSettings
    {
        public bool Value;
        public bool ValueExcludeSelf;
        public bool Gradient;
        public bool GradientExcludeSelf;
        public bool Area;
        public float AreaReach;
        public bool AreaExcludeSelf;
        public bool Max;
        public float MaxReach;
        public bool Nearest;
        public float NearestReach;
        public short NearestThreshold;
    }

    public static class GiSensorBuilder
    {
        public static void Apply<T>(ref T commands, in GiSensorSettings settings)
            where T : struct, IEntityCommands
        {
            if (settings.Value) commands.AddComponent(new GiSensorValue { ExcludeSelf = settings.ValueExcludeSelf });
            if (settings.Gradient) commands.AddComponent(new GiSensorGradient { ExcludeSelf = settings.GradientExcludeSelf });
            if (settings.Area) commands.AddComponent(new GiSensorArea { Reach = settings.AreaReach, ExcludeSelf = settings.AreaExcludeSelf });
            if (settings.Max) commands.AddComponent(new GiSensorMax { Reach = settings.MaxReach });
            if (settings.Nearest)
            {
                commands.AddComponent(new GiSensorNearest { Reach = settings.NearestReach, Threshold = settings.NearestThreshold });
                commands.AddComponent(new GiSensorFound());
                commands.SetComponentEnabled<GiSensorFound>(false);
            }
        }
    }
}
