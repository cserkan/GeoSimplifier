using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.Voxels;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class VoxelTests
    {
        [Fact]
        public void Triangle_box_overlap_detects_hit_and_miss()
        {
            var tri = new Triangle(new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(0, 1, 0));
            var half = new Vec3(0.5, 0.5, 0.5);

            Assert.True(TriangleBoxOverlap.Test(new Vec3(0, 0, 0.4), half, tri));
            Assert.False(TriangleBoxOverlap.Test(new Vec3(0, 0, 0.6), half, tri));
            Assert.False(TriangleBoxOverlap.Test(new Vec3(2, 2, 0), half, tri));
        }

        [Fact]
        public void Dilate_then_erode_restores_a_block()
        {
            var g = new VoxelGrid(12, 12, 12, new Vec3(0, 0, 0), 1);
            for (int k = 4; k < 8; k++)
            for (int j = 4; j < 8; j++)
            for (int i = 4; i < 8; i++)
                g[i, j, k] = true;

            var dilated = Morphology.Dilate(g, 2);
            Assert.Equal(8 * 8 * 8, dilated.Count());
            Assert.Equal(64, Morphology.Erode(dilated, 2).Count());
        }

        [Fact]
        public void Edge_contact_is_repaired()
        {
            var g = new VoxelGrid(4, 4, 4, new Vec3(0, 0, 0), 1);
            g[1, 1, 1] = true;
            g[2, 2, 1] = true;
            Assert.False(ManifoldFixer.IsManifold(g));

            Assert.True(ManifoldFixer.Fix(g) > 0);
            Assert.True(ManifoldFixer.IsManifold(g));
            Assert.Equal(1, Connectivity.CountComponents(g));
        }

        [Fact]
        public void Vertex_contact_is_repaired()
        {
            var g = new VoxelGrid(4, 4, 4, new Vec3(0, 0, 0), 1);
            g[1, 1, 1] = true;
            g[2, 2, 2] = true;

            ManifoldFixer.Fix(g);
            Assert.True(ManifoldFixer.IsManifold(g));
            Assert.Equal(1, Connectivity.CountComponents(g));
        }

        [Fact]
        public void Component_table_matches_simple_cases()
        {
            Assert.Equal(1, ManifoldFixer.Components(0b0000_0011));
            Assert.Equal(2, ManifoldFixer.Components(0b1000_0001));
            Assert.Equal(1, ManifoldFixer.Components(0xFF));
        }
    }
}
