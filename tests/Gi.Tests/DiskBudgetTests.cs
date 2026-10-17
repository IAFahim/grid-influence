using Xunit;

namespace Gi.Tests;

public sealed class DiskBudgetTests
{
    private readonly record struct ExpectedDisk(long Sum, short Max, int MaxX, int MaxY, short Nearest, int NearestX, int NearestY, bool Complete);

    private static ExpectedDisk Scan(short[] cells, int size, int qx, int qy, int radius, short threshold)
    {
        var radiusSquared = (long)radius * radius;
        var centerX = qx >> 8;
        var centerY = qy >> 8;
        var total = 0L;
        var max = short.MinValue;
        var maxX = 0;
        var maxY = 0;
        var found = false;
        var nearest = short.MinValue;
        var nearestX = -1;
        var nearestY = -1;
        var bestDistance = long.MaxValue;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = ((long)x << 8) + 128 - qx;
            var dy = ((long)y << 8) + 128 - qy;
            var distance = dx * dx + dy * dy;
            if (distance > radiusSquared && (x != centerX || y != centerY)) continue;

            var cell = cells[y * size + x];
            total += cell;
            if (!found || cell > max)
            {
                found = true;
                max = cell;
                maxX = x;
                maxY = y;
            }

            if (cell < threshold || distance >= bestDistance) continue;
            bestDistance = distance;
            nearest = cell;
            nearestX = x;
            nearestY = y;
        }

