using UnityEngine;

namespace GiDemo
{

internal sealed class GiMinimap
{
    private const int Size = 128;

    private readonly int[] _threat = new int[Size * Size];
    private readonly int[] _food = new int[Size * Size];
    private readonly int[] _herd = new int[Size * Size];
    private readonly Color32[] _pixels = new Color32[Size * Size];
    private Texture2D _texture;
    private float _norm = 600f;
    private int _frame;

    internal void Draw()
    {
        if (_texture == null)
        {
            _texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            _texture.filterMode = FilterMode.Point;
        }

        if (++_frame % 6 == 0) Rebuild();

        var rect = new Rect(Screen.width - 320f, 20f, 296f, 296f);
        GUI.Box(rect, GUIContent.none);
        GUI.DrawTexture(rect, _texture, ScaleMode.StretchToFill);
    }

    private void Rebuild()
    {
        var world = (byte)GiEcosystemDemo.WorldId;
        var grid = GiEcosystemDemo.GridOne;
        var threatLayer = GiEcosystemDemo.Threat;
        var foodLayer = GiEcosystemDemo.Food;
        var herdLayer = GiEcosystemDemo.Herd;

        var peak = 1;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var threat = GiEcosystemDemo.Q(world, grid, threatLayer, x * 2, y * 2);
            var food = GiEcosystemDemo.Q(world, grid, foodLayer, x * 2, y * 2);
            var herd = GiEcosystemDemo.Q(world, grid, herdLayer, x * 2, y * 2);
            _threat[y * Size + x] = threat;
            _food[y * Size + x] = food;
            _herd[y * Size + x] = herd;
            var magnitude = threat > food ? threat : food;
            if (herd > magnitude) magnitude = herd;
            if (magnitude > peak) peak = magnitude;
        }

        _norm = Mathf.Max(_norm * 0.97f, peak);
        for (var i = 0; i < _pixels.Length; i++)
        {
            var t = Mathf.Clamp01(_threat[i] / _norm);
            var f = Mathf.Clamp01(_food[i] / _norm);
            var h = Mathf.Clamp01(_herd[i] / _norm);
            _pixels[i] = new Color32((byte)(t * 235f), (byte)(f * 225f + 18f), (byte)(h * 200f + 22), 255);
        }

        _texture.SetPixels32(_pixels);
        _texture.Apply(false);
    }
}
}
