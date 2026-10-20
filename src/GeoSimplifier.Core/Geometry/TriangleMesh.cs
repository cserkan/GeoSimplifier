using System.Collections.Generic;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>An unindexed triangle soup. Coordinates are in model units (SolidWorks API: meters).</summary>
    public sealed class TriangleMesh
    {
        private readonly List<Triangle> _triangles = new List<Triangle>();

        public IReadOnlyList<Triangle> Triangles => _triangles;

        public Aabb Bounds { get; private set; } = Aabb.Empty;

        public void Add(Triangle t)
        {
            _triangles.Add(t);
            Bounds = Bounds.Union(t.Bounds);
        }

        public void Add(Vec3 a, Vec3 b, Vec3 c) => Add(new Triangle(a, b, c));

        /// <summary>
        /// Adds triangles from a flat array of 9 values per triangle (x0,y0,z0,x1,y1,z1,x2,y2,z2),
        /// the layout returned by SolidWorks IFace2.GetTessTriangles.
        /// </summary>
        public void AddFlat(float[] data)
        {
            for (int i = 0; i + 8 < data.Length; i += 9)
            {
                Add(new Vec3(data[i], data[i + 1], data[i + 2]),
                    new Vec3(data[i + 3], data[i + 4], data[i + 5]),
                    new Vec3(data[i + 6], data[i + 7], data[i + 8]));
            }
        }

        /// <summary>Adds the 12 triangles of an axis-aligned box. Handy for tests and previews.</summary>
        public void AddBox(Vec3 min, Vec3 max)
        {
            var p = new Vec3[8];
            for (int i = 0; i < 8; i++)
            {
                p[i] = new Vec3((i & 1) == 0 ? min.X : max.X,
                                (i & 2) == 0 ? min.Y : max.Y,
                                (i & 4) == 0 ? min.Z : max.Z);
            }

            int[,] quads =
            {
                { 0, 2, 3, 1 }, { 4, 5, 7, 6 }, // -Z, +Z
                { 0, 1, 5, 4 }, { 2, 6, 7, 3 }, // -Y, +Y
                { 0, 4, 6, 2 }, { 1, 3, 7, 5 }, // -X, +X
            };
            for (int q = 0; q < 6; q++)
            {
                Add(p[quads[q, 0]], p[quads[q, 1]], p[quads[q, 2]]);
                Add(p[quads[q, 0]], p[quads[q, 2]], p[quads[q, 3]]);
            }
        }
    }
}
