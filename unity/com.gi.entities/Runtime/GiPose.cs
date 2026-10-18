using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities
{
    public static class GiPose
    {
        public static void Read(in GiPlane plane, bool useMatrix, NativeArray<LocalTransform> transforms,
            NativeArray<LocalToWorld> matrices, int index, out float x, out float y, out float heading, out float scale)
        {
            if (!useMatrix)
            {
                var transform = transforms[index];
                plane.Project(transform.Position.x, transform.Position.y, transform.Position.z, out x, out y);
                var q = transform.Rotation.value;
                heading = plane.Heading(q.x, q.y, q.z, q.w);
                scale = transform.Scale;
                return;
            }

            if (!matrices.IsCreated)
            {
                x = 0f;
                y = 0f;
                heading = 0f;
                scale = 1f;
                return;
            }

            var matrix = matrices[index];
            plane.Project(matrix.Value.c3.x, matrix.Value.c3.y, matrix.Value.c3.z, out x, out y);
            var rotation = matrix.Rotation.value;
            heading = plane.Heading(rotation.x, rotation.y, rotation.z, rotation.w);
            scale = math.length(matrix.Value.c0.xyz);
        }
    }
}
