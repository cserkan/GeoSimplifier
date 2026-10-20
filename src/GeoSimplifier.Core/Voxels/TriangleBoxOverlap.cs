using System;
using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>Separating-axis triangle/AABB overlap test (Akenine-Möller).</summary>
    public static class TriangleBoxOverlap
    {
        public static bool Test(Vec3 boxCenter, Vec3 boxHalf, Triangle tri)
        {
            var v0 = tri.A - boxCenter;
            var v1 = tri.B - boxCenter;
            var v2 = tri.C - boxCenter;
            var e0 = v1 - v0;
            var e1 = v2 - v1;
            var e2 = v0 - v2;

            // 9 cross-product axes.
            var edges = new[] { e0, e1, e2 };
            for (int a = 0; a < 3; a++)
            {
                var axisUnit = a == 0 ? new Vec3(1, 0, 0) : a == 1 ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
                foreach (var e in edges)
                {
                    var axis = Vec3.Cross(axisUnit, e);
                    if (Separated(axis, v0, v1, v2, boxHalf))
                        return false;
                }
            }

            // Box face normals.
            for (int a = 0; a < 3; a++)
            {
                double min = Math.Min(v0[a], Math.Min(v1[a], v2[a]));
                double max = Math.Max(v0[a], Math.Max(v1[a], v2[a]));
                if (min > boxHalf[a] || max < -boxHalf[a])
                    return false;
            }

            // Triangle plane.
            var normal = Vec3.Cross(e0, e1);
            return !Separated(normal, v0, v1, v2, boxHalf);
        }

        private static bool Separated(Vec3 axis, Vec3 v0, Vec3 v1, Vec3 v2, Vec3 half)
        {
            if (Math.Abs(axis.X) < 1e-30 && Math.Abs(axis.Y) < 1e-30 && Math.Abs(axis.Z) < 1e-30)
                return false;
            double p0 = Vec3.Dot(axis, v0);
            double p1 = Vec3.Dot(axis, v1);
            double p2 = Vec3.Dot(axis, v2);
            double r = half.X * Math.Abs(axis.X) + half.Y * Math.Abs(axis.Y) + half.Z * Math.Abs(axis.Z);
            double min = Math.Min(p0, Math.Min(p1, p2));
            double max = Math.Max(p0, Math.Max(p1, p2));
            return min > r || max < -r;
        }
    }
}
