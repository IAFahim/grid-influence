using System.Runtime.InteropServices;
using Xunit;

namespace Gi.Tests;

public sealed unsafe class AuditTransformOverlapTests
{
    [Fact]
    public void SyncTransformsMustRejectAMaskThatOverlapsTheSecondHalfOfTheIdColumn()
    {
        var world = World.New();
        var box = Stamp.Box(4, 4, 20);
        var block = (byte*)NativeMemory.AllocZeroed((nuint)(16 * sizeof(World.SourceId) + 64));
        try
        {
            Grid.New(world, 8, 0f, 0f, 256f);
            var layer = Layer.New(world, Layer.Precision.Cell);
            const int rows = 16;
            var sources = (World.SourceId*)block;
            var xs = stackalloc float[rows];
            var ys = stackalloc float[rows];
            for (var i = 0; i < rows; i++)
            {
                sources[i] = World.Place(world, layer, 10f + i, 10f, box, 1);
                xs[i] = 20f + i;
                ys[i] = 20f;
            }

            World.Process(world);
            var token = World.Capture(world);
            var mask = (ulong*)(block + 104);
            var ranges = new[]
            {
                new World.TransformRange
                {
                    Sources = sources, Xs = xs, Ys = ys, Invalid = mask, InvalidWords = 1, Count = rows
                }
            };
            Assert.Equal(World.BatchStatus.OverlappingBuffers, World.SyncTransforms(token, ranges));
        }
        finally
        {
            NativeMemory.Free(block);
            World.Free(world);
            Stamp.Free(box);
        }
    }
}
