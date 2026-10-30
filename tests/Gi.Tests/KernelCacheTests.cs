using Xunit;

namespace Gi.Tests;

public sealed unsafe class KernelCacheTests
{
    private const int GridPower = 10;
    private const int Size = 1 << GridPower;
    private static readonly int[] Scales = { 128, 256, 947 };
    private static readonly int[] Angles = { 0, 1, 8192, 16384, 32768, 49152, 60000 };
    private static readonly int[] Gains = { 1, -1, 3, 16, -16 };

    private static readonly (int X, int Y)[] Phases =
    {
        (0, 0), (1, 1), (13, 200), (64, 127), (128, 128), (255, 255), (200, 13)
    };

    private static byte NewWorld()
    {
        var world = World.New();
        Grid.New(world, GridPower, 0f, 0f, Size);
        return world;
    }

    private static float PositionFor(StampVariant* v, bool xAxis, int cell, int phase, int scaleQ8)
    {
        var origin = xAxis ? v->OriginQ8X : v->OriginQ8Y;
        var lead = ((long)cell << 8) + phase;
        return (lead - (((long)origin * scaleQ8) >> 8) + 0.5f) / 256f;
    }

    private static void EmitOracle(Placement* p, int gain, int* cells, long* q40, int ox0, int oy0, int ox1, int oy1)
    {
        var difference = stackalloc int[TileBake.DiffRows * TileBake.DiffPitch];
        var dense = stackalloc int[TileBake.TileSize * TileBake.TileSize];
        var smooth = stackalloc long[TileBake.TileSize * TileBake.TileSize];
        var carryRow = stackalloc int[TileBake.TileSize];
        for (var ty = oy0 >> TileBake.TileBits; ty <= (oy1 - 1) >> TileBake.TileBits; ty++)
        for (var tx = ox0 >> TileBake.TileBits; tx <= (ox1 - 1) >> TileBake.TileBits; tx++)
        {
            var tx0 = tx << TileBake.TileBits;
            var ty0 = ty << TileBake.TileBits;
            if (p->Plane == Plane.Difference) new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
            else if (p->Plane == Plane.Dense) new Span<int>(dense, TileBake.TileSize * TileBake.TileSize).Clear();
            else new Span<long>(smooth, TileBake.TileSize * TileBake.TileSize).Clear();
            new Span<int>(carryRow, TileBake.TileSize).Clear();

            if (p->Turned)
            {
                if (p->Plane == Plane.Dense) TileBake.EmitTurnedDense(dense, tx0, ty0, p, gain);
                else TileBake.EmitTurnedSmooth(smooth, tx0, ty0, p, gain);
            }
            else if (p->Plane == Plane.Difference)
            {
                TileBake.EmitBox(difference, tx0, ty0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
            }
            else if (p->Plane == Plane.Dense)
            {
                TileBake.EmitRaster(dense, tx0, ty0, p->Px, p->Py, p->Fx, p->Fy, p->X1, p->Y1, p->Sampling, p->V, gain);
            }
            else if (p->Plane == Plane.Tent)
            {
                TileBake.EmitTent(smooth, tx0, ty0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
            }
            else
            {
                TileBake.EmitBell(smooth, tx0, ty0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
            }

            for (var y = 0; y < TileBake.TileSize; y++)
            {
                var cellY = ty0 + y;
                if (cellY < oy0 || cellY >= oy1) continue;
                var row = cellY - oy0;
                if (p->Plane == Plane.Difference)
                {
                    var diffRow = difference + y * TileBake.DiffPitch;
                    var carry = 0;
                    for (var x = 0; x < TileBake.TileSize; x++)
                    {
                        carry += diffRow[x];
                        var value = carryRow[x] + carry;
                        carryRow[x] = value;
                        var cellX = tx0 + x;
                        if (cellX >= ox0 && cellX < ox1) cells[row * (ox1 - ox0) + cellX - ox0] += value;
                    }
                }
                else if (p->Plane == Plane.Dense)
                {
                    var denseRow = dense + y * TileBake.TileSize;
                    for (var x = 0; x < TileBake.TileSize; x++)
                    {
                        var cellX = tx0 + x;
                        if (cellX >= ox0 && cellX < ox1) cells[row * (ox1 - ox0) + cellX - ox0] += denseRow[x];
                    }
                }
                else
                {
                    var smoothRow = smooth + y * TileBake.TileSize;
                    for (var x = 0; x < TileBake.TileSize; x++)
                    {
                        var cellX = tx0 + x;
                        if (cellX >= ox0 && cellX < ox1) q40[row * (ox1 - ox0) + cellX - ox0] += smoothRow[x];
                    }
                }
            }
        }
    }

    private static void CheckTaps(
        byte world, byte stamp, int scaleQ8, int angle, int gain, int cellX, int cellY, int phaseX, int phaseY,
        bool metadata, bool clamped, string context)
    {
        var w = World.GetContext(world);
        Assert.True(w != null);
        var v = StampCatalog.Get(stamp);
        var x = PositionFor(v, true, cellX, phaseX, scaleQ8);
        var y = PositionFor(v, false, cellY, phaseY, scaleQ8);
        Assert.True(TileBake.Place(x, y, 0f, 0f, 256, Size, v, angle, scaleQ8, Layer.Precision.Exact, out var p),
            context);
        if (!clamped)
        {
            Assert.Equal(phaseX, p.Fx);
            Assert.Equal(phaseY, p.Fy);
            Assert.Equal(cellX, p.Px);
            Assert.Equal(cellY, p.Py);
        }

        var ox0 = Math.Max(p.X0, 0);
        var oy0 = Math.Max(p.Y0, 0);
        var ox1 = Math.Min(p.X1, Size);
        var oy1 = Math.Min(p.Y1, Size);
        if (ox1 <= ox0 || oy1 <= oy0)
        {
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, angle, Layer.Precision.Exact, &p, out var empty),
                context);
            Assert.True(empty.Width == 0 && empty.Height == 0, $"{context}: expected an empty kernel");
            return;
        }

        var width = ox1 - ox0;
        var height = oy1 - oy0;
        var cells = new int[width * height];
        var q40 = new long[width * height];
        fixed (int* cellPtr = cells)
        fixed (long* q40Ptr = q40)
        {
            EmitOracle(&p, gain, cellPtr, q40Ptr, ox0, oy0, ox1, oy1);
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, angle, Layer.Precision.Exact, &p, out var taps), context);

            var intKernel = taps.Cells != null;
            for (var cy = oy0; cy < oy1; cy++)
            for (var cx = ox0; cx < ox1; cx++)
            {
                var i = cx - p.Px - taps.OriginX;
                var j = cy - p.Py - taps.OriginY;
                long expected = 0;
                if (i >= 0 && i < taps.Width && j >= 0 && j < taps.Height)
                    expected = intKernel ? taps.Cells[j * taps.Width + i] * gain : taps.Q40[j * taps.Width + i] * gain;
                var actual = intKernel
                    ? cells[(cy - oy0) * width + cx - ox0]
                    : q40[(cy - oy0) * width + cx - ox0];
                if (expected != actual) Assert.Fail($"{context}: cell ({cx},{cy}) expected {expected} got {actual}");
            }

            var boundsX0 = p.Px + taps.OriginX;
            var boundsY0 = p.Py + taps.OriginY;
            var insideGrid = boundsX0 >= ox0 && boundsY0 >= oy0 && boundsX0 + taps.Width <= ox1 &&
                             boundsY0 + taps.Height <= oy1;
            if (!(metadata && Math.Abs((long)gain) <= 16 && insideGrid)) return;

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = -1;
            var maxY = -1;
            var maxTap = 0L;
            var rowFirst = new int[height];
            var rowEnd = new int[height];
            for (var j = 0; j < height; j++)
            {
                var first = -1;
                var last = -1;
                for (var i = 0; i < width; i++)
                {
                    var value = intKernel ? cells[j * width + i] : q40[j * width + i];
                    if (value == 0) continue;
                    if (first < 0) first = i;
                    last = i;
                    var magnitude = value < 0 ? -value : value;
                    if (magnitude > maxTap) maxTap = magnitude;
                }

                if (first < 0) continue;
                rowFirst[j] = first;
                rowEnd[j] = last + 1;
                if (first < minX) minX = first;
                if (last > maxX) maxX = last;
                if (j < minY) minY = j;
                maxY = j;
            }

            if (maxX < 0)
            {
                Assert.Equal(0, taps.Width);
                Assert.Equal(0, taps.Height);
                return;
            }

            Assert.True(minX + ox0 - p.Px == taps.OriginX, $"{context}: originX {minX + ox0 - p.Px} vs {taps.OriginX}");
            Assert.True(minY + oy0 - p.Py == taps.OriginY, $"{context}: originY");
            Assert.True(maxX - minX + 1 == taps.Width, $"{context}: width {maxX - minX + 1} vs {taps.Width}");
            Assert.True(maxY - minY + 1 == taps.Height, $"{context}: height");
            var expectedMax = Math.Abs((long)gain) * (intKernel ? taps.MaxTap : taps.MaxTapQ40);
            Assert.True(expectedMax == maxTap, $"{context}: maxTap {expectedMax} vs {maxTap}");
            for (var j = 0; j < height; j++)
            {
                var tapRow = j - minY;
                if (tapRow < 0 || tapRow >= taps.Height) continue;
                if (rowFirst[j] == 0 && rowEnd[j] == 0)
                {
                    Assert.True(taps.RowExtents[2 * tapRow] == 0 && taps.RowExtents[2 * tapRow + 1] == 0,
                        $"{context}: empty row {tapRow}");
                    continue;
                }

                Assert.True(rowFirst[j] - minX == taps.RowExtents[2 * tapRow], $"{context}: rowFirst {tapRow}");
                Assert.True(rowEnd[j] - minX == taps.RowExtents[2 * tapRow + 1], $"{context}: rowEnd {tapRow}");
            }
        }
    }

    private static void CheckCase(byte world, byte stamp, int scaleQ8, int angle, int gain, string context)
    {
        foreach (var (phaseX, phaseY) in Phases)
        {
            CheckTaps(world, stamp, scaleQ8, angle, gain, 35, 41, phaseX, phaseY, true, false, context);
            CheckTaps(world, stamp, scaleQ8, angle, gain, 30, 62, phaseX, phaseY, false, false, context);
        }
    }

    [Fact]
    public void BoxTaps_MatchEmissionForWidthsScalesAnglesAndGains()
    {
        var world = NewWorld();
        try
        {
            foreach (var (width, height) in new[]
                         { (1, 1), (2, 2), (1, 2), (2, 1), (3, 3), (4, 5), (7, 3), (8, 8), (16, 16) })
            foreach (var value in new sbyte[] { 1, 37, -50, 127, -128 })
            {
                var stamp = Stamp.Box((ushort)width, (ushort)height, value);
                foreach (var scale in Scales)
                foreach (var angle in Angles)
                foreach (var gain in Gains)
                    CheckCase(world, stamp, scale, angle, gain,
                        $"box {width}x{height} v{value} s{scale} a{angle} g{gain}");
                Assert.True(Stamp.Free(stamp));
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void RasterTaps_MatchEmissionForWidthsScalesAnglesAndGains()
    {
        var world = NewWorld();
        try
        {
            foreach (var (width, height) in new[] { (2, 1), (1, 2), (3, 2), (4, 3), (7, 5) })
            {
                var samples = RasterSamples(width, height);
                fixed (sbyte* data = samples)
                {
                    var stamp = Stamp.New(data, (ushort)width, (ushort)height);
                    foreach (var scale in Scales)
                    foreach (var angle in Angles)
                    foreach (var gain in Gains)
                        CheckCase(world, stamp, scale, angle, gain,
                            $"raster {width}x{height} s{scale} a{angle} g{gain}");
                    Assert.True(Stamp.Free(stamp));
                }
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void SmoothTaps_MatchEmissionForWidthsScalesAnglesAndGains()
    {
        var world = NewWorld();
        try
        {
            var stamps = new[]
            {
                Stamp.Tent(1, 1, 60), Stamp.Tent(2, 2, -60), Stamp.Tent(3, 5, 127), Stamp.Tent(8, 8, -128),
                Stamp.Tent(12, 7, 40), Stamp.Bell(1, 1, 60), Stamp.Bell(2, 2, -60), Stamp.Bell(3, 3, 127),
                Stamp.Bell(6, 6, -128), Stamp.Bell(9, 4, 40), Stamp.Bell(64, 64, 90)
            };
            foreach (var stamp in stamps)
            {
                foreach (var scale in Scales)
                foreach (var angle in Angles)
                foreach (var gain in Gains)
                    CheckCase(world, stamp, scale, angle, gain,
                        $"smooth {StampCatalog.Get(stamp)->Kind} s{scale} a{angle} g{gain}");
                Assert.True(Stamp.Free(stamp));
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void RoundTaps_MatchEmissionForKindsRadiiArcsScalesAnglesAndGains()
    {
        var world = NewWorld();
        try
        {
            foreach (var radius in new[] { 1, 2, 3, 5, 16 })
            foreach (var kind in new[] { StampKind.Disk, StampKind.Cone, StampKind.Dome })
            {
                var stamp = StampOf(kind, radius, 90, 360);
                foreach (var scale in Scales)
                foreach (var angle in Angles)
                foreach (var gain in Gains)
                    CheckCase(world, stamp, scale, angle, gain, $"{kind} r{radius} s{scale} a{angle} g{gain}");
                Assert.True(Stamp.Free(stamp));
            }

            foreach (var radius in new[] { 8, 33, 64 })
            foreach (var arc in new[] { 45, 90, 270 })
            foreach (var kind in new[] { StampKind.Disk, StampKind.Cone, StampKind.Dome })
            {
                var stamp = StampOf(kind, radius, 100, arc);
                foreach (var scale in new[] { 128, 256 })
                foreach (var angle in Angles)
                foreach (var gain in Gains)
                    CheckCase(world, stamp, scale, angle, gain, $"{kind} r{radius} arc{arc} s{scale} a{angle} g{gain}");
                Assert.True(Stamp.Free(stamp));
            }

            foreach (var kind in new[] { StampKind.Disk, StampKind.Cone, StampKind.Dome })
            {
                var stamp = StampOf(kind, 64, 90, 360);
                foreach (var gain in Gains)
                    CheckCase(world, stamp, 256, 0, gain, $"{kind} r64 s256 g{gain}");
                Assert.True(Stamp.Free(stamp));
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void BoxTaps_MatchEmissionAtEdgePlacements()
    {
        var world = NewWorld();
        try
        {
            foreach (var (width, height) in new[] { (1, 1), (2, 2), (3, 4), (8, 8), (16, 16) })
            {
                var stamp = Stamp.Box((ushort)width, (ushort)height, 70);
                foreach (var scale in Scales)
                foreach (var (cellX, cellY) in new[]
                         {
                             (0, 0), (1, 1), (0, 41), (35, 0), (Size - 1, Size - 1), (Size - 3, 40), (40, Size - 3),
                             (Size - 20, Size - 20)
                         })
                foreach (var (phaseX, phaseY) in Phases)
                {
                    var clamped = cellX < 16 || cellY < 16 || cellX > Size - 16 || cellY > Size - 16;
                    CheckTaps(world, stamp, scale, 0, 5, cellX, cellY, phaseX, phaseY, true, clamped,
                        $"edge box {width}x{height} s{scale} @{cellX},{cellY} p{phaseX},{phaseY}");
                }

                CheckTaps(world, stamp, 256, 0, 5, -5, -5, 25, 25, true, true, $"left-top box {width}x{height}");
                Assert.True(Stamp.Free(stamp));
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void BoxTaps_MatchEmissionForAllPhasesOfBoxesUpTo8x8()
    {
        var world = NewWorld();
        var w = World.GetContext(world);
        var difference = stackalloc int[TileBake.DiffRows * TileBake.DiffPitch];
        var carryRow = stackalloc int[TileBake.TileSize];
        var cells = stackalloc int[16 * 16];
        try
        {
            for (var width = 1; width <= 8; width++)
            for (var height = 1; height <= 8; height++)
            {
                var stamp = Stamp.Box((ushort)width, (ushort)height, 37);
                var variant = StampCatalog.Get(stamp);
                var offsetX = ((long)variant->OriginQ8X * 256) >> 8;
                var offsetY = ((long)variant->OriginQ8Y * 256) >> 8;
                for (var phaseX = 0; phaseX < 256; phaseX++)
                for (var phaseY = 0; phaseY < 256; phaseY++)
                {
                    var x = ((35 << 8) + phaseX - offsetX + 0.5f) / 256f;
                    var y = ((41 << 8) + phaseY - offsetY + 0.5f) / 256f;
                    Assert.True(TileBake.Place(x, y, 0f, 0f, 256, Size, variant, 0, 256, Layer.Precision.Exact,
                        out var p));
                    Assert.True(p.Px == 35 && p.Py == 41 && p.Fx == phaseX && p.Fy == phaseY,
                        $"{width}x{height} {phaseX},{phaseY}");

                    new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
                    new Span<int>(carryRow, TileBake.TileSize).Clear();
                    new Span<int>(cells, 16 * 16).Clear();
                    TileBake.EmitBox(difference, 32, 32, p.Px, p.Py, p.Fx, p.Fy, p.ExtentX, p.ExtentY, variant, 3);
                    for (var row = 0; row < 16; row++)
                    {
                        var carry = 0;
                        for (var column = 0; column < 16; column++)
                        {
                            carry += difference[row * TileBake.DiffPitch + column];
                            var value = carryRow[column] + carry;
                            carryRow[column] = value;
                            cells[row * 16 + column] += value;
                        }
                    }

                    Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &p, out var taps));
                    for (var row = 0; row < 16; row++)
                    for (var column = 0; column < 16; column++)
                    {
                        var i = 32 + column - p.Px - taps.OriginX;
                        var j = 32 + row - p.Py - taps.OriginY;
                        var expected = i >= 0 && i < taps.Width && j >= 0 && j < taps.Height
                            ? taps.Cells[j * taps.Width + i] * 3
                            : 0;
                        if (expected != cells[row * 16 + column])
                            Assert.Fail(
                                $"box {width}x{height} phase {phaseX},{phaseY}: cell ({column},{row}) expected {expected} got {cells[row * 16 + column]}");
                    }
                }

                Assert.True(Stamp.Free(stamp));
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void WrapGains_MatchInInt32AndWrappingInt64()
    {
        var world = NewWorld();
        try
        {
            foreach (var gain in new[] { 1 << 24, -(1 << 24), 1 << 30, -(1 << 30), int.MinValue, int.MaxValue })
            {
                var box = Stamp.Box(8, 8, 127);
                CheckCase(world, box, 256, 0, gain, $"wrap box g{gain}");
                Assert.True(Stamp.Free(box));

                var turned = Stamp.Box(4, 4, -100);
                CheckCase(world, turned, 947, 16384, gain, $"wrap turned box g{gain}");
                Assert.True(Stamp.Free(turned));
            }

            foreach (var gain in new[] { 1 << 30, -(1 << 30), int.MaxValue, int.MinValue })
            {
                var tent = Stamp.Tent(6, 6, 127);
                CheckCase(world, tent, 256, 0, gain, $"wrap tent g{gain}");
                CheckCase(world, tent, 256, 8192, gain, $"wrap turned tent g{gain}");
                Assert.True(Stamp.Free(tent));

                var bell = Stamp.Bell(9, 9, -128);
                CheckCase(world, bell, 947, 0, gain, $"wrap bell g{gain}");
                Assert.True(Stamp.Free(bell));

                var dome = StampOf(StampKind.Dome, 8, 100, 360);
                CheckCase(world, dome, 256, 0, gain, $"wrap dome g{gain}");
                Assert.True(Stamp.Free(dome));
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void AccumulatedTaps_MatchAccumulatedEmissionWithWrapping()
    {
        const int count = 600;
        var world = NewWorld();
        try
        {
            var stamp = Stamp.Tent(6, 6, 127);
            var w = World.GetContext(world);
            var variant = StampCatalog.Get(stamp);
            var plane = new long[64 * 64];
            var tapsPlane = new long[64 * 64];
            fixed (long* planePtr = plane)
            fixed (long* tapsPtr = tapsPlane)
            {
                var smooth = stackalloc long[TileBake.TileSize * TileBake.TileSize];
                var seed = 0x12345678u;
                for (var n = 0; n < count; n++)
                {
                    seed = seed * 1664525u + 1013904223u;
                    var gain = (int)(seed >> 24) | 64;
                    var phaseX = (int)(seed >> 16) & 255;
                    seed = seed * 1664525u + 1013904223u;
                    var phaseY = (int)(seed >> 16) & 255;
                    var x = ((35 << 8) + phaseX - (((long)variant->OriginQ8X * 256) >> 8) + 0.5f) / 256f;
                    var y = ((41 << 8) + phaseY - (((long)variant->OriginQ8Y * 256) >> 8) + 0.5f) / 256f;
                    Assert.True(TileBake.Place(x, y, 0f, 0f, 256, Size, variant, 0, 256, Layer.Precision.Exact,
                        out var p));
                    new Span<long>(smooth, TileBake.TileSize * TileBake.TileSize).Clear();
                    TileBake.EmitTent(smooth, 32, 32, p.Px, p.Py, p.Fx, p.Fy, p.ExtentX, p.ExtentY, variant, gain);
                    for (var row = 0; row < TileBake.TileSize; row++)
                    for (var column = 0; column < TileBake.TileSize; column++)
                        planePtr[row * 64 + column] += smooth[row * TileBake.TileSize + column];

                    Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &p, out var taps));
                    for (var j = 0; j < taps.Height; j++)
                    for (var i = 0; i < taps.Width; i++)
                    {
                        var cellX = p.Px + taps.OriginX + i;
                        var cellY = p.Py + taps.OriginY + j;
                        if (cellX < 32 || cellX >= 64 || cellY < 32 || cellY >= 64) continue;
                        tapsPtr[cellX - 32 + (cellY - 32) * 64] += taps.Q40[j * taps.Width + i] * gain;
                    }
                }

                Assert.Equal(tapsPlane, plane);
            }

            Assert.True(Stamp.Free(stamp));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void EdgeClippedBox_DoesNotReuseAPureEntryOfTheSamePhase()
    {
        var world = NewWorld();
        try
        {
            var stamp = Stamp.Box(8, 8, 90);
            var w = World.GetContext(world);
            var variant = StampCatalog.Get(stamp);
            var x = ((35 << 8) + 128 - (((long)variant->OriginQ8X * 256) >> 8) + 0.5f) / 256f;
            var y = ((41 << 8) + 128 - (((long)variant->OriginQ8Y * 256) >> 8) + 0.5f) / 256f;
            Assert.True(TileBake.Place(x, y, 0f, 0f, 256, Size, variant, 0, 256, Layer.Precision.Exact, out var mid));
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &mid, out _));
            var key = new KernelKey(stamp, mid.Sampling, 0, mid.Fx, mid.Fy, 0);
            Assert.True(w->Kernels.Find(key.Main, key.Turn) != null);

            CheckTaps(world, stamp, 256, 0, 7, Size - 3, 40, 128, 128, true, false, "right edge reuses phase 128");
            CheckTaps(world, stamp, 256, 0, 7, 40, Size - 3, 128, 128, true, false, "bottom edge reuses phase 128");
            CheckTaps(world, stamp, 256, 0, 7, -5, -5, 25, 25, true, true, "corner clamps to phase 0");
            Assert.True(Stamp.Free(stamp));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void Cache_StoresHitsKeepsAWorkingSetResidentEvictsOnOverflowAndSkipsOversizedKernels()
    {
        var world = NewWorld();
        try
        {
            var w = World.GetContext(world);
            var stamp = Stamp.Box(4, 4, 60);
            var variant = StampCatalog.Get(stamp);

            Placement PlaceAt(int phase)
            {
                var x = ((35L << 8) + phase - (((long)variant->OriginQ8X * 256) >> 8) + 0.5f) / 256f;
                Assert.True(TileBake.Place(x, 41.5f, 0f, 0f, 256, Size, variant, 0, 256, Layer.Precision.Exact,
                    out var p));
                return p;
            }

            var first = PlaceAt(1);
            var key = new KernelKey(stamp, first.Sampling, 0, first.Fx, first.Fy, 0);
            Assert.True(w->Kernels.Find(key.Main, key.Turn) == null);
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &first, out var taps));
            Assert.Equal(KernelShape.Box, taps.Shape);
            Assert.Equal(Plane.Difference, taps.Plane);
            Assert.True(w->Kernels.Find(key.Main, key.Turn) != null);

            var again = PlaceAt(1);
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &again, out var second));
            Assert.Equal(taps.Cells[0], second.Cells[0]);

            var resident = new List<KernelKey> { key };
            var phase = 2;
            for (; resident.Count < KernelCache.EntryCount; phase++)
            {
                var placement = PlaceAt(phase);
                w->Kernels.BeginFrame();
                Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &placement, out _));
                resident.Add(new KernelKey(stamp, placement.Sampling, 0, placement.Fx, placement.Fy, 0));
            }

            Assert.Equal(KernelCache.EntryCount, resident.Count);
            foreach (var residentKey in resident)
                Assert.True(w->Kernels.Find(residentKey.Main, residentKey.Turn) != null,
                    "a working set within the entry count stays fully resident");
            Assert.Equal(KernelCache.EntryCount, w->Kernels.LiveEntries);

            w->Kernels.BeginFrame();
            var overflow = PlaceAt(phase);
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &overflow, out _));
            Assert.Equal(KernelCache.EntryCount, w->Kernels.LiveEntries);
            var evicted = 0;
            foreach (var residentKey in resident)
                if (w->Kernels.Find(residentKey.Main, residentKey.Turn) == null)
                    evicted++;
            Assert.Equal(1, evicted);
            var overflowKey = new KernelKey(stamp, overflow.Sampling, 0, overflow.Fx, overflow.Fy, 0);
            Assert.True(w->Kernels.Find(overflowKey.Main, overflowKey.Turn) != null);

            var churned = new List<KernelKey>();
            for (; phase < 256; phase++)
            {
                var placement = PlaceAt(phase);
                w->Kernels.BeginFrame();
                Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &placement, out _));
                churned.Add(new KernelKey(stamp, placement.Sampling, 0, placement.Fx, placement.Fy, 0));
            }

            Assert.Equal(KernelCache.EntryCount, w->Kernels.LiveEntries);
            var served = 0;
            foreach (var churnedKey in churned)
                if (w->Kernels.Find(churnedKey.Main, churnedKey.Turn) != null)
                    served++;
            Assert.Equal(churned.Count, served);
            foreach (var retiredKey in resident.Take(resident.Count - 1))
                Assert.True(w->Kernels.Find(retiredKey.Main, retiredKey.Turn) == null,
                    "a full table evicts in insertion order");
            Assert.True(w->Kernels.Find(resident[^1].Main, resident[^1].Turn) != null);

            var big = Stamp.Box(100, 100, 60);
            Assert.True(TileBake.Place(300.5f, 300.5f, 0f, 0f, 256, Size, StampCatalog.Get(big), 0, 256,
                Layer.Precision.Exact, out var oversized));
            Assert.True(KernelFactory.TryGetTaps(w, 0, big, 0, Layer.Precision.Exact, &oversized, out var bigTaps));
            Assert.True(bigTaps.Width > KernelCache.MaxSide || bigTaps.Height > KernelCache.MaxSide);
            var bigKey = new KernelKey(big, oversized.Sampling, 0, oversized.Fx, oversized.Fy, 0);
            Assert.True(w->Kernels.Find(bigKey.Main, bigKey.Turn) == null);

            Assert.True(Stamp.Free(big));
            Assert.True(Stamp.Free(stamp));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void InsertCap_ClosesAfterTheFrameBudgetAndReopensEachFrame()
    {
        var world = NewWorld();
        try
        {
            var w = World.GetContext(world);
            var stamp = Stamp.Box(4, 4, 60);
            var variant = StampCatalog.Get(stamp);
            w->Kernels.BeginFrame();
            for (var phase = 1; phase <= KernelCache.FrameInsertCap + 4; phase++)
            {
                Assert.Equal(phase <= KernelCache.FrameInsertCap, w->Kernels.CanInsert);
                var x = ((35L << 8) + phase - (((long)variant->OriginQ8X * 256) >> 8) + 0.5f) / 256f;
                Assert.True(TileBake.Place(x, 41.5f, 0f, 0f, 256, Size, variant, 0, 256, Layer.Precision.Exact,
                    out var p));
                Assert.False(KernelFactory.TryFindTaps(w, 0, stamp, 0, Layer.Precision.Exact, &p, out _));
                Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &p, out _));
                Assert.True(KernelFactory.TryFindTaps(w, 0, stamp, 0, Layer.Precision.Exact, &p, out var found));
                Assert.True(found.MaxTap > 0);
            }

            w->Kernels.BeginFrame();
            Assert.True(w->Kernels.CanInsert);
            w->Kernels.InsertsDisabled = true;
            Assert.False(w->Kernels.CanInsert);
            Assert.True(Stamp.Free(stamp));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void FullArcRoundKernels_ShareOneEntryAcrossAngles_AndArcsDoNot()
    {
        var world = NewWorld();
        try
        {
            var w = World.GetContext(world);
            var full = Stamp.Disk(3, 40);
            var arc = Stamp.Disk(3, 40, 120);
            foreach (var (stamp, shared) in new[] { (full, true), (arc, false) })
            {
                var seen = new HashSet<long>();
                foreach (var angle in new[] { 0, 777, 16384, 40000 })
                {
                    w->Kernels.BeginFrame();
                    Assert.True(TileBake.Place(35.5f, 41.5f, 0f, 0f, 256, Size, StampCatalog.Get(stamp), angle, 256,
                        Layer.Precision.Exact, out var p));
                    Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, angle, Layer.Precision.Exact, &p, out var taps));
                    seen.Add((long)taps.Cells);
                }

                Assert.Equal(shared ? 1 : 4, seen.Count);
            }

            Assert.True(Stamp.Free(full));
            Assert.True(Stamp.Free(arc));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void DistinctKeys_SeparateStampsGridsScalesAndAngles()
    {
        var world = NewWorld();
        try
        {
            var w = World.GetContext(world);
            Grid.New(world, GridPower, 0f, 0f, Size);
            var a = Stamp.Box(4, 4, 60);
            var b = Stamp.Box(4, 4, 60);

            void CheckDistinct(byte stampId, int grid, int angle, int scale)
            {
                Assert.True(TileBake.Place(35.5f, 41.5f, 0f, 0f, 256, Size, StampCatalog.Get(stampId), angle, scale,
                    Layer.Precision.Exact, out var p));
                var key = new KernelKey(stampId, p.Sampling, grid, p.Fx, p.Fy, angle);
                Assert.True(w->Kernels.Find(key.Main, key.Turn) == null);
                Assert.True(KernelFactory.TryGetTaps(w, grid, stampId, angle, Layer.Precision.Exact, &p, out _));
                Assert.True(w->Kernels.Find(key.Main, key.Turn) != null);
                Assert.Equal(angle & 0xFFFF, (int)key.Turn);
            }

            CheckDistinct(a, 0, 0, 256);
            CheckDistinct(b, 1, 0, 256);
            CheckDistinct(a, 0, 0, 128);
            CheckDistinct(a, 0, 16384, 256);
            Assert.True(Stamp.Free(a));
            Assert.True(Stamp.Free(b));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void StampFree_FlushesCachedKernels()
    {
        var world = NewWorld();
        try
        {
            var w = World.GetContext(world);
            var stamp = Stamp.Box(4, 4, 60);
            Assert.True(TileBake.Place(35.5f, 41.5f, 0f, 0f, 256, Size, StampCatalog.Get(stamp), 0, 256,
                Layer.Precision.Exact, out var p));
            Assert.True(KernelFactory.TryGetTaps(w, 0, stamp, 0, Layer.Precision.Exact, &p, out _));
            var key = new KernelKey(stamp, p.Sampling, 0, p.Fx, p.Fy, 0);
            Assert.True(w->Kernels.Find(key.Main, key.Turn) != null);
            Assert.True(Stamp.Free(stamp));
            Assert.True(w->Kernels.Find(key.Main, key.Turn) == null);
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void StructureTags_ClassifyKernels()
    {
        var world = NewWorld();
        try
        {
            var w = World.GetContext(world);
            var samples = RasterSamples(3, 2);
            byte raster;
            fixed (sbyte* data = samples)
            {
                raster = Stamp.New(data, 3, 2);
            }

            var stamps = new[]
            {
                (Stamp.Box(4, 4, 60), 0, KernelShape.Box, Plane.Difference),
                (raster, 0, KernelShape.Dense, Plane.Dense),
                (Stamp.Tent(5, 5, 60), 0, KernelShape.Separable, Plane.Tent),
                (Stamp.Bell(5, 5, 60), 0, KernelShape.Separable, Plane.Bell),
                (StampOf(StampKind.Disk, 8, 60, 360), 0, KernelShape.Spans, Plane.Dense),
                (StampOf(StampKind.Cone, 8, 60, 360), 0, KernelShape.Spans, Plane.Dense),
                (StampOf(StampKind.Dome, 8, 60, 360), 0, KernelShape.Spans, Plane.Dense),
                (StampOf(StampKind.Cone, 8, 60, 90), 0, KernelShape.Dense, Plane.Dense),
                (Stamp.Box(4, 4, 60), 16384, KernelShape.Dense, Plane.Dense),
                (Stamp.Tent(5, 5, 60), 8192, KernelShape.Dense, Plane.Tent),
                (Stamp.Bell(5, 5, 60), 32768, KernelShape.Dense, Plane.Bell)
            };
            foreach (var (id, angle, shape, plane) in stamps)
            {
                Assert.True(TileBake.Place(35.5f, 41.5f, 0f, 0f, 256, Size, StampCatalog.Get(id), angle, 256,
                    Layer.Precision.Exact, out var p));
                Assert.True(KernelFactory.TryGetTaps(w, 0, id, angle, Layer.Precision.Exact, &p, out var taps));
                Assert.Equal(shape, taps.Shape);
                Assert.Equal(plane, taps.Plane);
                Assert.True(taps.Width > 0 && taps.Height > 0);
                Assert.True(taps.MaxTap > 0 || taps.MaxTapQ40 > 0);
            }

            foreach (var (id, _, _, _) in stamps) Assert.True(Stamp.Free(id));
        }
        finally
        {
            World.Free(world);
        }
    }

    private static sbyte[] RasterSamples(int width, int height)
    {
        var samples = new sbyte[width * height];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (sbyte)(i * 37 % 231 - 115);
        return samples;
    }

    private static byte StampOf(StampKind kind, int radius, sbyte value, int arc)
    {
        return kind switch
        {
            StampKind.Disk => Stamp.Disk((ushort)radius, value, (ushort)arc),
            StampKind.Cone => Stamp.Cone((ushort)radius, value, (ushort)arc),
            _ => Stamp.Dome((ushort)radius, value, (ushort)arc)
        };
    }
}
