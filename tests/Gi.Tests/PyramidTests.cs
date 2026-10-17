using Xunit;

namespace Gi.Tests;

public unsafe class PyramidTests
{
    [Theory]
    [InlineData(5, 1)]
    [InlineData(33, 2)]
    [InlineData(64, 3)]
    [InlineData(130, 4)]
    public void MaxRootEqualsTheMaximumOfEveryValidLeafUnderRandomUpdates(int side, int seed)
    {
        var rng = new Random(seed);
        var pyramid = default(Pyramid<short, ShortMaxOp>);
        var leaves = new short[side * side];
        try
        {
            for (var step = 0; step < 4000; step++)
            {
                var tile = rng.Next(leaves.Length);
                var value = (short)rng.Next(-300, 300);
                leaves[tile] = value;
                pyramid.Update(side, tile, value);
                if (step % 97 != 0) continue;

                var root = pyramid.Levels - 1;
                var childSide = pyramid.ChildSide(root, side);
                var reduced = Pyramid<short, ShortMaxOp>.ReduceMax(pyramid.Node(root, 0, 0), Math.Min(8, childSide), Math.Min(8, childSide));
                Assert.Equal(leaves.Max(), reduced);
            }
        }
        finally
        {
            pyramid.Reset();
        }
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(33, 2)]
    [InlineData(130, 3)]
    public void EpochTreeCollectsExactlyTheLeavesNewerThanTheThreshold(int side, int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        try
        {
            var tree = default(Pyramid<int, EpochMaxOp>);
            var leaves = new int[side * side];
            tree.Ensure(side);
            var tick = 10;
            for (var step = 0; step < 400; step++)
            {
                tick++;
                var batch = rng.Next(1, 40);
                for (var i = 0; i < batch; i++)
                {
                    var tile = rng.Next(leaves.Length);
                    leaves[tile] = tick;
                    tree.Update(side, tile, tick);
                }

                var since = tick - rng.Next(0, 6);
                var expected = new List<int>();
                for (var i = 0; i < leaves.Length; i++)
                    if (leaves[i] - since > 0) expected.Add(i);

                var found = new List<int>();
                var count = 0;
                var buffer = new int[leaves.Length];
                fixed (int* pBuffer = buffer)
                    CollectLeaves(tree, side, tree.Levels - 1, 0, 0, since, pBuffer, ref count);
                for (var i = 0; i < count; i++) found.Add(buffer[i]);
                found.Sort();
                Assert.Equal(expected, found);
            }

            tree.Reset();
        }
        finally
        {
            World.Free(w);
        }
    }

    private static void CollectLeaves(
        in Pyramid<int, EpochMaxOp> tree, int side, int level, int nodeX, int nodeY, int since, int* destination, ref int count)
    {
        var node = tree.Node(level, nodeX, nodeY);
        var childSide = tree.ChildSide(level, side);
        var vx = Math.Min(8, childSide - (nodeX << 3));
        var vy = Math.Min(8, childSide - (nodeY << 3));
        for (var cy = 0; cy < vy; cy++)
        for (var cx = 0; cx < vx; cx++)
        {
            if (node[cy * 8 + cx] - since <= 0) continue;
            var childX = (nodeX << 3) + cx;
            var childY = (nodeY << 3) + cy;
            if (level != 0)
            {
                CollectLeaves(tree, side, level - 1, childX, childY, since, destination, ref count);
                continue;
            }

            destination[count++] = childY * side + childX;
        }
    }
}
