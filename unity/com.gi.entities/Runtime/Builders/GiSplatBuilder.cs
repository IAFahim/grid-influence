using BovineLabs.Core.EntityCommands;

namespace Gi.Entities
{
    public struct GiSplatSettings
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }

    public static class GiSplatBuilder
    {
        public static void Apply<T>(ref T commands, in GiSplatSettings settings)
            where T : struct, IEntityCommands
        {
            commands.AddComponent(new GiSplat { Layer = settings.Layer, Stamp = settings.Stamp, Gain = settings.Gain });
            commands.AddComponent(new GiFault());
            commands.SetComponentEnabled<GiFault>(false);
        }
    }
}
