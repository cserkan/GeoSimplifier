using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.ShrinkWrap;
using GeoSimplifier.Core.Voxels;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class ShrinkWrapperTests
    {
        [Fact]
        public void Cube_becomes_single_solid_block()
        {
            var result = ShrinkWrapper.Run(TestMeshes.Cube(10), new ShrinkWrapOptions { VoxelSize = 1, GapClosingVoxels = 0 });

            Assert.Equal(1, result.BodyCount);
            // Surface cells of a 10-unit cube on a unit grid span 11 cells per side (both boundary layers).
            Assert.Equal(11 * 11 * 11, result.Solid.Count());
            Assert.Single(result.Boxes);
        }

        [Fact]
        public void Internal_cavity_is_filled_even_with_a_hole_when_gap_closing_bridges_it()
        {
            var mesh = TestMeshes.HollowBoxWithHole();
            var result = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.5, GapClosingVoxels = 3 });

            Assert.Equal(1, result.BodyCount);
            AssertCenterFilled(result.Solid, new Vec3(5, 5, 5));
        }

        [Fact]
        public void Cavity_stays_open_without_gap_closing()
        {
            var mesh = TestMeshes.HollowBoxWithHole();
            var result = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.5, GapClosingVoxels = 0 });

            var g = result.Solid;
            var c = new Vec3(5, 5, 5) - g.Origin;
            Assert.False(g[(int)(c.X / g.VoxelSize), (int)(c.Y / g.VoxelSize), (int)(c.Z / g.VoxelSize)]);
        }

        [Fact]
        public void Nearby_bodies_are_merged_into_one()
        {
            var mesh = new TriangleMesh();
            mesh.AddBox(new Vec3(0, 0, 0), new Vec3(5, 5, 5));
            mesh.AddBox(new Vec3(6, 0, 0), new Vec3(11, 5, 5)); // 1 unit gap

            var separate = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingVoxels = 0 });
            var merged = ShrinkWrapper.Run(mesh, new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingVoxels = 3 });

            Assert.Equal(2, separate.BodyCount);
            Assert.Equal(1, merged.BodyCount);
        }

        [Fact]
        public void Result_encloses_every_surface_voxel_and_boxes_cover_solid_exactly()
        {
            var mesh = TestMeshes.HollowBoxWithHole();
            var options = new ShrinkWrapOptions { Resolution = 40, GapClosingVoxels = 2 };
            var result = ShrinkWrapper.Run(mesh, options);
            var g = result.Solid;

            var shell = Voxelizer.VoxelizeSurface(mesh, result.VoxelSize, options.GapClosingVoxels + 2, options.MaxCells);
            for (int n = 0; n < g.Length; n++)
                if (shell[n]) Assert.True(g[n]);

            var cover = new int[g.Length];
            foreach (var b in result.Boxes)
                for (int k = b.K0; k < b.K1; k++)
                for (int j = b.J0; j < b.J1; j++)
                for (int i = b.I0; i < b.I1; i++)
                    cover[g.Index(i, j, k)]++;
            for (int n = 0; n < g.Length; n++)
                Assert.Equal(g[n] ? 1 : 0, cover[n]);

            Assert.True(ManifoldFixer.IsManifold(g));
        }

        [Fact]
        public void Gap_width_is_converted_to_a_closing_radius()
        {
            Assert.Equal(3, ShrinkWrapper.ResolveGapVoxels(0.5, new ShrinkWrapOptions { GapClosingWidth = 2.6 }));
            Assert.Equal(0, ShrinkWrapper.ResolveGapVoxels(0.5, new ShrinkWrapOptions { GapClosingWidth = 0, GapClosingVoxels = 4 }));
        }

        private static void AssertCenterFilled(VoxelGrid g, Vec3 p)
        {
            var c = p - g.Origin;
            Assert.True(g[(int)(c.X / g.VoxelSize), (int)(c.Y / g.VoxelSize), (int)(c.Z / g.VoxelSize)]);
        }
    }
}
