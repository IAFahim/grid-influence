using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static unsafe class GiWorldSnapshot
    {
        private const uint Magic = 0x53474945u;
        private const ushort Version = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public uint Magic;
            public ushort Version;
            public ushort Reserved;
            public ulong ProfileHash;
            public GiFaults Faults;
            public GiStats Stats;
            public int GiBytes;
            public int SourceCount;
            public int MirrorCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceRecord
        {
            public Entity Entity;
            public GiSource Source;
        }

        public static bool Capture(EntityManager manager, NativeList<byte> destination)
        {
            destination.Clear();
            using var frames = new EntityQueryBuilder(Allocator.Temp).WithAll<GiFrame, GiStats, GiPulseMirror>().Build(manager);
            if (frames.IsEmptyIgnoreFilter) return false;

            GiRuntime.CompleteAll();
            manager.CompleteAllTrackedJobs();

            var frameEntity = frames.GetSingletonEntity();
            var frame = manager.GetComponentData<GiFrame>(frameEntity);
            if (!frame.Alive) return false;
            var stats = manager.GetComponentData<GiStats>(frameEntity);

            var giSize = GiWorld.SnapshotSize(frame.World);
            if (giSize <= 0) return false;

            var mirror = manager.GetComponentData<GiPulseMirror>(frameEntity);
            var mirrorCount = mirror.Entries != null ? mirror.Entries->Length : 0;

            using var sourceQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<GiSource>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build(manager);
            using var entities = sourceQuery.ToEntityArray(Allocator.Temp);
            using var sources = sourceQuery.ToComponentDataArray<GiSource>(Allocator.Temp);

            var headerSize = sizeof(Header);
            var sourcesSize = entities.Length * sizeof(SourceRecord);
            var mirrorSize = mirrorCount * sizeof(GiPulseEntry);
            var totalSize = headerSize + giSize + sourcesSize + mirrorSize;

            destination.ResizeUninitialized(totalSize);
            var basePtr = (byte*)destination.GetUnsafePtr();

            if (!GiWorld.TrySnapshot(frame.World, new Span<byte>(basePtr + headerSize, giSize), out var written) || written != giSize)
            {
                destination.Clear();
                return false;
            }

            *(Header*)basePtr = new Header
            {
                Magic = Magic,
                Version = Version,
                ProfileHash = frame.ProfileHash,
                Faults = frame.Faults,
                Stats = stats,
                GiBytes = written,
                SourceCount = entities.Length,
                MirrorCount = mirrorCount,
            };

            var sourcePtr = (SourceRecord*)(basePtr + headerSize + written);
            for (var i = 0; i < entities.Length; i++)
                sourcePtr[i] = new SourceRecord { Entity = entities[i], Source = sources[i] };

            if (mirrorCount > 0)
            {
                var mirrorPtr = (GiPulseEntry*)(basePtr + headerSize + written + sourcesSize);
                UnsafeUtility.MemCpy(mirrorPtr, mirror.Entries->Ptr, mirrorSize);
            }

            return true;
        }

        public static bool Restore(EntityManager manager, NativeArray<byte> snapshot)
        {
            if (!snapshot.IsCreated || snapshot.Length < sizeof(Header)) return false;

            return Restore(manager, (byte*)snapshot.GetUnsafeReadOnlyPtr(), snapshot.Length);
        }

        public static bool Restore(EntityManager manager, NativeList<byte> snapshot)
        {
            if (!snapshot.IsCreated || snapshot.Length < sizeof(Header)) return false;

            return Restore(manager, (byte*)snapshot.GetUnsafeReadOnlyPtr(), snapshot.Length);
        }

        private static bool Restore(EntityManager manager, byte* basePtr, int length)
        {
            var header = *(Header*)basePtr;
            if (header.Magic != Magic || header.Version != Version) return false;
            if (header.GiBytes <= 0 || header.SourceCount < 0 || header.MirrorCount < 0) return false;

            var headerSize = sizeof(Header);
            var sourcesSize = (long)header.SourceCount * sizeof(SourceRecord);
            var mirrorSize = (long)header.MirrorCount * sizeof(GiPulseEntry);
            var expectedLength = (long)headerSize + header.GiBytes + sourcesSize + mirrorSize;
            if (expectedLength != length) return false;

            using var frames = new EntityQueryBuilder(Allocator.Temp).WithAll<GiFrame, GiStats, GiPulseMirror>().Build(manager);
            if (frames.IsEmptyIgnoreFilter) return false;

            GiRuntime.CompleteAll();
            manager.CompleteAllTrackedJobs();

            var frameEntity = frames.GetSingletonEntity();
            var frame = manager.GetComponentData<GiFrame>(frameEntity);
            if (!frame.Alive || frame.ProfileHash != header.ProfileHash) return false;

            byte restoredWorld;
            try
            {
                restoredWorld = GiWorld.New();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            if (!GiWorld.TryRestore(restoredWorld, new ReadOnlySpan<byte>(basePtr + headerSize, header.GiBytes)))
            {
                GiWorld.Free(restoredWorld);
                return false;
            }

            GiWorld.Free(frame.World);
            frame.World = restoredWorld;
            frame.Token = GiWorld.Capture(restoredWorld);
            frame.Tick = GiWorld.Tick(restoredWorld);
            frame.Generation++;
            frame.Faults = header.Faults;
            manager.SetComponentData(frameEntity, frame);
            manager.SetComponentData(frameEntity, header.Stats);

            var mirror = manager.GetComponentData<GiPulseMirror>(frameEntity);
            if (mirror.Entries != null)
            {
                mirror.Entries->Clear();
                if (header.MirrorCount > 0)
                {
                    mirror.Entries->Resize(header.MirrorCount);
                    var mirrorPtr = (GiPulseEntry*)(basePtr + headerSize + header.GiBytes + sourcesSize);
                    UnsafeUtility.MemCpy(mirror.Entries->Ptr, mirrorPtr, header.MirrorCount * sizeof(GiPulseEntry));
                }
            }

            DrainPendingPulses(manager);

            var sourcePtr = (SourceRecord*)(basePtr + headerSize + header.GiBytes);
            var savedMap = new NativeHashMap<Entity, GiSource>(Math.Max(1, header.SourceCount), Allocator.Temp);
            try
            {
                for (var i = 0; i < header.SourceCount; i++)
                    savedMap[sourcePtr[i].Entity] = sourcePtr[i].Source;

                using var existingQuery = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<GiSource>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                    .Build(manager);
                using var existingEntities = existingQuery.ToEntityArray(Allocator.Temp);
                for (var i = 0; i < existingEntities.Length; i++)
                {
                    var entity = existingEntities[i];
                    if (savedMap.TryGetValue(entity, out var restoredSource))
                    {
                        manager.SetComponentData(entity, restoredSource);
                        savedMap.Remove(entity);
                    }
                    else if (manager.HasComponent<GiEmitter>(entity))
                    {
                        manager.SetComponentData(entity, default(GiSource));
                    }
                    else
                    {
                        manager.RemoveComponent<GiSource>(entity);
                    }
                }

                for (var i = 0; i < header.SourceCount; i++)
                {
                    var entity = sourcePtr[i].Entity;
                    if (savedMap.ContainsKey(entity) && manager.Exists(entity))
                        manager.AddComponentData(entity, sourcePtr[i].Source);
                }
            }
            finally
            {
                savedMap.Dispose();
            }

            return true;
        }

        private static void DrainPendingPulses(EntityManager manager)
        {
            using var pulseQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<GiPulseSingleton>()
                .WithOptions(EntityQueryOptions.IncludeSystems)
                .Build(manager);
            if (pulseQuery.IsEmptyIgnoreFilter) return;

            var pulseSingleton = pulseQuery.GetSingleton<GiPulseSingleton>();
            while (pulseSingleton.Queue.TryDequeue(out _))
            {
            }

            if (pulseSingleton.Pending != null) *pulseSingleton.Pending = 0;
        }
    }
}
