using System.Collections.Generic;
using Gi.Entities.Presentation;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed unsafe class GiMirrorTests
    {
        private static readonly int[] MirroredLayers = { 0, 1, 3, 4, 5 };

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("The GPU mirror needs compute shaders; run this suite on a machine with a graphics device.");
        }

        private static int[] Decode(GiMirror mirror)
        {
            var cells = mirror.Cells;
            using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cells * cells, sizeof(int));
            mirror.Decode(new RectInt(0, 0, cells, cells), output);
            var decoded = new int[cells * cells];
            output.GetData(decoded);
            return decoded;
        }

        private static short[] Expected(byte world, byte grid, byte layer, int cells)
        {
            var expected = new short[cells * cells];
            fixed (short* destination = expected) GiWorld.QueryRegion(world, grid, layer, 0, 0, cells, cells, destination);
            return expected;
        }

        private static void AssertSame(GiMirror mirror, byte world, string label)
        {
            var decoded = Decode(mirror);
            var expected = Expected(world, mirror.Grid, mirror.Layer, mirror.Cells);
            for (var i = 0; i < expected.Length; i++)
            {
                if (decoded[i] == expected[i]) continue;
                Assert.Fail($"{label}: cell {i % mirror.Cells},{i / mirror.Cells} mirror {decoded[i]} QueryRegion {expected[i]}");
            }
        }

        [Test]
        public void AfterRandomTicksTheMirrorDecodesToExactlyWhatQueryRegionReturnsOverFiftySeeds()
        {
            for (ulong seed = 1; seed <= 50; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, GiScenarioOptions.Standard);
                var mirrors = new List<GiMirror>();
                try
                {
                    GiScenarioWorld last = null;
                    GiReferenceExecutor.Run(scenario, (setup, tick) =>
                    {
                        if (tick == 0)
                        {
                            foreach (var layer in MirroredLayers)
                            {
                                mirrors.Add(new GiMirror(setup.World, 0, setup.Layers[layer], scenario.Profile.Grids[0].Power));
                                mirrors.Add(new GiMirror(setup.World, 1, setup.Layers[layer], scenario.Profile.Grids[1].Power));
                            }
                        }

                        foreach (var mirror in mirrors) mirror.Update(1 << 20);
                        if (tick == scenario.Ticks.Length - 1)
                        {
                            last = setup;
                            foreach (var mirror in mirrors) AssertSame(mirror, setup.World, $"seed {seed} grid {mirror.Grid} layer {mirror.Layer}");
                        }
                    });
                    Assert.That(last, Is.Not.Null);
                }
                finally
                {
                    foreach (var mirror in mirrors) mirror.Dispose();
                }
            }
        }

        [Test]
        public void UploadedBytesScaleWithTheNumberOfChangedTiles()
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var layer = setup.Layers[0];
            using var mirror = new GiMirror(setup.World, 1, layer, 7);
            var sources = new List<GiWorld.SourceId>();
            for (var i = 0; i < 12; i++) sources.Add(GiWorld.Place(setup.World, layer, 6 + (i % 4) * 33, 6 + (i / 4) * 33, setup.Stamps[0], 8));

            GiWorld.Process(setup.World);
            mirror.Update(1 << 20);
            var settled = mirror.UploadedTiles;
            AssertSame(mirror, setup.World, "first frame");

            var previous = settled;
            for (var moved = 1; moved <= 6; moved++)
            {
                for (var i = 0; i < moved; i++) GiWorld.SetGain(setup.World, sources[i], (sbyte)(2 + moved + i));
                GiWorld.Process(setup.World);
                var changed = GiWorld.ChangedTiles(setup.World, 1, layer, (int*)null);
                mirror.Update(1 << 20);
                var uploaded = mirror.UploadedTiles - previous;
                previous = mirror.UploadedTiles;
                Assert.That(uploaded, Is.EqualTo(changed), $"{moved} edited sources");
                Assert.That(uploaded, Is.LessThan(mirror.TileCount), "far below the whole grid");
            }

            GiWorld.Process(setup.World);
            var quiet = mirror.UploadedTiles;
            mirror.Update(1 << 20);
            Assert.That(mirror.UploadedTiles, Is.EqualTo(quiet), "no change, no upload");
            AssertSame(mirror, setup.World, "after edits");
        }

        [Test]
        public void OnceChangesStopTheBudgetCarryOverDrainsCompletely()
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var layer = setup.Layers[0];
            using var mirror = new GiMirror(setup.World, 1, layer, 7);
            for (var i = 0; i < 40; i++) GiWorld.Place(setup.World, layer, 4 + (i % 8) * 15, 4 + (i / 8) * 25, setup.Stamps[i % 6], 7);

            GiWorld.Process(setup.World);
            var frames = 0;
            mirror.Update(3);
            Assert.That(mirror.Pending, Is.GreaterThan(0));
            while (mirror.Pending > 0 && frames++ < 100) mirror.Update(3);
            Assert.That(mirror.Pending, Is.EqualTo(0));
            Assert.That(frames, Is.GreaterThan(1));
            AssertSame(mirror, setup.World, "after the drain");
        }

        [Test]
        public void SteadyFramesAllocateNothing()
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var layer = setup.Layers[0];
            using var mirror = new GiMirror(setup.World, 1, layer, 7);
            var sources = new List<GiWorld.SourceId>();
            for (var i = 0; i < 20; i++) sources.Add(GiWorld.Place(setup.World, layer, 8 + (i % 5) * 22, 8 + (i / 5) * 22, setup.Stamps[1], 5));

            var phase = 0;

            void Frame()
            {
                for (var t = 0; t < 30; t++)
                {
                    phase++;
                    GiWorld.SetGain(setup.World, sources[phase % sources.Count], (sbyte)(1 + phase % 7));
                    GiWorld.Process(setup.World);
                    mirror.Update(16);
                }
            }

            Frame();
            Assert.That(GiAllocation.SteadyZero(Frame, 14, 3), Is.True);
        }
    }
}
