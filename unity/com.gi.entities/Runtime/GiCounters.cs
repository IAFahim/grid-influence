using Unity.Profiling;

namespace Gi.Entities
{
    public static class GiCounters
    {
        public static ProfilerCounterValue<int> Sources = Counter("Gi Sources", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Pulses = Counter("Gi Pulses", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Splats = Counter("Gi Splats", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> ChangedTiles = Counter("Gi Changed Tiles", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Commands = Counter("Gi Commands", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Faults = Counter("Gi Faults", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> ImpureReads = Counter("Gi Impure Reads", ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<long> Bytes = Counter64("Gi Bytes", ProfilerMarkerDataUnit.Bytes);

        private static ProfilerCounterValue<int> Counter(string name, ProfilerMarkerDataUnit unit)
        {
            return new ProfilerCounterValue<int>(ProfilerCategory.Scripts, name, unit, ProfilerCounterOptions.FlushOnEndOfFrame);
        }

        private static ProfilerCounterValue<long> Counter64(string name, ProfilerMarkerDataUnit unit)
        {
            return new ProfilerCounterValue<long>(ProfilerCategory.Scripts, name, unit, ProfilerCounterOptions.FlushOnEndOfFrame);
        }
    }
}
