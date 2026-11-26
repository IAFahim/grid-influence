using Unity.Entities;

namespace Gi.Entities
{
    public static class GiWorlds
    {
        public const WorldSystemFilterFlags SimulationEditor =
            WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation;
    }
}
