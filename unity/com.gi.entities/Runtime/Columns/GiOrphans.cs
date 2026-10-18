using System;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public readonly struct GiReclaimReport
    {
        public readonly int Worlds;
        public readonly int Stamps;
        public readonly bool Torn;

        public GiReclaimReport(int worlds, int stamps, bool torn)
        {
            Worlds = worlds;
            Stamps = stamps;
            Torn = torn;
        }

        public bool Found => Worlds != 0 || Stamps != 0;
    }

    public static class GiOrphans
    {
        public static int LiveWorlds()
        {
            Span<byte> ids = stackalloc byte[256];
            return GiWorld.CopyWorldIds(ids);
        }

        public static GiReclaimReport Reclaim(GiStampRegistry registry)
        {
            Span<byte> ids = stackalloc byte[256];
            var live = GiWorld.CopyWorldIds(ids);
            var worlds = 0;
            for (var i = 0; i < live; i++)
                if (GiWorld.Free(ids[i]))
                    worlds++;

            var stamps = 0;
            for (var stamp = 1; stamp < 256; stamp++)
            {
                if (!GiStamp.TryGetSpec((byte)stamp, out _)) continue;
                if (GiStamp.Free((byte)stamp)) stamps++;
            }

            registry.Clear();
            return new GiReclaimReport(worlds, stamps, GiWorld.Teardown());
        }
    }
}
