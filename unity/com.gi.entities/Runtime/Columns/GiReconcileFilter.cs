namespace Gi.Entities
{
    public static class GiReconcileFilter
    {
        public static bool ShouldVisitChunk(bool force, bool inputsChanged, bool anyDirty)
        {
            return force || inputsChanged || anyDirty;
        }

        public static bool ShouldProcessEntity(bool dirty)
        {
            return dirty;
        }

        public static bool KeepDirty(int planCount)
        {
            return planCount > 0;
        }
    }
}
