using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static class GiSave
    {
        public static int Prepare(EntityManager manager)
        {
            using var frames = manager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>(), ComponentType.ReadOnly<GiPulseMirror>());
            if (frames.IsEmptyIgnoreFilter) return 0;

            GiRuntime.CompleteAll();
            manager.CompleteAllTrackedJobs();
            var frameEntity = frames.GetSingletonEntity();
            var frame = manager.GetComponentData<GiFrame>(frameEntity);
            if (!frame.Alive) return 0;

            var captured = 0;
            using (var sources = manager.CreateEntityQuery(ComponentType.ReadOnly<GiSource>()))
            using (var entities = sources.ToEntityArray(Allocator.Temp))
            using (var components = sources.ToComponentDataArray<GiSource>(Allocator.Temp))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!components[i].Placed || !GiSourceCapture.TryCapture(frame.World, frame.Tables, components[i].Id, out var state))
                    {
                        if (manager.HasComponent<GiSavedSource>(entity)) manager.RemoveComponent<GiSavedSource>(entity);
                        continue;
                    }

                    var scheduled = state.Fading || state.Expiring;
                    if (!scheduled && !manager.HasComponent<GiGainRamp>(entity))
                    {
                        if (manager.HasComponent<GiSavedSource>(entity)) manager.RemoveComponent<GiSavedSource>(entity);
                        continue;
                    }

                    var saved = GiSavedSource.Of(state, components[i].Gain);
                    if (manager.HasComponent<GiSavedSource>(entity)) manager.SetComponentData(entity, saved);
                    else manager.AddComponentData(entity, saved);
                    captured++;
                }
            }

            var holder = Holder(manager);
            var pulses = manager.GetBuffer<GiSavedPulse>(holder);
            pulses.Clear();
            var live = new List<GiSourceState>();
            var entries = Entries(manager, frameEntity);
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].EndTick > frame.Tick && GiSourceCapture.TryCapture(frame.World, frame.Tables, entries[i].Id, out var pulse))
                    live.Add(pulse);

            live.Sort((a, b) => a.Id.Slot != b.Id.Slot ? a.Id.Slot.CompareTo(b.Id.Slot) : a.Id.Generation.CompareTo(b.Id.Generation));
            foreach (var pulse in live) pulses.Add(GiSavedPulse.Of(pulse));
            return captured + live.Count;
        }

        public static void Release(EntityManager manager)
        {
            using (var scheduled = manager.CreateEntityQuery(ComponentType.ReadOnly<GiSavedSource>()))
                manager.RemoveComponent<GiSavedSource>(scheduled);

            using (var holders = manager.CreateEntityQuery(ComponentType.ReadOnly<GiSaveHolder>()))
            {
                if (holders.IsEmptyIgnoreFilter) return;

                manager.GetBuffer<GiSavedPulse>(holders.GetSingletonEntity()).Clear();
            }
        }

        public static Entity Holder(EntityManager manager)
        {
            using var holders = manager.CreateEntityQuery(ComponentType.ReadOnly<GiSaveHolder>());
            if (!holders.IsEmptyIgnoreFilter) return holders.GetSingletonEntity();

            var holder = manager.CreateEntity(typeof(GiSaveHolder));
            manager.AddBuffer<GiSavedPulse>(holder);
            return holder;
        }

        public static unsafe GiPulseEntry[] Entries(EntityManager manager, Entity frame)
        {
            var mirror = manager.GetComponentData<GiPulseMirror>(frame);
            var copy = new GiPulseEntry[mirror.Entries->Length];
            for (var i = 0; i < copy.Length; i++) copy[i] = mirror.Entries->ElementAt(i);
            return copy;
        }
    }
}
