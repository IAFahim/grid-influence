using System;
using BovineLabs.Core;
using BovineLabs.Quill;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using GiGrid = Gi.Grid;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Presentation
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(DebugSystemGroup))]
    public unsafe partial struct GiFieldDrawSystem : ISystem
    {
        internal const int TileBits = 5;
        internal const int TileCells = 1 << TileBits;
        internal const int CellsPerTile = TileCells * TileCells;
        internal const int TileKeyBits = 20;
        internal const int TileKeyMask = (1 << TileKeyBits) - 1;
        internal const int ResyncFrames = 60;

        private struct GiFieldBindingDraw
        {
            public TypeIndex Type;
            public int Size;
            public ushort Offset;
            public byte Kind;
            public float Reach;
        }

        private NativeHashMap<int, int> _slotOf;
        private NativeList<int> _keys;
        private NativeList<short> _cells;
        private NativeList<short> _maxes;
        private NativeList<int> _changed;
        private NativeList<EntityQuery> _bindingQueries;
        private NativeList<GiFieldBindingDraw> _bindingDraws;
        private EntityQuery _profiles;
        private EntityQuery _sources;
        private ComponentLookup<GiFieldDrawStats> _stats;
        private ulong _builtHash;
        private uint _generation;
        private byte _world;
        private int _layerKey;
        private int _since;
        private int _frameCount;
        private bool _resync;

#if UNITY_EDITOR
        [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
        private static readonly Plane[] OtherPlanes = new Plane[6];
#endif

        public void OnCreate(ref SystemState state)
        {
            _slotOf = new NativeHashMap<int, int>(1024, Allocator.Persistent);
            _keys = new NativeList<int>(1024, Allocator.Persistent);
            _cells = new NativeList<short>(1024 * CellsPerTile, Allocator.Persistent);
            _maxes = new NativeList<short>(1024, Allocator.Persistent);
            _changed = new NativeList<int>(1024, Allocator.Persistent);
            _bindingQueries = new NativeList<EntityQuery>(8, Allocator.Persistent);
            _bindingDraws = new NativeList<GiFieldBindingDraw>(8, Allocator.Persistent);
            _profiles = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            _sources = state.GetEntityQuery(ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<GiSource>());
            _stats = state.GetComponentLookup<GiFieldDrawStats>();
            _resync = true;
            state.RequireForUpdate<GiFrame>();
            state.RequireForUpdate<DrawSystem.Singleton>();
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            ClearBindings(ref state);
            _slotOf.Dispose();
            _keys.Dispose();
            _cells.Dispose();
            _maxes.Dispose();
            _changed.Dispose();
            _bindingQueries.Dispose();
            _bindingDraws.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            GiFieldDrawMarkers.Draw.Begin();
            try
            {
                Update(ref state);
            }
            finally
            {
                GiFieldDrawMarkers.Draw.End();
            }
        }

        private void Update(ref SystemState state)
        {
            var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
            var frame = state.EntityManager.GetComponentData<GiFrame>(frameEntity);
            _stats.Update(ref state);
            if (!_stats.HasComponent(frameEntity))
            {
                state.EntityManager.AddComponent<GiFieldDrawStats>(frameEntity);
                _stats.Update(ref state);
            }

            if (!frame.Alive)
            {
                state.Dependency.Complete();
                _slotOf.Clear();
                _keys.Clear();
                _cells.Clear();
                _maxes.Clear();
                _resync = true;
                _generation = 0u;
                _since = 0;
                PublishMain(ref state, frameEntity, default);
                return;
            }

            if (!GiDebugConfig.DrawEnabled.Data)
            {
                _resync = true;
                PublishMain(ref state, frameEntity, new GiFieldDrawStats());
                return;
            }

            var drawEntity = SystemAPI.GetSingletonEntity<DrawSystem.Singleton>();
            var singleton = state.EntityManager.GetComponentData<DrawSystem.Singleton>(drawEntity);
            var culling = singleton.CameraCulling;
            var cullingOther = default(CameraCulling);
            if (!GiDebugConfig.DrawCull.Data)
            {
                culling = default;
            }
#if UNITY_EDITOR
            else if (GiDebugConfig.DrawCullOther.Data)
            {
                cullingOther = OtherViewCulling();
            }
#endif

            var drawer = singleton.CreateDrawer();
            if (!_profiles.IsEmptyIgnoreFilter)
            {
                var reference = state.EntityManager.GetComponentData<GiProfileReference>(_profiles.GetSingletonEntity());
                if (reference.Value.IsCreated && reference.Value.Value.Hash != _builtHash) Rebuild(ref state, ref reference.Value.Value);
            }

            var layerKey = frame.Tables.LayerCount == 0
                ? 0
                : math.clamp(GiDebugConfig.DrawLayer.Data, 0, frame.Tables.LayerCount - 1);
            var hasLayer = frame.Tables.TryLayer(new GiLayerKey((byte)layerKey), out var layer);

            var full = _resync || _generation != frame.Generation || _world != frame.World || _layerKey != layerKey;
            _generation = frame.Generation;
            _world = frame.World;
            _layerKey = layerKey;
            _resync = false;
            if (++_frameCount % ResyncFrames == 0) full = true;

            var allocator = state.WorldUpdateAllocator;
            var slots = CollectionHelper.CreateNativeArray<int>(6, allocator, NativeArrayOptions.ClearMemory);
            var sourceDraws = new NativeQueue<int>(allocator);
            var bindingDraws = new NativeQueue<int>(allocator);
            var normal = NormalOf(frame.Plane);
            var lift = LiftOf(frame.Plane, GiDebugConfig.DrawHeight.Data);
            var handle = state.Dependency;

            if (hasLayer)
            {
                var read = GiRuntime.BeginRead(handle);
                read = new GiFieldDrawRefreshJob
                {
                    World = frame.World,
                    Layer = layer,
                    Tables = frame.Tables,
                    Since = _since,
                    Full = full,
                    SlotOf = _slotOf,
                    Keys = _keys,
                    Cells = _cells,
                    Maxes = _maxes,
                    Changed = _changed,
                    Slots = slots,
                }.Schedule(read);
                GiRuntime.EndRead(read);
                _since = GiWorld.Tick(frame.World);
                handle = read;
            }

            if (GiDebugConfig.DrawBounds.Data && frame.Tables.GridCount > 0)
            {
                handle = new GiFieldDrawBoundsJob
                {
                    Drawer = drawer,
                    Culling = culling,
                    CullingOther = cullingOther,
                    World = frame.World,
                    Tables = frame.Tables,
                    Plane = frame.Plane,
                    Lift = lift,
                    GridStep = GiDebugConfig.DrawGridStep.Data,
                    Bounds0 = GiDebugConfig.DrawBounds0.Data,
                    Bounds1 = GiDebugConfig.DrawBounds1.Data,
                    Bounds2 = GiDebugConfig.DrawBounds2.Data,
                    Bounds3 = GiDebugConfig.DrawBounds3.Data,
                }.Schedule(handle);
            }

            if (GiDebugConfig.DrawSources.Data && !_sources.IsEmptyIgnoreFilter)
            {
                handle = new GiFieldDrawSourcesJob
                {
                    Drawer = drawer,
                    Culling = culling,
                    CullingOther = cullingOther,
                    Transforms = state.GetComponentTypeHandle<LocalTransform>(true),
                    Sources = state.GetComponentTypeHandle<GiSource>(true),
                    Tables = frame.Tables,
                    Plane = frame.Plane,
                    Lift = lift,
                    Normal = normal,
                    Color = GiDebugConfig.DrawSource.Data,
                    Drawn = sourceDraws.AsParallelWriter(),
                }.ScheduleParallel(_sources, handle);
            }

            if (GiDebugConfig.DrawBindings.Data)
            {
                var transforms = state.GetComponentTypeHandle<LocalTransform>(true);
                for (var i = 0; i < _bindingDraws.Length; i++)
                {
                    var binding = _bindingDraws[i];
                    if (_bindingQueries[i].IsEmptyIgnoreFilter) continue;

                    var data = state.EntityManager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(binding.Type));
                    handle = new GiFieldDrawBindingsJob
                    {
                        Drawer = drawer,
                        Culling = culling,
                        CullingOther = cullingOther,
                        Transforms = transforms,
                        Data = data,
                        TypeSize = binding.Size,
                        Kind = binding.Kind,
                        Offset = binding.Offset,
                        Reach = binding.Reach,
                        Plane = frame.Plane,
                        Lift = lift,
                        Normal = normal,
                        Color = BindingColor(binding.Kind),
                        Drawn = bindingDraws.AsParallelWriter(),
                    }.ScheduleParallel(_bindingQueries[i], handle);
                }
            }

            if (hasLayer && GiDebugConfig.DrawHeat.Data)
            {
                for (var grid = 0; grid < frame.Tables.GridCount; grid++)
                {
                    if (!frame.Tables.TryGrid(grid, out var gridId)) continue;
                    if (!GiGrid.TryGetSpec(frame.World, gridId, out var power, out var originX, out var originY, out var size) || size <= 0f) continue;

                    handle = new GiFieldDrawHeatJob
                    {
                        Drawer = drawer,
                        Culling = culling,
                        CullingOther = cullingOther,
                        Keys = _keys,
                        Cells = _cells,
                        Maxes = _maxes,
                        GridKeyBase = gridId << TileKeyBits,
                        TilesPerSide = math.max(1, (1 << power) >> TileBits),
                        Plane = frame.Plane,
                        OriginX = originX,
                        OriginY = originY,
                        CellSize = size / (1 << power),
                        Lift = LiftOf(frame.Plane, GiDebugConfig.DrawHeight.Data + grid * GiDebugConfig.DrawGridStep.Data),
                        Scale = GiDebugConfig.DrawScale.Data,
                        Cold = ToFloat4(GiDebugConfig.DrawHeatCold.Data),
                        Hot = ToFloat4(GiDebugConfig.DrawHeatHot.Data),
                        Outline = GiDebugConfig.DrawTileOutline.Data,
                        ValueColor = GiDebugConfig.DrawValueColor.Data,
                        PerCell = GiDebugConfig.DrawCells.Data,
                        Values = GiDebugConfig.DrawValues.Data,
                        Slots = slots,
                    }.Schedule(handle);
                }
            }

            _stats.Update(ref state);
            handle = new GiFieldDrawPublishJob
            {
                Slots = slots,
                SourceDraws = sourceDraws,
                BindingDraws = bindingDraws,
                Stats = _stats,
                Target = frameEntity,
                GridCount = frame.Tables.GridCount,
                Layer = layer,
                Drawn = (byte)(drawer.IsEnabled ? 1 : 0),
            }.Schedule(handle);
            state.Dependency = handle;
        }

        private void PublishMain(ref SystemState state, Entity frameEntity, GiFieldDrawStats value)
        {
            if (!_stats.HasComponent(frameEntity))
            {
                state.EntityManager.AddComponent<GiFieldDrawStats>(frameEntity);
                _stats.Update(ref state);
            }

            _stats[frameEntity] = value;
        }

        private void Rebuild(ref SystemState state, ref GiProfileBlob blob)
        {
            ClearBindings(ref state);
            _builtHash = blob.Hash;
            var specs = new ReadOnlySpan<GiBindingSpec>(blob.Bindings.GetUnsafePtr(), blob.Bindings.Length);
            var layers = new ReadOnlySpan<GiLayerSpec>(blob.Layers.GetUnsafePtr(), blob.Layers.Length);
            var statuses = new GiBindingStatus[specs.Length];
            GiBindingTable.Validate(specs, layers, GiBindingSupport.All, statuses);
            for (var i = 0; i < specs.Length; i++)
            {
                if (statuses[i] != GiBindingStatus.Valid) continue;

                var spec = specs[i];
                var field = default(GiBindingField);
                if (spec.Kind == GiBindingKind.Gradient || spec.Kind == GiBindingKind.ValueGradient) field = spec.Gradient;
                else if (spec.Kind == GiBindingKind.Max || spec.Kind == GiBindingKind.Nearest) field = spec.Position;
                if (field.TypeHash == 0ul || field.Size != 8) continue;

                var type = TypeManager.GetTypeIndexFromStableTypeHash(field.TypeHash);
                if (type == TypeIndex.Null) continue;

                var info = TypeManager.GetTypeInfo(type);
                if (info.IsZeroSized || field.Offset + 8 > info.TypeSize) continue;

                _bindingQueries.Add(state.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly(type), ComponentType.ReadOnly<LocalTransform>()));
                _bindingDraws.Add(new GiFieldBindingDraw
                {
                    Type = type,
                    Size = info.TypeSize,
                    Offset = field.Offset,
                    Kind = (byte)spec.Kind,
                    Reach = spec.Reach,
                });
            }
        }

        private void ClearBindings(ref SystemState state)
        {
            for (var i = 0; i < _bindingQueries.Length; i++) _bindingQueries[i].Dispose();
            _bindingQueries.Clear();
            _bindingDraws.Clear();
            _builtHash = 0ul;
        }

        private static Color BindingColor(byte kind)
        {
            return (GiBindingKind)kind switch
            {
                GiBindingKind.Max => GiDebugConfig.DrawBindingMax.Data,
                GiBindingKind.Nearest => GiDebugConfig.DrawBindingNearest.Data,
                _ => GiDebugConfig.DrawBindingGradient.Data,
            };
        }

        private static float3 LiftOf(GiPlane plane, float height)
        {
            return plane.Axes == GiPlaneAxes.Xz ? new float3(0f, height, 0f) : new float3(0f, 0f, height);
        }

        private static float3 NormalOf(GiPlane plane)
        {
            return plane.Axes == GiPlaneAxes.Xz ? new float3(0f, 1f, 0f) : new float3(0f, 0f, 1f);
        }

        private static float4 ToFloat4(Color color)
        {
            return new float4(color.r, color.g, color.b, color.a);
        }

        public bool TryReadTile(byte gridId, int tile, Span<short> cells)
        {
            if (cells.Length < CellsPerTile) return false;
            if (!_slotOf.TryGetValue((gridId << TileKeyBits) | tile, out var slot)) return false;

            fixed (short* destination = cells)
            {
                UnsafeUtility.MemCpy(destination, (short*)_cells.GetUnsafeReadOnlyPtr() + (slot * CellsPerTile), CellsPerTile * sizeof(short));
            }

            return true;
        }

        internal static bool Visible(CameraCulling culling, CameraCulling cullingOther, in AABB bounds)
        {
            if (culling.IsDefault && cullingOther.IsDefault) return true;
            return culling.AnyIntersect(bounds) || cullingOther.AnyIntersect(bounds);
        }

        internal static bool Visible(CameraCulling culling, CameraCulling cullingOther, float3 a, float3 b, float pad)
        {
            var min = math.min(a, b) - pad;
            var max = math.max(a, b) + pad;
            var bounds = new AABB { Center = (min + max) * 0.5f, Extents = (max - min) * 0.5f };
            return Visible(culling, cullingOther, bounds);
        }

