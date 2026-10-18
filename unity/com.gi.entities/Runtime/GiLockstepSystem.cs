using BovineLabs.Core;
using Unity.Entities;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiProcessSystem))]
    public partial struct GiLockstepSystem : ISystem
    {
        private byte _world;
        private bool _tracking;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_tracking) GiLockstepGate.Forget(_world);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (GiNetConfig.Check.Data <= 0)
            {
                if (_tracking)
                {
                    GiLockstepGate.Forget(_world);
                    _tracking = false;
                }

                return;
            }

            var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
            var frame = state.EntityManager.GetComponentData<GiFrame>(frameEntity);
            if (!frame.Alive)
            {
                if (_tracking) GiLockstepGate.Forget(_world);
                _tracking = false;
                return;
            }

            state.CompleteDependency();
            GiRuntime.CompleteAll();
            frame = state.EntityManager.GetComponentData<GiFrame>(frameEntity);
            var track = GiLockstepGate.Track(frame.World, frame.Generation, frame.Tables);
            _world = frame.World;
            _tracking = true;
            track.Hashes.Update();
            track.Hashes.Layers(track.Layers);
            track.Lockstep.Record(frame.Tick, track.Layers);
        }
    }
}
