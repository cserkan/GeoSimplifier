using System.Linq;
using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.ShrinkWrap;
using GeoSimplifier.Core.Voxels;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class HoleTests
    {
        /// <summary>Plate 20x20x4 with a 2x2 through hole and a 6x6 counterbore 2 deep from the top, plus a 3x12 slot.</summary>
        private static TriangleMesh PlateWithHoles()
        {
            var mesh = new TriangleMesh();
            // Bottom half (z 0..2): everything except the 2x2 hole at x,y 4..6 and the slot at x 12..15, y 4..16.
            mesh.AddBox(new Vec3(0, 0, 0), new Vec3(4, 20, 2));
            mesh.AddBox(new Vec3(6, 0, 0), new Vec3(12, 20, 2));
            mesh.AddBox(new Vec3(4, 0, 0), new Vec3(6, 4, 2));
            mesh.AddBox(new Vec3(4, 6, 0), new Vec3(6, 20, 2));
            // Top half (z 2..4): counterbore 6x6 at x,y 2..8.
            mesh.AddBox(new Vec3(0, 0, 2), new Vec3(2, 20, 4));
            mesh.AddBox(new Vec3(8, 0, 2), new Vec3(12, 20, 4));
            mesh.AddBox(new Vec3(2, 0, 2), new Vec3(8, 2, 4));
            mesh.AddBox(new Vec3(2, 8, 2), new Vec3(8, 20, 4));
            // Right of the slot, full height.
            mesh.AddBox(new Vec3(15, 0, 0), new Vec3(20, 20, 4));
            mesh.AddBox(new Vec3(12, 0, 0), new Vec3(15, 4, 4));
            mesh.AddBox(new Vec3(12, 16, 0), new Vec3(15, 20, 4));
            return mesh;
        }

        [Fact]
        public void Countersunk_hole_is_closed_completely_when_its_throat_fits()
        {
            var result = ShrinkWrapper.Run(PlateWithHoles(), new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingWidth = 2.5 });

            Assert.True(At(result.Solid, new Vec3(5, 5, 1)));    // through hole
            Assert.True(At(result.Solid, new Vec3(3, 3, 3.5)));  // counterbore, up to the top face
            Assert.False(At(result.Solid, new Vec3(5, 5, 4.6))); // nothing above the plate
            Assert.False(At(result.Solid, new Vec3(13.5, 10, 2))); // the 3 wide slot stays open
        }

        [Fact]
        public void Plug_boxes_end_flush_with_the_plate_faces()
        {
            var result = ShrinkWrapper.Run(PlateWithHoles(), new ShrinkWrapOptions { VoxelSize = 0.3, GapClosingWidth = 2.5 });

            var hole = Assert.Single(result.Holes);
            Assert.Equal(2, hole.Axis);
            Assert.Equal(4.0, result.ExactFillBoxes.Max(b => b.Max.Z), 9);
            Assert.Equal(0.0, result.ExactFillBoxes.Min(b => b.Min.Z), 9);
        }

        [Fact]
        public void Slot_is_closed_when_wide_enough_and_nothing_is_closed_with_zero_width()
        {
            var wide = ShrinkWrapper.Run(PlateWithHoles(), new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingWidth = 3.5 });
            Assert.True(At(wide.Solid, new Vec3(13.5, 10, 2)));
            Assert.Equal(2, wide.Holes.Count);

            var none = ShrinkWrapper.Run(PlateWithHoles(), new ShrinkWrapOptions { VoxelSize = 0.25, GapClosingWidth = 0 });
            Assert.Empty(none.Holes);
            Assert.Empty(none.ExactFillBoxes);
            Assert.False(At(none.Solid, new Vec3(5, 5, 1)));
        }

        [Fact]
        public void Closed_cavity_is_filled_without_any_hole_closing()
        {
            var result = ShrinkWrapper.Run(TestMeshes.HollowBox(holeSize: 0), new ShrinkWrapOptions { VoxelSize = 0.5, GapClosingWidth = 0 });
            Assert.True(At(result.Solid, new Vec3(5, 5, 5)));
        }

        private static bool At(VoxelGrid g, Vec3 p)
        {
            var c = p - g.Origin;
            return g[(int)System.Math.Floor(c.X / g.VoxelSize), (int)System.Math.Floor(c.Y / g.VoxelSize), (int)System.Math.Floor(c.Z / g.VoxelSize)];
        }
    }
}
