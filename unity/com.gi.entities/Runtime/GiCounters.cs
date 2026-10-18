using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    [NoAutoStaticsCleanup]
    public static class GiCounters
    {
        public const string SourcesName = "Gi Sources";
        public const string PulsesName = "Gi Pulses";
        public const string SplatsName = "Gi Splats";
        public const string ChangedTilesName = "Gi Changed Tiles";
        public const string CommandsName = "Gi Commands";
        public const string FaultsName = "Gi Faults";
        public const string ImpureReadsName = "Gi Impure Reads";
        public const string BytesName = "Gi Bytes";

        public static readonly string[] Names =
        {
            SourcesName, PulsesName, SplatsName, ChangedTilesName, CommandsName, FaultsName, ImpureReadsName, BytesName,
        };

        public static ProfilerCounterValue<int> Sources = Counter(SourcesName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Pulses = Counter(PulsesName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Splats = Counter(SplatsName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> ChangedTiles = Counter(ChangedTilesName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Commands = Counter(CommandsName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> Faults = Counter(FaultsName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<int> ImpureReads = Counter(ImpureReadsName, ProfilerMarkerDataUnit.Count);
        public static ProfilerCounterValue<long> Bytes = Counter64(BytesName, ProfilerMarkerDataUnit.Bytes);

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
