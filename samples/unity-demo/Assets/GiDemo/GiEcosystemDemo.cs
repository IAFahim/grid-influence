using System;
using System.Globalization;
using Gi;
using UnityEngine;

namespace GiDemo;

public sealed class GiEcosystemDemo : MonoBehaviour
{
    public const float WorldUnits = 256f;
    public const int CellCount = 256;
    public const int SheepCount = 120;
    public const int WolfCount = 8;
    public const int FoodClusterCount = 6;

    internal static int WorldId = -1;
    internal static byte GridOne;
    internal static byte GridHalf;
    internal static byte Food;
    internal static byte Threat;
    internal static long Queries;

    private readonly System.Random _rng = new(42);
    private GiSheep[] _sheep = Array.Empty<GiSheep>();
    private GiWolf[] _wolves = Array.Empty<GiWolf>();
    private readonly GiMinimap _minimap = new();
    private Camera _camera;
    private Plane _ground = new(Vector3.up, Vector3.zero);
    private int _fearSource = -1;
    private Vector2 _lastPaint;
    private Vector2 _guiMouse;
    private bool _mouseValid;
    private bool _leftDown;
    private bool _rightDown;
    private int _eaten;
    private double _frameMs;
    private int _frames;
    private int _fps;
    private float _statClock;
    private long _gcMark = GC.GetTotalAllocatedBytes();
    private long _gcPerSecond;
    private long _queriesPerSecond;
    private long _queriesAtStat;
    private string _conservation = "";

    private void Awake()
    {
        if (WorldId < 0)
        {
            var id = World.New();
            WorldId = id;
            GridOne = Grid.New(id, 8, 0f, 0f, WorldUnits);
            GridHalf = Grid.New(id, 7, 0f, 0f, WorldUnits);
            Food = Layer.New(id);
            Threat = Layer.New(id);
        }
        else
        {
            World.Clear((byte)WorldId);
        }

        _ = GiDemoStamps.WolfAura;
        _gcMark = GC.GetTotalAllocatedBytes();
        BuildScene();
        SpawnFood();
        SpawnAgents();
    }

    private void OnDestroy()
    {
        if (WorldId >= 0) World.Clear((byte)WorldId);
    }

    internal static short Q(byte world, byte grid, byte layer, int x, int y)
    {
        Queries++;
        return World.Query(world, grid, layer, x, y);
    }

    internal static Vector2 ClampWorld(Vector2 p) =>
        new(Mathf.Clamp(p.x, 3f, WorldUnits - 3f), Mathf.Clamp(p.y, 3f, WorldUnits - 3f));

    private void BuildScene()
    {
        var main = Camera.main;
        if (main == null)
        {
            var camGo = new GameObject("DemoCamera");
            camGo.tag = "MainCamera";
            main = camGo.AddComponent<Camera>();
            main.transform.position = new Vector3(128f, 165f, 215f);
            main.transform.LookAt(new Vector3(128f, 0f, 96f));
        }
        _camera = main;

        var lightGo = new GameObject("DemoLight");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.15f;
        lightGo.transform.rotation = Quaternion.Euler(52f, 32f, 0f);

        var groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
        groundGo.name = "Ground";
        groundGo.transform.position = new Vector3(128f, 0f, 128f);
        groundGo.transform.localScale = new Vector3(26f, 1f, 26f);
        Paint(groundGo, new Color32(36, 44, 34, 255));
    }

    private void SpawnFood()
    {
        for (var c = 0; c < FoodClusterCount; c++)
        {
            var cx = 34f + (float)(_rng.NextDouble() * 188);
            var cy = 34f + (float)(_rng.NextDouble() * 188);
            for (var i = 0; i < 12; i++)
                Place(GiDemoStamps.FoodCrumb, Food, cx + (float)(_rng.NextDouble() * 18 - 9),
                    cy + (float)(_rng.NextDouble() * 18 - 9), 5);

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "FoodCluster";
            marker.transform.position = new Vector3(cx, 0.12f, cy);
            marker.transform.localScale = new Vector3(16f, 0.08f, 16f);
            Paint(marker, new Color32(28, 84, 40, 255));
        }
    }

    private void SpawnAgents()
    {
        _sheep = new GiSheep[SheepCount];
        for (var i = 0; i < SheepCount; i++)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "Sheep";
            body.transform.localScale = new Vector3(0.9f, 0.7f, 0.9f);
            Paint(body, new Color32(228, 226, 214, 255));
            _sheep[i] = new GiSheep
            {
                Body = body.transform,
                Pos = new Vector2(12f + (float)_rng.NextDouble() * 232f, 12f + (float)_rng.NextDouble() * 232f),
                Phase = (float)_rng.NextDouble() * 10f
            };
        }

