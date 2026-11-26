using Unity.Entities;

namespace Gi.Entities
{
    [WorldSystemFilter(GiWorlds.SimulationEditor)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    public partial class GiSystemGroup : ComponentSystemGroup
    {
    }
}
