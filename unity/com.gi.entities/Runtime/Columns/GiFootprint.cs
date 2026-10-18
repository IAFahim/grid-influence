using System;

namespace Gi.Entities
{
    public static class GiFootprint
    {
        public static bool IsRound(GiStampShape shape)
        {
            return shape == GiStampShape.Disk || shape == GiStampShape.Cone || shape == GiStampShape.Dome;
        }

        public static void HalfExtents(in GiStampSpec spec, float headingRadians, float scale, out float halfWidth, out float halfHeight)
        {
            if (IsRound(spec.Shape))
            {
                halfWidth = spec.Radius * scale;
                halfHeight = halfWidth;
                return;
            }

            var pad = spec.Shape == GiStampShape.Raster ? 2 : 0;
            var width = (spec.Width + pad) * scale;
            var height = (spec.Height + pad) * scale;
            var cos = Math.Abs((float)Math.Cos(headingRadians));
            var sin = Math.Abs((float)Math.Sin(headingRadians));
            halfWidth = (cos * width + sin * height) * 0.5f;
            halfHeight = (sin * width + cos * height) * 0.5f;
        }

        public static void WorldRect(in GiStampSpec spec, float x, float y, float headingRadians, float scale,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            HalfExtents(spec, headingRadians, scale, out var halfWidth, out var halfHeight);
            minX = x - halfWidth;
            maxX = x + halfWidth;
            minY = y - halfHeight;
            maxY = y + halfHeight;
        }

        public static void Corners(in GiStampSpec spec, float x, float y, float headingRadians, float scale, float[] cornersXy)
        {
            if (cornersXy.Length < 8) throw new ArgumentException("A footprint has four corners.", nameof(cornersXy));

            var pad = spec.Shape == GiStampShape.Raster ? 1f : 0f;
            var halfWidth = (IsRound(spec.Shape) ? spec.Radius : spec.Width * 0.5f + pad) * scale;
            var halfHeight = (IsRound(spec.Shape) ? spec.Radius : spec.Height * 0.5f + pad) * scale;
            var cos = (float)Math.Cos(headingRadians);
            var sin = (float)Math.Sin(headingRadians);
            for (var i = 0; i < 4; i++)
            {
                var sx = i == 0 || i == 3 ? -1f : 1f;
                var sy = i < 2 ? -1f : 1f;
                var lx = sx * halfWidth;
                var ly = sy * halfHeight;
                cornersXy[2 * i] = x + lx * cos - ly * sin;
                cornersXy[2 * i + 1] = y + lx * sin + ly * cos;
            }
        }
    }
}
