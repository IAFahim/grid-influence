using System.Collections.Generic;
using Gi.Entities.Presentation;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiExplainWindow : EditorWindow
    {
        private readonly GiExplainModel _model = new GiExplainModel();
        private DropdownField _worlds;
        private DropdownField _layers;
        private FloatField _x;
        private FloatField _y;
        private Label _summary;
        private VisualElement _contributions;
        private VisualElement _recipe;
        private GiSourceInspector _entity;
        private IVisualElementScheduledItem _poll;
        private GiExplainResult _shown;
        private GiEntityReport _shownReport;

        public GiExplainModel Model => _model;

        [MenuItem("Gi/Explain Cell")]
        public static GiExplainWindow Open()
        {
            var window = GetWindow<GiExplainWindow>("Gi Explain");
            window.Show();
            return window;
        }

        public static void ExplainAt(string ecsWorld, int layerKey, Vector2 world)
        {
            var window = Open();
            window.Query(ecsWorld, layerKey, world);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            _worlds = new DropdownField("ECS world");
            _layers = new DropdownField("Layer");
            _x = new FloatField("X");
            _y = new FloatField("Y");
            root.Add(_worlds);
            root.Add(_layers);
            root.Add(_x);
            root.Add(_y);
            root.Add(new Button(Submit) { text = "Explain cell" });
            root.Add(new Button(InspectSelected) { text = "Inspect selected entity" });
            _summary = new Label("Pick a cell in the Field window, or enter one here.");
            root.Add(_summary);
            _contributions = new VisualElement();
            root.Add(_contributions);
            root.Add(new Label("Recipe"));
            _recipe = new VisualElement();
            root.Add(_recipe);
            root.Add(new Label("Entity"));
            _entity = new GiSourceInspector();
            _entity.Show(null);
            root.Add(_entity);

            GiInspectorBoard.WorldsChanged += RefreshWorlds;
            RefreshWorlds();
            _poll = root.schedule.Execute(Tick).Every(100);
        }

        public void Query(string ecsWorld, int layerKey, Vector2 world)
        {
            if (_worlds == null) CreateGUI();

            if (!_worlds.choices.Contains(ecsWorld)) RefreshWorlds();
            _worlds.SetValueWithoutNotify(ecsWorld);
            FillLayers();
            _layers.SetValueWithoutNotify("Layer " + layerKey);
            _x.SetValueWithoutNotify(world.x);
            _y.SetValueWithoutNotify(world.y);
            Submit();
        }

        public void Tick()
        {
            _model.Poll();
            Show();
            Selected();
        }

        private void OnDisable()
        {
            GiInspectorBoard.WorldsChanged -= RefreshWorlds;
            _poll?.Pause();
            _model.Clear();
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
                return;
            }

            if (!names.Contains(_worlds.value)) _worlds.SetValueWithoutNotify(names[0]);
            FillLayers();
        }

        private void FillLayers()
        {
            var info = GiInspectorBoard.Find(_worlds.value);
            var layers = new List<string>();
            if (info != null)
                for (var i = 0; i < info.LayerCount; i++)
                    layers.Add("Layer " + i);
            _layers.choices = layers;
            if (layers.Count > 0 && !layers.Contains(_layers.value)) _layers.SetValueWithoutNotify(layers[0]);
        }

        private int LayerKey()
        {
            var index = _layers.choices == null ? -1 : new List<string>(_layers.choices).IndexOf(_layers.value);
            return Mathf.Max(0, index);
        }

        private void Submit()
        {
            if (string.IsNullOrEmpty(_worlds.value)) return;

            _model.Explain(_worlds.value, LayerKey(), new Vector2(_x.value, _y.value));
        }

        private void InspectSelected()
        {
            if (!GiEntitySelection.TryGetSelected(out var world, out var entity)) return;

            _model.Inspect(world.Name, entity);
        }

        private void Selected()
        {
            if (_model.Waiting || !GiEntitySelection.TryGetSelected(out var world, out var entity)) return;
            if (GiInspectorBoard.Find(world.Name) == null) return;

            _model.Inspect(world.Name, entity);
        }

        private void Show()
        {
            if (_model.Report != _shownReport)
            {
                _shownReport = _model.Report;
                _entity.Show(_shownReport);
            }

            if (_model.Latest == _shown) return;

            _shown = _model.Latest;
            _contributions.Clear();
            _recipe.Clear();
            if (_shown == null) return;

            _summary.text = _shown.Valid ? GiExplainText.Summary(_shown.Explanation) : "Nothing to explain at that cell.";
            for (var i = 0; i < _shown.Contributions.Length; i++)
            {
                var entity = _shown.Entities[i];
                var world = _model.EcsWorld;
                var button = new Button(() => Follow(world, entity)) { text = GiExplainText.Contribution(_shown.Contributions[i]) };
                button.SetEnabled(entity != Entity.Null);
                _contributions.Add(button);
            }

            if (_shown.Steps.Length == 0) _recipe.Add(new Label("This is a deposited layer; there is no recipe."));
            foreach (var step in _shown.Steps) _recipe.Add(new Label(GiExplainText.Step(step)));
        }

        private void Follow(string ecsWorld, Entity entity)
        {
            var world = GiEntitySelection.Find(ecsWorld);
            if (GiEntitySelection.Select(world, entity)) _model.Inspect(ecsWorld, entity);
        }
    }
}
