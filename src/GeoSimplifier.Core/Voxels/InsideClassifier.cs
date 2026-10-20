using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>Marks the cells whose centers lie inside the closed mesh (ray parity along +X).</summary>
    public static class InsideClassifier
    {
        public static VoxelGrid Classify(TriangleMesh mesh, VoxelGrid like)
        {
            var inside = like.CloneEmpty();
            double s = like.VoxelSize;
            var o = like.Origin;
            // Rays run through cell centers, nudged off the grid to avoid hitting mesh edges exactly.
            double jy = s * 1.234567e-4, jz = s * 2.345678e-4;
            var hits = new List<double>[like.Ny * like.Nz];

            foreach (var t in mesh.Triangles)
            {
                var b = t.Bounds;
                int j0 = Math.Max(0, (int)Math.Ceiling((b.Min.Y - o.Y - jy) / s - 0.5));
                int j1 = Math.Min(like.Ny - 1, (int)Math.Floor((b.Max.Y - o.Y - jy) / s - 0.5));
                int k0 = Math.Max(0, (int)Math.Ceiling((b.Min.Z - o.Z - jz) / s - 0.5));
                int k1 = Math.Min(like.Nz - 1, (int)Math.Floor((b.Max.Z - o.Z - jz) / s - 0.5));
                for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                {
                    double y = o.Y + (j + 0.5) * s + jy;
                    double z = o.Z + (k + 0.5) * s + jz;
                    if (IntersectX(t, y, z, out double x))
                    {
                        int row = j + like.Ny * k;
                        (hits[row] ?? (hits[row] = new List<double>())).Add(x);
                    }
                }
            }

            for (int k = 0; k < like.Nz; k++)
            for (int j = 0; j < like.Ny; j++)
            {
                var xs = hits[j + like.Ny * k];
                if (xs == null || xs.Count < 2)
                    continue;
                xs.Sort();
                for (int h = 0; h + 1 < xs.Count; h += 2)
                {
                    int i0 = Math.Max(0, (int)Math.Ceiling((xs[h] - o.X) / s - 0.5));
                    int i1 = Math.Min(like.Nx - 1, (int)Math.Floor((xs[h + 1] - o.X) / s - 0.5));
                    for (int i = i0; i <= i1; i++)
                        inside[i, j, k] = true;
                }
            }
            return inside;
        }

        /// <summary>Intersection of the line {y, z fixed} with the triangle, projected to the YZ plane.</summary>
        private static bool IntersectX(Triangle t, double y, double z, out double x)
        {
            x = 0;
            double d1 = Edge(t.A, t.B, y, z), d2 = Edge(t.B, t.C, y, z), d3 = Edge(t.C, t.A, y, z);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (neg && pos)
                return false;
            double area = d1 + d2 + d3;
            if (Math.Abs(area) < 1e-300)
                return false; // triangle parallel to the ray
            // Barycentric weights: d2 belongs to A, d3 to B, d1 to C.
            x = (d2 * t.A.X + d3 * t.B.X + d1 * t.C.X) / area;
            return true;
        }

        private static double Edge(Vec3 a, Vec3 b, double y, double z) =>
            (b.Y - a.Y) * (z - a.Z) - (b.Z - a.Z) * (y - a.Y);
    }
}
