using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class LoopMetricsTests
    {
        private static List<Vec3> Circle(Vec3 center, double radius, int n = 64) =>
            Enumerable.Range(0, n).Select(i =>
            {
                double a = 2 * Math.PI * i / n;
                return center + new Vec3(radius * Math.Cos(a), radius * Math.Sin(a), 0);
            }).ToList();

        [Fact]
        public void Round_hole_width_is_its_diameter()
        {
            var loop = Circle(new Vec3(5, 5, 6), 3.5);
            var n = LoopMetrics.Normal(loop);
            Assert.Equal(1, Math.Abs(n.Z), 9);
            Assert.InRange(LoopMetrics.MinWidth(loop, n), 6.98, 7.0);
        }

        [Fact]
        public void Rotated_slot_width_is_its_narrow_side()
        {
            // Oblong 60 x 12, rotated 30 degrees.
            var pts = new List<Vec3>();
            for (int i = 0; i <= 32; i++)
            {
                double a = -Math.PI / 2 + Math.PI * i / 32;
                pts.Add(new Vec3(24 + 6 * Math.Cos(a), 6 * Math.Sin(a), 0));
            }
            for (int i = 0; i <= 32; i++)
            {
                double a = Math.PI / 2 + Math.PI * i / 32;
                pts.Add(new Vec3(-24 + 6 * Math.Cos(a), 6 * Math.Sin(a), 0));
            }
            double r = Math.PI / 6;
            var rotated = pts.Select(p => new Vec3(p.X * Math.Cos(r) - p.Y * Math.Sin(r), p.X * Math.Sin(r) + p.Y * Math.Cos(r), 3)).ToList();

            Assert.Equal(12, LoopMetrics.MinWidth(rotated, LoopMetrics.Normal(rotated)), 1);
        }

        [Fact]
        public void Hole_below_the_face_is_a_depression_and_boss_above_is_not()
        {
            var loop = Circle(new Vec3(0, 0, 6), 3);
            var up = new Vec3(0, 0, 1);
            var hole = Circle(new Vec3(0, 0, 0), 3).Concat(loop);
            var boss = Circle(new Vec3(0, 0, 10), 3).Concat(loop);

            Assert.True(LoopMetrics.IsDepression(loop, up, hole));
            Assert.False(LoopMetrics.IsDepression(loop, up, boss));
        }
    }
}
