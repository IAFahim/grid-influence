using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiSourceState
    {
        public GiWorld.SourceId Id;
        public byte LayerKey;
        public byte StampKey;
        public sbyte Gain;
        public float X;
        public float Y;
        public float Heading;
        public float Scale;
        public sbyte FadeFrom;
        public sbyte FadeTo;
        public int FadeTicks;
        public int FadeTicksLeft;
        public int ExpireTicksLeft;

        public bool Fading => FadeTicks > 0;

        public bool Expiring => ExpireTicksLeft > 0;
    }

    public sealed class GiKeyframe
    {
        public int Tick;
        public GiSourceState[] Sources = Array.Empty<GiSourceState>();
        public GiTickRecord Splats = new GiTickRecord();
        public ulong[] Hashes = Array.Empty<ulong>();
    }

    public sealed class GiTickRecord
    {
        public const int SyncCategories = GiSyncPass.Categories;
        public const int SplatKeys = GiSplatPass.Layers;

        public int Tick;
        public int CommandCount;
        public GiCommand[] Commands = Array.Empty<GiCommand>();
        public GiCommandResult[] Results = Array.Empty<GiCommandResult>();
        public int[] SyncStart = new int[SyncCategories];
        public int[] SyncCount = new int[SyncCategories];
        public GiWorld.SourceId[] SyncSources = Array.Empty<GiWorld.SourceId>();
        public float[] SyncXs = Array.Empty<float>();
        public float[] SyncYs = Array.Empty<float>();
        public float[] SyncHeadings = Array.Empty<float>();
        public float[] SyncScales = Array.Empty<float>();
        public int[] SplatStart = new int[SplatKeys];
        public int[] SplatCount = new int[SplatKeys];
        public float[] SplatXs = Array.Empty<float>();
        public float[] SplatYs = Array.Empty<float>();
        public byte[] SplatStamps = Array.Empty<byte>();
        public sbyte[] SplatGains = Array.Empty<sbyte>();
        public ulong[] Hashes = Array.Empty<ulong>();

        public int SyncTotal
        {
            get
            {
                var total = 0;
                for (var k = 0; k < SyncCategories; k++) total += SyncCount[k];
                return total;
            }
        }

        public int SplatTotal
        {
            get
            {
                var total = 0;
                for (var k = 0; k < SplatKeys; k++) total += SplatCount[k];
                return total;
            }
        }

        public void Clear()
        {
            Tick = 0;
            CommandCount = 0;
            Array.Clear(SyncStart, 0, SyncStart.Length);
            Array.Clear(SyncCount, 0, SyncCount.Length);
            Array.Clear(SplatStart, 0, SplatStart.Length);
            Array.Clear(SplatCount, 0, SplatCount.Length);
        }

        public void SizeCommands(int count)
        {
            CommandCount = count;
            if (Commands.Length >= count) return;

            Array.Resize(ref Commands, Math.Max(count, Commands.Length * 2));
            Array.Resize(ref Results, Commands.Length);
        }

        public void SizeSync(int total)
        {
            if (SyncSources.Length >= total) return;

            SyncSources = new GiWorld.SourceId[total];
            SyncXs = new float[total];
            SyncYs = new float[total];
            SyncHeadings = new float[total];
            SyncScales = new float[total];
        }

        public void SizeSplats(int total)
        {
            if (SplatXs.Length >= total) return;

            SplatXs = new float[total];
            SplatYs = new float[total];
            SplatStamps = new byte[total];
            SplatGains = new sbyte[total];
        }
    }

    public sealed class GiRecordingData
    {
        public GiProfileSpec Profile = new GiProfileSpec();
        public List<GiKeyframe> Keyframes = new List<GiKeyframe>();
        public List<GiTickRecord> Ticks = new List<GiTickRecord>();
    }
}
