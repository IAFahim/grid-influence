using System;

namespace Gi.Entities.Tests
{
    public static class GiAllocation
    {
        private static readonly Func<bool, long> Counter = Bind();

        public static bool Available => Counter != null;

        public static long Total()
        {
            return Counter(true);
        }

        public static bool SteadyZero(Action window, int windows, int consecutive)
        {
            var run = 0;
            for (var i = 0; i < windows; i++)
            {
                var before = Total();
                window();
                run = Total() - before == 0L ? run + 1 : 0;
                if (run >= consecutive) return true;
            }

            return false;
        }

        private static Func<bool, long> Bind()
        {
            var method = typeof(GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });
            return method == null ? null : (Func<bool, long>)Delegate.CreateDelegate(typeof(Func<bool, long>), method);
        }
    }
}
