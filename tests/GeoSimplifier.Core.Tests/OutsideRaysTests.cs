using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class OutsideRaysTests
    {
        private static List<Vec3> Cube(Vec3 min, Vec3 max)
        {
            Vec3 P(int i) => new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var t = new List<Vec3>();
            void Quad(int a, int b, int c, int d) => t.AddRange(new[] { P(a), P(b), P(c), P(a), P(c), P(d) });
            Quad(0, 1, 3, 2); Quad(4, 5, 7, 6); Quad(0, 1, 5, 4); Quad(2, 3, 7, 6); Quad(0, 2, 6, 4); Quad(1, 3, 7, 5);
            return t;
        }

        [Fact]
        public void A_body_inside_a_closed_box_is_not_seen()
        {
            var rays = new OutsideRays();
            rays.AddBody(0, Cube(new Vec3(0, 0, 0), new Vec3(10, 10, 10)));   // outer box (surface only)
            rays.AddBody(1, Cube(new Vec3(4, 4, 4), new Vec3(6, 6, 6)));      // inside it
            rays.AddBody(2, Cube(new Vec3(12, 4, 4), new Vec3(14, 6, 6)));    // beside it
            var seen = rays.Seen(directions: 32, spacing: 0.5);
            Assert.Contains(0, seen);
            Assert.DoesNotContain(1, seen);
            Assert.Contains(2, seen);
        }
    }
}
