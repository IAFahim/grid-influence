using GridInfluence;
using GridInfluence.Io;
using Xunit;

namespace GridInfluence.Tests;

public sealed class InfluenceIoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlinfluence-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Pnm_Gray16RoundTrip_PreservesSignedWeights()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "weights.pgm");
        var weights = new int[16 * 8];
        for (var i = 0; i < weights.Length; i++) weights[i] = (i % 7 - 3) * 1000;

        Pnm.SaveGraySigned(path, weights, 16, 8, 65535);
        using var map = Pnm.LoadWeights(path);

        Assert.Equal(16, map.Width);
        Assert.Equal(8, map.Height);
        Assert.Equal(Pnm.SignedBias(65535), map.Bias);
        for (var i = 0; i < weights.Length; i++)
            Assert.Equal(weights[i], map.Samples[i]);
    }

    [Fact]
    public void Pnm_Gray8Header_ParsesComments()
    {
        var path = Path.Combine(_directory, "small.pgm");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(path, "P5\n# a comment\n4 2\n255\n"u8.ToArray().Concat(new byte[8]).ToArray());

        using var map = Pnm.LoadWeights(path, signed: false);

        Assert.Equal(4, map.Width);
        Assert.Equal(2, map.Height);
        Assert.Equal(255, map.MaxVal);
        Assert.Equal(0, map.Samples[3]);
    }

    [Fact]
    public void Pnm_RejectsAsciiAndColorWeights()
    {
        Directory.CreateDirectory(_directory);
        var ascii = Path.Combine(_directory, "ascii.pgm");
        File.WriteAllText(ascii, "P2\n2 2\n255\n0 0 0 0\n");
        Assert.Throws<InvalidDataException>(() => Pnm.LoadWeights(ascii));

        var color = Path.Combine(_directory, "color.ppm");
        File.WriteAllBytes(color, "P6\n2 2\n255\n"u8.ToArray().Concat(new byte[12]).ToArray());
        Assert.Throws<InvalidDataException>(() => Pnm.LoadWeights(color));
    }

    [Fact]
    public void WeightMap_IntoField_InjectsThenExports()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "layer.pgm");
        var weights = new int[8 * 8];
        for (var i = 0; i < weights.Length; i++) weights[i] = i * 3 - 60;

        Pnm.SaveGraySigned(path, weights, 8, 8, 65535);
        using var map = Pnm.LoadWeights(path);

        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        field.WriteRegion(new Int2(-3, 5), new Int2(map.Width, map.Height), map.Samples);

        var exported = new int[8 * 8];
        field.ReadRegion(new Int2(-3, 5), new Int2(8, 8), exported);
        Assert.Equal(weights, exported);
    }

    [Fact]
    public void SceneRunner_RunsClipsAndCapturesFrames()
    {
        Directory.CreateDirectory(_directory);
        var scene = new SceneDocument
        {
            Frames = 3,
            Fields = [new FieldSpec { Key = "threat", ChunkPower = 3, DecayPerMille = 100, SpreadDenominator = 4 }],
            Clips =
            [
                new ClipSpec
                {
                    Field = "threat",
                    FromFrame = 0,
                    ToFrame = 2,
                    Weight = 100,
                    Shape = new ShapeSpec { Kind = "disc", Radius = 3 },
                    Motion = new MotionSpec { Kind = "line", From = [0, 0], To = [6, 0] }
                }
            ],
            Captures = [new CaptureSpec { Frame = 2, Field = "threat", Origin = [-16, -16], Size = [32, 32], Path = "frame.ppm", Color = true }]
        };

        using var runner = new SceneRunner(scene, _directory);
        for (var frame = 0; frame < runner.Frames; frame++)
        {
            runner.Step(frame);
            foreach (var capture in scene.Captures.Where(c => c.Frame == frame)) runner.Capture(capture);
        }

        var output = Path.Combine(_directory, "frame.ppm");
        Assert.True(File.Exists(output));
        var bytes = File.ReadAllBytes(output);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'6', bytes[1]);
        Assert.Equal(32 * 32 * 3 + "P6\n32 32\n255\n".Length, bytes.Length);
        Assert.Contains(bytes, b => b > 0);
    }

    [Fact]
    public void SceneRunner_LoadsImageLayersFromFile()
    {
        Directory.CreateDirectory(_directory);
        var imagePath = Path.Combine(_directory, "bases.pgm");
        Pnm.SaveGraySigned(imagePath, [100, -100, 100, -100], 2, 2, 65535);

        var scene = new SceneDocument
        {
            Frames = 1,
            Fields = [new FieldSpec { Key = "terrain", ChunkPower = 2 }],
            Images = [new ImageLayer { Field = "terrain", Path = "bases.pgm", Origin = [0, 0], EveryFrame = true }]
        };

        using var runner = new SceneRunner(scene, _directory);
        runner.Step(0);

        var front = runner.Registry.Pair(runner.IdOf("terrain")).Front;
        Assert.Equal(100, front.AsReader().ReadCell(new Int2(0, 0)));
        Assert.Equal(-100, front.AsReader().ReadCell(new Int2(1, 0)));
    }
}
