using System;
using System.Collections.Generic;
using Gi.Entities.Presentation;
using UnityEngine;

namespace Gi.Entities.Editor
{
    public sealed class GiPin
    {
        public Vector2 World;
        public GiHistory History = new GiHistory(240);
        internal int Pending;
    }

    public sealed class GiFieldModel : IDisposable
    {
        public const int KindCount = 5;

        private readonly int[] _pending = new int[KindCount];
        private readonly GiProbeResult[] _latest = new GiProbeResult[KindCount];
        private readonly bool[] _has = new bool[KindCount];
        private readonly List<GiPin> _pins = new List<GiPin>();

        public string EcsWorld { get; private set; }

        public int Grid { get; private set; }

        public int LayerKey { get; private set; }

        public GiView View { get; private set; }

        public float Reach { get; set; } = 8f;

        public short Threshold { get; set; } = 1;

        public bool HasCursor { get; private set; }

        public Vector2 Cursor { get; private set; }

        public IReadOnlyList<GiPin> Pins => _pins;

        public GiWorldInfo Info => EcsWorld == null ? null : GiInspectorBoard.Find(EcsWorld);

        public bool TryLatest(GiProbeKind kind, out GiProbeResult result)
        {
            result = _latest[(int)kind];
            return _has[(int)kind];
        }

        public void Select(string ecsWorld, int grid, int layerKey)
        {
            CloseView();
            EcsWorld = ecsWorld;
            Grid = grid;
            LayerKey = layerKey;
            Array.Clear(_has, 0, _has.Length);
            Array.Clear(_pending, 0, _pending.Length);
            _pins.Clear();
            HasCursor = false;
            if (ecsWorld != null) View = GiInspectorBoard.Open(ecsWorld, grid, layerKey);
        }

        public Vector2 CellToWorld(Vector2 cell)
        {
            var grid = GridInfo();
            return grid == null ? Vector2.zero : new Vector2(grid.X + cell.x / grid.CellsPerUnit, grid.Y + cell.y / grid.CellsPerUnit);
        }

        public Vector2 WorldToCell(Vector2 world)
        {
            var grid = GridInfo();
            return grid == null ? Vector2.zero : new Vector2((world.x - grid.X) * grid.CellsPerUnit, (world.y - grid.Y) * grid.CellsPerUnit);
        }

        public void Hover(Vector2 world)
        {
            if (EcsWorld == null) return;

            HasCursor = true;
            Cursor = world;
            for (var kind = 0; kind < KindCount; kind++) _pending[kind] = GiInspectorBoard.Request(EcsWorld, Request((GiProbeKind)kind, world));
        }

        public GiPin Pin()
        {
            if (!HasCursor) return null;

            var pin = new GiPin { World = Cursor };
            _pins.Add(pin);
            return pin;
        }

        public void Unpin(GiPin pin)
        {
            _pins.Remove(pin);
        }

        public int Poll()
        {
            var consumed = 0;
            for (var kind = 0; kind < KindCount; kind++)
            {
                if (_pending[kind] == 0 || !GiInspectorBoard.TryTake(_pending[kind], out var result)) continue;

                _latest[kind] = result;
                _has[kind] = true;
                _pending[kind] = 0;
                consumed++;
            }

            if (EcsWorld == null) return consumed;

            foreach (var pin in _pins)
            {
                if (pin.Pending != 0)
                {
                    if (!GiInspectorBoard.TryTake(pin.Pending, out var result)) continue;

                    pin.Pending = 0;
                    pin.History.Push(result.Tick, result.Value);
                    consumed++;
                }

                pin.Pending = GiInspectorBoard.Request(EcsWorld, Request(GiProbeKind.Value, pin.World));
            }

            return consumed;
        }

        public void Dispose()
        {
            CloseView();
            _pins.Clear();
            EcsWorld = null;
        }

        private GiProbeRequest Request(GiProbeKind kind, Vector2 world)
        {
            return new GiProbeRequest { Kind = kind, Layer = (byte)LayerKey, X = world.x, Y = world.y, Reach = Reach, Threshold = Threshold };
        }

        private GiGridInfo GridInfo()
        {
            var info = Info;
            return info != null && Grid >= 0 && Grid < info.Grids.Length ? info.Grids[Grid] : null;
        }

        private void CloseView()
        {
            if (View == null) return;

            GiInspectorBoard.Close(View);
            View = null;
        }
    }
}