        _wolves = new GiWolf[WolfCount];
        for (var i = 0; i < WolfCount; i++)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Wolf";
            body.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            Paint(body, new Color32(168, 42, 42, 255));
            var pos = new Vector2(24f + (float)_rng.NextDouble() * 208f, 24f + (float)_rng.NextDouble() * 208f);
            _wolves[i] = new GiWolf
            {
                Body = body.transform,
                Pos = pos,
                Source = World.Place((byte)WorldId, Threat, pos.x, pos.y, GiDemoStamps.WolfAura, 7)
            };
        }
    }

    private void Place(byte stamp, byte layer, float x, float y, int gain) =>
        World.Place((byte)WorldId, layer, x, y, stamp, gain);

    private static void Paint(GameObject go, Color32 color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        var material = new Material(shader);
        material.color = color;
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    private void Update()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = Time.time;

        UpdateWolves();
        UpdateCursor();
        World.Process((byte)WorldId);

        for (var i = 0; i < _sheep.Length; i++) _sheep[i].Update(t);
        for (var i = 0; i < _wolves.Length; i++) _wolves[i].Sync();

        _frameMs = _frameMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
        _frames++;

        _statClock += Time.unscaledDeltaTime;
        if (_statClock >= 1f)
        {
            _fps = _frames;
            _frames = 0;
            _statClock = 0f;
            var gc = GC.GetTotalAllocatedBytes();
            _gcPerSecond = gc - _gcMark;
            _gcMark = gc;
            _queriesPerSecond = Queries - _queriesAtStat;
            _queriesAtStat = Queries;
            RefreshConservation();
        }
    }

    private void UpdateWolves()
    {
        var dt = Time.deltaTime;
        for (var i = 0; i < _wolves.Length; i++)
        {
            var w = _wolves[i];
            w.RetryClock -= dt;
            if (w.RetryClock <= 0f && w.Target < 0)
            {
                w.RetryClock = 0.25f;
                var bestSq = float.MaxValue;
                for (var s = 0; s < _sheep.Length; s++)
                {
                    var sq = (_sheep[s].Pos - w.Pos).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        w.Target = s;
                    }
                }
            }

            if (w.Target >= 0)
            {
                var to = _sheep[w.Target].Pos - w.Pos;
                var dist = to.magnitude;
                if (dist > 1.1f)
                {
                    w.Vel = to / dist * 3.6f;
                }
                else
                {
                    _eaten++;
                    _sheep[w.Target].Respawn(_rng);
                    w.Target = -1;
                    w.Vel *= 0.4f;
                }
            }
            else
            {
                w.Vel *= 0.94f;
            }

            w.Pos = ClampWorld(w.Pos + w.Vel * dt);
            World.Move((byte)WorldId, w.Source, w.Pos.x, w.Pos.y);

            var hunting = false;
            if (w.Target >= 0 && (_sheep[w.Target].Pos - w.Pos).sqrMagnitude < 484f) hunting = true;
            var gain = hunting ? 12 : 7;
            if (gain != w.Gain)
            {
                World.SetGain((byte)WorldId, w.Source, gain);
                w.Gain = gain;
            }
        }
    }

    private void UpdateCursor()
    {
        if (!_mouseValid || _camera == null) return;

        var ray = _camera.ScreenPointToRay(new Vector3(_guiMouse.x, Screen.height - _guiMouse.y, 0f));
        if (!_ground.Raycast(ray, out var enter)) return;
        var hit = ray.origin + ray.direction * enter;
        var x = Mathf.Clamp(hit.x, 2f, WorldUnits - 2f);
        var y = Mathf.Clamp(hit.z, 2f, WorldUnits - 2f);

        if (_leftDown)
        {
            if (_fearSource < 0)
                _fearSource = World.Place((byte)WorldId, Threat, x, y, GiDemoStamps.FearBrush, 11);
            else
                World.Move((byte)WorldId, _fearSource, x, y);
        }
        else if (_fearSource >= 0)
        {
            World.Remove((byte)WorldId, _fearSource);
            _fearSource = -1;
        }

        if (_rightDown)
        {
            var p = new Vector2(x, y);
            if ((p - _lastPaint).sqrMagnitude > 9f)
            {
                Place(GiDemoStamps.FoodCrumb, Food, x, y, 4);
                _lastPaint = p;
            }
        }
    }

    private void RefreshConservation()
    {
        var full = World.Query((byte)WorldId, GridOne, Food, 0, 0, CellCount, CellCount);
        var half = World.Query((byte)WorldId, GridHalf, Food, 0, 0, CellCount >> 1, CellCount >> 1);
        _conservation = full == 4 * half
            ? "food 1x " + full.ToString(CultureInfo.InvariantCulture) + " == 4 x half " + half.ToString(CultureInfo.InvariantCulture)
            : "food 1x " + full.ToString(CultureInfo.InvariantCulture) + " != 4 x half " + half.ToString(CultureInfo.InvariantCulture);
    }

    private void OnGUI()
    {
        var e = Event.current;
        if (e != null && (e.type == EventType.MouseDown || e.type == EventType.MouseUp))
        {
            _guiMouse = e.mousePosition;
            _mouseValid = true;
            if (e.button == 0) _leftDown = e.type == EventType.MouseDown;
            if (e.button == 1) _rightDown = e.type == EventType.MouseDown;
        }
        else if (e != null && (e.type == EventType.Repaint || e.type == EventType.MouseMove))
        {
            _guiMouse = e.mousePosition;
            _mouseValid = true;
        }

        _minimap.Draw();

        var stats = "fps " + _fps.ToString(CultureInfo.InvariantCulture)
            + "   sim+queries " + _frameMs.ToString("F2", CultureInfo.InvariantCulture) + " ms"
            + "   field queries/s " + _queriesPerSecond.ToString("N0", CultureInfo.InvariantCulture)
            + "\nwolves " + WolfCount + "   sheep " + SheepCount + "   eaten " + _eaten.ToString(CultureInfo.InvariantCulture)
            + "\nmanaged alloc/s (incl UI) " + _gcPerSecond.ToString("N0", CultureInfo.InvariantCulture) + " B"
            + "\n" + _conservation
            + "\nhold LMB: fear brush chases the cursor   hold RMB: paint food";
        GUI.Label(new Rect(14f, 14f, 940f, 140f), stats);
    }
}
