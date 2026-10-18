using System;

namespace Gi.Entities
{
    public readonly struct GiPlane
    {
        public readonly GiPlaneAxes Axes;
        public readonly float OriginX;
        public readonly float OriginY;

        public GiPlane(GiPlaneAxes axes, float originX, float originY)
        {
            Axes = axes;
            OriginX = originX;
            OriginY = originY;
        }

        public GiPlane(GiPlaneSpec spec)
            : this(spec.Axes, spec.OriginX, spec.OriginY)
        {
        }

        public void Project(float x, float y, float z, out float planeX, out float planeY)
        {
            planeX = x - OriginX;
            planeY = (Axes == GiPlaneAxes.Xz ? z : y) - OriginY;
        }

        public void EmbedPoint(float planeX, float planeY, out float x, out float y, out float z)
        {
            EmbedVector(planeX + OriginX, planeY + OriginY, out x, out y, out z);
        }

        public void EmbedVector(float planeX, float planeY, out float x, out float y, out float z)
        {
            x = planeX;
            if (Axes == GiPlaneAxes.Xz)
            {
                y = 0f;
                z = planeY;
            }
            else
            {
                y = planeY;
                z = 0f;
            }
        }

        public float Heading(float qx, float qy, float qz, float qw)
        {
            if (Axes == GiPlaneAxes.Xz)
            {
                var forwardX = 2f * (qx * qz + qw * qy);
                var forwardZ = 1f - 2f * (qx * qx + qy * qy);
                return MathF.Atan2(forwardZ, forwardX);
            }

            var rightX = 1f - 2f * (qy * qy + qz * qz);
            var rightY = 2f * (qx * qy + qw * qz);
            return MathF.Atan2(rightY, rightX);
        }
    }
}
