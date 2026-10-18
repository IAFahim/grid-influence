using Unity.Entities;
using Unity.Profiling;

namespace Gi.Entities.Presentation
{
    public struct GiFieldDrawStats : IComponentData
    {
        public int UpdatedTiles;
        public int MirrorTiles;
        public int VisibleTiles;
        public int DrawnTiles;
        public int DrawnSources;
        public int DrawnBindings;
        public byte GridCount;
        public byte Layer;
        public byte Drawn;
    }

    public static class GiFieldDrawMarkers
    {
        public const string DrawName = "Gi.Draw";
        public const string MirrorName = "Gi.Draw.Mirror";
        public const string HeatName = "Gi.Draw.Heat";

        public static readonly string[] Names = { DrawName, MirrorName, HeatName };

        public static readonly ProfilerMarker Draw = new ProfilerMarker(ProfilerCategory.Scripts, DrawName);
        public static readonly ProfilerMarker Mirror = new ProfilerMarker(ProfilerCategory.Scripts, MirrorName);
        public static readonly ProfilerMarker Heat = new ProfilerMarker(ProfilerCategory.Scripts, HeatName);
    }
}
