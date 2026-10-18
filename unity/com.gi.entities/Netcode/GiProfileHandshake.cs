using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace Gi.Entities.Netcode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial struct GiProfileHandshakeServerSystem : ISystem
    {
        private EntityQuery _connections;
        private EntityQuery _profiles;

        public void OnCreate(ref SystemState state)
        {
            _connections = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.Exclude<GiProfileSent>());
            _profiles = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_connections.IsEmptyIgnoreFilter || _profiles.IsEmptyIgnoreFilter) return;

            var blob = _profiles.GetSingleton<GiProfileReference>().Value;
            if (!blob.IsCreated) return;

            var hash = blob.Value.Hash;
            using var connections = _connections.ToEntityArray(Allocator.Temp);
            foreach (var connection in connections)
            {
                var request = state.EntityManager.CreateEntity(typeof(GiProfileHashRpc), typeof(SendRpcCommandRequest));
                state.EntityManager.SetComponentData(request, new GiProfileHashRpc { Hash = hash });
                state.EntityManager.SetComponentData(request, new SendRpcCommandRequest { TargetConnection = connection });
                state.EntityManager.AddComponent<GiProfileSent>(connection);
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(GiSystemGroup))]
    public partial struct GiProfileHandshakeClientSystem : ISystem
    {
        private EntityQuery _received;
        private EntityQuery _profiles;
        private EntityQuery _suppress;
        private ulong _serverHash;
        private Entity _suppressor;

        public void OnCreate(ref SystemState state)
        {
            _received = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileHashRpc>(), ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _profiles = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            _suppress = state.GetEntityQuery(ComponentType.ReadOnly<GiSuppress>());
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!_received.IsEmptyIgnoreFilter)
            {
                using var rpcs = _received.ToComponentDataArray<GiProfileHashRpc>(Allocator.Temp);
                _serverHash = rpcs[rpcs.Length - 1].Hash;
                state.EntityManager.DestroyEntity(_received);
            }

            if (_serverHash == 0ul || _profiles.IsEmptyIgnoreFilter) return;

            var blob = _profiles.GetSingleton<GiProfileReference>().Value;
            if (!blob.IsCreated) return;

            var matches = blob.Value.Hash == _serverHash;
            var suppressed = !_suppress.IsEmptyIgnoreFilter;
            if (!matches && !suppressed)
            {
                _suppressor = state.EntityManager.CreateEntity(typeof(GiSuppress));
            }
            else if (matches && suppressed && state.EntityManager.Exists(_suppressor))
            {
                state.EntityManager.DestroyEntity(_suppressor);
                _suppressor = Entity.Null;
            }
        }
    }
}
