using System;
using Gi.Validation;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public readonly struct GiPreviewPose
    {
        public readonly float PhaseX;
        public readonly float PhaseY;
        public readonly float AngleRadians;
        public readonly float Scale;

        public GiPreviewPose(float phaseX, float phaseY, float angleRadians, float scale)
        {
            PhaseX = phaseX;
            PhaseY = phaseY;
            AngleRadians = angleRadians;
            Scale = scale;
        }
    }

    public readonly struct GiPreviewBounds
    {
        public readonly int MinX;
        public readonly int MinY;
        public readonly int MaxX;
        public readonly int MaxY;

        public GiPreviewBounds(int minX, int minY, int maxX, int maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public bool Empty => MaxX < MinX || MaxY < MinY;

        public int Width => Empty ? 0 : MaxX - MinX + 1;

        public int Height => Empty ? 0 : MaxY - MinY + 1;
    }

    public sealed class GiStampPreview : IDisposable
    {
        public const int Power = 6;
        public const int Cells = 1 << Power;
        public const float Center = Cells * 0.5f;
        public const sbyte Gain = 8;

        private readonly GiStampRegistry _registry = new GiStampRegistry();
        private readonly byte _world;
        private readonly byte _exact;
        private readonly byte _cell;
        private byte _stamp;
        private bool _has;
        private bool _disposed;

        public GiStampPreview()
        {
            _world = GiWorld.New();
            GiGrid.New(_world, Power, 0f, 0f, Cells);
            _exact = GiLayer.New(_world, GiLayer.Precision.Exact);
            _cell = GiLayer.New(_world, GiLayer.Precision.Cell);
        }

        public int TemporaryStamps => _registry.LiveCount;

        public bool HasStamp => _has;

        public bool Set(GiStampSpec spec, ReadOnlySpan<sbyte> cells, out string error)
        {
            Clear();
            var issues = new System.Collections.Generic.List<GiProfileIssue>();
            GiProfileCheck.CheckStamp(spec, 0, cells.Length, issues);
            if (issues.Count > 0)
            {
                error = issues[0].Message;
                return false;
            }

            if (!_registry.TryAcquire(spec, cells, out _stamp))
            {
                error = "Gi could not create the stamp.";
                return false;
            }

            _has = true;
            error = null;
            return true;
        }

        public void Clear()
        {
            if (!_has) return;

            _registry.Release(_stamp);
            _has = false;
            _stamp = 0;
        }

        public static bool TryFootprint(GiStampSpec spec, float scale, out string error)
        {
            var width = spec.Shape == GiStampShape.Disk || spec.Shape == GiStampShape.Cone || spec.Shape == GiStampShape.Dome
                ? spec.Radius * 2
                : spec.Width;
            var height = spec.Shape == GiStampShape.Disk || spec.Shape == GiStampShape.Cone || spec.Shape == GiStampShape.Dome
                ? spec.Radius * 2
                : spec.Height;
            return GiValidation.TryFootprint((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), out error);
        }

        public unsafe ulong Render(in GiPreviewPose pose, short[] exact, short[] cell, out GiPreviewBounds bounds)
        {
            if (exact.Length < Cells * Cells || cell.Length < Cells * Cells) throw new ArgumentException("The preview buffers hold 64 x 64 cells.");

            Array.Clear(exact, 0, Cells * Cells);
            Array.Clear(cell, 0, Cells * Cells);
            bounds = new GiPreviewBounds(0, 0, -1, -1);
            if (!_has) return Hash(exact, cell);

            var x = Center + pose.PhaseX;
            var y = Center + pose.PhaseY;
            var first = GiWorld.Place(_world, _exact, x, y, _stamp, Gain);
            var second = GiWorld.Place(_world, _cell, x, y, _stamp, Gain);
            GiWorld.Turn(_world, first, pose.AngleRadians);
            GiWorld.Turn(_world, second, pose.AngleRadians);
            GiWorld.Scale(_world, first, pose.Scale);
            GiWorld.Scale(_world, second, pose.Scale);
            GiWorld.Process(_world);
            fixed (short* e = exact) GiWorld.QueryRegion(_world, 0, _exact, 0, 0, Cells, Cells, e);
            fixed (short* c = cell) GiWorld.QueryRegion(_world, 0, _cell, 0, 0, Cells, Cells, c);
            GiWorld.Remove(_world, first);
            GiWorld.Remove(_world, second);
            GiWorld.Process(_world);

            bounds = BoundsOf(exact);
            return Hash(exact, cell);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Clear();
            GiWorld.Free(_world);
        }

        private static GiPreviewBounds BoundsOf(short[] cells)
        {
            int minX = Cells, minY = Cells, maxX = -1, maxY = -1;
            for (var y = 0; y < Cells; y++)
            for (var x = 0; x < Cells; x++)
            {
                if (cells[y * Cells + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            return new GiPreviewBounds(minX, minY, maxX, maxY);
        }

        private static ulong Hash(short[] exact, short[] cell)
        {
            var hash = 14695981039346656037UL;
            foreach (var value in exact) hash = (hash ^ (ushort)value) * 1099511628211UL;
            foreach (var value in cell) hash = (hash ^ (ushort)value) * 1099511628211UL;
            return hash;
        }
    }
}
