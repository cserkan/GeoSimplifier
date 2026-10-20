using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.ShrinkWrap;
using GeoSimplifier.Core.Voxels;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class FillTests
    {
        [Fact]
        public void Inside_classifier_marks_cell_centers_inside_a_cube()
        {
            var mesh = TestMeshes.Cube(10);
            var grid = Voxelizer.VoxelizeSurface(mesh, 0.8, 2, long.MaxValue);
            var inside = InsideClassifier.Classify(mesh, grid);

            // Grid origin is -2.2, so cell centers sit at 0.6, 1.4, ... 9.4 inside the cube: 12 per axis.
            Assert.Equal(12 * 12 * 12, inside.Count());
            Assert.True(At(inside, new Vec3(5, 5, 5)));
            Assert.False(At(inside, new Vec3(-0.5, 5, 5)));
        }

        [Fact]
        public void Solid_part_without_cavities_needs_no_fill()
        {
            var result = ShrinkWrapper.Run(TestMeshes.Cube(10), new ShrinkWrapOptions { VoxelSize = 0.5, GapClosingVoxels = 2 });
            Assert.Equal(0, result.Fill.Count());
        }

        [Fact]
        public void Closed_cavity_is_filled_even_without_gap_closing()
        {
            var mesh = TestMeshes.HollowBox(holeSize: 0);
            var result = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.5, GapClosingVoxels = 0 });

            Assert.True(At(result.Fill, new Vec3(5, 5, 5)));
            AssertWithin(result.Fill, new Vec3(0, 0, 0), new Vec3(10, 10, 10));
        }

        [Fact]
        public void Hole_is_plugged_only_when_closing_and_fill_never_leaves_the_envelope()
        {
            var mesh = TestMeshes.HollowBoxWithHole();

            var open = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingVoxels = 0 });
            Assert.False(At(open.Fill, new Vec3(5, 5, 5)));
            Assert.False(At(open.Fill, new Vec3(5, 5, 9.5)));

            var closed = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingVoxels = 6 });
            Assert.True(At(closed.Fill, new Vec3(5, 5, 5)));
            Assert.True(At(closed.Fill, new Vec3(5, 5, 9.2)));  // plug inside the hole
            Assert.True(At(closed.Fill, new Vec3(4.1, 5, 9.2))); // plug reaches the hole wall
            AssertWithin(closed.Fill, new Vec3(0, 0, 0), new Vec3(10, 10, 10));
        }

        private static bool At(VoxelGrid g, Vec3 p)
        {
            var c = p - g.Origin;
            return g[(int)System.Math.Floor(c.X / g.VoxelSize), (int)System.Math.Floor(c.Y / g.VoxelSize), (int)System.Math.Floor(c.Z / g.VoxelSize)];
        }

        private static void AssertWithin(VoxelGrid g, Vec3 min, Vec3 max)
        {
            for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                if (!g[i, j, k]) continue;
                var lo = g.CellMin(i, j, k);
                var hi = g.CellMin(i + 1, j + 1, k + 1);
                Assert.True(lo.X >= min.X - 1e-9 && lo.Y >= min.Y - 1e-9 && lo.Z >= min.Z - 1e-9, $"cell {lo} below {min}");
                Assert.True(hi.X <= max.X + 1e-9 && hi.Y <= max.Y + 1e-9 && hi.Z <= max.Z + 1e-9, $"cell {hi} above {max}");
            }
        }
    }
}
