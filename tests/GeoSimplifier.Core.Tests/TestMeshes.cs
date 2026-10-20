using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Tests
{
    internal static class TestMeshes
    {
        public static TriangleMesh Cube(double size)
        {
            var mesh = new TriangleMesh();
            mesh.AddBox(new Vec3(0, 0, 0), new Vec3(size, size, size));
            return mesh;
        }

        /// <summary>A hollow box (outer 10, wall 1) with a 2x2 hole in the top wall.</summary>
        public static TriangleMesh HollowBoxWithHole()
        {
            var mesh = new TriangleMesh();
            mesh.AddBox(new Vec3(0, 0, 0), new Vec3(10, 10, 1));   // bottom
            mesh.AddBox(new Vec3(0, 0, 1), new Vec3(1, 10, 9));    // -X wall
            mesh.AddBox(new Vec3(9, 0, 1), new Vec3(10, 10, 9));   // +X wall
            mesh.AddBox(new Vec3(1, 0, 1), new Vec3(9, 1, 9));     // -Y wall
            mesh.AddBox(new Vec3(1, 9, 1), new Vec3(9, 10, 9));    // +Y wall
            // top with a 2x2 hole at x,y in [4,6]
            mesh.AddBox(new Vec3(0, 0, 9), new Vec3(4, 10, 10));
            mesh.AddBox(new Vec3(6, 0, 9), new Vec3(10, 10, 10));
            mesh.AddBox(new Vec3(4, 0, 9), new Vec3(6, 4, 10));
            mesh.AddBox(new Vec3(4, 6, 9), new Vec3(6, 10, 10));
            return mesh;
        }
    }
}
