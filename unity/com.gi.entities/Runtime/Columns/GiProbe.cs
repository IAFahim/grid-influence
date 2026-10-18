using GiWorld = Gi.World;

namespace Gi.Entities
{
    public enum GiProbeKind : byte
    {
        Value,
        Gradient,
        Area,
        Max,
        Nearest,
    }

    public struct GiProbeRequest
    {
        public int Id;
        public GiProbeKind Kind;
        public byte World;
        public byte Layer;
        public float X;
        public float Y;
        public float Reach;
        public short Threshold;
    }

    public struct GiProbeResult
    {
        public int Id;
        public GiProbeKind Kind;
        public bool Ok;
        public int Tick;
        public short Value;
        public float GradientX;
        public float GradientY;
        public long Total;
        public float PositionX;
        public float PositionY;
    }

    public static class GiProbe
    {
        public static GiProbeResult Run(in GiProbeRequest request)
        {
            var result = new GiProbeResult { Id = request.Id, Kind = request.Kind, Tick = GiWorld.Tick(request.World) };
            switch (request.Kind)
            {
                case GiProbeKind.Value:
                    result.Ok = GiWorld.TrySense(request.World, request.Layer, request.X, request.Y, out result.Value);
                    break;
                case GiProbeKind.Gradient:
                    result.Ok = GiWorld.TrySenseGradient(request.World, request.Layer, request.X, request.Y, out result.GradientX, out result.GradientY);
                    break;
                case GiProbeKind.Area:
                    result.Ok = GiWorld.TrySenseArea(request.World, request.Layer, request.X, request.Y, request.Reach, out result.Total);
                    break;
                case GiProbeKind.Max:
                    result.Ok = GiWorld.TrySenseMax(request.World, request.Layer, request.X, request.Y, request.Reach, out result.Value,
                        out result.PositionX, out result.PositionY);
                    break;
                default:
                    GiWorld.TrySenseNearest(request.World, request.Layer, request.X, request.Y, request.Reach, request.Threshold, out result.Value,
                        out result.PositionX, out result.PositionY);
                    result.Ok = result.Value != short.MinValue;
                    break;
            }

            return result;
        }
    }
}
