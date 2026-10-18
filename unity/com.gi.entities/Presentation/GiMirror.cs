using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using GiWorld = Gi.World;

namespace Gi.Entities.Presentation
{
    public sealed unsafe class GiMirror : IDisposable
    {
        public const string ComputeName = "GiMirror";

        private static readonly int TableId = Shader.PropertyToID("_Table");
        private static readonly int PoolId = Shader.PropertyToID("_Pool");
        private static readonly int OutId = Shader.PropertyToID("_Out");
        private static readonly int ResultId = Shader.PropertyToID("_Result");
        private static readonly int SourceId = Shader.PropertyToID("_Source");
        private static readonly int TargetId = Shader.PropertyToID("_Target");
        private static readonly int TilesPerSideId = Shader.PropertyToID("_TilesPerSide");
        private static readonly int RegionId = Shader.PropertyToID("_Region");
        private static readonly int ScaleId = Shader.PropertyToID("_Scale");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int CountId = Shader.PropertyToID("_Count");

        private static int _live;

        private readonly GiMirrorPlanner _planner;
        private readonly ComputeShader _compute;
        private readonly int _decode;
        private readonly int _heatmap;
        private readonly int _grow;
        private readonly int _tilesPerSide;
        private readonly int[] _taken;
        private readonly byte[] _changedFlags;
        private int _lastChanged;
        private NativeArray<int> _changed;
        private NativeArray<int> _staging;
        private NativeArray<int> _entry;
        private GraphicsBuffer _table;
        private GraphicsBuffer _pool;
        private int _poolSlots;
        private int _since;

        public GiMirror(byte world, byte grid, byte layer, int power, ComputeShader compute = null, int maxBudget = 4096)
        {
            World = world;
            _live++;
            Grid = grid;
            Layer = layer;
            Power = power;
            _tilesPerSide = GiMirrorPlanner.TilesPerSide(power);
            var tiles = _tilesPerSide * _tilesPerSide;
            _planner = new GiMirrorPlanner(tiles);
            _compute = compute != null ? compute : Resources.Load<ComputeShader>(ComputeName);
            _decode = _compute.FindKernel("Decode");
            _heatmap = _compute.FindKernel("Heatmap");
            _grow = _compute.FindKernel("Grow");
            _taken = new int[maxBudget];
            _changedFlags = new byte[tiles];
            _changed = new NativeArray<int>(tiles, Allocator.Persistent);
            _staging = new NativeArray<int>(GiMirrorPlanner.TileInts, Allocator.Persistent);
            _entry = new NativeArray<int>(1, Allocator.Persistent);
            _table = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tiles, sizeof(int));
            _table.SetData(new int[tiles]);
            Grow(GiMirrorPlanner.GrownCapacity(0, 1));
        }

        public static int Live => _live;

        public byte World { get; }

        public byte Grid { get; }

        public byte Layer { get; }

        public int Power { get; }

        public int Cells => 1 << Power;

        public int TileCount => _tilesPerSide * _tilesPerSide;

        public int LiveTiles => _planner.LiveTiles;

        public int Pending => _planner.Pending;

        public int TilesPerSide => _tilesPerSide;

        public bool IsLive(int tile) => _planner.SlotOf(tile) >= 0;

        public bool WasChanged(int tile) => (uint)tile < (uint)_changedFlags.Length && _changedFlags[tile] != 0;

        public int PoolSlots => _poolSlots;

        public long UploadedBytes { get; private set; }

        public int UploadedTiles { get; private set; }

        public int Update(int budget)
        {
            for (var i = 0; i < _lastChanged; i++) _changedFlags[_changed[i]] = 0;
            _lastChanged = 0;

            var changed = GiWorld.ChangedTiles(World, Grid, Layer, _since, (int*)null);
            if (changed > 0)
            {
                GiWorld.ChangedTiles(World, Grid, Layer, _since, (int*)_changed.GetUnsafePtr());
                for (var i = 0; i < changed; i++)
                {
                    _planner.Enqueue(_changed[i]);
                    _changedFlags[_changed[i]] = 1;
                }

                _lastChanged = changed;
            }

            _since = GiWorld.Tick(World);

            var take = _planner.Take(budget, _taken);
            var uploaded = 0;
            for (var i = 0; i < take; i++)
            {
                var tile = _taken[i];
                var slot = Upload(tile);
                if (slot >= -1) uploaded++;
            }

            UploadedTiles += uploaded;
            UploadedBytes += GiMirrorPlanner.UploadBytes(uploaded);
            return uploaded;
        }

        public void Decode(RectInt region, GraphicsBuffer output)
        {
            Bind(_decode, region);
            _compute.SetBuffer(_decode, OutId, output);
            _compute.Dispatch(_decode, (region.width + 7) / 8, (region.height + 7) / 8, 1);
        }

        public void Draw(RenderTexture target, RectInt region, float scale, float opacity = 0.85f)
        {
            Bind(_heatmap, region);
            _compute.SetFloat(ScaleId, scale);
            _compute.SetFloat(OpacityId, opacity);
            _compute.SetTexture(_heatmap, ResultId, target);
            _compute.Dispatch(_heatmap, (region.width + 7) / 8, (region.height + 7) / 8, 1);
        }

        public void Dispose()
        {
            if (_table != null) _live--;
            _table?.Dispose();
            _pool?.Dispose();
            _table = null;
            _pool = null;
            if (_changed.IsCreated) _changed.Dispose();
            if (_staging.IsCreated) _staging.Dispose();
            if (_entry.IsCreated) _entry.Dispose();
        }

        private int Upload(int tile)
        {
            var side = _tilesPerSide;
            var x = (tile % side) * GiMirrorPlanner.TileCells;
            var y = (tile / side) * GiMirrorPlanner.TileCells;
            GiWorld.QueryRegion(World, Grid, Layer, x, y, GiMirrorPlanner.TileCells, GiMirrorPlanner.TileCells, (short*)_staging.GetUnsafePtr());

            var empty = true;
            for (var i = 0; i < _staging.Length; i++)
                if (_staging[i] != 0)
                {
                    empty = false;
                    break;
                }

            var slot = _planner.Commit(tile, empty);
            if (slot >= _poolSlots) Grow(GiMirrorPlanner.GrownCapacity(_poolSlots, slot + 1));
            if (!empty) _pool.SetData(_staging, 0, slot * GiMirrorPlanner.TileInts, GiMirrorPlanner.TileInts);

            _entry[0] = empty ? 0 : slot + 1;
            _table.SetData(_entry, 0, tile, 1);
            return slot;
        }

        private void Grow(int slots)
        {
            var bigger = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots * GiMirrorPlanner.TileInts, sizeof(int));
            if (_pool != null)
            {
                var count = _poolSlots * GiMirrorPlanner.TileInts;
                _compute.SetBuffer(_grow, SourceId, _pool);
                _compute.SetBuffer(_grow, TargetId, bigger);
                _compute.SetInt(CountId, count);
                _compute.Dispatch(_grow, (count + 63) / 64, 1, 1);
                _pool.Dispose();
            }

            _pool = bigger;
            _poolSlots = slots;
        }

        private void Bind(int kernel, RectInt region)
        {
            _compute.SetBuffer(kernel, TableId, _table);
            _compute.SetBuffer(kernel, PoolId, _pool);
            _compute.SetInt(TilesPerSideId, _tilesPerSide);
            _compute.SetInts(RegionId, region.x, region.y, region.width, region.height);
        }
    }
}
