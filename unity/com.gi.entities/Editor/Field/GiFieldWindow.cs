using System.Collections.Generic;
using System.Globalization;
using Gi.Entities.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiFieldWindow : EditorWindow
    {
        private readonly GiFieldModel _model = new GiFieldModel();
        private DropdownField _worlds;
        private DropdownField _grids;
        private DropdownField _layers;
        private Image _heatmap;
        private Image _tiles;
        private Toggle _occupancy;
        private Toggle _changed;
        private Slider _reach;
        private Label _readout;
        private Label _status;
        private VisualElement _pinList;
        private VisualElement _facts;
        private Texture2D _tileTexture;
        private IVisualElementScheduledItem _poll;

        public GiFieldModel Model => _model;

        [MenuItem("Gi/Field Window")]
        public static void Open()
        {
            GetWindow<GiFieldWindow>("Gi Field").Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            _worlds = new DropdownField("ECS world");
            _grids = new DropdownField("Grid");
            _layers = new DropdownField("Layer");
            _worlds.RegisterValueChangedCallback(_ => Reselect(true));
            _grids.RegisterValueChangedCallback(_ => Reselect(false));
            _layers.RegisterValueChangedCallback(_ => Reselect(false));
            root.Add(_worlds);
            root.Add(_grids);
            root.Add(_layers);

            _heatmap = new Image { scaleMode = ScaleMode.ScaleToFit };
            _heatmap.style.width = 512;
            _heatmap.style.height = 512;
            _heatmap.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _tiles = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            _tiles.style.position = Position.Absolute;
            _tiles.style.width = 512;
            _tiles.style.height = 512;
            var stage = new VisualElement();
            stage.style.width = 512;
            stage.style.height = 512;
            stage.Add(_heatmap);
            stage.Add(_tiles);
            root.Add(stage);

            _occupancy = new Toggle("Tile occupancy") { value = false };
            _changed = new Toggle("Tiles changed this tick") { value = false };
            root.Add(_occupancy);
            root.Add(_changed);
            _reach = new Slider("Reach (world units)", 1f, 64f) { value = _model.Reach, showInputField = true };
            _reach.RegisterValueChangedCallback(change => _model.Reach = change.newValue);
            root.Add(_reach);
            _readout = new Label("Move over the heatmap to probe.");
            root.Add(_readout);
            root.Add(new Button(() => _model.Pin()) { text = "Pin probe" });
            _pinList = new VisualElement();
            root.Add(_pinList);
            _status = new Label();
            root.Add(_status);
            _facts = new VisualElement();
            root.Add(new Label("Runtime"));
            root.Add(_facts);

            GiInspectorBoard.WorldsChanged += RefreshWorlds;
            RefreshWorlds();
            _poll = root.schedule.Execute(Tick).Every(100);
        }

        public void Tick()
        {
            _model.Poll();
            Paint();
            Report();
        }

        private void OnDisable()
        {
            GiInspectorBoard.WorldsChanged -= RefreshWorlds;
            _poll?.Pause();
            _model.Dispose();
            if (_tileTexture != null) DestroyImmediate(_tileTexture);
            _tileTexture = null;
        }

        private void RefreshWorlds()
        {
            if (_worlds == null) return;

            var names = new List<string>();
            foreach (var world in GiInspectorBoard.All)
                if (world.Alive)
                    names.Add(world.EcsWorld);
            _worlds.choices = names;
            if (names.Count == 0)
            {
                _worlds.SetValueWithoutNotify(string.Empty);
                _model.Select(null, 0, 0);
                return;
            }

            if (!names.Contains(_worlds.value)) _worlds.SetValueWithoutNotify(names[0]);
            Reselect(true);
        }

        private void Reselect(bool worldChanged)
        {
            var info = GiInspectorBoard.Find(_worlds.value);
            if (info == null || !info.Alive) return;

            if (worldChanged)
            {
                var grids = new List<string>();
                foreach (var grid in info.Grids) grids.Add(grid.Index + ": 2^" + grid.Power + " cells over " + grid.Size.ToString("0.##", CultureInfo.InvariantCulture));
                _grids.choices = grids;
                if (grids.Count > 0) _grids.SetValueWithoutNotify(grids[0]);
                var layers = new List<string>();
                for (var i = 0; i < info.LayerCount; i++) layers.Add("Layer " + i);
                _layers.choices = layers;
                if (layers.Count > 0) _layers.SetValueWithoutNotify(layers[0]);
            }

            var gridIndex = Mathf.Max(0, _grids.choices == null ? 0 : new List<string>(_grids.choices).IndexOf(_grids.value));
            var layerIndex = Mathf.Max(0, _layers.choices == null ? 0 : new List<string>(_layers.choices).IndexOf(_layers.value));
            _model.Select(_worlds.value, gridIndex, layerIndex);
        }

        private void OnPointerMove(PointerMoveEvent move)
        {
            var view = _model.View;
            if (view == null || view.Mirror == null) return;

            var size = _heatmap.contentRect;
            var cell = new Vector2(view.Region.x + move.localPosition.x / size.width * view.Region.width,
                view.Region.y + (1f - move.localPosition.y / size.height) * view.Region.height);
            _model.Hover(_model.CellToWorld(cell));
        }

        private void Paint()
        {
            var view = _model.View;
            _heatmap.image = view?.Texture;
            _tiles.image = null;
            if (view == null || view.Mirror == null || !(_occupancy.value || _changed.value)) return;

            var side = view.Mirror.TilesPerSide;
            if (_tileTexture == null || _tileTexture.width != side)
            {
                if (_tileTexture != null) DestroyImmediate(_tileTexture);
                _tileTexture = new Texture2D(side, side, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            }

            var pixels = new Color32[side * side];
            for (var tile = 0; tile < pixels.Length; tile++)
            {
                var color = new Color32(0, 0, 0, 0);
                if (_occupancy.value && view.Mirror.IsLive(tile)) color = new Color32(0, 114, 178, 70);
                if (_changed.value && view.Mirror.WasChanged(tile)) color = new Color32(213, 94, 0, 140);
                pixels[tile] = color;
            }

            _tileTexture.SetPixels32(pixels);
            _tileTexture.Apply(false);
            _tiles.image = _tileTexture;
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void Report()
        {
            var text = new System.Text.StringBuilder();
            if (_model.HasCursor)
            {
                text.Append("(").Append(F(_model.Cursor.x)).Append(", ").Append(F(_model.Cursor.y)).Append(")");
                if (_model.TryLatest(GiProbeKind.Value, out var value)) text.Append("  value ").Append(value.Value);
                if (_model.TryLatest(GiProbeKind.Gradient, out var gradient))
                    text.Append("  gradient (").Append(F(gradient.GradientX)).Append(", ").Append(F(gradient.GradientY)).Append(")");
                if (_model.TryLatest(GiProbeKind.Area, out var area)) text.Append("  area ").Append(area.Total);
                if (_model.TryLatest(GiProbeKind.Max, out var max))
                    text.Append("  max ").Append(max.Value).Append(" at (").Append(F(max.PositionX)).Append(", ").Append(F(max.PositionY)).Append(")");
                if (_model.TryLatest(GiProbeKind.Nearest, out var near))
                {
                    if (near.Ok) text.Append("  nearest ").Append(near.Value).Append(" at (").Append(F(near.PositionX)).Append(", ").Append(F(near.PositionY)).Append(")");
                    else text.Append("  nothing in reach");
                }
            }
            else
            {
                text.Append("Move over the heatmap to probe.");
            }

            _readout.text = text.ToString();

            _pinList.Clear();
            foreach (var pin in _model.Pins)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
                row.Add(new Label("(" + F(pin.World.x) + ", " + F(pin.World.y) + ")"));
                var spark = new GiSparkline();
                spark.Show(pin.History);
                row.Add(spark);
                var captured = pin;
                row.Add(new Button(() => _model.Unpin(captured)) { text = "x" });
                _pinList.Add(row);
            }

            _status.text = _model.Info == null ? "No Gi world is running. Enter play mode or open a subscene with a profile." : "Tick " + _model.Info.Tick;
            _facts.Clear();
            foreach (var row in GiRuntimeFacts.Collect(_model.Info)) _facts.Add(new Label(row.Key + ": " + row.Value));
        }
    }
}
