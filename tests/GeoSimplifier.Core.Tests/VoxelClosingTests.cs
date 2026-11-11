using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class VoxelClosingTests
    {
        private static void Quad(List<Vec3> t, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            t.AddRange(new[] { a, b, c, a, c, d });
        }

        private static void Cube(List<Vec3> t, Vec3 min, Vec3 max)
        {
            Vec3 P(int i) => new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            Quad(t, P(0), P(1), P(3), P(2)); Quad(t, P(4), P(5), P(7), P(6));
            Quad(t, P(0), P(1), P(5), P(4)); Quad(t, P(2), P(3), P(7), P(6));
            Quad(t, P(0), P(2), P(6), P(4)); Quad(t, P(1), P(3), P(7), P(5));
        }

        private static bool Covered(VoxelClosing.Result r, Vec3 p) =>
            r.Boxes.Any(b => p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z);

        [Fact]
        public void Open_cup_made_of_surfaces_is_filled()
        {
            // Five 100 mm walls, open at the top (like a fan cover open at one side).
            var t = new List<Vec3>();
            Quad(t, new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 0), new Vec3(0, 100, 0));
            Quad(t, new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 0, 100), new Vec3(0, 0, 100));
            Quad(t, new Vec3(0, 100, 0), new Vec3(100, 100, 0), new Vec3(100, 100, 100), new Vec3(0, 100, 100));
            Quad(t, new Vec3(0, 0, 0), new Vec3(0, 100, 0), new Vec3(0, 100, 100), new Vec3(0, 0, 100));
            Quad(t, new Vec3(100, 0, 0), new Vec3(100, 100, 0), new Vec3(100, 100, 100), new Vec3(100, 0, 100));

            var r = VoxelClosing.Fill(t, radius: 60, maxCellsPerAxis: 60);

            Assert.True(Covered(r, new Vec3(50, 50, 50)));
            Assert.True(Covered(r, new Vec3(50, 50, 90)));
            // Nothing above the opening, outside the cup.
            Assert.False(Covered(r, new Vec3(50, 50, 110)));
        }

        [Fact]
        public void Gap_narrower_than_the_limit_is_bridged_and_a_wider_one_is_not()
        {
            var t = new List<Vec3>();
            Cube(t, new Vec3(0, 0, 0), new Vec3(50, 50, 50));
            Cube(t, new Vec3(56, 0, 0), new Vec3(106, 50, 50));   // 6 mm gap
            Cube(t, new Vec3(140, 0, 0), new Vec3(190, 50, 50));  // 34 mm gap

            var r = VoxelClosing.Fill(t, radius: 10, maxCellsPerAxis: 200);

            Assert.True(Covered(r, new Vec3(53, 25, 25)));
            Assert.False(Covered(r, new Vec3(123, 25, 25)));
        }

        [Fact]
        public void Fill_stays_inside_a_plain_block()
        {
            var t = new List<Vec3>();
            Cube(t, new Vec3(0, 0, 0), new Vec3(80, 40, 20));

            var r = VoxelClosing.Fill(t, radius: 10, maxCellsPerAxis: 100);

            foreach (var b in r.Boxes)
            {
                Assert.InRange(b.Min.X, -1e-9, 80); Assert.InRange(b.Max.X, 0, 80 + 1e-9);
                Assert.InRange(b.Min.Y, -1e-9, 40); Assert.InRange(b.Max.Y, 0, 40 + 1e-9);
                Assert.InRange(b.Min.Z, -1e-9, 20); Assert.InRange(b.Max.Z, 0, 20 + 1e-9);
            }
        }
    }
}
