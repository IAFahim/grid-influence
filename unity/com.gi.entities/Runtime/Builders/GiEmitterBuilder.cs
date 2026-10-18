using BovineLabs.Core.EntityCommands;

namespace Gi.Entities
{
    public struct GiEmitterSettings
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
        public ushort RampTicks;
        public bool Oriented;
        public bool Scaled;
    }

    public static class GiEmitterBuilder
    {
        public static void Apply<T>(ref T commands, in GiEmitterSettings settings)
            where T : struct, IEntityCommands
        {
            commands.AddComponent(new GiEmitter { Layer = settings.Layer, Stamp = settings.Stamp, Gain = settings.Gain });
            commands.AddComponent(new GiFault());
            commands.SetComponentEnabled<GiFault>(false);
            if (settings.RampTicks > 0) commands.AddComponent(new GiGainRamp { Ticks = settings.RampTicks });
            if (settings.Oriented) commands.AddComponent(new GiOriented());
            if (settings.Scaled) commands.AddComponent(new GiScaled());
        }
    }
}
