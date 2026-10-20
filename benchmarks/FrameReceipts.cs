using System.Globalization;
using Gi;

internal static unsafe class FrameReceipts
{
    private const int Entities = 20_000;
    private const int Power = 8;
    private const int WarmFrames = 8;

    public static int Run(TextWriter log)
    {
        var failures = 0;

        void Check(string name, bool ok)
        {
            log.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failures++;
        }

        Check("frames-percentile-is-nearest-rank", PercentileIsNearestRank());
        Check("frames-json-is-culture-invariant", JsonIsCultureInvariant());
        Check("frames-morton-sort-is-an-ordered-permutation", MortonSortIsAnOrderedPermutation());
        Check("frames-b1-legacy-deterministic-and-matches-rebuild",
            Scenario(() => new FrameLegacy(Entities, Power, 7), 12, log));
        Check("frames-b2-one-mover-deterministic-and-matches-rebuild",
            Scenario(() => new FrameSparse(Entities, Power, 7, 1), 60, log));
        Check("frames-b2-200-movers-deterministic-and-matches-rebuild",
            Scenario(() => new FrameSparse(Entities, Power, 7, 200), 60, log));
        Check("frames-b4-idle-deterministic-and-matches-rebuild",
            Scenario(() => new FrameIdle(Entities, Power, 7), 12, log));
        Check("frames-c-crowd-deterministic-and-matches-rebuild",
            Scenario(() => new FrameCrowd(Entities, Power, 7), 140, log));
        Check("frames-w-world-deterministic-and-matches-rebuild",
            Scenario(() => new FrameWorld(Entities, Power, 7), 130, log));
        Check("frames-b1-cell-deterministic-and-matches-rebuild",
            Scenario(() => new FrameLegacy(Entities, Power, 7, true), 12, log));
        Check("frames-c-cell-deterministic-and-matches-rebuild",
            Scenario(() => new FrameCrowd(Entities, Power, 7, true), 140, log));
        Check("frames-w-cell-deterministic-and-matches-rebuild",
            Scenario(() => new FrameWorld(Entities, Power, 7, true), 130, log));
        return failures;
    }

    private static bool PercentileIsNearestRank()
    {
        var ascending = new long[300];
        for (var i = 0; i < ascending.Length; i++) ascending[i] = i + 1;
        var seven = new long[] { 1, 2, 3, 4, 5, 6, 7 };
        return FrameStats.Rank(ascending, 50) == 150 && FrameStats.Rank(ascending, 99) == 297 &&
               FrameStats.Rank(seven, 50) == 4 && FrameStats.Rank(seven, 99) == 7 &&
               FrameStats.Rank(new long[] { 9 }, 99) == 9;
    }

