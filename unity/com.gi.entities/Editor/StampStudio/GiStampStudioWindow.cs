using System.Collections.Generic;
using Gi.Entities.Authoring;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiStampStudioWindow : EditorWindow
    {
        private const int Cells = GiStampPreview.Cells;

        private readonly short[] _exact = new short[Cells * Cells];
        private readonly short[] _cell = new short[Cells * Cells];
        private readonly Color32[] _pixels = new Color32[Cells * Cells];
        private GiStampPreview _preview;
        private GiStampAsset _stamp;
        private Texture2D _exactTexture;
        private Texture2D _cellTexture;
        private Image _exactImage;
        private Image _cellImage;
        private GiIssueBox _issues;
        private Label _status;
        private float _phaseX;
        private float _phaseY;
        private float _angleDegrees;
        private float _scale = 1f;

        public GiStampAsset Stamp => _stamp;

        public GiStampPreview Preview => _preview;

        public ulong LastHash { get; private set; }

        public GiPreviewBounds LastBounds { get; private set; }

        public int TemporaryStamps => _preview == null ? 0 : _preview.TemporaryStamps;

        [MenuItem("Gi/Stamp Studio")]
        public static void Open()
        {
            GetWindow<GiStampStudioWindow>("Gi Stamp Studio").Show();
        }

        public static GiStampStudioWindow Open(GiStampAsset stamp)
        {
            var window = GetWindow<GiStampStudioWindow>("Gi Stamp Studio");
            window.Show();
            window.Edit(stamp);
            return window;
        }

        public void Edit(GiStampAsset stamp)
        {
            _stamp = stamp;
            Refresh();
        }

        public void SetPose(float phaseX, float phaseY, float angleDegrees, float scale)
        {
            _phaseX = Mathf.Clamp01(phaseX);
            _phaseY = Mathf.Clamp01(phaseY);
            _angleDegrees = angleDegrees;
            _scale = Mathf.Max(0.05f, scale);
            Refresh();
        }

        public void Refresh()
        {
            Ensure();
            var issues = new List<GiProfileIssue>();
            if (_stamp == null)
            {
                _preview.Clear();
                Draw();
                Report(issues, "Choose a stamp.");
                return;
            }

            var cells = new List<sbyte>();
            var spec = _stamp.ToSpec(cells);
            if (!GiStampPreview.TryFootprint(spec, _scale, out var footprint)) issues.Add(new GiProfileIssue(GiProfileSection.Stamp, 0, footprint));
            if (!_preview.Set(spec, cells.ToArray(), out var error))
            {
                issues.Add(new GiProfileIssue(GiProfileSection.Stamp, 0, error));
                Draw();
                Report(issues, "No preview.");
                return;
            }

            if (issues.Count == 0)
            {
                LastHash = _preview.Render(new GiPreviewPose(_phaseX, _phaseY, _angleDegrees * Mathf.Deg2Rad, _scale), _exact, _cell, out var bounds);
                LastBounds = bounds;
            }

            Draw();
            Report(issues, issues.Count == 0
                ? "Footprint " + LastBounds.Width + " x " + LastBounds.Height + " cells, hash " + LastHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + "."
                : "No preview.");
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            var picker = new ObjectField("Stamp") { objectType = typeof(GiStampAsset), allowSceneObjects = false };
            picker.RegisterValueChangedCallback(change => Edit(change.newValue as GiStampAsset));
            root.Add(picker);
            root.Add(Slider("Phase X", 0f, 1f, _phaseX, value => SetPose(value, _phaseY, _angleDegrees, _scale)));
            root.Add(Slider("Phase Y", 0f, 1f, _phaseY, value => SetPose(_phaseX, value, _angleDegrees, _scale)));
            root.Add(Slider("Angle", 0f, 360f, _angleDegrees, value => SetPose(_phaseX, _phaseY, value, _scale)));
            root.Add(Slider("Scale", 0.25f, 4f, _scale, value => SetPose(_phaseX, _phaseY, _angleDegrees, value)));

            var texture = new ObjectField("Import texture") { objectType = typeof(Texture), allowSceneObjects = false };
            texture.RegisterValueChangedCallback(change =>
            {
                if (_stamp == null || change.newValue == null) return;

                if (!GiStampImport.Apply(new SerializedObject(_stamp), change.newValue as Texture, out var error)) _status.text = error;
                else Refresh();
            });
            root.Add(texture);

            var panes = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _exactImage = Pane(panes, "Exact");
            _cellImage = Pane(panes, "Cell");
            root.Add(panes);
            _status = new Label();
            root.Add(_status);
            _issues = new GiIssueBox();
            root.Add(_issues);
            if (EditorApplication.isPlaying)
                root.Add(new HelpBox("Editing during play flushes Gi's kernel caches, so expect a hitch.", HelpBoxMessageType.Warning));
            Refresh();
        }

        private void OnDisable()
        {
            _preview?.Dispose();
            _preview = null;
            if (_exactTexture != null) DestroyImmediate(_exactTexture);
            if (_cellTexture != null) DestroyImmediate(_cellTexture);
            _exactTexture = null;
            _cellTexture = null;
        }

        private static Slider Slider(string label, float low, float high, float value, System.Action<float> changed)
        {
            var slider = new Slider(label, low, high) { showInputField = true, value = value };
            slider.RegisterValueChangedCallback(change => changed(change.newValue));
            return slider;
        }

        private static Image Pane(VisualElement parent, string title)
        {
            var column = new VisualElement();
            column.Add(new Label(title));
            var image = new Image { scaleMode = ScaleMode.ScaleToFit };
            image.style.width = 256;
            image.style.height = 256;
            column.Add(image);
            parent.Add(column);
            return image;
        }

        private void Ensure()
        {
            if (_preview == null) _preview = new GiStampPreview();
            if (_exactTexture == null) _exactTexture = NewTexture();
            if (_cellTexture == null) _cellTexture = NewTexture();
        }

        private static Texture2D NewTexture()
        {
            return new Texture2D(Cells, Cells, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
        }

        private void Draw()
        {
            Paint(_exact, _exactTexture);
            Paint(_cell, _cellTexture);
            if (_exactImage != null) _exactImage.image = _exactTexture;
            if (_cellImage != null) _cellImage.image = _cellTexture;
        }

        private void Paint(short[] values, Texture2D texture)
        {
            var peak = 1;
            foreach (var value in values) peak = Mathf.Max(peak, Mathf.Abs(value));
            for (var i = 0; i < values.Length; i++) _pixels[i] = Color(values[i], peak);
            texture.SetPixels32(_pixels);
            texture.Apply(false);
        }

        private static Color32 Color(int value, int peak)
        {
            var t = Mathf.Clamp(value / (float)peak, -1f, 1f);
            var warm = new Color(0.90f, 0.45f, 0.05f);
            var cool = new Color(0.05f, 0.35f, 0.75f);
            var color = t >= 0f ? UnityEngine.Color.Lerp(UnityEngine.Color.white, warm, t) : UnityEngine.Color.Lerp(UnityEngine.Color.white, cool, -t);
            return value == 0 ? new Color32(255, 255, 255, 0) : (Color32)color;
        }

        private void Report(List<GiProfileIssue> issues, string status)
        {
            if (_status != null) _status.text = status;
            _issues?.Show(issues);
        }
    }
}
