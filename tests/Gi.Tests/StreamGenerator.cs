namespace Gi.Tests;

internal enum StreamProfile
{
    Gate,
    Heavy,
}

internal enum PrecisionMode : byte
{
    Exact = 0,
    Cell = 1,
    Quarter = 2,
    Half = 3,
    Mixed = 4,
}

internal struct SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed) => _state = seed;

    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15ul;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
        return z ^ (z >> 31);
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        var span = (ulong)(maxExclusive - minInclusive);
        return (int)(minInclusive + (long)(Next() % span));
    }

    public float NextUnit() => (Next() >> 40) * (1f / (1 << 24));

    public bool Chance(int percent) => NextInt(0, 100) < percent;
}

internal static class StreamGenerator
{
    public static StreamSetup Generate(int seed, PrecisionMode mode, StreamProfile profile)
    {
        var rng = new SplitMix64((ulong)seed * 0x2545F4914F6CDD1Dul + 0x9E3779B97F4A7C15ul);
        var setup = new StreamSetup { Seed = seed };
        var heavy = profile == StreamProfile.Heavy;

        var gridCount = heavy ? rng.NextInt(1, 4) : rng.NextInt(1, 3);
        setup.Grids = new GridSpec[gridCount];
        for (var g = 0; g < gridCount; g++)
        {
            var power = heavy ? (rng.Chance(30) ? 7 : rng.Chance(50) ? 6 : 5) : (rng.Chance(20) ? 6 : 5);
            setup.Grids[g] = new GridSpec
            {
                Power = power,
                X = rng.NextInt(-2, 3) * 16f,
                Y = rng.NextInt(-2, 3) * 16f,
                Size = (1 << power) / PickSizeDivisor(ref rng),
            };
        }

        var baseCount = heavy ? rng.NextInt(2, 4) : rng.NextInt(1, 4);
        var derivedCount = heavy ? rng.NextInt(1, 3) : (rng.Chance(50) ? 1 : 0);
        setup.Layers = new LayerSpec[baseCount + derivedCount];
        for (var l = 0; l < baseCount; l++)
            setup.Layers[l] = new LayerSpec { Precision = PickPrecision(ref rng, mode) };

        for (var d = 0; d < derivedCount; d++)
        {
            var a = (byte)rng.NextInt(0, baseCount + d);
            var b = (byte)rng.NextInt(0, baseCount + d);
            LayerSpec spec;
            switch (rng.NextInt(0, 4))
            {
                case 0:
                    spec = new LayerSpec
                    {
                        Derived = true,
                        Op = LayerOp.Sum,
                        A = a,
                        B = b,
                        WeightA = rng.NextInt(-3, 4),
                        WeightB = rng.NextInt(-3, 4),
                        Shift = rng.Chance(25) ? rng.NextInt(1, 3) : 0,
                    };
                    break;
                case 1:
                    spec = new LayerSpec { Derived = true, Op = LayerOp.Min, A = a, B = b };
                    break;
                case 2:
                    spec = new LayerSpec { Derived = true, Op = LayerOp.Max, A = a, B = b };
                    break;
                default:
                    var low = (short)rng.NextInt(-400, 401);
                    spec = new LayerSpec
                    {
                        Derived = true,
                        Op = LayerOp.Mask,
                        A = a,
                        B = b,
                        Low = low,
                        High = (short)Math.Min(short.MaxValue, low + rng.NextInt(0, 700)),
                    };
                    break;
            }

            setup.Layers[baseCount + d] = spec;
        }

        var stampCount = heavy ? rng.NextInt(6, 10) : rng.NextInt(4, 8);
        setup.Stamps = new StampSpec[stampCount];
        for (var s = 0; s < stampCount; s++)
        {
            StampSpec stamp;
            var axisLimit = heavy ? 8 : 6;
            switch (rng.NextInt(0, 7))
            {
                case 0:
                    stamp = new StampSpec
                    {
                        Kind = StampKind.Box,
                        Width = rng.NextInt(1, axisLimit + 1),
                        Height = rng.NextInt(1, axisLimit + 1),
                        Value = PickValue(ref rng),
                    };
                    break;
                case 1:
                    stamp = new StampSpec
                    {
                        Kind = StampKind.Tent,
                        Width = rng.NextInt(2, axisLimit + 1),
                        Height = rng.NextInt(2, axisLimit + 1),
                        Value = PickValue(ref rng),
                    };
                    break;
                case 2:
                    stamp = new StampSpec
                    {
                        Kind = StampKind.Bell,
                        Width = rng.NextInt(2, axisLimit + 1),
                        Height = rng.NextInt(2, axisLimit + 1),
                        Value = PickValue(ref rng),
                    };
                    break;
                case 3:
                case 4:
                case 5:
                    stamp = new StampSpec
                    {
                        Kind = (StampKind)((int)StampKind.Disk + rng.NextInt(0, 3)),
                        Radius = rng.NextInt(1, (heavy ? 5 : 4) + 1),
                        Arc = rng.Chance(heavy ? 45 : 25) ? rng.NextInt(1, 360) : 360,
                        Value = PickValue(ref rng),
                    };
                    break;
                default:
                    stamp = new StampSpec
                    {
                        Kind = StampKind.Raster,
                        Width = rng.NextInt(2, heavy ? 6 : 5),
                        Height = rng.NextInt(2, heavy ? 6 : 5),
                        Value = 0,
                        DataSeed = rng.Next(),
                    };
                    break;
            }

            setup.Stamps[s] = stamp;
        }

        setup.MaxLive = heavy ? rng.NextInt(16, 29) : rng.NextInt(6, 15);

        var frameCount = heavy ? rng.NextInt(7, 13) : rng.NextInt(4, 7);
        var commands = setup.Commands;
        var attempts = 0;
        var live = 0;
        var recording = false;
        var frames = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            if (frame > 0 && !recording && rng.Chance(heavy ? 10 : 6))
            {
                commands.Add(new StreamCommand { Op = StreamOp.Record, Aux = rng.Next() });
                recording = true;
            }
            else if (recording && rng.Chance(35))
            {
                if (rng.Chance(60))
                {
                    commands.Add(new StreamCommand { Op = StreamOp.Rewind, Aux = rng.Next() });
                    recording = false;
                }
                else
                {
                    commands.Add(new StreamCommand { Op = StreamOp.StopRecording, Aux = rng.Next() });
                    recording = false;
                }
            }
            else if (frame > 1 && rng.Chance(4))
            {
                commands.Add(new StreamCommand { Op = StreamOp.Clear, Aux = rng.Next() });
                live = 0;
            }

            var ops = heavy ? rng.NextInt(8, 17) : rng.NextInt(5, 11);
            for (var op = 0; op < ops; op++)
            {
                if (rng.Chance(heavy ? 10 : 8))
                {
                    commands.Add(ReadCommand(ref rng, setup, attempts, frames));
                    continue;
                }

                MutationCommand(ref rng, setup, ref attempts, ref live, commands);
            }

            commands.Add(new StreamCommand { Op = StreamOp.Process, Aux = rng.Next() });
            frames++;

            var reads = heavy ? rng.NextInt(2, 5) : rng.NextInt(2, 4);
            for (var r = 0; r < reads; r++)
                commands.Add(ReadCommand(ref rng, setup, attempts, frames));
        }

