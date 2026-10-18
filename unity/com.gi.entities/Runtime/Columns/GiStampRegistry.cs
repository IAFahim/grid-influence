using System;
using System.Collections.Generic;
using GiStamp = Gi.Stamp;

namespace Gi.Entities
{
    public sealed class GiStampRegistry
    {
        private readonly Dictionary<ulong, byte> _byHash = new Dictionary<ulong, byte>();
        private readonly ulong[] _hashOf = new ulong[256];
        private readonly int[] _references = new int[256];
        private readonly HashSet<byte> _deferred = new HashSet<byte>();

        public int LiveCount => _byHash.Count;

        public int DeferredCount => _deferred.Count;

        public bool TryAcquire(GiStampSpec spec, ReadOnlySpan<sbyte> cells, out byte stamp)
        {
            var hash = GiProfileHash.StampHash(spec, cells);
            if (_byHash.TryGetValue(hash, out stamp))
            {
                _references[stamp]++;
                return true;
            }

            if (!TryCreate(spec, cells, out stamp)) return false;
            _byHash.Add(hash, stamp);
            _hashOf[stamp] = hash;
            _references[stamp] = 1;
            return true;
        }

        public void Release(byte stamp)
        {
            if (_references[stamp] == 0) return;
            if (--_references[stamp] > 0) return;

            _byHash.Remove(_hashOf[stamp]);
            _hashOf[stamp] = 0;
            if (!GiStamp.Free(stamp)) _deferred.Add(stamp);
            RetryDeferred();
        }

        public void RetryDeferred()
        {
            if (_deferred.Count == 0) return;
            var pending = new byte[_deferred.Count];
            _deferred.CopyTo(pending);
            foreach (var stamp in pending)
                if (GiStamp.Free(stamp))
                    _deferred.Remove(stamp);
        }

        public void Clear()
        {
            _byHash.Clear();
            _deferred.Clear();
            Array.Clear(_hashOf, 0, _hashOf.Length);
            Array.Clear(_references, 0, _references.Length);
        }

        private static bool TryCreate(GiStampSpec spec, ReadOnlySpan<sbyte> cells, out byte stamp)
        {
            stamp = 0;
            try
            {
                switch (spec.Shape)
                {
                    case GiStampShape.Box:
                        stamp = GiStamp.Box(spec.Width, spec.Height, spec.Value);
                        break;
                    case GiStampShape.Tent:
                        stamp = GiStamp.Tent(spec.Width, spec.Height, spec.Value);
                        break;
                    case GiStampShape.Bell:
                        stamp = GiStamp.Bell(spec.Width, spec.Height, spec.Value);
                        break;
                    case GiStampShape.Disk:
                        stamp = GiStamp.Disk(spec.Radius, spec.Value, spec.Arc);
                        break;
                    case GiStampShape.Cone:
                        stamp = GiStamp.Cone(spec.Radius, spec.Value, spec.Arc);
                        break;
                    case GiStampShape.Dome:
                        stamp = GiStamp.Dome(spec.Radius, spec.Value, spec.Arc);
                        break;
                    default:
                        stamp = GiStamp.New(cells.Slice(spec.CellOffset, spec.CellCount), spec.Width, spec.Height);
                        break;
                }

                return stamp != 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
