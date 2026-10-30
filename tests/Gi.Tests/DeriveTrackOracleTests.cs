using Xunit;

namespace Gi.Tests;

public sealed unsafe class DeriveTrackOracleTests
{
    private const int Frames = 90;
    private const int Size = 128;
    private const int Cells = Size * Size;

    private sealed unsafe class Driver
    {
        private readonly float[] _xs = new float[16];
        private readonly float[] _ys = new float[16];
        private readonly sbyte[] _gains = new sbyte[16];
        private readonly byte[] _kinds = new byte[16];
        private World.SourceId[] _ids = new World.SourceId[16];
        private int _live;

        public byte Id;
        public byte P0;
        public byte P1;
        public byte T2;
        public byte SumPT;
        public byte MaxPP;
        public byte BoxStamp;
        public byte TentStamp;
        public byte ConeStamp;

        public short[] Page = new short[Cells];

        public Driver(byte boxStamp, byte tentStamp, byte coneStamp)
        {
            BoxStamp = boxStamp;
            TentStamp = tentStamp;
            ConeStamp = coneStamp;
        }

        public void Build()
        {
            Id = World.New();
            Grid.New(Id, 7, 0f, 0f, Size);
            P0 = Layer.New(Id);
            P1 = Layer.New(Id);
            T2 = Layer.New(Id, Layer.Residency.Transient, Layer.Precision.Exact);
            SumPT = Layer.Sum(Id, P1, 1, T2, 1);
            MaxPP = Layer.Max(Id, P0, P1);
        }

        public void Place(int i, float x, float y, byte kind, sbyte gain)
        {
            _xs[i] = x;
            _ys[i] = y;
            _gains[i] = gain;
            _kinds[i] = kind;
            _ids[i] = World.Place(Id, kind == 0 ? P0 : P1, x, y, StampOf(kind), gain);
            _live = Math.Max(_live, i + 1);
        }

        public void Move(int i, float x, float y)
        {
            _xs[i] = x;
            _ys[i] = y;
            World.Move(Id, _ids[i], x, y);
        }

        public void SetGain(int i, sbyte gain)
        {
            _gains[i] = gain;
            World.SetGain(Id, _ids[i], gain);
        }

        public void Remove(int i)
        {
            World.Remove(Id, _ids[i]);
            _ids[i] = default;
        }

        public byte StampOf(byte kind)
        {
            return kind switch
            {
                0 => BoxStamp,
                1 => TentStamp,
                _ => ConeStamp
            };
        }

        public void ReplayWrites(Driver target)
        {
            for (var i = 0; i < _live; i++)
            {
                if (_ids[i] == default) continue;

                target._xs[i] = _xs[i];
                target._ys[i] = _ys[i];
                target._gains[i] = _gains[i];
                target._kinds[i] = _kinds[i];
                target._ids[i] = World.Place(target.Id, _kinds[i] == 0 ? target.P0 : target.P1, _xs[i], _ys[i],
                    target.StampOf(_kinds[i]), _gains[i]);
            }

            target._live = _live;
        }

        public void CopyStateFrom(Driver source)
        {
            Array.Copy(source._ids, _ids, _ids.Length);
            Array.Copy(source._xs, _xs, _xs.Length);
            Array.Copy(source._ys, _ys, _ys.Length);
            Array.Copy(source._gains, _gains, _gains.Length);
            Array.Copy(source._kinds, _kinds, _kinds.Length);
            _live = source._live;
        }

        public void FillPage(byte grid, byte layer)
        {
            fixed (short* p = Page)
            {
                World.QueryRegion(Id, grid, layer, 0, 0, Size, Size, p);
            }
        }
    }

    private static void DriveFrame(Driver d, Random rng, int frame)
    {
        for (var i = 0; i < 10; i++)
        {
            var x = 4f + (float)(rng.NextDouble() * 120);
            var y = 4f + (float)(rng.NextDouble() * 120);
            if (frame % 3 == 0)
                d.Move(i, x, y);
            else
                d.Move(i, 6f + i * 11.3f + frame * 0.35f + (float)((rng.NextDouble() - 0.5) * 1.4),
                    9f + i * 9.7f - frame * 0.28f + (float)((rng.NextDouble() - 0.5) * 1.4));
        }

        if (frame % 7 == 3) d.SetGain(rng.Next(10), (sbyte)rng.Next(-6, 7));
        if (frame % 11 == 5)
        {
            var victim = rng.Next(10);
            d.Remove(victim);
            d.Place(victim, 8f + (float)(rng.NextDouble() * 112), 8f + (float)(rng.NextDouble() * 112),
                (byte)rng.Next(3), (sbyte)rng.Next(1, 6));
        }

        var sx = 10f + frame * 0.9f;
        var sy = 100f - frame * 0.7f;
        World.Splat(d.Id, d.T2, new[] { sx, sy + 5f }, new[] { sy, sx }, d.ConeStamp, 4);
        World.Process(d.Id);
    }