#if UNITY_EDITOR
        private static CameraCulling OtherViewCulling()
        {
            var sceneView = UnityEditor.SceneView.lastActiveSceneView;
            var sceneCamera = sceneView != null ? sceneView.camera : null;
            var gameCamera = Camera.main;
            if (gameCamera == null && sceneCamera == null) return default;

            var focusedOnScene = sceneView != null && UnityEditor.EditorWindow.focusedWindow == sceneView && sceneCamera != null;
            var other = focusedOnScene ? gameCamera : sceneCamera;
            if (other == null) return default;

            return CameraCulling.Create(other, OtherPlanes, other.farClipPlane);
        }
#endif

        private struct GiFieldDrawRefreshJob : IJob
        {
            public byte World;
            public byte Layer;
            public GiTables Tables;
            public int Since;
            public bool Full;
            public NativeHashMap<int, int> SlotOf;
            public NativeList<int> Keys;
            public NativeList<short> Cells;
            public NativeList<short> Maxes;
            public NativeList<int> Changed;
            public NativeArray<int> Slots;

            public void Execute()
            {
                using var marker = GiFieldDrawMarkers.Mirror.Auto();
                var updated = 0;
                for (var grid = 0; grid < Tables.GridCount; grid++)
                {
                    if (!Tables.TryGrid(grid, out var gridId)) continue;
                    if (!GiGrid.TryGetSpec(World, gridId, out var power, out _, out _, out _)) continue;

                    var tilesPerSide = math.max(1, (1 << power) >> TileBits);
                    var tiles = tilesPerSide * tilesPerSide;
                    var keyBase = gridId << TileKeyBits;
                    int count;
                    if (Full)
                    {
                        count = tiles;
                    }
                    else
                    {
                        Changed.ResizeUninitialized(tiles);
                        count = math.min(GiWorld.ChangedTiles(World, gridId, Layer, Since, (int*)Changed.GetUnsafePtr()), tiles);
                    }

                    for (var tile = 0; tile < count; tile++)
                    {
                        var index = Full ? tile : Changed[tile];
                        if ((uint)index >= (uint)tiles) continue;
                        RefreshTile(keyBase | index, tilesPerSide, gridId);
                        updated++;
                    }
                }

                Slots[0] = updated;
                Slots[1] = Keys.Length;
            }

            private void RefreshTile(int key, int tilesPerSide, byte gridId)
            {
                var tile = key & TileKeyMask;
                var tx = tile % tilesPerSide;
                var ty = tile / tilesPerSide;

                int slot;
                if (SlotOf.TryGetValue(key, out var found))
                {
                    slot = found;
                }
                else
                {
                    Cells.ResizeUninitialized(Cells.Length + CellsPerTile);
                    Keys.Add(key);
                    Maxes.Add((short)0);
                    slot = Keys.Length - 1;
                    SlotOf.Add(key, slot);
                }

                GiWorld.QueryRegion(World, gridId, Layer, tx * TileCells, ty * TileCells, TileCells, TileCells,
                    (short*)Cells.GetUnsafePtr() + (slot * CellsPerTile));

                var cells = (short*)Cells.GetUnsafePtr() + (slot * CellsPerTile);
                var max = short.MinValue;
                var nonzero = false;
                for (var i = 0; i < CellsPerTile; i++)
                {
                    if (cells[i] != 0) nonzero = true;
                    if (cells[i] > max) max = cells[i];
                }

                if (!nonzero)
                {
                    Remove(key, slot);
                    return;
                }

                Maxes[slot] = max < 0 ? (short)0 : max;
            }

            private void Remove(int key, int slot)
            {
                var last = Keys.Length - 1;
                var lastKey = Keys[last];
                Keys.RemoveAtSwapBack(slot);
                Maxes.RemoveAtSwapBack(slot);
                if (slot < Keys.Length)
                {
                    var cells = (short*)Cells.GetUnsafePtr();
                    UnsafeUtility.MemCpy(cells + (slot * CellsPerTile), cells + (Keys.Length * CellsPerTile), CellsPerTile * sizeof(short));
                    SlotOf[lastKey] = slot;
                }

                Cells.ResizeUninitialized(Cells.Length - CellsPerTile);
                SlotOf.Remove(key);
            }
        }

        private struct GiFieldDrawBoundsJob : IJob
        {
            public Drawer Drawer;
            public CameraCulling Culling;
            public CameraCulling CullingOther;
            public byte World;
            public GiTables Tables;
            public GiPlane Plane;
            public float3 Lift;
            public float GridStep;
            public Color Bounds0;
            public Color Bounds1;
            public Color Bounds2;
            public Color Bounds3;

            public void Execute()
            {
                for (var grid = 0; grid < Tables.GridCount; grid++)
                {
                    if (!Tables.TryGrid(grid, out var gridId)) continue;
                    if (!GiGrid.TryGetSpec(World, gridId, out _, out var originX, out var originY, out var size) || size <= 0f) continue;

                    Plane.EmbedPoint(originX + (size * 0.5f), originY + (size * 0.5f), out var cx, out var cy, out var cz);
                    var center = new float3(cx, cy, cz) + (Lift * grid);
                    var bounds = new AABB { Center = center, Extents = new float3(size * 0.5f, 0.5f, size * 0.5f) };
                    if (!Visible(Culling, CullingOther, bounds)) continue;

                    Drawer.Cuboid(center, quaternion.identity, new float3(size, 0.02f, size), GridColor(grid));
                }
            }

            private Color GridColor(int grid)
            {
                return (grid & 3) switch
                {
                    0 => Bounds0,
                    1 => Bounds1,
                    2 => Bounds2,
                    _ => Bounds3,
                };
            }
        }

        private struct GiFieldDrawSourcesJob : IJobChunk
        {
            public Drawer Drawer;
            public CameraCulling Culling;
            public CameraCulling CullingOther;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] public ComponentTypeHandle<GiSource> Sources;
            public GiTables Tables;
            public GiPlane Plane;
            public float3 Lift;
            public float3 Normal;
            public Color Color;
            public NativeQueue<int>.ParallelWriter Drawn;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var transforms = chunk.GetNativeArray(ref Transforms);
                var sources = chunk.GetNativeArray(ref Sources);
                for (var i = 0; i < chunk.Count; i++)
                {
                    var source = sources[i];
                    if (!source.Placed) continue;

                    var position = transforms[i].Position;
                    Plane.Project(position.x, position.y, position.z, out var x, out var y);
                    var radius = math.max(0.25f, StampRadius(Tables, source.Stamp));
                    Plane.EmbedPoint(x, y, out var ox, out var oy, out var oz);
                    var origin = new float3(ox, oy, oz) + Lift;
                    if (!Visible(Culling, CullingOther, position, origin, radius)) continue;

                    Drawer.Point(position, 0.2f, Color);
                    Drawer.Circle(origin, Normal * radius, 24, Color);
                    Drawn.Enqueue(1);
                }
            }

            private static float StampRadius(GiTables tables, GiStampKey stamp)
            {
                if (!tables.TryStamp(stamp, out var handle) || !GiStamp.TryGetSpec(handle, out var spec)) return 0.5f;
                return spec.StampShape == GiStamp.Shape.Disk || spec.StampShape == GiStamp.Shape.Cone ||
                       spec.StampShape == GiStamp.Shape.Dome
                    ? math.max(1f, spec.Width)
                    : math.max(1f, spec.Width + spec.Height) * 0.5f;
            }
        }

        [BurstCompile]
        private unsafe struct GiFieldDrawBindingsJob : IJobChunk
        {
            public Drawer Drawer;
            public CameraCulling Culling;
            public CameraCulling CullingOther;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
            public DynamicComponentTypeHandle Data;
            public int TypeSize;
            public ushort Offset;
            public byte Kind;
            public float Reach;
            public GiPlane Plane;
            public float3 Lift;
            public float3 Normal;
            public Color Color;
            public NativeQueue<int>.ParallelWriter Drawn;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                if (TypeSize <= 0 || !chunk.Has(ref Data)) return;

                var data = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref Data, TypeSize).GetUnsafeReadOnlyPtr();
                var transforms = chunk.GetNativeArray(ref Transforms);
                var rows = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (rows.NextEntityIndex(out var i))
                {
                    var field = (float*)(data + (i * TypeSize) + Offset);
                    var x = field[0];
                    var y = field[1];
                    if (x == 0f && y == 0f) continue;

                    var position = transforms[i].Position;
                    Plane.Project(position.x, position.y, position.z, out var px, out var py);
                    Plane.EmbedPoint(px, py, out var ox, out var oy, out var oz);
                    var origin = new float3(ox, oy, oz) + Lift;

                    if (Kind == (byte)GiBindingKind.Max || Kind == (byte)GiBindingKind.Nearest)
                    {
                        Plane.EmbedPoint(x, y, out var bx, out var by, out var bz);
                        var best = new float3(bx, by, bz) + Lift;
                        if (!Visible(Culling, CullingOther, origin, best, math.max(0.15f, Reach))) continue;

                        Drawer.Line(origin, best, Color);
                        Drawer.Point(best, 0.15f, Color);
                        if (Kind == (byte)GiBindingKind.Nearest && Reach > 0f)
                        {
                            Drawer.Circle(origin, Normal * Reach, 24, Color);
                        }
                    }
                    else
                    {
                        Plane.EmbedVector(x, y, out var vx, out var vy, out var vz);
                        var vector = new float3(vx, vy, vz);
                        if (math.lengthsq(vector) <= 0f) continue;

                        if (!Visible(Culling, CullingOther, origin, origin + vector, 0.1f)) continue;
                        Drawer.Arrow(origin, math.normalizesafe(vector) * 2f, Color);
                    }

                    Drawn.Enqueue(1);
                }
            }
        }

        [BurstCompile]
        private struct GiFieldDrawHeatJob : IJob
        {
            public Drawer Drawer;
            public CameraCulling Culling;
            public CameraCulling CullingOther;
            [ReadOnly] public NativeList<int> Keys;
            [ReadOnly] public NativeList<short> Cells;
            [ReadOnly] public NativeList<short> Maxes;
            public int GridKeyBase;
            public int TilesPerSide;
            public GiPlane Plane;
            public float OriginX;
            public float OriginY;
            public float CellSize;
            public float3 Lift;
            public float Scale;
            public float4 Cold;
            public float4 Hot;
            public Color Outline;
            public Color ValueColor;
            public bool PerCell;
            public bool Values;
            public NativeArray<int> Slots;

            public void Execute()
            {
                using var marker = GiFieldDrawMarkers.Heat.Auto();
                var span = TileCells * CellSize;
                var frameMax = 0;
                if (Scale <= 0f)
                {
                    for (var i = 0; i < Keys.Length; i++)
                    {
                        if ((Keys[i] & ~TileKeyMask) != GridKeyBase) continue;
                        frameMax = math.max(frameMax, Maxes[i]);
                    }
                }

                var scale = Scale > 0f ? Scale : math.max(1f, frameMax);
                var visible = 0;
                var drawn = 0;

                for (var i = 0; i < Keys.Length; i++)
                {
                    if ((Keys[i] & ~TileKeyMask) != GridKeyBase) continue;

                    var tile = Keys[i] & TileKeyMask;
                    var tx = tile % TilesPerSide;
                    var ty = tile / TilesPerSide;
                    Plane.EmbedPoint(OriginX + ((tx + 0.5f) * span), OriginY + ((ty + 0.5f) * span), out var cx, out var cy, out var cz);
                    var center = new float3(cx, cy, cz) + Lift;
                    var bounds = new AABB { Center = center, Extents = new float3(span * 0.5f, 0.5f, span * 0.5f) };
                    if (!Visible(Culling, CullingOther, bounds)) continue;

                    visible++;
                    var max = Maxes[i];

                    if (Values && max != 0)
                    {
                        var text = new FixedString64Bytes();
                        text.Append(max);
                        Drawer.Text64(center, text, ValueColor, 8f);
                    }

                    if (PerCell)
                    {
                        drawn += DrawCells(i, tx, ty, scale);
                        continue;
                    }

                    if (max <= 0) continue;

                    Corners(tx, ty, span, out var a, out var b, out var c, out var d);
                    Drawer.SolidQuad(a, b, c, d, ColorFor(max / scale));
                    Drawer.Quad(a, b, c, d, Outline);
                    drawn++;
                }

                Slots[2] += visible;
                Slots[3] += drawn;
            }

            private int DrawCells(int slot, int tx, int ty, float scale)
            {
                var cells = (short*)Cells.GetUnsafeReadOnlyPtr() + (slot * CellsPerTile);
                var drawn = 0;
                for (var cy = 0; cy < TileCells; cy++)
                {
                    for (var cx = 0; cx < TileCells; cx++)
                    {
                        var value = cells[(cy * TileCells) + cx];
                        if (value <= 0) continue;

                        var x0 = OriginX + ((tx * TileCells + cx) * CellSize);
                        var y0 = OriginY + ((ty * TileCells + cy) * CellSize);
                        Plane.EmbedPoint(x0, y0, out var ax, out var ay, out var az);
                        Plane.EmbedPoint(x0 + CellSize, y0, out var bx, out var by, out var bz);
                        Plane.EmbedPoint(x0 + CellSize, y0 + CellSize, out var ex, out var ey, out var ez);
                        Plane.EmbedPoint(x0, y0 + CellSize, out var dx, out var dy, out var dz);
                        Drawer.SolidQuad(
                            new float3(ax, ay, az) + Lift,
                            new float3(bx, by, bz) + Lift,
                            new float3(ex, ey, ez) + Lift,
                            new float3(dx, dy, dz) + Lift,
                            ColorFor(value / scale));
                        drawn++;
                    }
                }

                return drawn;
            }

            private void Corners(int tx, int ty, float span, out float3 a, out float3 b, out float3 c, out float3 d)
            {
                Plane.EmbedPoint(OriginX + (tx * span), OriginY + (ty * span), out var ax, out var ay, out var az);
                Plane.EmbedPoint(OriginX + ((tx + 1f) * span), OriginY + (ty * span), out var bx, out var by, out var bz);
                Plane.EmbedPoint(OriginX + ((tx + 1f) * span), OriginY + ((ty + 1f) * span), out var cx, out var cy, out var cz);
                Plane.EmbedPoint(OriginX + (tx * span), OriginY + ((ty + 1f) * span), out var dx, out var dy, out var dz);
                a = new float3(ax, ay, az) + Lift;
                b = new float3(bx, by, bz) + Lift;
                c = new float3(cx, cy, cz) + Lift;
                d = new float3(dx, dy, dz) + Lift;
            }

            private Color ColorFor(float normalized)
            {
                var value = math.lerp(Cold, Hot, math.saturate(normalized));
                return new Color(value.x, value.y, value.z, value.w);
            }
        }

        [BurstCompile]
        private struct GiFieldDrawPublishJob : IJob
        {
            public NativeArray<int> Slots;
            public NativeQueue<int> SourceDraws;
            public NativeQueue<int> BindingDraws;
            public ComponentLookup<GiFieldDrawStats> Stats;
            public Entity Target;
            public byte GridCount;
            public byte Layer;
            public byte Drawn;

            public void Execute()
            {
                var sources = 0;
                while (SourceDraws.TryDequeue(out _)) sources++;
                var bindings = 0;
                while (BindingDraws.TryDequeue(out _)) bindings++;

                var stats = Stats[Target];
                stats.UpdatedTiles = Slots[0];
                stats.MirrorTiles = Slots[1];
                stats.VisibleTiles = Slots[2];
                stats.DrawnTiles = Slots[3];
                stats.DrawnSources = sources;
                stats.DrawnBindings = bindings;
                stats.GridCount = GridCount;
                stats.Layer = Layer;
                stats.Drawn = Drawn;
                Stats[Target] = stats;
            }
        }
    }
}