        var firstX = Math.Min(-(-(long)(qx - radius - 128) >> 8), centerX);
        var firstY = Math.Min(-(-(long)(qy - radius - 128) >> 8), centerY);
        var lastX = Math.Max(((long)qx + radius - 128) >> 8, centerX);
        var lastY = Math.Max(((long)qy + radius - 128) >> 8, centerY);
        return new ExpectedDisk(total, max, maxX, maxY, nearest, nearestX, nearestY,
            firstX >= 0 && firstY >= 0 && lastX < size && lastY < size);
    }

    private static unsafe short[] Snapshot(byte world, byte grid, byte layer, int size)
    {
        var cells = new short[size * size];
        fixed (short* pointer = cells) World.QueryRegion(world, grid, layer, 0, 0, size, size, pointer);
        return cells;
    }

    [Fact]
    public void DiskReads_MatchIntegerMembershipAtEverySubcellPhase()
    {
        var world = World.New();
        try
        {
            const int size = 64;
            var grid = Grid.New(world, 6, 0, 0, size);
            var layer = Layer.New(world);
            var box = Stamp.Box(8, 7, 91);
            var tent = Stamp.Tent(19, 13, 103);
            for (var i = 0; i < 45; i++)
                World.Place(world, layer, (i * 13 % size) + 0.25f, (i * 29 % size) + 0.75f,
                    (i & 1) == 0 ? box : tent, (sbyte)(i % 9 - 4));
            World.Process(world);
            var cells = Snapshot(world, grid, layer, size);
            int[] radii = [0, 1, 31, 127, 128, 181, 255, 256, 257, 1023, 4096, 7936, 65536];
            for (var phase = 0; phase < 256; phase++)
            {
                foreach (var radius in radii)
                {
                    var cellX = (phase % 4) switch { 0 => 0, 1 => 31, 2 => 32, _ => 63 };
                    var cellY = (phase % 3) switch { 0 => 0, 1 => 32, _ => 63 };
                    var qx = (cellX << 8) + phase;
                    var qy = (cellY << 8) + ((phase * 73) & 255);
                    var x = qx / 256f;
                    var y = qy / 256f;
                    var threshold = (short)((phase % 5) switch { 0 => -200, 1 => 0, 2 => 100, 3 => 1000, _ => short.MaxValue });
                    var expected = Scan(cells, size, qx, qy, radius, threshold);

                    Assert.Equal(expected.Complete, World.TrySenseArea(world, layer, x, y, radius / 256f, out var sum));
                    Assert.Equal(expected.Sum, sum);
                    Assert.Equal(expected.Complete, World.TrySenseMax(world, layer, x, y, radius / 256f, out var max, out var maxX, out var maxY));
                    Assert.Equal((expected.Max, expected.MaxX + 0.5f, expected.MaxY + 0.5f), (max, maxX, maxY));
                    Assert.Equal(expected.Complete, World.TrySenseNearest(world, layer, x, y, radius / 256f, threshold,
                        out var nearest, out var nearestX, out var nearestY));
                    Assert.Equal(expected.Nearest, nearest);
                    Assert.Equal(expected.NearestX < 0 ? x : expected.NearestX + 0.5f, nearestX);
                    Assert.Equal(expected.NearestY < 0 ? y : expected.NearestY + 0.5f, nearestY);
                }
            }
        }
        finally { World.Free(world); }
    }

    [Fact]
    public void DiskReads_IncludeTheContainingCellWhenNoCenterMeetsTheRadius()
    {
        var world = World.New();
        try
        {
            Grid.New(world, 6, 0, 0, 64);
            var layer = Layer.New(world);
            World.Place(world, layer, 32.5f, 32.5f, Stamp.Box(1, 1, 79), 1);
            World.Process(world);
            foreach (var reach in new[] { 0f, 1 / 256f, 0.125f })
            {
                Assert.True(World.TrySenseArea(world, layer, 32f, 32f, reach, out var area));
                Assert.Equal(79, area);
                Assert.True(World.TrySenseMax(world, layer, 32f, 32f, reach, out var max, out var maxX, out var maxY));
                Assert.Equal(((short)79, 32.5f, 32.5f), (max, maxX, maxY));
                Assert.True(World.TrySenseNearest(world, layer, 32f, 32f, reach, 1, out var nearest, out var nearestX, out var nearestY));
                Assert.Equal(((short)79, 32.5f, 32.5f), (nearest, nearestX, nearestY));
            }
        }
        finally { World.Free(world); }
    }

    [Fact]
    public void DiskMaxAndNearest_KeepRowMajorTiesAcrossTileEdges()
    {
        var world = World.New();
        try
        {
            Grid.New(world, 7, 0, 0, 128);
            var layer = Layer.New(world);
            var stamp = Stamp.Box(1, 1, 95);
            World.Place(world, layer, 32.5f, 31.5f, stamp, 1);
            World.Place(world, layer, 31.5f, 32.5f, stamp, 1);
            World.Place(world, layer, 33.5f, 32.5f, stamp, 1);
            World.Process(world);
            Assert.True(World.TrySenseMax(world, layer, 32.5f, 32.5f, 10, out var max, out var maxX, out var maxY));
            Assert.Equal(((short)95, 32.5f, 31.5f), (max, maxX, maxY));
            Assert.True(World.TrySenseNearest(world, layer, 32.5f, 32.5f, 10, 95, out var nearest, out var nearestX, out var nearestY));
            Assert.Equal(((short)95, 32.5f, 31.5f), (nearest, nearestX, nearestY));
        }
        finally { World.Free(world); }
    }

    [Fact]
    public void Changed_RequiresACellCenterOrTheContainingCellInTheTouchedTile()
    {
        var world = World.New();
        try
        {
            Grid.New(world, 6, 0, 0, 64);
            var layer = Layer.New(world);
            World.Place(world, layer, 32.5f, 32.5f, Stamp.Box(1, 1, 37), 1);
            World.Process(world);
            var since = World.Tick(world) - 1;
            Assert.False(World.Changed(world, layer, 31.99f, 31.99f, 0.5f, since));
            Assert.True(World.Changed(world, layer, 31.99f, 31.99f, 0.75f, since));
            Assert.True(World.Changed(world, layer, 32f, 32f, 0, since));
        }
        finally { World.Free(world); }
    }

    [Fact]
    public unsafe void Sensing_RejectsNonFiniteCoordinatesAndReach()
    {
        var world = World.New();
        try
        {
            Grid.New(world, 6, 0, 0, 64);
            var layer = Layer.New(world);
            World.Place(world, layer, 0.5f, 0.5f, Stamp.Box(1, 1, 63), 1);
            World.Process(world);
            foreach (var invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            {
                Assert.False(World.Covers(world, invalid, 0));
                Assert.False(World.Covers(world, 0, invalid));
                Assert.False(World.TrySense(world, layer, invalid, 0, out var point));
                Assert.Equal(0, point);
                Assert.False(World.TrySenseGradient(world, layer, 0, invalid, out var gx, out var gy));
                Assert.Equal((0f, 0f), (gx, gy));
                Assert.False(World.TrySenseArea(world, layer, invalid, 0, 1, out var area));
                Assert.Equal(0, area);
                Assert.False(World.TrySenseArea(world, layer, 0.5f, 0.5f, invalid, out area));
                Assert.Equal(0, area);
                Assert.False(World.TrySenseMax(world, layer, 0, invalid, 1, out var max, out var maxX, out var maxY));
                Assert.Equal((0, 0f, 0f), (max, maxX, maxY));
                Assert.False(World.TrySenseMax(world, layer, 0.5f, 0.5f, invalid, out max, out maxX, out maxY));
                Assert.Equal((0, 0f, 0f), (max, maxX, maxY));
                Assert.False(World.TrySenseNearest(world, layer, invalid, 0, 1, 0, out var nearest, out var nx, out var ny));
                Assert.Equal((short.MinValue, 0f, 0f), (nearest, nx, ny));
                Assert.False(World.TrySenseNearest(world, layer, 0.5f, 0.5f, invalid, 0, out nearest, out nx, out ny));
                Assert.Equal((short.MinValue, 0f, 0f), (nearest, nx, ny));
                Assert.False(World.Changed(world, layer, 0.5f, 0.5f, invalid, 0));

                float x = 0.5f, y = 0.5f, r = invalid, bx = 9f, by = 9f;
                short v = 99, threshold = 0;
                long total = 99;
                var mask = ulong.MaxValue;
                Assert.False((World.SenseAreaColumn(world, layer, &x, &y, &r, null, 1, &total, &mask) == World.ReadStatus.Complete));
                Assert.Equal((0L, 0ul), (total, mask));
                mask = ulong.MaxValue;
                Assert.False((World.SenseMaxColumn(world, layer, &x, &y, &r, 1, &v, &bx, &by, &mask) == World.ReadStatus.Complete));
                Assert.Equal(((short)0, 0f, 0f, 0ul), (v, bx, by, mask));
                bx = 9f;
                by = 9f;
                mask = ulong.MaxValue;
                Assert.False((World.SenseNearestColumn(world, layer, &x, &y, &r, &threshold, 1, &v, &bx, &by, &mask) == World.ReadStatus.Complete));
                Assert.Equal((short.MinValue, 0f, 0f, 0ul), (v, bx, by, mask));
                mask = ulong.MaxValue;
                Assert.Equal(0, World.ChangedMask(world, layer, &x, &y, invalid, 0, 1, &mask));
                Assert.Equal(0ul, mask);
            }
        }
        finally { World.Free(world); }
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(0, false, -1)]
    [InlineData(1, false, 1)]
    [InlineData(1, false, -1)]
    [InlineData(2, false, 1)]
    [InlineData(2, false, -1)]
    [InlineData(3, false, 1)]
    [InlineData(3, false, -1)]
    [InlineData(0, true, 1)]
    [InlineData(0, true, -1)]
    [InlineData(1, true, 1)]
    [InlineData(1, true, -1)]
    [InlineData(2, true, 1)]
    [InlineData(2, true, -1)]
    [InlineData(3, true, 1)]
    [InlineData(3, true, -1)]
    public void AreaExclusion_MatchesRemovalAcrossSaturationAndRotation(int kind, bool derived, int sign)
    {
        var world = World.New();
        try
        {
            Grid.New(world, 7, 0, 0, 128);
            var sourceLayer = Layer.New(world);
            var otherLayer = Layer.New(world);
            var layer = derived ? Layer.Sum(world, sourceLayer, 1, otherLayer, -2) : sourceLayer;
            var stamp = kind switch
            {
                0 => Stamp.Box(17, 13, 127),
                1 => Stamp.Tent(17, 13, 127),
                2 => Stamp.Bell(17, 13, 127),
                _ => Stamp.New(new sbyte[] { -127, 120, 127, -40, 80, 127, 100, -90, 127 }, 3, 3),
            };
            var background = Stamp.Box(80, 80, 127);
            for (var i = 0; i < 60; i++)
                World.Place(world, sourceLayer, 64, 64, background, (sbyte)(sign * (i < 38 ? 16 : -16)));
            World.Place(world, otherLayer, 65, 66, Stamp.Bell(40, 30, 113), -16);
            var excluded = World.Place(world, sourceLayer, 63.875f, 64.125f, stamp, (sbyte)(16 * sign));
            World.Turn(world, excluded, 0.7f);
            World.Scale(world, excluded, 1.25f);
            World.Process(world);
            float[] reaches = [0, 1 / 256f, 0.625f, 4.5f, 16, 40];
            var sums = new long[reaches.Length];
            for (var i = 0; i < reaches.Length; i++)
                Assert.True(World.TrySenseArea(world, layer, 64.25f, 64.75f, reaches[i], excluded, out sums[i]));
            World.Move(world, excluded, 24, 24);
            for (var i = 0; i < reaches.Length; i++)
            {
                Assert.True(World.TrySenseArea(world, layer, 64.25f, 64.75f, reaches[i], excluded, out var pending));
                Assert.Equal(sums[i], pending);
            }
            World.Remove(world, excluded);
            World.Process(world);
            for (var i = 0; i < reaches.Length; i++)
            {
                Assert.True(World.TrySenseArea(world, layer, 64.25f, 64.75f, reaches[i], out var removed));
                Assert.Equal(removed, sums[i]);
            }
        }
        finally { World.Free(world); }
    }

    [Fact]
    public void WarmDiskReads_AllocateNothing()
    {
        var world = World.New();
        try
        {
            Grid.New(world, 8, 0, 0, 256);
            var layer = Layer.New(world);
            var source = World.Place(world, layer, 128.25f, 128.75f, Stamp.Tent(17, 13, 127), 16);
            World.Process(world);
            long consumed = 0;
            void Read()
            {
                consumed += World.TrySenseArea(world, layer, 128, 128, 64, source, out var area) ? area : 1;
                consumed += World.TrySenseMax(world, layer, 128, 128, 64, out var max, out _, out _) ? max : 1;
                consumed += World.TrySenseNearest(world, layer, 128, 128, 64, 1, out var nearest, out _, out _) ? nearest : 1;
                consumed += World.Changed(world, layer, 128, 128, 64, 0) ? 1 : 0;
            }
            for (var i = 0; i < 100; i++) Read();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) Read();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, bytes);
            Assert.NotEqual(0, consumed);
        }
        finally { World.Free(world); }
    }
}
