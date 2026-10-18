namespace Gi.Entities
{
    public struct GiReconcileContext
    {
        public bool Alive;
        public byte World;
        public int Tick;
        public GiPlane Plane;
        public GiTables Tables;

        public static GiReconcileContext Of(in GiFrame frame)
        {
            return new GiReconcileContext { Alive = frame.Alive, World = frame.World, Tick = frame.Tick, Plane = frame.Plane, Tables = frame.Tables };
        }
    }
}
