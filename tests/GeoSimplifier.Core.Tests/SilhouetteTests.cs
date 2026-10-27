using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class SilhouetteTests
    {
        private static List<Vec3> Box(Vec3 min, Vec3 max)
        {
            Vec3 P(int i) => new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var t = new List<Vec3>();
            void Quad(int a, int b, int c, int d) => t.AddRange(new[] { P(a), P(b), P(c), P(a), P(c), P(d) });
            Quad(0, 1, 3, 2); Quad(4, 5, 7, 6); Quad(0, 1, 5, 4); Quad(2, 3, 7, 6); Quad(0, 2, 6, 4); Quad(1, 3, 7, 5);
            return t;
        }

        private static double Area(List<(double u, double v)> p) =>
            0.5 * Enumerable.Range(0, p.Count).Sum(i => p[i].u * p[(i + 1) % p.Count].v - p[(i + 1) % p.Count].u * p[i].v);

        [Fact]
        public void A_box_seen_along_z_is_one_rectangle()
        {
            var loops = Silhouette.Outlines(Box(new Vec3(0, 0, 0), new Vec3(2, 1, 0.5)), axis: 2, cellsAlongLongest: 100);
            Assert.Single(loops);
            Assert.Equal(4, loops[0].Count);
            // Counter-clockwise, about the box's 2 x 1 (in the plane, u is X and v is Y), grown by at most a cell.
            Assert.InRange(Area(loops[0]), 2.0, 2.0 * 1.1);
        }

        [Fact]
        public void An_l_shape_keeps_its_corner_and_a_hole_is_filled()
        {
            // An L of two boxes, and a frame (four bars round a hole) beside it, seen from above.
            var t = Box(new Vec3(0, 0, 0), new Vec3(3, 1, 1));
            t.AddRange(Box(new Vec3(0, 0, 0), new Vec3(1, 3, 1)));
            t.AddRange(Box(new Vec3(5, 0, 0), new Vec3(8, 0.5, 1)));
            t.AddRange(Box(new Vec3(5, 2.5, 0), new Vec3(8, 3, 1)));
            t.AddRange(Box(new Vec3(5, 0, 0), new Vec3(5.5, 3, 1)));
            t.AddRange(Box(new Vec3(7.5, 0, 0), new Vec3(8, 3, 1)));
            var loops = Silhouette.Outlines(t, axis: 2, cellsAlongLongest: 160);
            Assert.Equal(2, loops.Count);
            var l = loops.OrderBy(p => p.Min(q => q.u)).First();
            var frame = loops.OrderBy(p => p.Min(q => q.u)).Last();
            Assert.Equal(6, l.Count);
            Assert.InRange(Area(l), 5.0, 5.0 * 1.1);
            Assert.Equal(4, frame.Count);
            Assert.InRange(Area(frame), 9.0, 9.0 * 1.1);
        }

        [Fact]
        public void An_l_shape_as_rectangles_covers_its_area_with_two()
        {
            var t = Box(new Vec3(0, 0, 0), new Vec3(3, 1, 1));
            t.AddRange(Box(new Vec3(0, 0, 0), new Vec3(1, 3, 1)));
            var rects = Silhouette.Rectangles(t, axis: 2, cellsAlongLongest: 60);
            Assert.Equal(2, rects.Count);
            Assert.InRange(rects.Sum(r => (r.u1 - r.u0) * (r.v1 - r.v0)), 5.0, 5.0 * 1.15);
        }

        [Fact]
        public void A_v_shape_splits_into_two_convex_pieces_of_its_area()
        {
            // The end view of a V engine block: a base with two banks and the valley between them.
            var v = new List<(double, double)> { (0, 0), (4, 0), (4, 2), (3, 4), (2, 2.5), (1, 4), (0, 2) };
            var pieces = Silhouette.ConvexPieces(v);
            Assert.InRange(pieces.Count, 2, 3);
            Assert.Equal(Area(v), pieces.Sum(Area), 9);
            foreach (var p in pieces)
                for (int k = 0; k < p.Count; k++)
                {
                    var (ou, ov) = p[k]; var (au, av) = p[(k + 1) % p.Count]; var (bu, bv) = p[(k + 2) % p.Count];
                    Assert.True((au - ou) * (bv - ov) - (av - ov) * (bu - ou) >= -1e-12);
                }
        }

        [Fact]
        public void No_triangles_no_outline()
        {
            Assert.Empty(Silhouette.Outlines(new List<Vec3>(), 0));
        }
    }
}
