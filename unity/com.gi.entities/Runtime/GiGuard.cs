using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Gi.Entities
{
    public static unsafe class GiGuard
    {
        public const int Process = 1;
        public const int Reconcile = 2;
        public const int Sync = 3;
        public const int Splat = 4;
        public const int Sense = 5;
        public const int FirstCustom = 16;

        private const string Checks = "ENABLE_UNITY_COLLECTIONS_CHECKS";

        private static readonly string[] Known = { "unknown", "GiProcessSystem", "GiReconcileSystem", "GiSyncSystem", "GiSplatSystem", "GiSenseSystem" };
        private static readonly List<string> Custom = new List<string>();
        private static GiGuardTable* _table;

        internal static void Create()
        {
            if (_table != null) return;
            _table = (GiGuardTable*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<GiGuardTable>(), 64, Allocator.Persistent);
            _table->Reset();
        }

        internal static void Destroy()
        {
            if (_table == null) return;
            UnsafeUtility.Free(_table, Allocator.Persistent);
            _table = null;
            Custom.Clear();
        }

        public static int Register(string name)
        {
            Create();
            var index = Custom.IndexOf(name);
            if (index < 0)
            {
                Custom.Add(name);
                index = Custom.Count - 1;
            }

            return FirstCustom + index;
        }

        public static string Name(int tag)
        {
            if (tag > 0 && tag < Known.Length) return Known[tag];
            var index = tag - FirstCustom;
            return index >= 0 && index < Custom.Count ? Custom[index] : Known[0];
        }

        [Conditional(Checks)]
        public static void Begin(byte world, int tag, bool write)
        {
            if (_table != null && GiDebugConfig.Guard.Data) _table->Begin(world, tag, write);
        }

        [Conditional(Checks)]
        public static void End(byte world, bool write)
        {
            if (_table != null && GiDebugConfig.Guard.Data) _table->End(world, write);
        }

        public static int Violations => _table != null ? _table->Total() : 0;

        public static bool TryTake(out GiGuardReport report)
        {
            if (_table != null) return _table->Take(out report);
            report = default;
            return false;
        }

        public static string Describe(in GiGuardReport report)
        {
            return "Gi guard: " + Name(report.Intruder) + (report.IntruderWrites ? " wrote" : " read") + " Gi world " + report.World +
                   " while " + Name(report.Holder) + " held it (" + report.Count + " overlaps)";
        }
    }
}
