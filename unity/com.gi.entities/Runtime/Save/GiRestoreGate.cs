namespace Gi.Entities
{
    public static class GiRestoreGate
    {
        private static readonly GiRestoreDriver[] Drivers = new GiRestoreDriver[256];

        public static GiRestoreDriver For(byte world)
        {
            return Drivers[world] ?? (Drivers[world] = new GiRestoreDriver());
        }

        public static int Pending(byte world)
        {
            return Drivers[world]?.Count ?? 0;
        }

        public static void Forget(byte world)
        {
            Drivers[world] = null;
        }
    }
}
