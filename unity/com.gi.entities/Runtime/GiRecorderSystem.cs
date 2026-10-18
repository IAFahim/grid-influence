using BovineLabs.Core;
using Unity.Collections;
using Unity.Entities;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    [UpdateBefore(typeof(GiReconcileSystem))]
    public partial struct GiRecorderSystem : ISystem
    {
        private EntityQuery _profiles;
        private EntityQuery _sources;
        private uint _recordedGeneration;
        private byte _recordedWorld;
        private bool _attached;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _profiles = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            _sources = state.GetEntityQuery(ComponentType.ReadOnly<GiSource>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_attached) GiRecorderGate.Forget(_recordedWorld);
        }

        public void OnUpdate(ref SystemState state)
        {
            var wanted = GiDebugConfig.Record.Data;
            if (!wanted && !GiRecorderGate.Enabled) return;

            var frame = state.EntityManager.GetComponentData<GiFrame>(SystemAPI.GetSingletonEntity<GiFrame>());
            if (!frame.Alive) return;

            var recording = GiRecorderGate.IsRecording(frame.World);
            if (recording && (!wanted || _recordedGeneration != frame.Generation))
            {
                GiRecorderGate.Detach(frame.World);
                recording = false;
                _attached = false;
            }

            if (!wanted || recording || _profiles.IsEmptyIgnoreFilter) return;

            var reference = _profiles.GetSingleton<GiProfileReference>();
            if (!reference.Value.IsCreated) return;

            GiRuntime.CompleteAll();
            state.EntityManager.CompleteAllTrackedJobs();
            var recorder = new GiRecorder(frame.World, frame.Tables, GiProfileBlobBuilder.ToSpec(ref reference.Value.Value),
                GiDebugConfig.RecordTicks.Data, GiDebugConfig.RecordKeyframe.Data, GiLz4Codec.Create());
            using (var sources = _sources.ToComponentDataArray<GiSource>(Allocator.Temp))
            {
                var ids = new Gi.World.SourceId[sources.Length];
                var count = 0;
                for (var i = 0; i < sources.Length; i++)
                    if (sources[i].Placed)
                        ids[count++] = sources[i].Id;
                recorder.Adopt(new System.ReadOnlySpan<Gi.World.SourceId>(ids, 0, count));
            }

            recorder.Start();
            GiRecorderGate.Attach(recorder);
            _recordedGeneration = frame.Generation;
            _recordedWorld = frame.World;
            _attached = true;
        }
    }
}
