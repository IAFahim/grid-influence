using BovineLabs.Core;
using Unity.Entities;
using Unity.Jobs;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    public partial struct GiProcessSystem : ISystem
    {
        private ComponentLookup<GiFrame> _frames;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _frames = state.GetComponentLookup<GiFrame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _frames.Update(ref state);
            var job = new ProcessJob { Frame = SystemAPI.GetSingletonEntity<GiFrame>(), Frames = _frames };
            state.Dependency = job.Schedule(GiRuntime.BeginWrite(state.Dependency));
            GiRuntime.EndWrite(state.Dependency);
        }

        private struct ProcessJob : IJob
        {
            public Entity Frame;
            public ComponentLookup<GiFrame> Frames;

            public void Execute()
            {
                var frame = Frames[Frame];
                if (!frame.Alive) return;

                GiGuard.Begin(frame.World, GiGuard.Process, true);
                using (GiMarkers.Process.Auto())
                {
                    var status = GiWorld.Process(frame.Token);
                    if (status == GiWorld.BatchStatus.StaleRevision)
                    {
                        frame.Token = GiWorld.Capture(frame.World);
                        status = GiWorld.Process(frame.Token);
                        if (status == GiWorld.BatchStatus.StaleRevision) frame.Faults.Raise(GiFaultKind.StaleToken, frame.Tick);
                    }

                    if (status != GiWorld.BatchStatus.Applied && status != GiWorld.BatchStatus.StaleRevision)
                        frame.Faults.Raise(GiFaultKind.Adapter, frame.Tick);

                    frame.Tick = GiWorld.Tick(frame.World);
                    if (GiRecorderGate.Enabled) GiRecorderGate.Processed(frame.World);
                }

                GiGuard.End(frame.World, true);
                Frames[Frame] = frame;
            }
        }
    }
}
