using BovineLabs.Core;
using Unity.Entities;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    public partial class GiSystemGroup : ComponentSystemGroup
    {
    }
}