    private static bool JsonIsCultureInvariant()
    {
        string Sample()
        {
            return new FrameJson().BeginObject()
                .Text("name", "a\"b\\cé")
                .Number("float", 1234.56789, 3)
                .Number("negative_zero", -0.0000001, 3)
                .Number("not_finite", double.NaN, 3)
                .Integer("big", 1234567890123L)
                .Flag("flag", true)
                .BeginArray("list").BeginObject().Integer("n", 1).EndObject().EndArray()
                .Null("none")
                .EndObject()
                .ToString();
        }

        const string expected =
            "{\"name\":\"a\\\"b\\\\c\\u00e9\",\"float\":1234.568,\"negative_zero\":0.000,\"not_finite\":null,\"big\":1234567890123," +
            "\"flag\":true,\"list\":[{\"n\":1}],\"none\":null}";
        var saved = CultureInfo.CurrentCulture;
        try
        {
            var invariant = Sample();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = Sample();
            return invariant == expected && german == expected;
        }
        catch (CultureNotFoundException)
        {
            return Sample() == expected;
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static bool MortonSortIsAnOrderedPermutation()
    {
        const int count = 5000;
        using var arena = new FrameArena();
        var xs = arena.Alloc<float>(count);
        var ys = arena.Alloc<float>(count);
        var pairs = arena.Alloc<ulong>(count);
        var scratch = arena.Alloc<ulong>(count);
        var counts = arena.Alloc<uint>(2048);
        var seen = new bool[count];
        var rng = new FrameRandom(11);
        for (var i = 0; i < count; i++)
        {
            xs[i] = rng.Unit() * (1 << Power);
            ys[i] = rng.Unit() * (1 << Power);
        }

        var sorted = FrameOrder.Sort(xs, ys, count, Power, pairs, scratch, counts);
        var previous = 0u;
        for (var i = 0; i < count; i++)
        {
            var key = (uint)(sorted[i] >> 32);
            var index = (int)(uint)sorted[i];
            if (key < previous || seen[index]) return false;
            if (key != FrameOrder.Morton((uint)(int)xs[index], (uint)(int)ys[index])) return false;
            seen[index] = true;
            previous = key;
        }

        return true;
    }

    private static bool Scenario(Func<FrameScenario> create, int frames, TextWriter log)
    {
        var first = Drive(create, frames, out var firstFault);
        var second = Drive(create, frames, out var secondFault);
        if (firstFault.Length != 0) log.WriteLine("  fault: " + firstFault);
        if (secondFault.Length != 0) log.WriteLine("  fault: " + secondFault);
        if (first != second) log.WriteLine("  fault: checksum differs between identical runs");
        return firstFault.Length == 0 && secondFault.Length == 0 && first == second && first != 0;
    }

    private static ulong Drive(Func<FrameScenario> create, int frames, out string fault)
    {
        using var scenario = create();
        scenario.Setup();
        var marks = stackalloc long[scenario.Stages.Length + 1];
        var checksum = FrameHash.Seed;
        var complete = true;
        var allocated = 0L;
        for (var frame = 0; frame < frames; frame++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            complete &= FrameDriver.Execute(scenario, frame, marks, out var hash);
            var spent = GC.GetAllocatedBytesForCurrentThread() - before;
            if (frame >= WarmFrames) allocated += spent;
            checksum = FrameHash.Mix(checksum, hash);
        }

        fault = !complete ? "a read was incomplete"
            : allocated != 0 ? "warm frames allocated " + allocated.ToString(CultureInfo.InvariantCulture) + " B"
            : !scenario.VerifyState() ? "driver state disagrees with the world"
            : !FieldMatchesRebuild(scenario) ? "field differs from a fresh rebuild"
            : "";
        return FrameHash.Mix(checksum, scenario.FinalState());
    }

    private static bool FieldMatchesRebuild(FrameScenario scenario)
    {
        var source = World.GetContext(scenario.World);
        if (source == null) return false;

        var fresh = World.New();
        try
        {
            Grid.New(fresh, scenario.Power, 0f, 0f, scenario.Size);
            foreach (var layer in scenario.LayerIds)
            {
                if (!Layer.TryGetRecipe(scenario.World, layer, out var recipe)) return false;
                byte made;
                if (recipe.Op == Layer.Derivation.Base)
                {
                    if (!Layer.TryGetPrecision(scenario.World, layer, out var precision)) return false;
                    made = Layer.New(fresh, precision);
                }
                else if (recipe.Op == Layer.Derivation.Sum)
                {
                    made = Layer.Sum(fresh, recipe.FirstLayer, recipe.FirstWeight, recipe.SecondLayer,
                        recipe.SecondWeight, recipe.Shift);
                }
                else
                {
                    made = byte.MaxValue;
                }

                if (made != layer) return false;
            }

            var sources = &source->Sources;
            for (var slot = 0; slot < sources->Count; slot++)
            {
                if (sources->Alive[slot] == 0) continue;
                if (sources->Angle[slot] != 0 || sources->Scale[slot] != 256) return false;
                var id = World.Place(fresh, sources->Layer[slot], sources->X[slot], sources->Y[slot],
                    sources->Stamp[slot], (sbyte)sources->Gain[slot]);
                if (id == default) return false;
            }

            World.Process(fresh);
            var cells = scenario.Size * scenario.Size;
            var left = new short[cells];
            var right = new short[cells];
            fixed (short* l = left, r = right)
            {
                foreach (var layer in scenario.LayerIds)
                {
                    World.QueryRegion(scenario.World, scenario.Grid, layer, 0, 0, scenario.Size, scenario.Size, l);
                    World.QueryRegion(fresh, 0, layer, 0, 0, scenario.Size, scenario.Size, r);
                    if (!left.AsSpan().SequenceEqual(right)) return false;
                }
            }

            return true;
        }
        finally
        {
            World.Free(fresh);
        }
    }
}
