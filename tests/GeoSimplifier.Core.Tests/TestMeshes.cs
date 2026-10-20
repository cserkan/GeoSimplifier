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
        public static TriangleMesh HollowBoxWithHole() => HollowBox(holeSize: 2);

        /// <summary>A hollow box (outer 10, wall 1) with a square hole of the given size centered in the top wall.</summary>
        public static TriangleMesh HollowBox(double holeSize)
        {
            var mesh = new TriangleMesh();
            mesh.AddBox(new Vec3(0, 0, 0), new Vec3(10, 10, 1));   // bottom
            mesh.AddBox(new Vec3(0, 0, 1), new Vec3(1, 10, 9));    // -X wall
            mesh.AddBox(new Vec3(9, 0, 1), new Vec3(10, 10, 9));   // +X wall
            mesh.AddBox(new Vec3(1, 0, 1), new Vec3(9, 1, 9));     // -Y wall
            mesh.AddBox(new Vec3(1, 9, 1), new Vec3(9, 10, 9));    // +Y wall
            if (holeSize <= 0)
            {
                mesh.AddBox(new Vec3(0, 0, 9), new Vec3(10, 10, 10));
                return mesh;
            }
            double a = 5 - holeSize / 2, b = 5 + holeSize / 2;
            mesh.AddBox(new Vec3(0, 0, 9), new Vec3(a, 10, 10));
            mesh.AddBox(new Vec3(b, 0, 9), new Vec3(10, 10, 10));
            mesh.AddBox(new Vec3(a, 0, 9), new Vec3(b, a, 10));
            mesh.AddBox(new Vec3(a, b, 9), new Vec3(b, 10, 10));
            return mesh;
        }
    }
}