    [Fact]
    public void IncrementalDeriveMatchesAFullRebuildEveryFrame()
    {
        var box = Stamp.Box(4, 4, 18);
        var tent = Stamp.Tent(7, 7, 30);
        var cone = Stamp.Cone(5, 40, 200);
        var live = new Driver(box, tent, cone);
        live.Build();
        for (var i = 0; i < 10; i++)
            live.Place(i, 6f + i * 11.3f, 9f + i * 9.7f, (byte)(i % 3), (sbyte)(1 + i % 4));

        var rebuilt = new Driver(box, tent, cone);
        for (var frame = 0; frame < Frames; frame++)
        {
            DriveFrame(live, new Random(frame * 131 + 17), frame);

            rebuilt.Build();
            live.ReplayWrites(rebuilt);
            var sx = 10f + frame * 0.9f;
            var sy = 100f - frame * 0.7f;
            World.Splat(rebuilt.Id, rebuilt.T2, new[] { sx, sy + 5f }, new[] { sy, sx }, cone, 4);
            World.Process(rebuilt.Id);

            foreach (var layer in new[] { live.P0, live.P1, live.T2, live.SumPT, live.MaxPP })
            {
                live.FillPage(0, layer);
                var expected = (short[])live.Page.Clone();
                rebuilt.FillPage(0, layer);
                var same = PageEqual(expected, rebuilt.Page, out var cell);
                Assert.True(same, $"layer {layer} frame {frame} cell {cell}: {expected[Math.Max(cell, 0)]} vs {rebuilt.Page[Math.Max(cell, 0)]}");
                Assert.Equal(World.LayerHash(live.Id, 0, layer), World.LayerHash(rebuilt.Id, 0, layer));
            }

            World.QueryMax(live.Id, 0, live.SumPT, out var liveX, out var liveY);
            World.QueryMax(rebuilt.Id, 0, rebuilt.SumPT, out var rebuildX, out var rebuildY);
            Assert.Equal((liveX, liveY), (rebuildX, rebuildY));
            Assert.True(World.Free(rebuilt.Id));
        }

        Assert.True(World.Free(live.Id));
        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(tent));
        Assert.True(Stamp.Free(cone));
    }

    [Fact]
    public void RestoredSnapshotDerivesIdenticallyToTheLiveWorld()
    {
        var box = Stamp.Box(5, 5, 22);
        var tent = Stamp.Tent(8, 8, 26);
        var cone = Stamp.Cone(6, 55, 160);
        var rng = new Random(9119);
        var live = new Driver(box, tent, cone);
        live.Build();
        for (var i = 0; i < 10; i++)
            live.Place(i, 5f + i * 12.1f, 7f + i * 10.3f, (byte)(i % 3), (sbyte)(1 + i % 5));

        for (var frame = 0; frame < Frames; frame++)
        {
            DriveFrame(live, rng, frame);
            if (frame != 40) continue;

            var size = World.SnapshotSize(live.Id);
            Assert.True(size > 0);
            var blob = new byte[size];
            Assert.True(World.TrySnapshot(live.Id, blob, out var written));
            Assert.Equal(size, written);

            var restored = new Driver(box, tent, cone);
            restored.Id = World.New();
            Assert.True(World.TryRestore(restored.Id, blob));
            restored.P0 = 0;
            restored.P1 = 1;
            restored.T2 = 2;
            restored.SumPT = 3;
            restored.MaxPP = 4;
            restored.CopyStateFrom(live);

            for (var tail = 41; tail < Frames; tail++)
            {
                DriveFrame(live, new Random(tail * 31 + 7), tail);
                DriveFrame(restored, new Random(tail * 31 + 7), tail);
                foreach (var layer in new[] { live.P0, live.P1, live.T2, live.SumPT, live.MaxPP })
                {
                    live.FillPage(0, layer);
                    var expected = (short[])live.Page.Clone();
                    restored.FillPage(0, layer);
                    var same = PageEqual(expected, restored.Page, out var cell);
                    Assert.True(same, $"layer {layer} frame {tail} cell {cell}: {expected[Math.Max(cell, 0)]} vs {restored.Page[Math.Max(cell, 0)]}");
                    Assert.Equal(World.LayerHash(live.Id, 0, layer),
                        World.LayerHash(restored.Id, 0, layer));
                }
            }

            Assert.True(World.Free(restored.Id));
            break;
        }

        Assert.True(World.Free(live.Id));
        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(tent));
        Assert.True(Stamp.Free(cone));
    }

    [Fact]
    public void LayerHashIsStableAcrossIdleFramesAndFlushesOnce()
    {
        var box = Stamp.Box(4, 4, 20);
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var derived = Layer.Sum(world, layer, 1, layer, 0);
        World.Place(world, layer, 20f, 20f, box, 3);
        World.Process(world);
        var seeded = World.LayerHash(world, 0, derived);
        Assert.NotEqual(0ul, seeded);

        var context = World.GetContext(world);
        var data = &context->Grids[0].Layers[derived];
        Assert.Equal(0, data->HashPendingCount);

        for (var idle = 0; idle < 5; idle++)
        {
            World.Process(world);
            Assert.Equal(seeded, World.LayerHash(world, 0, derived));
            Assert.Equal(0, data->HashPendingCount);
        }

        Assert.True(World.Free(world));
        Assert.True(Stamp.Free(box));
    }

    private static bool PageEqual(short[] first, short[] second, out int cell)
    {
        for (var i = 0; i < first.Length; i++)
            if (first[i] != second[i])
            {
                cell = i;
                return false;
            }

        cell = -1;
        return true;
    }
}
