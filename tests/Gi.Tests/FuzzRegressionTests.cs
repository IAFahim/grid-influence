using Xunit;

namespace Gi.Tests;

public class FuzzRegressionTests
{
    [Fact]
    public void Seed7000217_ExcludeByAPendingRemovedIdAfterSlotReuse()
    {
        var setup = new StreamSetup { Seed = 7000217,
            Grids = new[]
            {
                new GridSpec { Power = 6, X = BitConverter.Int32BitsToSingle(0x42000000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC1800000)), Size = BitConverter.Int32BitsToSingle(0x41800000) },
                new GridSpec { Power = 5, X = BitConverter.Int32BitsToSingle(unchecked((int)0xC1800000)), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC1800000)), Size = BitConverter.Int32BitsToSingle(0x41000000) },
                new GridSpec { Power = 7, X = BitConverter.Int32BitsToSingle(0x00000000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC2000000)), Size = BitConverter.Int32BitsToSingle(0x42800000) },
            },
            Layers = new[]
            {
                new LayerSpec { Precision = Layer.Precision.Half },
                new LayerSpec { Precision = Layer.Precision.Half },
                new LayerSpec { Derived = true, Op = LayerOp.Min, A = 0, B = 1 },
                new LayerSpec { Derived = true, Op = LayerOp.Sum, A = 1, B = 2, WeightA = -2, WeightB = 0, Shift = 0 },
            },
            Stamps = new[]
            {
                new StampSpec { Kind = StampKind.Cone, Radius = 1, Arc = 9, Value = -89 },
                new StampSpec { Kind = StampKind.Disk, Radius = 2, Arc = 267, Value = 25 },
                new StampSpec { Kind = StampKind.Dome, Radius = 3, Arc = 105, Value = 114 },
                new StampSpec { Kind = StampKind.Cone, Radius = 4, Arc = 360, Value = -97 },
                new StampSpec { Kind = StampKind.Dome, Radius = 4, Arc = 360, Value = -84 },
                new StampSpec { Kind = StampKind.Box, Width = 6, Height = 6, Value = 31 },
                new StampSpec { Kind = StampKind.Bell, Width = 5, Height = 2, Value = 36 },
            },
            Commands = new List<StreamCommand>
            {
                new StreamCommand { Op = StreamOp.Place, Layer = 1, Stamp = 4, Gain = -10, X = BitConverter.Int32BitsToSingle(unchecked((int)0xC1380000)), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC1640000)), Aux = 8853113263088646409ul },
                new StreamCommand { Op = StreamOp.Place, Stamp = 2, Handle = 1, Gain = -3, X = BitConverter.Int32BitsToSingle(0x42852A88), Y = BitConverter.Int32BitsToSingle(0x3F8F3490), Aux = 8193734833592670896ul },
                new StreamCommand { Op = StreamOp.Place, Layer = 1, Stamp = 5, Handle = 2, Gain = 39, X = BitConverter.Int32BitsToSingle(0x42240000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xBFA00000)), Aux = 17273624304830286304ul },
                new StreamCommand { Op = StreamOp.Place, Handle = 3, Gain = 8, X = BitConverter.Int32BitsToSingle(0x40700000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC1540000)), Aux = 13995460532405415101ul },
                new StreamCommand { Op = StreamOp.Place, Stamp = 1, Handle = 5, Gain = 19, X = BitConverter.Int32BitsToSingle(0x41C20000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xFF800000)), Aux = 4732519452457977285ul },
                new StreamCommand { Op = StreamOp.Place, Layer = 2, Stamp = 4, Handle = 6, Gain = -3, X = BitConverter.Int32BitsToSingle(0x422F0000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC0000000)), Aux = 10211741922571522417ul },
                new StreamCommand { Op = StreamOp.Place, Layer = 1, Stamp = 6, Handle = 7, Gain = 15, X = BitConverter.Int32BitsToSingle(0x42090000), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC0700000)), Aux = 6299442607894211580ul },
                new StreamCommand { Op = StreamOp.Place, Layer = 1, Stamp = 5, Handle = 8, Gain = 14, X = BitConverter.Int32BitsToSingle(unchecked((int)0xC1080000)), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC14C0000)), Aux = 6607345407307681418ul },
                new StreamCommand { Op = StreamOp.Sync, Grid = 1, Handle = 7, Int0 = 4, Aux = 1402803159446615221ul },
                new StreamCommand { Op = StreamOp.Process, Aux = 5632256786185354228ul },
                new StreamCommand { Op = StreamOp.Remove, Handle = 7, Aux = 7333066647481262012ul },
                new StreamCommand { Op = StreamOp.Place, Layer = 1, Stamp = 4, Handle = 17, Gain = 10, X = BitConverter.Int32BitsToSingle(unchecked((int)0xC1300000)), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC11C0000)), Aux = 6130835478369664534ul },
                new StreamCommand { Op = StreamOp.Read, Read = ReadKind.SenseColumnEx, Layer = 3, Grid = 1, Handle = 7, X = BitConverter.Int32BitsToSingle(unchecked((int)0xC1380000)), Y = BitConverter.Int32BitsToSingle(unchecked((int)0xC1500000)), Aux = 11732712974641317974ul },
            },
        };
        Assert.Null(StreamRunner.TryRun(setup, setup.Commands));
    }

}
