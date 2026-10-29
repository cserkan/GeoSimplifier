using System;
using System.Collections.Generic;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>Measurements on the boundary loop of a hole (sampled points along its edges).</summary>
    public static class LoopMetrics
    {
        /// <summary>Unit normal of the best-fit plane of a closed polygon (Newell's method). Zero if degenerate.</summary>
        public static Vec3 Normal(IReadOnlyList<Vec3> loop)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                x += (a.Y - b.Y) * (a.Z + b.Z);
                y += (a.Z - b.Z) * (a.X + b.X);
                z += (a.X - b.X) * (a.Y + b.Y);
            }
            return new Vec3(x, y, z).Normalized();
        }

        public static Vec3 Centroid(IReadOnlyList<Vec3> points)
        {
            var sum = new Vec3(0, 0, 0);
            foreach (var p in points)
                sum += p;
            return points.Count > 0 ? sum * (1.0 / points.Count) : sum;
        }

        /// <summary>
        /// Narrowest width of the loop measured in its own plane (minimum caliper width over directions
        /// perpendicular to <paramref name="normal"/>). For a round hole this is the diameter, for a slot its width.
        /// </summary>
        public static double MinWidth(IReadOnlyList<Vec3> loop, Vec3 normal, int directions = 90)
        {
            if (loop.Count < 2)
                return 0;
            var (u, v) = Basis(normal);
            double best = double.PositiveInfinity;
            for (int d = 0; d < directions; d++)
            {
                double angle = Math.PI * d / directions;
                var dir = u * Math.Cos(angle) + v * Math.Sin(angle);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (var p in loop)
                {
                    double t = Vec3.Dot(p, dir);
                    if (t < lo) lo = t;
                    if (t > hi) hi = t;
                }
                best = Math.Min(best, hi - lo);
            }
            return best;
        }

        /// <summary>
        /// True when the hole faces lie behind the loop plane, i.e. on the material side of the face that holds
        /// the loop: a hole or pocket rather than a boss standing on the face.
        /// </summary>
        /// <param name="outwardNormal">Normal of the face holding the loop, pointing out of the material.</param>
        public static bool IsDepression(IReadOnlyList<Vec3> loop, Vec3 outwardNormal, IEnumerable<Vec3> holePoints)
        {
            var c = Centroid(loop);
            double below = 0, above = 0;
            foreach (var p in holePoints)
            {
                double h = Vec3.Dot(p - c, outwardNormal);
                if (h < 0) below = Math.Max(below, -h);
                else above = Math.Max(above, h);
            }
            return below > above;
        }

        private static (Vec3, Vec3) Basis(Vec3 n)
        {
            var helper = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            var u = Vec3.Cross(n, helper).Normalized();
            var v = Vec3.Cross(n, u).Normalized();
            return (u, v);
        }
    }
}
