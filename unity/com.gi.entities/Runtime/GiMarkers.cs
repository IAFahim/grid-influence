using Unity.Profiling;

namespace Gi.Entities
{
    public static class GiMarkers
    {
        public const string ProcessName = "Gi.Process";
        public const string ReconcileName = "Gi.Reconcile.Apply";
        public const string SyncName = "Gi.Sync.Apply";
        public const string SplatName = "Gi.Splat.Apply";
        public const string SenseName = "Gi.Sense.Read";

        public static readonly string[] Names = { ProcessName, ReconcileName, SyncName, SplatName, SenseName };

        public static readonly ProfilerMarker Process = new ProfilerMarker(ProfilerCategory.Scripts, ProcessName);
        public static readonly ProfilerMarker Reconcile = new ProfilerMarker(ProfilerCategory.Scripts, ReconcileName);
        public static readonly ProfilerMarker Sync = new ProfilerMarker(ProfilerCategory.Scripts, SyncName);
        public static readonly ProfilerMarker Splat = new ProfilerMarker(ProfilerCategory.Scripts, SplatName);
        public static readonly ProfilerMarker Sense = new ProfilerMarker(ProfilerCategory.Scripts, SenseName);
    }
}
