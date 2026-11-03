using BovineLabs.Core;
using Unity.Burst;
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

#if !ENABLE_CORECLR || GI_BURST_PROCESS
        [BurstCompile]
#endif
        private unsafe struct ProcessJob : IJob
        {
            public Entity Frame;
            public ComponentLookup<GiFrame> Frames;
            internal Gi.GiRoot Root;

            public ProcessJob()
            {
                Root = Gi.Runtime.Root;
            }

            public void Execute()
            {
                var frame = Frames[Frame];
                if (!frame.Alive) return;

                Gi.GiRoot captured = Root;
                Gi.GiRoot* root = &captured;
                GuardBegin(frame.World, GiGuard.Process, true);
                var scope = GiMarkers.Process.Auto();
                var status = GiWorld.Process(root, frame.Token);
                if (status == GiWorld.BatchStatus.StaleRevision)
                {
                    frame.Token = Refresh(frame.World, root);
                    status = GiWorld.Process(root, frame.Token);
                    if (status == GiWorld.BatchStatus.StaleRevision) frame.Faults.Raise(GiFaultKind.StaleToken, frame.Tick);
                }

                if (status != GiWorld.BatchStatus.Applied && status != GiWorld.BatchStatus.StaleRevision)
                    frame.Faults.Raise(GiFaultKind.Adapter, frame.Tick);

                frame.Tick = GiWorld.Tick(root, frame.World);
                RecordProcessed(frame.World);
                scope.Dispose();

                GuardEnd(frame.World, true);
                Frames[Frame] = frame;
            }

            private static GiWorld.Token Refresh(byte world, Gi.GiRoot* root)
            {
                return new GiWorld.Token(world, GiWorld.GetContext(root, world) == null ? 0 : root->WorldRevisions[world]);
            }

            [BurstDiscard]
            private static void GuardBegin(byte world, int tag, bool write)
            {
                GiGuard.Begin(world, tag, write);
            }

            [BurstDiscard]
            private static void GuardEnd(byte world, bool write)
            {
                GiGuard.End(world, write);
            }

            [BurstDiscard]
            private static void RecordProcessed(byte world)
            {
                if (GiRecorderGate.Enabled) GiRecorderGate.Processed(world);
            }
        }
    }
}
