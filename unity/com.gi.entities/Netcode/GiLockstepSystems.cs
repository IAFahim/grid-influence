using BovineLabs.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace Gi.Entities.Netcode
{
    public struct GiDesyncRpc : IRpcCommand
    {
        public int Tick;
        public byte Layer;
        public ulong Hash;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiLockstepSystem))]
    public partial struct GiLockstepServerSystem : ISystem
    {
        private int _sentTick;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _sentTick = -1;
        }

        public void OnUpdate(ref SystemState state)
        {
            var interval = GiNetConfig.Check.Data;
            if (interval <= 0) return;

            var frame = state.EntityManager.GetComponentData<GiFrame>(SystemAPI.GetSingletonEntity<GiFrame>());
            if (!frame.Alive || frame.Tick <= 0 || frame.Tick % interval != 0 || frame.Tick == _sentTick) return;

            var track = GiLockstepGate.Find(frame.World);
            if (track == null) return;

            _sentTick = frame.Tick;
            for (var layer = 0; layer < track.Lockstep.LayerCount; layer++)
            {
                if (!track.Lockstep.TryGet(frame.Tick, layer, out var hash)) continue;

                var request = state.EntityManager.CreateEntity(typeof(GiDesyncRpc), typeof(SendRpcCommandRequest));
                state.EntityManager.SetComponentData(request, new GiDesyncRpc { Tick = frame.Tick, Layer = (byte)layer, Hash = hash });
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiLockstepSystem))]
    public partial struct GiLockstepClientSystem : ISystem
    {
        private const int PendingLimit = 256;

        private EntityQuery _received;
        private NativeList<GiDesyncRpc> _pending;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _received = state.GetEntityQuery(ComponentType.ReadOnly<GiDesyncRpc>(), ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _pending = new NativeList<GiDesyncRpc>(PendingLimit, Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            _pending.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!_received.IsEmptyIgnoreFilter)
            {
                using var rpcs = _received.ToComponentDataArray<GiDesyncRpc>(Allocator.Temp);
                foreach (var rpc in rpcs)
                    if (_pending.Length < PendingLimit)
                        _pending.Add(rpc);
                state.EntityManager.DestroyEntity(_received);
            }

            if (_pending.Length == 0) return;

            var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
            var frame = state.EntityManager.GetComponentData<GiFrame>(frameEntity);
            var track = frame.Alive ? GiLockstepGate.Find(frame.World) : null;
            if (track == null) return;

            for (var i = _pending.Length - 1; i >= 0; i--)
            {
                var rpc = _pending[i];
                if (track.Lockstep.Ahead(rpc.Tick)) continue;

                _pending.RemoveAtSwapBack(i);
                var verdict = track.Lockstep.Check(rpc.Tick, rpc.Layer, rpc.Hash);
                if (verdict != GiLockstepVerdict.Mismatch) continue;

                if (frame.Faults.Raise(GiFaultKind.Desync, rpc.Tick))
                    BLGlobalLogger.LogErrorString("Gi desync: layer " + rpc.Layer + " differs from the server at tick " + rpc.Tick);
                state.EntityManager.SetComponentData(frameEntity, frame);
            }
        }
    }
}
