using Gi;
using Xunit;

namespace Gi.Tests;

public unsafe class NativeHeapTests
{
    [Fact]
    public void WarmFramesMakeNoNativeHeapCalls()
    {
        var w = World.New();
        Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(8, 8, 40);
        var tent = Stamp.Tent(10, 10, 40);
        var count = 64;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (i * 17.3f) % 240f + 12f;
            ys[i] = (i * 9.1f) % 240f + 12f;
            ids[i] = World.Place(w, l, xs[i], ys[i], i % 2 == 0 ? box : tent, 3);
        }

        World.Process(w);

        var values = new short[count];
        var gradientX = new float[count];
        var gradientY = new float[count];
        var whole = new ulong[(count + 63) >> 6];
        fixed (World.SourceId* idPointer = ids)
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (short* valuePointer = values)
        fixed (float* gx = gradientX)
        fixed (float* gy = gradientY)
        fixed (ulong* wholeBits = whole)
        {
            for (var warm = 0; warm < 8; warm++)
            {
                var step = (warm & 1) == 0 ? 8f : -8f;
                for (var i = 0; i < count; i++)
                {
                    xPointer[i] += step;
                    yPointer[i] += step * 0.5f;
                }

                World.Sync(w, idPointer, xPointer, yPointer, count);
                World.Process(w);
            }

            var before = NativeHeap.Calls;
            for (var frame = 0; frame < 8; frame++)
            {
                var step = (frame & 1) == 0 ? 8f : -8f;
                for (var i = 0; i < count; i++)
                {
                    xPointer[i] += step;
                    yPointer[i] += step * 0.5f;
                }

                World.Sync(w, idPointer, xPointer, yPointer, count);
                World.Process(w);
                var since = World.Tick(w);
                World.SenseColumn(w, l, xPointer, yPointer, count, valuePointer, wholeBits);
                World.SenseGradientColumn(w, l, xPointer, yPointer, null, count, gx, gy, wholeBits);
                World.ChangedMask(w, l, xPointer, yPointer, 16f, since, count, wholeBits);
                Assert.True(World.Query(w, 0, l, 128, 128) >= 0);
                World.TrySenseArea(w, l, 128f, 128f, 24f, out var total);
                Assert.True(total >= 0);
            }

            Assert.Equal(before, NativeHeap.Calls);
        }
    }
}
