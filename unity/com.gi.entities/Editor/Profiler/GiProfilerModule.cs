using System;
using Unity.Profiling;
using Unity.Profiling.Editor;

namespace Gi.Entities.Editor
{
    [Serializable]
    [ProfilerModuleMetadata("Gi")]
    public sealed class GiProfilerModule : ProfilerModule
    {
        private static readonly ProfilerCounterDescriptor[] Counters = Describe();

        public GiProfilerModule()
            : base(Counters, ProfilerModuleChartType.Line)
        {
        }

        public static string[] CounterNames()
        {
            var names = new string[Counters.Length];
            for (var i = 0; i < names.Length; i++) names[i] = Counters[i].Name;
            return names;
        }

        private static ProfilerCounterDescriptor[] Describe()
        {
            var descriptors = new ProfilerCounterDescriptor[GiCounters.Names.Length];
            for (var i = 0; i < descriptors.Length; i++) descriptors[i] = new ProfilerCounterDescriptor(GiCounters.Names[i], ProfilerCategory.Scripts);
            return descriptors;
        }
    }
}
