using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class FaceVisibilityTests
    {
        // A square face as two triangles, with its outward normal at every corner.
        private static void Square(FaceVisibility v, int face, Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 n)
        {
            var t = new List<float>();
            var normals = new List<float>();
            foreach (var p in new[] { a, b, c, a, c, d })
            {
                t.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
                normals.AddRange(new[] { (float)n.X, (float)n.Y, (float)n.Z });
            }
            v.AddFace(face, t, normals, 2);
        }

        private static void Cube(FaceVisibility v, int firstFace, double x0, double x1)
        {
            Vec3 P(double x, double y, double z) => new Vec3(x, y, z);
            Square(v, firstFace, P(x0, 0, 0), P(x0, 1, 0), P(x0, 1, 1), P(x0, 0, 1), new Vec3(-1, 0, 0));
            Square(v, firstFace + 1, P(x1, 0, 0), P(x1, 1, 0), P(x1, 1, 1), P(x1, 0, 1), new Vec3(1, 0, 0));
            Square(v, firstFace + 2, P(x0, 0, 0), P(x1, 0, 0), P(x1, 0, 1), P(x0, 0, 1), new Vec3(0, -1, 0));
            Square(v, firstFace + 3, P(x0, 1, 0), P(x1, 1, 0), P(x1, 1, 1), P(x0, 1, 1), new Vec3(0, 1, 0));
            Square(v, firstFace + 4, P(x0, 0, 0), P(x1, 0, 0), P(x1, 1, 0), P(x0, 1, 0), new Vec3(0, 0, -1));
            Square(v, firstFace + 5, P(x0, 0, 1), P(x1, 0, 1), P(x1, 1, 1), P(x0, 1, 1), new Vec3(0, 0, 1));
        }

        [Fact]
        public void Every_face_of_a_lone_cube_is_visible()
        {
            var v = new FaceVisibility();
            Cube(v, 0, 0, 1);
            Assert.Equal(6, v.Visible(1e-6).Count);
        }

        [Fact]
        public void Faces_pressed_against_each_other_are_hidden()
        {
            // Two cubes 0.001 apart: the faces that look at each other see only the other cube.
            var v = new FaceVisibility();
            Cube(v, 0, 0, 1);
            Cube(v, 6, 1.001, 2.001);
            var visible = v.Visible(1e-6);
            Assert.DoesNotContain(1, visible);   // first cube's +X face
            Assert.DoesNotContain(6, visible);   // second cube's -X face
            Assert.Equal(10, visible.Count);
        }
    }
}
