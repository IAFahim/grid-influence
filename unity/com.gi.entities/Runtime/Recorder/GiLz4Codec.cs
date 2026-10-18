using System;
using BovineLabs.Core.Utility;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    [NoAutoStaticsCleanup]
    public sealed unsafe class GiLz4Codec : IGiCodec
    {
        private static bool _probed;
        private static bool _available;

        public static bool Available
        {
            get
            {
                if (_probed) return _available;

                _probed = true;
                try
                {
                    var sample = new byte[64];
                    var packed = new byte[CodecService.GetBoundedSize(Codec.LZ4, sample.Length)];
                    fixed (byte* source = sample)
                    fixed (byte* destination = packed)
                        _available = CodecService.Compress(Codec.LZ4, source, sample.Length, destination, packed.Length) > 0;
                }
                catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
                {
                    _available = false;
                }

                return _available;
            }
        }

        public static IGiCodec Create()
        {
            return Available ? new GiLz4Codec() : new GiStoredCodec();
        }

        public int Bound(int length)
        {
            return CodecService.GetBoundedSize(Codec.LZ4, length);
        }

        public int Compress(byte[] source, int length, byte[] destination)
        {
            fixed (byte* from = source)
            fixed (byte* to = destination)
                return CodecService.Compress(Codec.LZ4, from, length, to, destination.Length);
        }

        public bool Decompress(byte[] source, int length, byte[] destination, int rawLength)
        {
            fixed (byte* from = source)
            fixed (byte* to = destination)
                return CodecService.Decompress(Codec.LZ4, from, length, to, rawLength);
        }
    }
}
