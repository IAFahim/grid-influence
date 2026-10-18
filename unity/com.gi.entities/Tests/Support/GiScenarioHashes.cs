using System.Globalization;
using System.Text;

namespace Gi.Entities.Tests.Support
{
    public sealed class GiScenarioHashes
    {
        public readonly ulong[][] PerTick;

        public GiScenarioHashes(ulong[][] perTick)
        {
            PerTick = perTick;
        }

        public ulong Digest()
        {
            var hash = 14695981039346656037ul;
            foreach (var tick in PerTick)
            foreach (var layer in tick)
            {
                var value = layer;
                for (var i = 0; i < 8; i++)
                {
                    unchecked
                    {
                        hash ^= (byte)(value & 0xFF);
                        hash *= 1099511628211ul;
                    }

                    value >>= 8;
                }
            }

            return hash;
        }

        public string Compare(GiScenarioHashes other)
        {
            if (PerTick.Length != other.PerTick.Length)
                return "tick count " + PerTick.Length.ToString(CultureInfo.InvariantCulture) + " vs " +
                       other.PerTick.Length.ToString(CultureInfo.InvariantCulture);

            for (var t = 0; t < PerTick.Length; t++)
            {
                if (PerTick[t].Length != other.PerTick[t].Length)
                    return "tick " + t.ToString(CultureInfo.InvariantCulture) + " layer count";
                for (var l = 0; l < PerTick[t].Length; l++)
                    if (PerTick[t][l] != other.PerTick[t][l])
                        return "tick " + t.ToString(CultureInfo.InvariantCulture) + " layer " +
                               l.ToString(CultureInfo.InvariantCulture) + ": " +
                               PerTick[t][l].ToString("x16", CultureInfo.InvariantCulture) + " vs " +
                               other.PerTick[t][l].ToString("x16", CultureInfo.InvariantCulture);
            }

            return string.Empty;
        }

        public string ToText()
        {
            var text = new StringBuilder();
            for (var t = 0; t < PerTick.Length; t++)
            {
                text.Append("tick ").Append(t.ToString(CultureInfo.InvariantCulture));
                foreach (var layer in PerTick[t]) text.Append(' ').Append(layer.ToString("x16", CultureInfo.InvariantCulture));
                text.Append('\n');
            }

            return text.ToString();
        }
    }
}
