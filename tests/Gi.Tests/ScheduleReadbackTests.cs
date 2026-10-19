using Xunit;

namespace Gi.Tests;

public unsafe class ScheduleReadbackTests
{
    [Theory]
    [InlineData(0, 10, 10, 0)]
    [InlineData(10, 0, 10, 0)]
    [InlineData(-5, 7, 7, 0)]
    [InlineData(3, -9, 4, 0)]
    [InlineData(0, 3, 10, 0)]
    [InlineData(0, 10, 6, 9)]
    [InlineData(0, 10, 4, 9)]
    [InlineData(5, 5, 6, 0)]
    [InlineData(-8, -2, 5, 3)]
    public void ReadBackMatchesTheReferenceAtEveryTick(int from, int to, int fadeTicks, int expireTicks)
    {
        var w = World.New();
        var rw = new ReferenceWorld();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30) };
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            rw.AddGrid(6, 0f, 0f, 64f);
            var l = Layer.New(w, Layer.Precision.Cell);
            rw.AddLayer(Layer.Precision.Cell);

            var id = World.Place(w, l, 20f, 24f, stamps[0], (sbyte)from);
            Assert.Equal(rw.Place(l, 20f, 24f, stamps[0], (sbyte)from), id);
            World.Fade(w, id, (sbyte)to, fadeTicks);
            rw.Fade(id, (sbyte)to, fadeTicks);
            if (expireTicks > 0)
            {
                World.Expire(w, id, expireTicks);
                rw.Expire(id, expireTicks);
            }

            var span = Math.Max(fadeTicks, expireTicks) + 3;
            for (var k = 0; k <= span; k++)
            {
                CompareReadBackWithReference(w, rw, id);
                if (k < span)
                {
                    World.Process(w);
                    rw.Process();
                }
            }
        }
        finally
        {
            World.Free(w);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void AMidFadeReadReportsTheRampAndTicksLeft()
    {
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30) };
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            var l = Layer.New(w, Layer.Precision.Cell);
            var id = World.Place(w, l, 20f, 24f, stamps[0], 0);
            World.Fade(w, id, 10, 10);

            Assert.True(World.TryGetSchedule(w, id, out var gain, out var fadeFrom, out var fadeTo,
                out var fadeTicks, out var fadeTicksLeft, out var expireTicksLeft));
            Assert.Equal((sbyte)0, gain);
            Assert.Equal((sbyte)0, fadeFrom);
            Assert.Equal((sbyte)10, fadeTo);
            Assert.Equal(10, fadeTicks);
            Assert.Equal(10, fadeTicksLeft);
            Assert.Equal(0, expireTicksLeft);

            for (var k = 0; k < 3; k++) World.Process(w);
            Assert.True(World.TryGetSchedule(w, id, out gain, out fadeFrom, out fadeTo,
                out fadeTicks, out fadeTicksLeft, out expireTicksLeft));
            Assert.Equal((sbyte)3, gain);
            Assert.Equal((sbyte)0, fadeFrom);
            Assert.Equal((sbyte)10, fadeTo);
            Assert.Equal(10, fadeTicks);
            Assert.Equal(7, fadeTicksLeft);
            Assert.Equal(0, expireTicksLeft);
        }
        finally
        {
            World.Free(w);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void AFinishedFadeStopsReportingBeforeItsNominalEnd()
    {
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30) };
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            var l = Layer.New(w, Layer.Precision.Cell);
            var id = World.Place(w, l, 20f, 24f, stamps[0], 0);
            World.Fade(w, id, 3, 10);

            for (var k = 0; k < 8; k++) World.Process(w);
            Assert.True(World.TryGetSchedule(w, id, out var gain, out _, out var fadeTo,
                out var fadeTicks, out var fadeTicksLeft, out _));
            Assert.Equal((sbyte)2, gain);
            Assert.Equal((sbyte)3, fadeTo);
            Assert.Equal(10, fadeTicks);
            Assert.Equal(2, fadeTicksLeft);

            World.Process(w);
            Assert.True(World.TryGetSourceSpec(w, id, out var spec));
            Assert.Equal((sbyte)3, spec.Gain);
            Assert.False(World.TryGetSchedule(w, id, out gain, out _, out _, out _, out _, out _));
            Assert.Equal((sbyte)0, gain);

            World.Process(w);
            Assert.False(World.TryGetSchedule(w, id, out _, out _, out _, out _, out _, out _));
        }
        finally
        {
            World.Free(w);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void APlainExpiryReportsRemainingTicksUntilRemoval()
    {
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30) };
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            var l = Layer.New(w, Layer.Precision.Cell);
            var id = World.Place(w, l, 20f, 24f, stamps[0], 5);
            World.Expire(w, id, 4);

            Assert.True(World.TryGetSchedule(w, id, out var gain, out var fadeFrom, out var fadeTo,
                out var fadeTicks, out var fadeTicksLeft, out var expireTicksLeft));
            Assert.Equal((sbyte)5, gain);
            Assert.Equal((sbyte)0, fadeFrom);
            Assert.Equal((sbyte)0, fadeTo);
            Assert.Equal(0, fadeTicks);
            Assert.Equal(0, fadeTicksLeft);
            Assert.Equal(4, expireTicksLeft);

            for (var k = 3; k >= 1; k--)
            {
                World.Process(w);
                Assert.True(World.TryGetSchedule(w, id, out gain, out _, out _, out _, out _, out expireTicksLeft));
                Assert.Equal((sbyte)5, gain);
                Assert.Equal(k, expireTicksLeft);
            }

            World.Process(w);
            Assert.False(World.TryGetSchedule(w, id, out _, out _, out _, out _, out _, out _));
            Assert.False(World.TryGetSourceSpec(w, id, out _));
        }
        finally
        {
            World.Free(w);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void UnscheduledDeadAndRecycledSourcesReadFalse()
    {
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30) };
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            var l = Layer.New(w, Layer.Precision.Cell);
            var plain = World.Place(w, l, 10f, 12f, stamps[0], 4);
            Assert.False(World.TryGetSchedule(w, plain, out _, out _, out _, out _, out _, out _));

            var a = World.Place(w, l, 20f, 24f, stamps[0], 3);
            World.Fade(w, a, 9, 5);
            Assert.True(World.TryGetSchedule(w, a, out _, out _, out _, out _, out _, out _));
            World.Remove(w, a);
            Assert.False(World.TryGetSchedule(w, a, out _, out _, out _, out _, out _, out _));

            var b = World.Place(w, l, 30f, 36f, stamps[0], 2);
            Assert.Equal(a.Slot, b.Slot);
            Assert.False(World.TryGetSchedule(w, a, out _, out _, out _, out _, out _, out _));
            World.Fade(w, b, 6, 3);
            Assert.True(World.TryGetSchedule(w, b, out _, out _, out _, out _, out _, out _));
            Assert.False(World.TryGetSchedule(w, a, out _, out _, out _, out _, out _, out _));

            Assert.False(World.TryGetSchedule(w, default, out _, out _, out _, out _, out _, out _));
        }
        finally
        {
            World.Free(w);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void AFreedWorldReadsFalse()
    {
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(2, 2, 10) };
        Grid.New(w, 5, 0f, 0f, 32f);
        var l = Layer.New(w);
        var id = World.Place(w, l, 5f, 5f, stamps[0], 3);
        World.Fade(w, id, 8, 4);
        World.Free(w);
        Assert.False(World.TryGetSchedule(w, id, out _, out _, out _, out _, out _, out _));
        Stamp.Free(stamps[0]);
    }

    [Fact]
    public void ReadingAScheduleAllocatesNothing()
    {
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(3, 3, 30) };
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            var l = Layer.New(w, Layer.Precision.Cell);
            var fading = World.Place(w, l, 10f, 12f, stamps[0], 1);
            World.Fade(w, fading, 9, 20);
            var plain = World.Place(w, l, 20f, 24f, stamps[0], 4);
            World.Process(w);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                Assert.True(World.TryGetSchedule(w, fading,
                    out _, out _, out _, out _, out _, out _));
                Assert.False(World.TryGetSchedule(w, plain,
                    out _, out _, out _, out _, out _, out _));
                Assert.False(World.TryGetSchedule(w, default,
                    out _, out _, out _, out _, out _, out _));
            }

            Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        }
        finally
        {
            World.Free(w);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void ARestoredRunMatchesAnUninterruptedRunTickForTick()
    {
        const int interrupt = 15;
        const int total = 90;
        var stamps = new List<byte> { Stamp.Box(3, 3, 30), Stamp.Tent(5, 5, -20), Stamp.Cone(4, 25) };
        var page = new short[64 * 64];
        try
        {
            var expected = new ulong[total];
            {
                var w = World.New();
                SeedDeterministicWorld(w, stamps);
                for (var k = 1; k <= total; k++)
                {
                    World.Process(w);
                    expected[k - 1] = DeterministicFrameHash(w, page);
                }

                World.Free(w);
            }

            var saved = new List<SavedSource>();
            {
                var w = World.New();
                var ids = SeedDeterministicWorld(w, stamps);
                for (var k = 1; k <= interrupt; k++)
                {
                    World.Process(w);
                    Assert.Equal(expected[k - 1], DeterministicFrameHash(w, page));
                }

                for (var i = 0; i < ids.Length; i++) Save(w, ids[i], i, saved);
                World.Free(w);
            }

            {
                var w = World.New();
                Grid.New(w, 6, 0f, 0f, 64f);
                Layer.New(w, Layer.Precision.Cell);
                Layer.New(w, Layer.Precision.Exact);
                var drives = new List<Drive>();
                foreach (var source in saved)
                {
                    var spec = source.Spec;
                    var id = World.Place(w, spec.Layer, spec.X, spec.Y, spec.Stamp, spec.Gain);
                    Assert.NotEqual(default, id);
                    World.Turn(w, id, RestoreHeading(spec.HeadingRadians));
                    World.Scale(w, id, spec.Scale);
                    AssertRoundTrip(w, id, spec);
                    if (source.ExpireLeft != 0) World.Expire(w, id, source.ExpireLeft);
                    if (source.Fade)
                        drives.Add(new Drive
                        {
                            Id = id,
                            From = source.From,
                            To = source.To,
                            Ticks = source.Ticks,
                            Elapsed0 = source.Ticks - source.Left
                        });
                }

                Assert.Equal(3, drives.Count);
                for (var k = interrupt + 1; k <= total; k++)
                {
                    ApplyDrives(w, drives, k - interrupt, null);
                    World.Process(w);
                    Assert.Equal(expected[k - 1], DeterministicFrameHash(w, page));
                }

                World.Free(w);
            }
        }
        finally
        {
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    [Theory]
    [InlineData(32, 6)]
    [InlineData(39, 7)]
    [InlineData(38, 16)]
    [InlineData(33, 23)]
    [InlineData(34, 32)]
    [InlineData(35, 54)]
    [InlineData(36, 59)]
    public void AnInterruptedStreamRestoredFromReadBackHashesLikeAnUninterruptedRun(int seed, int interruptFrame)
    {
        var setup = StreamGenerator.Generate(seed, PrecisionMode.Mixed, StreamProfile.Corpus);
        var commands = new List<StreamCommand>(setup.Commands.Count);
        foreach (var command in setup.Commands)
            if (command.Op != StreamOp.Rewind)
                commands.Add(command);

        var maxCells = 1;
        foreach (var grid in setup.Grids) maxCells = Math.Max(maxCells, 1 << grid.Power);
        var page = new short[maxCells * maxCells];
        var stamps = CreateStamps(setup);
        try
        {
            var expected = new List<ulong>();
            {
                using var h = new StreamHarness(setup, stamps, page, null, 0);
                foreach (var command in commands)
                {
                    if (command.Op == StreamOp.Process)
                    {
                        World.Process(h.WorldId);
                        expected.Add(h.FrameHash());
                        continue;
                    }

                    h.Apply(command);
                }
            }

            var saved = new List<SavedSource>();
            int savedCount;
            int resume;
            {
                using var h = new StreamHarness(setup, stamps, page, null, 0);
                var frame = 0;
                resume = -1;
                for (var i = 0; i < commands.Count; i++)
                {
                    var command = commands[i];
                    if (command.Op == StreamOp.Process)
                    {
                        World.Process(h.WorldId);
                        frame++;
                        Assert.Equal(expected[frame - 1], h.FrameHash());
                        if (frame == interruptFrame)
                        {
                            resume = i + 1;
                            break;
                        }

                        continue;
                    }

                    h.Apply(command);
                }

                Assert.True(resume >= 0, $"stream {seed} has fewer than {interruptFrame} frames");
                savedCount = h.Attempts.Count;
                for (var handle = 0; handle < h.Attempts.Count; handle++)
                    Save(h.WorldId, h.Attempts[handle], handle, saved);
            }

            var scheduled = 0;
            var mapping = new World.SourceId[savedCount];
            var drives = new List<Drive>();
            {
                using var h = new StreamHarness(setup, stamps, page, handle => mapping[handle], savedCount);
                foreach (var source in saved)
                {
                    var spec = source.Spec;
                    var id = World.Place(h.WorldId, spec.Layer, spec.X, spec.Y, spec.Stamp, spec.Gain);
                    Assert.NotEqual(default, id);
                    World.Turn(h.WorldId, id, RestoreHeading(spec.HeadingRadians));
                    World.Scale(h.WorldId, id, spec.Scale);
                    AssertRoundTrip(h.WorldId, id, spec);
                    mapping[source.Handle] = id;
                    if (source.ExpireLeft != 0)
                    {
                        World.Expire(h.WorldId, id, source.ExpireLeft);
                        scheduled++;
                    }

                    if (source.Fade)
                    {
                        scheduled++;
                        drives.Add(new Drive
                        {
                            Id = id,
                            Handle = source.Handle,
                            From = source.From,
                            To = source.To,
                            Ticks = source.Ticks,
                            Elapsed0 = source.Ticks - source.Left,
                            Active = true
                        });
                    }
                }

                var frame = interruptFrame;
                for (var i = resume; i < commands.Count; i++)
                {
                    var command = commands[i];
                    if (command.Op == StreamOp.Process)
                    {
                        ApplyDrives(h.WorldId, drives, frame + 1 - interruptFrame, mapping);
                        World.Process(h.WorldId);
                        frame++;
                        Assert.Equal(expected[frame - 1], h.FrameHash());
                        continue;
                    }

                    if (command.Op == StreamOp.Clear)
                        DeactivateAll(drives);
                    else if (command.Op is StreamOp.Fade or StreamOp.SetGain or StreamOp.Remove)
                        Deactivate(drives, command.Handle);
                    h.Apply(command);
                }
            }

            Assert.True(scheduled > 0, $"stream {seed} had no schedule in flight at frame {interruptFrame}");
        }
        finally
        {
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    private static void CompareReadBackWithReference(byte w, ReferenceWorld rw, World.SourceId id)
    {
        var found = World.TryGetSchedule(w, id, out var gain, out var fadeFrom, out var fadeTo,
            out var fadeTicks, out var fadeTicksLeft, out var expireTicksLeft);
        if (!rw.TryGetSource(id, out var source))
        {
            Assert.False(found);
            return;
        }

        var now = rw.Tick;
        var stepping = (source.Timed & 1) != 0 && Stepping(in source, now);
        var expiring = (source.Timed & 2) != 0;
        if (!stepping && !expiring)
        {
            Assert.False(found);
            return;
        }

        Assert.True(found, $"source {id} at tick {now}");
        Assert.Equal(source.Gain, gain);
        Assert.Equal(stepping ? source.FadeFrom : (sbyte)0, fadeFrom);
        Assert.Equal(stepping ? source.FadeTo : (sbyte)0, fadeTo);
        Assert.Equal(stepping ? source.FadeTicks : 0, fadeTicks);
        Assert.Equal(stepping ? source.FadeStart + source.FadeTicks - now : 0, fadeTicksLeft);
        Assert.Equal(expiring ? source.ExpireAt - now : 0, expireTicksLeft);

        if (!stepping) return;

        var elapsed = Math.Clamp(now - source.FadeStart, 0, source.FadeTicks);
        var delta = source.FadeTo - source.FadeFrom;
        var step = (int)(((long)Math.Abs(delta) * elapsed + source.FadeTicks / 2) / source.FadeTicks);
        Assert.Equal((sbyte)(source.FadeFrom + (delta < 0 ? -step : step)), gain);
    }

    private static bool Stepping(in ReferenceWorld.Source source, int now)
    {
        var elapsed = now - source.FadeStart;
        if (elapsed >= source.FadeTicks) return false;
        var delta = Math.Abs(source.FadeTo - source.FadeFrom);
        var step = (int)(((long)delta * Math.Max(elapsed, 0) + source.FadeTicks / 2) / source.FadeTicks);
        return step < delta;
    }

    private static World.SourceId[] SeedDeterministicWorld(byte w, List<byte> stamps)
    {
        Grid.New(w, 6, 0f, 0f, 64f);
        Layer.New(w, Layer.Precision.Cell);
        Layer.New(w, Layer.Precision.Exact);
        var longFade = World.Place(w, 0, 10f, 12f, stamps[0], 0);
        World.Turn(w, longFade, 1.1f);
        World.Fade(w, longFade, 40, 60);
        var steppingDown = World.Place(w, 1, 30f, 30f, stamps[1], 20);
        World.Scale(w, steppingDown, 2f);
        World.Fade(w, steppingDown, -20, 25);
        var pulse = World.Place(w, 0, 40f, 20f, stamps[2], 0);
        World.Fade(w, pulse, 30, 40);
        World.Expire(w, pulse, 37);
        var expiring = World.Place(w, 1, 50f, 44f, stamps[0], 5);
        World.Expire(w, expiring, 50);
        var steady = World.Place(w, 0, 22f, 26f, stamps[1], 7);
        return [longFade, steppingDown, pulse, expiring, steady];
    }

    private static ulong DeterministicFrameHash(byte w, short[] page)
    {
        var hash = new CorpusHash(CorpusHash.Offset);
        fixed (short* p = page)
        {
            for (var l = 0; l < 2; l++)
            {
                World.QueryRegion(w, 0, (byte)l, 0, 0, 64, 64, p);
                hash.Mix(page, 64 * 64);
            }
        }

        return hash.Value;
    }

    private static void Save(byte world, World.SourceId id, int handle, List<SavedSource> saved)
    {
        if (!World.TryGetSourceSpec(world, id, out var spec)) return;

        var source = new SavedSource { Spec = spec, Handle = handle };
        if (World.TryGetSchedule(world, id, out var gain, out var from, out var to,
                out var ticks, out var left, out var expireLeft))
        {
            Assert.Equal(spec.Gain, gain);
            source.Fade = ticks > 0;
            source.From = from;
            source.To = to;
            source.Ticks = ticks;
            source.Left = left;
            source.ExpireLeft = expireLeft;
        }

        saved.Add(source);
    }

    private static void AssertRoundTrip(byte w, World.SourceId id, World.SourceSpec spec)
    {
        Assert.True(World.TryGetSourceSpec(w, id, out var again));
        Assert.Equal(BitConverter.SingleToInt32Bits(spec.X), BitConverter.SingleToInt32Bits(again.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(spec.Y), BitConverter.SingleToInt32Bits(again.Y));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(spec.HeadingRadians),
            BitConverter.SingleToInt32Bits(again.HeadingRadians));
        Assert.Equal(BitConverter.SingleToInt32Bits(spec.Scale), BitConverter.SingleToInt32Bits(again.Scale));
        Assert.Equal(spec.Gain, again.Gain);
        Assert.Equal(spec.Layer, again.Layer);
        Assert.Equal(spec.Stamp, again.Stamp);
    }

    private static float RestoreHeading(float heading)
    {
        var unit = (float)Math.PI * 2 / 65536;
        var angle = (int)Math.Floor(heading / (double)unit + 0.5) & 0xFFFF;
        return (float)((angle + 0.5) * (Math.PI * 2 / 65536));
    }

    private static void ApplyDrives(byte w, List<Drive> drives, int frame, World.SourceId[]? mapping)
    {
        for (var d = 0; d < drives.Count; d++)
        {
            if (mapping != null && !drives[d].Active) continue;
            var elapsed = drives[d].Elapsed0 + frame;
            if (elapsed > drives[d].Ticks) continue;
            World.Fade(w, drives[d].Id, FadeGainAt(drives[d].From, drives[d].To, drives[d].Ticks, elapsed), 0);
        }
    }

    private static void Deactivate(List<Drive> drives, int handle)
    {
        for (var d = 0; d < drives.Count; d++)
        {
            if (drives[d].Handle != handle) continue;
            var drive = drives[d];
            drive.Active = false;
            drives[d] = drive;
        }
    }

    private static void DeactivateAll(List<Drive> drives)
    {
        for (var d = 0; d < drives.Count; d++)
        {
            var drive = drives[d];
            drive.Active = false;
            drives[d] = drive;
        }
    }

    private static sbyte FadeGainAt(sbyte from, sbyte to, int ticks, int elapsed)
    {
        var clamped = Math.Clamp(elapsed, 0, ticks);
        var delta = to - from;
        var step = (int)(((long)Math.Abs(delta) * clamped + ticks / 2) / ticks);
        return (sbyte)(from + (delta < 0 ? -step : step));
    }

    private static byte[] CreateStamps(StreamSetup setup)
    {
        var stamps = new byte[setup.Stamps.Length];
        for (var s = 0; s < setup.Stamps.Length; s++)
        {
            var spec = setup.Stamps[s];
            switch (spec.Kind)
            {
                case StampKind.Box:
                    stamps[s] = Stamp.Box((ushort)spec.Width, (ushort)spec.Height, (sbyte)spec.Value);
                    break;
                case StampKind.Tent:
                    stamps[s] = Stamp.Tent((ushort)spec.Width, (ushort)spec.Height, (sbyte)spec.Value);
                    break;
                case StampKind.Bell:
                    stamps[s] = Stamp.Bell((ushort)spec.Width, (ushort)spec.Height, (sbyte)spec.Value);
                    break;
                case StampKind.Disk:
                    stamps[s] = Stamp.Disk((ushort)spec.Radius, (sbyte)spec.Value, (ushort)spec.Arc);
                    break;
                case StampKind.Cone:
                    stamps[s] = Stamp.Cone((ushort)spec.Radius, (sbyte)spec.Value, (ushort)spec.Arc);
                    break;
                case StampKind.Dome:
                    stamps[s] = Stamp.Dome((ushort)spec.Radius, (sbyte)spec.Value, (ushort)spec.Arc);
                    break;
                default:
                    var rng = new SplitMix64(spec.DataSeed);
                    var data = new sbyte[spec.Width * spec.Height];
                    for (var i = 0; i < data.Length; i++) data[i] = (sbyte)(rng.NextInt(0, 201) - 100);
                    stamps[s] = Stamp.New(data, (ushort)spec.Width, (ushort)spec.Height);
                    break;
            }
        }

        return stamps;
    }

    private struct SavedSource
    {
        public World.SourceSpec Spec;
        public int Handle;
        public bool Fade;
        public sbyte From;
        public sbyte To;
        public int Ticks;
        public int Left;
        public int ExpireLeft;
    }

    private struct Drive
    {
        public World.SourceId Id;
        public int Handle;
        public sbyte From;
        public sbyte To;
        public int Ticks;
        public int Elapsed0;
        public bool Active;
    }

    private sealed class StreamHarness : IDisposable
    {
        private const int MaxColumn = 8;
        public readonly List<World.SourceId> Attempts = new();
        private readonly short[] _page;
        private readonly Func<int, World.SourceId>? _remap;
        private readonly int _remapped;

        private readonly StreamSetup _setup;
        private readonly byte[] _stamps;
        public byte WorldId;

        public StreamHarness(
            StreamSetup setup, byte[] stamps, short[] page, Func<int, World.SourceId>? remap, int remapped)
        {
            _setup = setup;
            _stamps = stamps;
            _page = page;
            _remap = remap;
            _remapped = remapped;
            WorldId = World.New();
            foreach (var grid in setup.Grids) Grid.New(WorldId, grid.Power, grid.X, grid.Y, grid.Size);
            for (var l = 0; l < setup.Layers.Length; l++)
            {
                var spec = setup.Layers[l];
                if (!spec.Derived)
                {
                    Layer.New(
                        WorldId, spec.Transient ? Layer.Residency.Transient : Layer.Residency.Persistent,
                        spec.Precision);
                    continue;
                }

                switch (spec.Op)
                {
                    case LayerOp.Sum:
                        Layer.Sum(WorldId, spec.A, spec.WeightA, spec.B, spec.WeightB, spec.Shift);
                        break;
                    case LayerOp.Min:
                        Layer.Min(WorldId, spec.A, spec.B);
                        break;
                    case LayerOp.Max:
                        Layer.Max(WorldId, spec.A, spec.B);
                        break;
                    default:
                        Layer.Mask(WorldId, spec.A, spec.B, spec.Low, spec.High);
                        break;
                }
            }
        }

        public void Dispose()
        {
            World.Free(WorldId);
            WorldId = 0;
        }

        private World.SourceId Id(int handle)
        {
            if (_remap == null) return (uint)handle < (uint)Attempts.Count ? Attempts[handle] : default;
            if ((uint)handle < (uint)_remapped) return _remap(handle);
            var index = handle - _remapped;
            return (uint)index < (uint)Attempts.Count ? Attempts[index] : default;
        }

        public void Apply(in StreamCommand command)
        {
            switch (command.Op)
            {
                case StreamOp.Place:
                    Attempts.Add(World.Place(
                        WorldId, command.Layer, command.X, command.Y, _stamps[command.Stamp], command.Gain));
                    break;
                case StreamOp.Move:
                    World.Move(WorldId, Id(command.Handle), command.X, command.Y);
                    break;
                case StreamOp.Turn:
                    World.Turn(WorldId, Id(command.Handle), command.X);
                    break;
                case StreamOp.Scale:
                    World.Scale(WorldId, Id(command.Handle), command.X);
                    break;
                case StreamOp.SetGain:
                    World.SetGain(WorldId, Id(command.Handle), command.Gain);
                    break;
                case StreamOp.Remove:
                    World.Remove(WorldId, Id(command.Handle));
                    break;
                case StreamOp.Fade:
                    World.Fade(WorldId, Id(command.Handle), command.Gain, command.Int0);
                    break;
                case StreamOp.Expire:
                    World.Expire(WorldId, Id(command.Handle), command.Int0);
                    break;
                case StreamOp.Sync:
                    ExecuteSync(command);
                    break;
                case StreamOp.Record:
                    World.Record(WorldId);
                    break;
                case StreamOp.StopRecording:
                    World.StopRecording(WorldId);
                    break;
                case StreamOp.Clear:
                    World.Clear(WorldId);
                    break;
                case StreamOp.Splat:
                    World.Splat(
                        WorldId, command.Layer, new[] { command.X }, new[] { command.Y },
                        _stamps[command.Stamp], command.Gain);
                    break;
            }
        }

        public ulong FrameHash()
        {
            var hash = new CorpusHash(CorpusHash.Offset);
            fixed (short* p = _page)
            {
                for (var g = 0; g < _setup.Grids.Length; g++)
                {
                    var size = 1 << _setup.Grids[g].Power;
                    for (var l = 0; l < _setup.Layers.Length; l++)
                    {
                        World.QueryRegion(WorldId, (byte)g, (byte)l, 0, 0, size, size, p);
                        hash.Mix(_page, size * size);
                    }
                }
            }

            return hash.Value;
        }

        private void ExecuteSync(in StreamCommand command)
        {
            var count = Math.Min(command.Int0, MaxColumn);
            var rng = new SplitMix64(command.Aux ^ StreamOperands.AuxSalt);
            var target = _setup.Grids[command.Grid];
            var ids = new World.SourceId[Math.Max(1, count)];
            var xs = new float[Math.Max(1, count)];
            var ys = new float[Math.Max(1, count)];
            for (var i = 0; i < count; i++) ids[i] = Id(command.Handle + i);
            StreamOperands.ColumnPositions(ref rng, target, xs, ys, count);

            fixed (World.SourceId* pIds = ids)
            fixed (float* pXs = xs, pYs = ys)
            {
                World.Sync(WorldId, pIds, pXs, pYs, count);
            }
        }
    }
}