        return setup;
    }

    private static int PickSizeDivisor(ref SplitMix64 rng)
    {
        switch (rng.NextInt(0, 5))
        {
            case 0: return 1;
            case 1: return 2;
            case 2: return 4;
            case 3: return 3;
            default: return rng.NextInt(1, 6);
        }
    }

    private static Layer.Precision PickPrecision(ref SplitMix64 rng, PrecisionMode mode)
    {
        switch (mode)
        {
            case PrecisionMode.Exact:
                return Layer.Precision.Exact;
            case PrecisionMode.Cell:
                return Layer.Precision.Cell;
            case PrecisionMode.Quarter:
                return Layer.Precision.Quarter;
            case PrecisionMode.Half:
                return Layer.Precision.Half;
            default:
                Layer.Precision.TryCreate(rng.NextInt(0, 9), rng.NextInt(0, 17), out var mixed);
                return mixed;
        }
    }

    private static int PickValue(ref SplitMix64 rng)
    {
        var magnitude = rng.NextInt(8, 128);
        return rng.Chance(50) ? magnitude : -magnitude;
    }

    private static float CoordIn(ref SplitMix64 rng, float origin, float size)
    {
        switch (rng.NextInt(0, 20))
        {
            case 0:
                return float.NaN;
            case 1:
                return rng.Chance(50) ? float.PositiveInfinity : float.NegativeInfinity;
            case 2:
            case 3:
                return origin + size * (1f + rng.NextUnit() * 2f);
            case 4:
                return origin - size * rng.NextUnit() * 2f;
            default:
                var steps = Math.Max(1, (int)(size * 4f));
                return origin + rng.NextInt(0, steps + 1) * 0.25f;
        }
    }

    private static void MutationCommand(
        ref SplitMix64 rng,
        StreamSetup setup,
        ref int attempts,
        ref int live,
        List<StreamCommand> commands)
    {
        var command = new StreamCommand { Aux = rng.Next() };
        var pick = live >= setup.MaxLive ? rng.NextInt(74, 88) : rng.NextInt(0, 100);

        switch (pick)
        {
            case int p when p < 28:
                command.Op = StreamOp.Place;
                command.Layer = rng.Chance(3) ? (byte)rng.NextInt(0, setup.Layers.Length) : PickBaseLayer(ref rng, setup);
                command.Stamp = (byte)rng.NextInt(0, setup.Stamps.Length);
                var placeTarget = setup.Grids[rng.NextInt(0, setup.Grids.Length)];
                command.X = CoordIn(ref rng, placeTarget.X, placeTarget.Size);
                command.Y = CoordIn(ref rng, placeTarget.Y, placeTarget.Size);
                command.Gain = (sbyte)rng.NextInt(-40, 41);
                command.Handle = attempts;
                attempts++;
                live++;
                break;
            case int p when p < 46:
                command.Op = StreamOp.Move;
                command.Handle = Handle(ref rng, attempts);
                var moveTarget = setup.Grids[rng.NextInt(0, setup.Grids.Length)];
                command.X = CoordIn(ref rng, moveTarget.X, moveTarget.Size);
                command.Y = CoordIn(ref rng, moveTarget.Y, moveTarget.Size);
                break;
            case int p when p < 54:
                command.Op = StreamOp.Turn;
                command.Handle = Handle(ref rng, attempts);
                command.X = rng.Chance(20)
                    ? rng.NextInt(0, 8) * (float)(Math.PI / 2)
                    : (float)(rng.NextUnit() * Math.PI * 4 - Math.PI * 2);
                break;
            case int p when p < 60:
                command.Op = StreamOp.Scale;
                command.Handle = Handle(ref rng, attempts);
                command.X = PickScale(ref rng);
                break;
            case int p when p < 66:
                command.Op = StreamOp.SetGain;
                command.Handle = Handle(ref rng, attempts);
                command.Gain = (sbyte)rng.NextInt(-40, 41);
                break;
            case int p when p < 74:
                command.Op = StreamOp.Remove;
                command.Handle = Handle(ref rng, attempts);
                live = Math.Max(0, live - 1);
                break;
            case int p when p < 81:
                command.Op = StreamOp.Fade;
                command.Handle = Handle(ref rng, attempts);
                command.Gain = (sbyte)rng.NextInt(-40, 41);
                command.Int0 = rng.NextInt(0, 6);
                break;
            case int p when p < 87:
                command.Op = StreamOp.Expire;
                command.Handle = Handle(ref rng, attempts);
                command.Int0 = rng.NextInt(0, 5);
                break;
            default:
                command.Op = StreamOp.Sync;
                command.Handle = rng.NextInt(0, Math.Max(1, attempts + 2));
                command.Int0 = rng.NextInt(1, 6);
                command.Grid = (byte)rng.NextInt(0, setup.Grids.Length);
                break;
        }

        commands.Add(command);
    }

    private static int Handle(ref SplitMix64 rng, int attempts)
    {
        if (attempts == 0) return 0;
        if (rng.Chance(3)) return attempts + rng.NextInt(0, 64);
        return rng.NextInt(0, attempts);
    }

    private static float PickScale(ref SplitMix64 rng)
    {
        switch (rng.NextInt(0, 8))
        {
            case 0: return 0.1f;
            case 1: return 0.25f;
            case 2: return 0.5f;
            case 3: return 1f;
            case 4: return 2f;
            case 5: return 300f;
            default: return rng.NextUnit() * 4f;
        }
    }

    private static byte PickBaseLayer(ref SplitMix64 rng, StreamSetup setup)
    {
        var bases = 0;
        foreach (var l in setup.Layers)
            if (!l.Derived)
                bases++;
        return (byte)rng.NextInt(0, Math.Max(1, bases));
    }

    private static StreamCommand ReadCommand(ref SplitMix64 rng, StreamSetup setup, int attempts, int frames)
    {
        var command = new StreamCommand
        {
            Op = StreamOp.Read,
            Read = (ReadKind)rng.NextInt(0, (int)ReadKind.SourceSpec + 1),
            Layer = (byte)rng.NextInt(0, setup.Layers.Length),
            Grid = (byte)rng.NextInt(0, setup.Grids.Length),
            Handle = attempts == 0 ? 0 : rng.NextInt(0, attempts + 4),
            Aux = rng.Next(),
        };
        var target = setup.Grids[command.Grid];
        command.X = CoordIn(ref rng, target.X, target.Size);
        command.Y = CoordIn(ref rng, target.Y, target.Size);
        command.Int0 = 0;

        var cells = 1 << setup.Grids[command.Grid].Power;
        switch (command.Read)
        {
            case ReadKind.QueryRect:
            case ReadKind.QueryRegion:
            case ReadKind.QueryMaxRect:
                command.Int0 = rng.NextInt(1, cells + 3);
                break;
            case ReadKind.SenseArea:
            case ReadKind.SenseAreaEx:
            case ReadKind.SenseMax:
            case ReadKind.SenseNearest:
            case ReadKind.ChangedDisk:
            case ReadKind.ChangedMask:
                command.Int0 = rng.Chance(4) ? -1 : rng.NextInt(0, 17);
                break;
            case ReadKind.ChangedTilesSince:
                command.Int0 = rng.NextInt(-2, frames + 2);
                break;
        }

        if (command.Read == ReadKind.SenseNearest)
            command.Gain = (sbyte)rng.NextInt(-40, 61);

        return command;
    }
}
