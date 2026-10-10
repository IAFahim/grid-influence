using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Gi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal static class Live
{
    internal const float WorldUnits = 256f;
    private const int CellCount = 256;
    private const int SheepCount = 120;
    private const int WolfCount = 8;
    private const int FoodClusterCount = 6;
    internal const double TickSeconds = 0.05;

    internal static byte WorldId;
    internal static byte GridOne;
    internal static byte GridHalf;
    internal static byte Food;
    internal static byte Threat;
    internal static byte Herd;

    private static readonly byte FoodCrumb = Stamp.Box(3, 3, 40);
    private static readonly byte WolfAura = Gaussian(17, 110);
    private static readonly byte FearBrush = Gaussian(15, 140);
    private static readonly byte HerdPing = Stamp.Tent(9, 9, 60);

    private static Random _rng = new(42);
    private static LiveSheep[] _sheep = [];
    private static LiveWolf[] _wolves = [];
    private static int _fearSource = -1;
    private static float _lastPaintX = -99f;
    private static float _lastPaintY = -99f;
    private static int _eaten;
    private static double _time;
    private static long _tick;
    private static long _queries;
    private static long _queriesAtStat;
    private static double _simMs;
    private static long _maxAlloc;
    private static volatile bool _running = true;
    private static readonly Stopwatch _watch = new();
    private static int _recordTicks;
    private static int _snapTicks;
    private static int _snapSlot;
    private static readonly float[] _sheepSnaps = new float[6 * SheepCount * 2];
    private static readonly float[] _wolfSnaps = new float[6 * WolfCount * 2];

    private static readonly ConcurrentQueue<ClientOp> Ops = new();
    private static readonly List<WebSocket> Sockets = [];
    private static readonly byte[] Frame =
        new byte[16 + 8 * (SheepCount + WolfCount) + 6 * CellCount * CellCount];

    internal static void Run(string[] args)
    {
        var port = 8740;
        foreach (var a in args)
            if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
                port = p;

        WorldId = World.New();
        GridOne = Grid.New(WorldId, 8, 0f, 0f, WorldUnits);
        GridHalf = Grid.New(WorldId, 7, 0f, 0f, WorldUnits);
        Food = Layer.New(WorldId);
        Threat = Layer.New(WorldId);
        Herd = Layer.New(WorldId);
        SpawnFood();
        SpawnAgents();

        var sim = new Thread(SimLoop) { IsBackground = true };
        sim.Start();

        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/", () => Results.Content(LiveTemplate.Html, "text/html; charset=utf-8"));
        app.MapGet("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            lock (Sockets) Sockets.Add(socket);
            var buffer = new byte[16];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ctx.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count < 9) continue;
                    Ops.Enqueue(new ClientOp(
                        buffer[0],
                        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(1))),
                        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(5)))));
                }
            }
            catch (Exception)
            {
            }

            lock (Sockets) Sockets.Remove(socket);
        });

        Console.WriteLine("live field on http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
        app.Run();
        _running = false;
        sim.Join(TimeSpan.FromSeconds(2));
    }

    internal static short Q(byte grid, byte layer, int x, int y)
    {
        _queries++;
        return World.Query(WorldId, grid, layer, x, y);
    }

    internal static void G(byte grid, byte layer, float x, float y, out int gx, out int gy)
    {
        _queries++;
        World.QueryGradient(WorldId, grid, layer, x, y, out gx, out gy);
    }

    private static void SimLoop()
    {
        var clock = Stopwatch.StartNew();
        var next = clock.ElapsedMilliseconds;
        while (_running)
        {
            Tick();
            next += 50;
            var wait = next - clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            else if (wait < -1000) next = clock.ElapsedMilliseconds;
        }
    }

    private static void Tick()
    {
        var gc0 = GC.GetAllocatedBytesForCurrentThread();
        _watch.Restart();

        UpdateWolves();
        DrainOps();
        World.Process(WorldId);

        if (++_recordTicks >= 100)
        {
            _recordTicks = 0;
            World.Record(WorldId);
        }

        for (var i = 0; i < _sheep.Length; i++) _sheep[i].Update((float)_time);
        _time += TickSeconds;

        if (++_snapTicks >= 20)
        {
            _snapTicks = 0;
            _snapSlot = (_snapSlot + 1) % 6;
            for (var i = 0; i < _sheep.Length; i++)
            {
                _sheepSnaps[_snapSlot * SheepCount * 2 + i * 2] = _sheep[i].X;
                _sheepSnaps[_snapSlot * SheepCount * 2 + i * 2 + 1] = _sheep[i].Y;
            }

            for (var i = 0; i < _wolves.Length; i++)
            {
                _wolfSnaps[_snapSlot * WolfCount * 2 + i * 2] = _wolves[i].X;
                _wolfSnaps[_snapSlot * WolfCount * 2 + i * 2 + 1] = _wolves[i].Y;
            }
        }

        PackFrame();
        _tick++;
        _simMs = _simMs * 0.9 + _watch.Elapsed.TotalMilliseconds * 0.1;
        var alloc = GC.GetAllocatedBytesForCurrentThread() - gc0;
        if (alloc > _maxAlloc) _maxAlloc = alloc;

        SendFrame(_tick % 20 == 0 ? BuildStats() : null);
    }

    private static void DrainOps()
    {
        while (Ops.TryDequeue(out var op))
        {
            var x = Math.Clamp(op.X, 2f, WorldUnits - 2f);
            var y = Math.Clamp(op.Y, 2f, WorldUnits - 2f);
            if (op.Op == 0)
            {
                if (_fearSource < 0) _fearSource = World.Place(WorldId, Threat, x, y, FearBrush, 11);
                else World.Move(WorldId, _fearSource, x, y);
            }
            else if (op.Op == 1)
            {
                if (_fearSource >= 0)
                {
                    World.Remove(WorldId, _fearSource);
                    _fearSource = -1;
                }
            }
            else if (op.Op == 2)
            {
                var sx = SnapFood(x);
                var sy = SnapFood(y);
                var dx = sx - _lastPaintX;
                var dy = sy - _lastPaintY;
                if (dx * dx + dy * dy >= 4f)
                {
                    World.Place(WorldId, Food, sx, sy, FoodCrumb, 4);
                    _lastPaintX = sx;
                    _lastPaintY = sy;
                }
            }
            else if (op.Op == 3)
            {
                Reset();
            }
            else if (op.Op == 4)
            {
                Rewind();
            }
        }
    }

    private static void Rewind()
    {
        World.Rewind(WorldId);
        World.Process(WorldId);

        var slot = (_snapSlot + 1) % 6;
        for (var i = 0; i < _sheep.Length; i++)
        {
            var sx = _sheepSnaps[slot * SheepCount * 2 + i * 2];
            var sy = _sheepSnaps[slot * SheepCount * 2 + i * 2 + 1];
            if (sx < 0.01f && sy < 0.01f) { _sheep[i].Respawn(_rng); continue; }
            _sheep[i].X = sx;
            _sheep[i].Y = sy;
            _sheep[i].Vx = 0f;
            _sheep[i].Vy = 0f;
        }

        for (var i = 0; i < _wolves.Length; i++)
        {
            var wx = _wolfSnaps[slot * WolfCount * 2 + i * 2];
            var wy = _wolfSnaps[slot * WolfCount * 2 + i * 2 + 1];
            if (wx < 0.01f && wy < 0.01f)
            {
                wx = 24f + (float)_rng.NextDouble() * 208f;
                wy = 24f + (float)_rng.NextDouble() * 208f;
            }

            _wolves[i].X = wx;
            _wolves[i].Y = wy;
            _wolves[i].Vx = 0f;
            _wolves[i].Vy = 0f;
            _wolves[i].Hunting = false;
            _wolves[i].RetryClock = 0f;
        }

        if (_fearSource >= 0)
        {
            World.Remove(WorldId, _fearSource);
            _fearSource = -1;
        }
    }

    private static void UpdateWolves()
    {
        var dt = (float)TickSeconds;
        for (var i = 0; i < _wolves.Length; i++)
        {
            var w = _wolves[i];
            w.RetryClock -= dt;
            if (w.RetryClock <= 0f)
            {
                w.RetryClock = 0.25f;
                _queries++;
                var peak = World.QueryMax(WorldId, GridOne, Herd,
                    (int)w.X - 48, (int)w.Y - 48, 96, 96, out var bx, out var by);
                w.Hunting = peak > 0;
                if (w.Hunting)
                {
                    w.HuntX = bx + 0.5f;
                    w.HuntY = by + 0.5f;
                }
            }

            if (w.Hunting)
            {
                var toX = w.HuntX - w.X;
                var toY = w.HuntY - w.Y;
                var dist = MathF.Sqrt(toX * toX + toY * toY);
                if (dist > 1.1f)
                {
                    w.Vx = toX / dist * 3.6f;
                    w.Vy = toY / dist * 3.6f;
                }
                else
                {
                    var nearest = -1;
                    var bestSq = 16f;
                    for (var s = 0; s < _sheep.Length; s++)
                    {
                        var dx = _sheep[s].X - w.X;
                        var dy = _sheep[s].Y - w.Y;
                        var sq = dx * dx + dy * dy;
                        if (sq >= bestSq) continue;
                        bestSq = sq;
                        nearest = s;
                    }

                    if (nearest >= 0)
                    {
                        _eaten++;
                        _sheep[nearest].Respawn(_rng);
                    }

                    w.Hunting = false;
                    w.Vx *= 0.4f;
                    w.Vy *= 0.4f;
                }
            }
            else
            {
                w.Vx *= 0.94f;
                w.Vy *= 0.94f;
            }

            w.X = Math.Clamp(w.X + w.Vx * dt, 3f, WorldUnits - 3f);
            w.Y = Math.Clamp(w.Y + w.Vy * dt, 3f, WorldUnits - 3f);
            World.Move(WorldId, w.Source, w.X, w.Y);

            var gain = w.Hunting ? 12 : 7;
            if (gain == w.Gain) continue;
            World.SetGain(WorldId, w.Source, gain);
            w.Gain = gain;
        }
    }

    private static unsafe void PackFrame()
    {
        fixed (byte* fp = Frame)
        {
            ((int*)fp)[0] = (int)_tick;
            ((int*)fp)[1] = _sheep.Length;
            ((int*)fp)[2] = _wolves.Length;
            ((int*)fp)[3] = _eaten;
            var p = (float*)(fp + 16);
            for (var i = 0; i < _sheep.Length; i++)
            {
                *p++ = _sheep[i].X;
                *p++ = _sheep[i].Y;
            }
            for (var i = 0; i < _wolves.Length; i++)
            {
                *p++ = _wolves[i].X;
                *p++ = _wolves[i].Y;
            }
            var s = (short*)p;
            World.QueryRegion(WorldId, GridOne, Threat, 0, 0, CellCount, CellCount, s);
            s += CellCount * CellCount;
            World.QueryRegion(WorldId, GridOne, Food, 0, 0, CellCount, CellCount, s);
            s += CellCount * CellCount;
            World.QueryRegion(WorldId, GridOne, Herd, 0, 0, CellCount, CellCount, s);
        }
    }

    private static void SendFrame(string stats)
    {
        WebSocket[] snapshot;
        lock (Sockets) snapshot = [.. Sockets];
        if (snapshot.Length == 0) return;

        List<WebSocket> dead = null;
        foreach (var socket in snapshot)
        {
            try
            {
                socket.SendAsync(new ArraySegment<byte>(Frame), WebSocketMessageType.Binary, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (stats != null)
                    socket.SendAsync(Encoding.UTF8.GetBytes(stats), WebSocketMessageType.Text, true, CancellationToken.None)
                        .GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                dead ??= [];
                dead.Add(socket);
            }
        }

        if (dead == null) return;
        lock (Sockets)
            foreach (var socket in dead) Sockets.Remove(socket);
    }

    private static string BuildStats()
    {
        var full = World.Query(WorldId, GridOne, Food, 0, 0, CellCount, CellCount);
        var half = World.Query(WorldId, GridHalf, Food, 0, 0, CellCount >> 1, CellCount >> 1);
        var conservation = full == 4 * half
            ? "food 1x " + full.ToString(CultureInfo.InvariantCulture) + " == 4 x half " + half.ToString(CultureInfo.InvariantCulture)
            : "food 1x " + full.ToString(CultureInfo.InvariantCulture) + " != 4 x half " + half.ToString(CultureInfo.InvariantCulture);
        var queries = _queries - _queriesAtStat;
        _queriesAtStat = _queries;

        var sb = new StringBuilder();
        sb.Append("sim+pack ").Append(_simMs.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms/tick · 20 ticks/s\n");
        sb.Append("sheep ").Append(_sheep.Length).Append(" · wolves ").Append(_wolves.Length)
            .Append(" · eaten ").Append(_eaten.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("field queries/s ").Append(queries.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" · tick managed alloc max ").Append(_maxAlloc.ToString("N0", CultureInfo.InvariantCulture)).Append(" B\n");
        sb.Append("—\n");
        sb.Append(conservation).Append('\n');
        sb.Append("stream ").Append(Frame.Length.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" B/frame · QueryRegion into a pinned buffer");
        return sb.ToString();
    }

    private static void Reset()
    {
        World.Clear(WorldId);
        _fearSource = -1;
        _lastPaintX = -99f;
        _lastPaintY = -99f;
        _eaten = 0;
        _recordTicks = 0;
        _snapTicks = 0;
        _snapSlot = 0;
        Array.Clear(_sheepSnaps);
        Array.Clear(_wolfSnaps);
        _rng = new Random(42);
        SpawnFood();
        SpawnAgents();
    }

    private static void SpawnFood()
    {
        for (var c = 0; c < FoodClusterCount; c++)
        {
            var cx = 34f + (float)(_rng.NextDouble() * 188);
            var cy = 34f + (float)(_rng.NextDouble() * 188);
            for (var i = 0; i < 12; i++)
                World.Place(WorldId, Food,
                    SnapFood(cx + (float)(_rng.NextDouble() * 18 - 9)),
                    SnapFood(cy + (float)(_rng.NextDouble() * 18 - 9)), FoodCrumb, 5);
        }
    }

    private static float SnapFood(float v) =>
        1.5f + 2f * MathF.Floor(Math.Clamp(v, 3f, 251.5f) / 2f);

    private static void SpawnAgents()
    {
        _sheep = new LiveSheep[SheepCount];
        for (var i = 0; i < SheepCount; i++)
        {
            var x = 12f + (float)_rng.NextDouble() * 232f;
            var y = 12f + (float)_rng.NextDouble() * 232f;
            _sheep[i] = new LiveSheep
            {
                X = x,
                Y = y,
                Phase = (float)_rng.NextDouble() * 10f,
                Source = World.Place(WorldId, Herd, x, y, HerdPing, 5)
            };
        }

        _wolves = new LiveWolf[WolfCount];
        for (var i = 0; i < WolfCount; i++)
        {
            var x = 24f + (float)_rng.NextDouble() * 208f;
            var y = 24f + (float)_rng.NextDouble() * 208f;
            _wolves[i] = new LiveWolf
            {
                X = x,
                Y = y,
                Source = World.Place(WorldId, Threat, x, y, WolfAura, 7)
            };
        }
    }

    private static byte Gaussian(int size, int peak)
    {
        var samples = new sbyte[size * size];
        double c = (size - 1) / 2.0, sigma = size / 5.0;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            double dx = x - c, dy = y - c;
            var v = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
            samples[y * size + x] = (sbyte)Math.Clamp((int)Math.Round(v), -128, 127);
        }
        return Stamp.New(samples, size, size);
    }

    private readonly record struct ClientOp(byte Op, float X, float Y);
}

internal sealed class LiveSheep
{
    public float X, Y, Vx, Vy, Phase;
    public int Source;

    public void Update(float t)
    {
        var grid = Live.GridOne;

        var threat = Live.Q(grid, Live.Threat, (int)X, (int)Y);
        var food = Live.Q(grid, Live.Food, (int)X, (int)Y);

        var escapeX = 0f;
        var escapeY = 0f;
        if (threat > 60)
        {
            Live.G(grid, Live.Threat, X, Y, out var gx, out var gy);
            escapeX = -gx * 0.14f;
            escapeY = -gy * 0.14f;
        }

        var seekX = 0f;
        var seekY = 0f;
        if (food > 40 && threat < 400)
        {
            Live.G(grid, Live.Food, X, Y, out var gx, out var gy);
            seekX = gx * 0.04f;
            seekY = gy * 0.04f;
        }

        var wanderX = MathF.Sin(t * 0.7f + Phase) * 0.4f;
        var wanderY = MathF.Cos(t * 0.6f + Phase * 1.3f) * 0.4f;
        var desiredX = escapeX + seekX + wanderX;
        var desiredY = escapeY + seekY + wanderY;
        var mag2 = desiredX * desiredX + desiredY * desiredY;
        if (mag2 > 1f)
        {
            var inv = 1f / MathF.Sqrt(mag2);
            desiredX *= inv;
            desiredY *= inv;
        }

        var speed = threat > 900 ? 5.6f : 2.1f;
        if (food > 450 && threat < 60) speed *= 0.15f;

        Vx += (desiredX * speed - Vx) * 0.25f;
        Vy += (desiredY * speed - Vy) * 0.25f;
        var dt = (float)Live.TickSeconds;
        X = Math.Clamp(X + Vx * dt, 3f, Live.WorldUnits - 3f);
        Y = Math.Clamp(Y + Vy * dt, 3f, Live.WorldUnits - 3f);
        World.Move(Live.WorldId, Source, X, Y);
    }

    public void Respawn(Random rng)
    {
        X = 8f + (float)rng.NextDouble() * 240f;
        Y = 8f + (float)rng.NextDouble() * 240f;
        Vx = 0f;
        Vy = 0f;
        World.Move(Live.WorldId, Source, X, Y);
    }
}

internal sealed class LiveWolf
{
    public float X, Y, Vx, Vy;
    public int Source;
    public float HuntX, HuntY;
    public bool Hunting;
    public float RetryClock;
    public int Gain = 7;
}
