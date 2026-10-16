using Xunit;

namespace Gi.Tests;

public unsafe class ReferenceWorldTests
{
    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public void LockstepRandomizedStreamMatchesEverySurface(int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamps = new List<byte>();
        try
        {
            stamps.Add(Stamp.Box(6, 4, 40));
            stamps.Add(Stamp.Tent(8, 6, -35));
            stamps.Add(Stamp.Bell(10, 8, 60));
            stamps.Add(Stamp.Disk(5, 50, 360));
            stamps.Add(Stamp.Cone(7, -45, 360));
            stamps.Add(Stamp.Dome(9, 70, 360));
            stamps.Add(Stamp.Disk(6, 55, 135));
            stamps.Add(Stamp.Cone(8, 65, 210));
            stamps.Add(Stamp.Dome(8, -60, 270));
            ReadOnlySpan<sbyte> raster = [12, -25, 30, 45, -60, 15, 80, -10, 20, 35, -40, 50, -15, 25, -30, 60];
            stamps.Add(Stamp.New(raster, 4, 4));

            var g0 = Grid.New(w, 6, 0f, 0f, 64f);
            var rg0 = rw.AddGrid(6, 0f, 0f, 64f);
            Assert.Equal(g0, rg0);

            var g1 = Grid.New(w, 5, 16f, 16f, 32f);
            var rg1 = rw.AddGrid(5, 16f, 16f, 32f);
            Assert.Equal(g1, rg1);

            var l0 = Layer.New(w, Layer.Precision.Exact);
            var rl0 = rw.AddLayer(Layer.Precision.Exact);
            Assert.Equal(l0, rl0);

            var l1 = Layer.New(w, Layer.Precision.Quarter);
            var rl1 = rw.AddLayer(Layer.Precision.Quarter);
            Assert.Equal(l1, rl1);

            var l2 = Layer.New(w, Layer.Precision.Cell);
            var rl2 = rw.AddLayer(Layer.Precision.Cell);
            Assert.Equal(l2, rl2);

            var dSum = Layer.Sum(w, l0, 3, l1, -2, 1);
            var rdSum = rw.AddDerived(LayerOp.Sum, rl0, rl1, 3, -2, 1, 0, 0);
            Assert.Equal(dSum, rdSum);

            var dMin = Layer.Min(w, l0, l1);
            var rdMin = rw.AddDerived(LayerOp.Min, rl0, rl1, 0, 0, 0, 0, 0);
            Assert.Equal(dMin, rdMin);

            var dMax = Layer.Max(w, l1, l2);
            var rdMax = rw.AddDerived(LayerOp.Max, rl1, rl2, 0, 0, 0, 0, 0);
            Assert.Equal(dMax, rdMax);

            var dMask = Layer.Mask(w, dSum, l2, -500, 500);
            var rdMask = rw.AddDerived(LayerOp.Mask, rdSum, rl2, 0, 0, 0, -500, 500);
            Assert.Equal(dMask, rdMask);

            var baseLayers = new[] { l0, l1, l2 };
            var allLayers = new[] { l0, l1, l2, dSum, dMin, dMax, dMask };
            var ids = new List<int>();
            var recording = false;

            var syncIds = new int[16];
            var syncXs = new float[16];
            var syncYs = new float[16];

            for (var frame = 0; frame < 18; frame++)
            {
                if (frame == 4 && !recording)
                {
                    World.Record(w);
                    rw.Record();
                    recording = true;
                }
                else if (frame == 8 && recording)
                {
                    World.Rewind(w);
                    rw.Rewind();
                    recording = false;
                }
                else if (frame == 11 && !recording)
                {
                    World.Record(w);
                    rw.Record();
                    recording = true;
                }
                else if (frame == 13 && recording)
                {
                    World.StopRecording(w);
                    rw.StopRecording();
                    recording = false;
                }
                else if (frame == 15)
                {
                    World.Clear(w);
                    rw.Clear();
                    ids.Clear();
                }

                var ops = frame == 2 ? 75 : 18;
                for (var op = 0; op < ops; op++)
                {
                    var choice = rng.Next(100);
                    if (ids.Count == 0 || choice < 35)
                    {
                        var layer = baseLayers[rng.Next(baseLayers.Length)];
                        var stamp = stamps[rng.Next(stamps.Count)];
                        var x = (float)(rng.NextDouble() * 72.0 - 4.0);
                        var y = (float)(rng.NextDouble() * 72.0 - 4.0);
                        var gain = (sbyte)rng.Next(-16, 17);
                        var id = World.Place(w, layer, x, y, stamp, gain);
                        var rid = rw.Place(layer, x, y, stamp, gain);
                        Assert.Equal(rid, id);
                        if (id >= 0) ids.Add(id);
                    }
                    else if (choice < 50)
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var x = (float)(rng.NextDouble() * 72.0 - 4.0);
                        var y = (float)(rng.NextDouble() * 72.0 - 4.0);
                        World.Move(w, id, x, y);
                        rw.Move(id, x, y);
                    }
                    else if (choice < 60)
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var angle = (float)(rng.NextDouble() * Math.PI * 4.0 - Math.PI * 2.0);
                        World.Turn(w, id, angle);
                        rw.Turn(id, angle);
                    }
                    else if (choice < 70)
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var scale = (float)(rng.NextDouble() * 2.5 + 0.25);
                        World.Scale(w, id, scale);
                        rw.Scale(id, scale);
                    }
                    else if (choice < 78)
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var gain = (sbyte)rng.Next(-16, 17);
                        World.SetGain(w, id, gain);
                        rw.SetGain(id, gain);
                    }
                    else if (choice < 85)
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var gain = (sbyte)rng.Next(-16, 17);
                        var ticks = rng.Next(0, 6);
                        World.Fade(w, id, gain, ticks);
                        rw.Fade(id, gain, ticks);
                    }
                    else if (choice < 92)
                    {
                        var id = ids[rng.Next(ids.Count)];
                        var ticks = rng.Next(0, 6);
                        World.Expire(w, id, ticks);
                        rw.Expire(id, ticks);
                    }
                    else
                    {
                        var idx = rng.Next(ids.Count);
                        var id = ids[idx];
                        World.Remove(w, id);
                        rw.Remove(id);
                    }
                }

                if (ids.Count >= 4)
                {
                    var syncCount = Math.Min(ids.Count, 6);
                    for (var i = 0; i < syncCount; i++)
                    {
                        syncIds[i] = ids[i];
                        syncXs[i] = (float)(rng.NextDouble() * 64.0);
                        syncYs[i] = (float)(rng.NextDouble() * 64.0);
                    }

                    fixed (int* pIds = syncIds)
                    fixed (float* pXs = syncXs, pYs = syncYs)
                    {
                        World.Sync(w, pIds, pXs, pYs, syncCount);
                        rw.Sync(pIds, pXs, pYs, syncCount);
                    }
                }

                VerifyReads(w, rw, allLayers, ids, rng, frame);

                World.Process(w);
                rw.Process();

                VerifyFullState(w, rw, g0, 64, g1, 32, allLayers, ids, rng, frame + 1);
            }
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void VerifyFullState(
        byte w,
        ReferenceWorld rw,
        byte g0,
        int size0,
        byte g1,
        int size1,
        byte[] layers,
        List<int> ids,
        Random rng,
        int tick)
    {
        VerifyGridFields(w, rw, g0, size0, layers, rng);
        VerifyGridFields(w, rw, g1, size1, layers, rng);
        VerifyReads(w, rw, layers, ids, rng, tick);
    }

    private static void VerifyGridFields(byte w, ReferenceWorld rw, byte grid, int size, byte[] layers, Random rng)
    {
        var actual = new short[size * size];
        var expected = new short[size * size];
        fixed (short* pa = actual)
        fixed (short* pe = expected)
        {
            foreach (var layer in layers)
            {
                World.QueryRegion(w, grid, layer, 0, 0, size, size, pa);
                rw.QueryRegion(grid, layer, 0, 0, size, size, pe);
                Assert.Equal(expected, actual);

                Assert.Equal(rw.Query(grid, layer, 0, 0, size, size), World.Query(w, grid, layer, 0, 0, size, size));

                var rx = rng.Next(-4, size);
                var ry = rng.Next(-4, size);
                var rwWidth = rng.Next(1, 24);
                var rwHeight = rng.Next(1, 24);
                Assert.Equal(
                    rw.Query(grid, layer, rx, ry, rwWidth, rwHeight),
                    World.Query(w, grid, layer, rx, ry, rwWidth, rwHeight));

                var maxVal = World.QueryMax(w, grid, layer, out var mx, out var my);
                var rMaxVal = rw.QueryMax(grid, layer, out var rmx, out var rmy);
                Assert.Equal(rMaxVal, maxVal);
                Assert.Equal((rmx, rmy), (mx, my));

                var wMaxVal = World.QueryMax(w, grid, layer, rx, ry, rwWidth, rwHeight, out var wmx, out var wmy);
                var rwMaxVal = rw.QueryMax(grid, layer, rx, ry, rwWidth, rwHeight, out var rwmx, out var rwmy);
                Assert.Equal(rwMaxVal, wMaxVal);
                Assert.Equal((rwmx, rwmy), (wmx, wmy));
            }
        }
    }

    private static void VerifyReads(byte w, ReferenceWorld rw, byte[] layers, List<int> ids, Random rng, int tick)
    {
        for (var q = 0; q < 8; q++)
        {
            var x = (float)(rng.NextDouble() * 70.0 - 3.0);
            var y = (float)(rng.NextDouble() * 70.0 - 3.0);
            var reach = (float)(rng.NextDouble() * 12.0);
            var exclude = ids.Count > 0 && rng.Next(2) == 0 ? ids[rng.Next(ids.Count)] : -1;

            Assert.Equal(rw.Covers(x, y), World.Covers(w, x, y));

            foreach (var layer in layers)
            {
                World.QueryGradient(w, 0, layer, x, y, out var qgx, out var qgy);
                rw.QueryGradient(0, layer, x, y, out var rqgx, out var rqgy);
                Assert.Equal((rqgx, rqgy), (qgx, qgy));
                Assert.Equal(rw.QueryAt(0, layer, x, y), World.QueryAt(w, 0, layer, x, y));

                var okSense = World.TrySense(w, layer, x, y, out var sv);
                var rokSense = rw.TrySense(layer, x, y, out var rsv);
                Assert.Equal(rokSense, okSense);
                Assert.Equal(rsv, sv);

                var okSenseEx = World.TrySense(w, layer, x, y, exclude, out var svEx);
                var rokSenseEx = rw.TrySense(layer, x, y, exclude, out var rsvEx);
                Assert.Equal(rokSenseEx, okSenseEx);
                Assert.Equal(rsvEx, svEx);

                var okArea = World.TrySenseArea(w, layer, x, y, reach, out var area);
                var rokArea = rw.TrySenseArea(layer, x, y, reach, out var rarea);
                Assert.Equal(rokArea, okArea);
                Assert.Equal(rarea, area);

                var okAreaEx = World.TrySenseArea(w, layer, x, y, reach, exclude, out var areaEx);
                var rokAreaEx = rw.TrySenseArea(layer, x, y, reach, exclude, out var rareaEx);
                Assert.Equal(rokAreaEx, okAreaEx);
                Assert.Equal(rareaEx, areaEx);

                var okGrad = World.TrySenseGradient(w, layer, x, y, out var gx, out var gy);
                var rokGrad = rw.TrySenseGradient(layer, x, y, out var rgx, out var rgy);
                Assert.Equal(rokGrad, okGrad);
                Assert.Equal((rgx, rgy), (gx, gy));

                var okGradEx = World.TrySenseGradient(w, layer, x, y, exclude, out var gxEx, out var gyEx);
                var rokGradEx = rw.TrySenseGradient(layer, x, y, exclude, out var rgxEx, out var rgyEx);
                Assert.Equal(rokGradEx, okGradEx);
                Assert.Equal((rgxEx, rgyEx), (gxEx, gyEx));

                var okMax = World.TrySenseMax(w, layer, x, y, reach, out var mv, out var bx, out var by);
                var rokMax = rw.TrySenseMax(layer, x, y, reach, out var rmv, out var rbx, out var rby);
                Assert.Equal(rokMax, okMax);
                Assert.Equal((rmv, rbx, rby), (mv, bx, by));

                var threshold = (short)rng.Next(-20, 50);
                var okNear = World.TrySenseNearest(w, layer, x, y, reach, threshold, out var nv, out var nx, out var ny);
                var rokNear = rw.TrySenseNearest(layer, x, y, reach, threshold, out var rnv, out var rnx, out var rny);
                Assert.Equal(rokNear, okNear);
                Assert.Equal((rnv, rnx, rny), (nv, nx, ny));

                for (var since = -1; since <= tick + 1; since += Math.Max(1, (tick + 2) / 3))
                    Assert.Equal(rw.Changed(layer, x, y, reach, since), World.Changed(w, layer, x, y, reach, since));
            }
        }
    }
}
