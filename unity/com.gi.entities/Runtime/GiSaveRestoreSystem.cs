using BovineLabs.Core;
using Unity.Collections;
using Unity.Entities;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    [UpdateBefore(typeof(GiReconcileSystem))]
    public partial struct GiSaveRestoreSystem : ISystem
    {
        private EntityQuery _saved;
        private EntityQuery _holders;
        private uint _generation;
        private byte _world;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _saved = state.GetEntityQuery(ComponentType.ReadOnly<GiSavedSource>(), ComponentType.Exclude<GiSource>());
            _holders = state.GetEntityQuery(ComponentType.ReadOnly<GiSaveHolder>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_generation != 0u) GiRestoreGate.Forget(_world);
        }

        public void OnUpdate(ref SystemState state)
        {
            var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
            var frame = state.EntityManager.GetComponentData<GiFrame>(frameEntity);
            if (!frame.Alive)
            {
                _generation = 0u;
                return;
            }

            if (frame.Generation == _generation) return;

            _generation = frame.Generation;
            _world = frame.World;
            if (_saved.IsEmptyIgnoreFilter && !HasPulses(ref state)) return;

            state.CompleteDependency();
            GiRuntime.CompleteAll();
            var driver = GiRestoreGate.For(frame.World);
            driver.Clear();
            Sources(ref state, frame, driver);
            Pulses(ref state, frame, frameEntity, driver);
        }

        public static long Tag(Entity entity)
        {
            return (long)entity.Index << 32 | (uint)entity.Version;
        }

        public static Entity FromTag(long tag)
        {
            return new Entity { Index = (int)(tag >> 32), Version = (int)(uint)tag };
        }

        private bool HasPulses(ref SystemState state)
        {
            return !_holders.IsEmptyIgnoreFilter && state.EntityManager.GetBuffer<GiSavedPulse>(_holders.GetSingletonEntity()).Length > 0;
        }

        private void Sources(ref SystemState state, in GiFrame frame, GiRestoreDriver driver)
        {
            if (_saved.IsEmptyIgnoreFilter) return;

            using var entities = _saved.ToEntityArray(Allocator.Temp);
            using var saved = _saved.ToComponentDataArray<GiSavedSource>(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var source = saved[i];
                if (!GiRestore.Place(frame.World, frame.Tables, source.ToState(), driver, out var id, Tag(entities[i]), source.AppliedGain)) continue;

                state.EntityManager.AddComponentData(entities[i], new GiSource
                {
                    Id = id,
                    Layer = new GiLayerKey(source.LayerKey),
                    Stamp = new GiStampKey(source.StampKey),
                    Gain = source.AppliedGain,
                });
            }

            state.EntityManager.RemoveComponent<GiSavedSource>(_saved);
        }

        private void Pulses(ref SystemState state, in GiFrame frame, Entity frameEntity, GiRestoreDriver driver)
        {
            if (_holders.IsEmptyIgnoreFilter) return;

            var buffer = state.EntityManager.GetBuffer<GiSavedPulse>(_holders.GetSingletonEntity());
            if (buffer.Length == 0) return;

            var mirror = state.EntityManager.GetComponentData<GiPulseMirror>(frameEntity);
            for (var i = 0; i < buffer.Length; i++)
            {
                if (!GiRestore.Place(frame.World, frame.Tables, buffer[i].ToState(), driver, out var id)) continue;

                mirror.Place(id);
                mirror.Expire(id, frame.Tick + buffer[i].ExpireTicksLeft);
            }

            buffer.Clear();
        }
    }
}
