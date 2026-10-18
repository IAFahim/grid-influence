using System.Collections.Generic;
using System.Globalization;
using Gi.Entities.Authoring;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiProfileOverview : VisualElement
    {
        public const int MapSide = 128;
        public const int ThumbScale = 3;

        private static readonly Color32[] Palette =
        {
            new Color32(40, 40, 40, 255), new Color32(86, 180, 233, 255), new Color32(230, 159, 0, 255), new Color32(0, 158, 115, 255),
            new Color32(204, 121, 167, 255), new Color32(240, 228, 66, 255), new Color32(0, 114, 178, 255), new Color32(213, 94, 0, 255),
        };

        private readonly Label _estimate = new Label();
        private readonly Image _map = new Image { scaleMode = ScaleMode.ScaleToFit };
        private readonly VisualElement _thumbs = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
        private readonly List<Texture2D> _textures = new List<Texture2D>();

        public GiProfileOverview()
        {
            _map.style.width = 256;
            _map.style.height = 256;
            Add(new Label("Grid coverage (colour = the finest grid at that point)"));
            Add(_map);
            Add(_estimate);
            Add(new Label("Layer thumbnails (a sample field on the finest grid)"));
            Add(_thumbs);
            RegisterCallback<DetachFromPanelEvent>(_ => Release());
        }

        public string EstimateText => _estimate.text;

        public int ThumbnailCount => _thumbs.childCount;

        public bool HasMap => _map.image != null;

        public static string EstimateOf(GiProfileSpec spec)
        {
            return "Worst-case Gi memory: " + GiProfileEstimate.Format(GiProfileEstimate.Worst(spec)) + " (every tile live; " +
                   spec.Grids.Length.ToString(CultureInfo.InvariantCulture) + " grids x " + spec.Layers.Length.ToString(CultureInfo.InvariantCulture) + " layers)";
        }

        public void Show(GiProfile profile)
        {
            Release();
            var spec = profile.ToSpec();
            _estimate.text = EstimateOf(spec);
            DrawMap(spec);
            DrawThumbnails(profile, spec);
        }

        private void DrawMap(GiProfileSpec spec)
        {
            _map.image = null;
            if (spec.Grids.Length == 0) return;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var grid in spec.Grids)
            {
                if (!(grid.Size > 0f)) continue;

                minX = Mathf.Min(minX, grid.X);
                minY = Mathf.Min(minY, grid.Y);
                maxX = Mathf.Max(maxX, grid.X + grid.Size);
                maxY = Mathf.Max(maxY, grid.Y + grid.Size);
            }

            if (minX > maxX) return;

            var pad = Mathf.Max(maxX - minX, maxY - minY) * 0.05f;
            var map = new byte[MapSide * MapSide];
            GiGridCoverage.Map(spec.Grids, minX - pad, minY - pad, maxX + pad, maxY + pad, MapSide, MapSide, map);
            var pixels = new Color32[map.Length];
            for (var row = 0; row < MapSide; row++)
            for (var column = 0; column < MapSide; column++)
                pixels[(MapSide - 1 - row) * MapSide + column] = Palette[map[row * MapSide + column] % Palette.Length];
            var texture = NewTexture(MapSide, MapSide);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            _map.image = texture;
        }

        private void DrawThumbnails(GiProfile profile, GiProfileSpec spec)
        {
            _thumbs.Clear();
            if (profile.Check().Count > 0 || !GiProfileThumbnails.TryRender(spec, out var layers, out var error))
            {
                _thumbs.Add(new Label("Fix the profile's issues to see thumbnails."));
                return;
            }

            for (var l = 0; l < layers.Length; l++)
            {
                var cell = new VisualElement();
                var asset = profile.Layers[l];
                cell.Add(new Label(l + ": " + (asset == null ? "?" : asset.name)));
                var image = new Image { scaleMode = ScaleMode.ScaleToFit };
                image.style.width = GiProfileThumbnails.Side * ThumbScale;
                image.style.height = GiProfileThumbnails.Side * ThumbScale;
                image.image = Thumbnail(layers[l]);
                cell.Add(image);
                _thumbs.Add(cell);
            }
        }

        private Texture2D Thumbnail(short[] values)
        {
            var side = GiProfileThumbnails.Side;
            var peak = 1;
            foreach (var value in values) peak = Mathf.Max(peak, Mathf.Abs(value));
            var pixels = new Color32[values.Length];
            for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var t = Mathf.Clamp(values[y * side + x] / (float)peak, -1f, 1f);
                var color = t >= 0f ? Color.Lerp(Color.white, new Color(0.90f, 0.45f, 0.05f), t) : Color.Lerp(Color.white, new Color(0.05f, 0.35f, 0.75f), -t);
                pixels[y * side + x] = (Color32)color;
            }

            var texture = NewTexture(side, side);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private Texture2D NewTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            _textures.Add(texture);
            return texture;
        }

        private void Release()
        {
            foreach (var texture in _textures)
                if (texture != null)
                    Object.DestroyImmediate(texture);
            _textures.Clear();
        }
    }
}
