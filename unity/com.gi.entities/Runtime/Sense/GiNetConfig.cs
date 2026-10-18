using BovineLabs.Core.ConfigVars;
using Unity.Burst;

namespace Gi.Entities
{
    [Configurable]
    public static class GiNetConfig
    {
        private struct CheckKey
        {
        }

        [ConfigVar("gi.net.check", 0, "Ticks between lockstep field hashes sent to clients; 0 turns the check off.")]
        public static readonly SharedStatic<int> Check = SharedStatic<int>.GetOrCreate<CheckKey>();
    }
}
