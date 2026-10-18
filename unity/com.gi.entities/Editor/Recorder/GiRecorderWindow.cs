using System.Collections.Generic;
using System.Globalization;
using Gi.Entities.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using GiGrid = Gi.Grid;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor
{
    public sealed class GiRecorderWindow : EditorWindow
    {
        private const int Side = 256;

        private readonly GiRecorderModel _model = new GiRecorderModel();
        private DropdownField _worlds;
        private Toggle _record;
        private Label _status;
        private SliderInt _scrub;
        private Label _message;
        private Image _picture;
        private IntegerField _grid;
        private IntegerField _layer;
        private IntegerField _from;
        private IntegerField _to;
        private Label _tiles;
        private Texture2D _texture;
        private IVisualElementScheduledItem _poll;

        public GiRecorderModel Model => _model;

        [MenuItem("Gi/Flight Recorder")]
        public static void Open()
        {
            GetWindow<GiRecorderWindow>("Gi Recorder").Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            _worlds = new DropdownField("ECS world");
            _worlds.RegisterValueChangedCallback(_ => _model.Select(_worlds.value));
            root.Add(_worlds);
            _record = new Toggle("Record (gi.record)") { value = GiDebugConfig.Record.Data };
            _record.RegisterValueChangedCallback(change => _model.SetRecording(change.newValue));
            root.Add(_record);
            _status = new Label();
            root.Add(_status);
            _scrub = new SliderInt("Tick", 0, 1) { showInputField = true };
            root.Add(_scrub);
            root.Add(new Button(() => _model.ReplayTo(_scrub.value)) { text = "Replay to tick" });
            root.Add(new Button(() => _model.CompareWithLive()) { text = "Compare with live" });
            _grid = new IntegerField("Grid") { value = 0 };
            _layer = new IntegerField("Layer") { value = 0 };
            root.Add(_grid);
            root.Add(_layer);
            _picture = new Image { scaleMode = ScaleMode.ScaleToFit };
            _picture.style.width = Side * 2;
            _picture.style.height = Side * 2;
            root.Add(_picture);
            _tiles = new Label();
            root.Add(_tiles);
            _from = new IntegerField("Export from") { value = 0 };
            _to = new IntegerField("Export to") { value = 0 };
            root.Add(_from);
            root.Add(_to);
            root.Add(new Button(ExportRange) { text = "Export as a Gi test" });
            root.Add(new Button(SaveRecording) { text = "Save recording" });
            _message = new Label();
            root.Add(_message);

            GiInspectorBoard.WorldsChanged += RefreshWorlds;
            RefreshWorlds();
            _poll = root.schedule.Execute(Tick).Every(250);
        }

        public void Tick()
        {
            _record.SetValueWithoutNotify(GiDebugConfig.Record.Data);
            if (_model.TryStatus(out var ticks, out var first, out var last, out var bytes))
            {
                _status.text = (_model.Recording ? "Recording: " : "Paused: ") + ticks + " ticks (" + first + " to " + last + "), " +
                               bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
                _scrub.lowValue = first;
                _scrub.highValue = Mathf.Max(first + 1, last);
            }
            else
            {
                _status.text = _model.HasWorld ? "Nothing recorded yet. Turn recording on." : "No Gi world is running.";
            }

            _message.text = _model.Message;
            Paint();
        }

        private void OnDisable()
        {
            GiInspectorBoard.WorldsChanged -= RefreshWorlds;
            _poll?.Pause();
            _model.Dispose();
            if (_texture != null) DestroyImmediate(_texture);
            _texture = null;
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
                _model.Select(null);
                return;
            }

            if (!names.Contains(_worlds.value)) _worlds.SetValueWithoutNotify(names[0]);
            _model.Select(_worlds.value);
        }

        private void ExportRange()
        {
            _model.Export(System.IO.Path.Combine(Application.dataPath, "..", "Logs", "GiRecorder"), _from.value, _to.value, out _);
        }

        private void SaveRecording()
        {
            var folder = System.IO.Path.Combine(Application.dataPath, "..", "Logs", "GiRecorder");
            System.IO.Directory.CreateDirectory(folder);
            _model.Save(System.IO.Path.Combine(folder, "recording.girec"));
        }

        private void Paint()
        {
            var replay = _model.Replay;
            if (replay == null)
            {
                _picture.image = null;
                _tiles.text = string.Empty;
                return;
            }

            var pixels = Render(replay, _grid.value, _layer.value, _model.Diff, out var side);
            if (pixels == null)
            {
                _picture.image = null;
                return;
            }

            if (_texture == null || _texture.width != side)
            {
                if (_texture != null) DestroyImmediate(_texture);
                _texture = new Texture2D(side, side, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(false);
            _picture.image = _texture;
            _tiles.text = "Replay at tick " + _model.ReplayTick + ", " + _model.Diff.Count + " tiles differ from the live field.";
        }

        public static Color32[] Render(GiReplay replay, int gridIndex, int layerIndex, IReadOnlyList<GiTileDiff> diff, out int side)
        {
            side = 0;
            if (!replay.Tables.TryGrid(gridIndex, out var grid) || !replay.Tables.TryLayer(new GiLayerKey((byte)layerIndex), out var layer)) return null;
            if (!GiGrid.TryGetSpec(replay.World, grid, out var power, out _, out _, out _)) return null;

            var cells = 1 << power;
            side = Mathf.Min(cells, Side);
            var stride = cells / side;
            var pixels = new Color32[side * side];
            var scale = 1;
            for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
                scale = Mathf.Max(scale, Mathf.Abs(GiWorld.Query(replay.World, grid, layer, x * stride, y * stride)));

            for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var value = GiWorld.Query(replay.World, grid, layer, x * stride, y * stride);
                var t = Mathf.Clamp01(Mathf.Abs(value) / (float)scale);
                var color = value > 0 ? new Color32((byte)(40 + 173 * t), (byte)(40 + 54 * t), 0, 255)
                    : value < 0 ? new Color32(0, (byte)(40 + 74 * t), (byte)(40 + 138 * t), 255)
                    : new Color32(24, 24, 24, 255);
                pixels[(side - 1 - y) * side + x] = color;
            }

            var tilesPerSide = Mathf.Max(1, cells / GiTileHashes.TileSide);
            foreach (var d in diff)
            {
                if (d.Grid != gridIndex || d.Layer != layerIndex) continue;

                var tileX = d.Tile % tilesPerSide * GiTileHashes.TileSide / stride;
                var tileY = d.Tile / tilesPerSide * GiTileHashes.TileSide / stride;
                var span = Mathf.Max(1, GiTileHashes.TileSide / stride);
                for (var y = tileY; y < tileY + span && y < side; y++)
                for (var x = tileX; x < tileX + span && x < side; x++)
                {
                    var index = (side - 1 - y) * side + x;
                    var c = pixels[index];
                    pixels[index] = new Color32((byte)((c.r + 230) / 2), (byte)(c.g / 2), (byte)((c.b + 230) / 2), 255);
                }
            }

            return pixels;
        }
    }
}
