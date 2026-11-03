using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>
    /// Which faces of a model can be seen from outside: rays leave a few points of each face, along the face normal
    /// and in eight directions 60 degrees around it; a face from which any ray escapes without hitting the model is
    /// visible. Works on the tessellation, with a bounding volume hierarchy, on all processor cores.
    /// </summary>
    public sealed class FaceVisibility
    {
        // The corners in single precision, as SOLIDWORKS gives them: half the memory of doubles, with the same
        // values (TAG3's skin at 5 % ran out of memory with doubles).
        private readonly List<Vec3f> _a = new List<Vec3f>(), _b = new List<Vec3f>(), _c = new List<Vec3f>();
        private readonly List<int> _face = new List<int>();
        private readonly HashSet<int> _unsampled = new HashSet<int>();
        private readonly List<(Vec3 point, Vec3 normal, int face)> _samples = new List<(Vec3, Vec3, int)>();
        private Node[] _nodes;
        private int[] _order;

        private struct Vec3f
        {
            public readonly float X, Y, Z;
            public Vec3f(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static implicit operator Vec3(Vec3f v) => new Vec3(v.X, v.Y, v.Z);
        }

        private struct Node
        {
            public Aabb Box;
            public int Left, Right;   // children, or -1 for a leaf
            public int First, Count;  // triangles of a leaf, in _order
        }

        /// <summary>Adds the triangles of one face and points on it to send rays from.</summary>
        /// <param name="triangles">Nine numbers per triangle.</param>
        /// <param name="normals">Nine numbers per triangle: the normal at each corner, pointing out of the model.</param>
        /// <param name="samples">How many of the face's triangles rays are sent from (spread over the face).</param>
        /// <param name="twoSided">Rays leave on both sides (a surface body has no inside).</param>
        /// <param name="spacing">With a positive spacing, large triangles get more points, about one per spacing
        /// squared of their area (a 1.5 m² radiator panel of two triangles had two points, both behind a fan hub).</param>
        public void AddFace(int face, IReadOnlyList<float> triangles, IReadOnlyList<float> normals, int samples, bool twoSided = false,
                            double spacing = 0)
        {
            int count = triangles.Count / 9;
            for (int t = 0; t < count; t++)
            {
                _a.Add(new Vec3f(triangles[9 * t], triangles[9 * t + 1], triangles[9 * t + 2]));
                _b.Add(new Vec3f(triangles[9 * t + 3], triangles[9 * t + 4], triangles[9 * t + 5]));
                _c.Add(new Vec3f(triangles[9 * t + 6], triangles[9 * t + 7], triangles[9 * t + 8]));
                _face.Add(face);
            }
            if (normals == null || normals.Count < 9 * count)
            {
                // Without normals the direction of "out" is unknown: the face is kept as visible.
                _unsampled.Add(face);
                return;
            }
            if (count == 0)
                return;
            int step = Math.Max(1, count / Math.Max(1, samples));
            for (int t = 0, n = 0; t < count && n < samples; t += step, n++)
            {
                int i = _a.Count - count + t;
                var centre = ((Vec3)_a[i] + _b[i] + _c[i]) * (1.0 / 3);
                var normal = new Vec3(normals[9 * t] + normals[9 * t + 3] + normals[9 * t + 6],
                                      normals[9 * t + 1] + normals[9 * t + 4] + normals[9 * t + 7],
                                      normals[9 * t + 2] + normals[9 * t + 5] + normals[9 * t + 8]);
                if (normal.Length > 1e-12)
                {
                    _samples.Add((centre, normal * (1 / normal.Length), face));
                    if (twoSided)
                        _samples.Add((centre, normal * (-1 / normal.Length), face));
                }
            }
            if (spacing <= 0)
                return;
            // Large triangles: points on a grid over them as well (centres of the n x n smaller triangles pointing
            // the same way, n up to 8).
            for (int t = 0; t < count; t++)
            {
                int i = _a.Count - count + t;
                Vec3 a = _a[i], b = _b[i], c = _c[i];
                double area = Cross(b - a, c - a).Length / 2;
                int n = Math.Min(8, (int)Math.Ceiling(Math.Sqrt(area) / spacing));
                if (n < 2)
                    continue;
                var normal = new Vec3(normals[9 * t] + normals[9 * t + 3] + normals[9 * t + 6],
                                      normals[9 * t + 1] + normals[9 * t + 4] + normals[9 * t + 7],
                                      normals[9 * t + 2] + normals[9 * t + 5] + normals[9 * t + 8]);
                if (normal.Length <= 1e-12)
                    continue;
                normal = normal * (1 / normal.Length);
                for (int u = 0; u < n; u++)
                    for (int v = 0; u + v < n; v++)
                    {
                        var p = a + (b - a) * ((u + 1.0 / 3) / n) + (c - a) * ((v + 1.0 / 3) / n);
                        _samples.Add((p, normal, face));
                        if (twoSided)
                            _samples.Add((p, normal * -1, face));
                    }
            }
        }

        /// <summary>The faces from which some ray escapes.</summary>
        /// <param name="offset">How far off the face the rays start (model units), so they do not hit their own face.</param>
        public HashSet<int> Visible(double offset)
        {
            var visible = new HashSet<int>(_unsampled);
            foreach (var kv in Escapes(offset))
                if (kv.Value.escaped > 0)
                    visible.Add(kv.Key);
            return visible;
        }

        /// <summary>
        /// For each sampled face, how many of its sample points some ray escapes from, and how many points it has
        /// (faces without normals are not listed; they count as visible).
        /// </summary>
        public Dictionary<int, (int escaped, int samples)> Escapes(double offset)
        {
            Build();
            var escaped = new bool[_samples.Count];
            Parallel.For(0, _samples.Count, s =>
            {
                var (p, n, _) = _samples[s];
                var start = p + n * offset;
                var stack = new int[128];
                foreach (var dir in Directions(n))
                    if (!Blocked(start, dir, stack))
                    {
                        escaped[s] = true;
                        return;
                    }
            });
            var counts = new Dictionary<int, (int escaped, int samples)>();
            for (int s = 0; s < _samples.Count; s++)
            {
                int face = _samples[s].face;
                counts.TryGetValue(face, out var c);
                counts[face] = (c.escaped + (escaped[s] ? 1 : 0), c.samples + 1);
            }
            return counts;
        }

        /// <summary>
        /// The face a ray from <paramref name="origin"/> along <paramref name="dir"/> hits first, and how far away;
        /// (-1, infinity) when it escapes. For tracing why a face counts as hidden.
        /// </summary>
        public (int face, double distance) FirstHit(Vec3 origin, Vec3 dir)
        {
            Build();
            var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
            var stack = new int[128];
            int top = 0, best = -1;
            double bestT = double.PositiveInfinity;
            stack[top++] = 0;
            while (top > 0)
            {
                var node = _nodes[stack[--top]];
                if (!HitsBox(node.Box, origin, inv))
                    continue;
                if (node.Left < 0)
                {
                    for (int i = node.First; i < node.First + node.Count; i++)
                    {
                        double t = HitDistance(origin, dir, _sa[i], _sb[i], _sc[i]);
                        if (t < bestT) { bestT = t; best = _face[_order[i]]; }
                    }
                    continue;
                }
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
            return (best, bestT);
        }

        private static double HitDistance(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var p = Cross(d, e2);
            double det = Dot(e1, p);
            if (Math.Abs(det) < 1e-18) return double.PositiveInfinity;
            double inv = 1 / det;
            var s = o - a;
            double u = Dot(s, p) * inv;
            if (u < 0 || u > 1) return double.PositiveInfinity;
            var q = Cross(s, e1);
            double v = Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return double.PositiveInfinity;
            double t = Dot(e2, q) * inv;
            return t > 1e-9 ? t : double.PositiveInfinity;
        }

        /// <summary>Faces added without normals; they are always counted as visible.</summary>
        public IReadOnlyCollection<int> Unsampled => _unsampled;

        public int Triangles => _nodes != null ? _triangles : _a.Count;
        public int Samples => _samples.Count;

        private static IEnumerable<Vec3> Directions(Vec3 n)
        {
            yield return n;
            var u = Math.Abs(n.X) < 0.9 ? new Vec3(0, -n.Z, n.Y) : new Vec3(n.Z, 0, -n.X);
            u = u * (1 / u.Length);
            var v = new Vec3(n.Y * u.Z - n.Z * u.Y, n.Z * u.X - n.X * u.Z, n.X * u.Y - n.Y * u.X);
            double s = Math.Sin(Math.PI / 3), c = Math.Cos(Math.PI / 3);
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                yield return n * c + (u * Math.Cos(a) + v * Math.Sin(a)) * s;
            }
        }

        private void Build()
        {
            if (_nodes != null)
                return;
            int count = _a.Count;
            _order = new int[count];
            // Only the centres are kept per triangle while the tree is built; boxes are worked out from the corners.
            var centres = new Vec3f[count];
            for (int i = 0; i < count; i++)
            {
                _order[i] = i;
                var centre = ((Vec3)_a[i] + _b[i] + _c[i]) * (1.0 / 3);
                centres[i] = new Vec3f((float)centre.X, (float)centre.Y, (float)centre.Z);
            }
            var nodes = new List<Node>();
            Split(nodes, centres, 0, count);
            _nodes = nodes.ToArray();
            centres = null;
            // The triangles in tree order, side by side in memory for the leaves; the lists they came in go.
            _sa = new Vec3f[count];
            _sb = new Vec3f[count];
            _sc = new Vec3f[count];
            for (int i = 0; i < count; i++)
                _sa[i] = _a[_order[i]];
            _a.Clear();
            _a.TrimExcess();
            for (int i = 0; i < count; i++)
                _sb[i] = _b[_order[i]];
            _b.Clear();
            _b.TrimExcess();
            for (int i = 0; i < count; i++)
                _sc[i] = _c[_order[i]];
            _c.Clear();
            _c.TrimExcess();
            _triangles = count;
        }

        private Vec3f[] _sa, _sb, _sc;
        private int _triangles;

        private int Split(List<Node> nodes, Vec3f[] centres, int first, int count)
        {
            var box = Aabb.Empty;
            for (int i = first; i < first + count; i++)
            {
                int t = _order[i];
                box = box.Include(_a[t]).Include(_b[t]).Include(_c[t]);
            }
            int index = nodes.Count;
            nodes.Add(new Node { Box = box, Left = -1, Right = -1, First = first, Count = count });
            if (count <= 8)
                return index;
            var size = box.Size;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            // Only the median is needed, not a full sort (sorting at every level took most of a minute on V12's
            // 6.6 million triangles).
            int half = count / 2;
            Select(centres, axis, first, first + count - 1, first + half);
            int left = Split(nodes, centres, first, half);
            int right = Split(nodes, centres, first + half, count - half);
            nodes[index] = new Node { Box = box, Left = left, Right = right, First = first, Count = count };
            return index;
        }

        /// <summary>
        /// Reorders _order[lo..hi] so that position k holds the triangle that would be there if sorted by its
        /// centre along the axis, with none greater before it and none smaller after it (quickselect).
        /// </summary>
        private void Select(Vec3f[] centres, int axis, int lo, int hi, int k)
        {
            float At(int t) => axis == 0 ? centres[t].X : axis == 1 ? centres[t].Y : centres[t].Z;
            while (hi > lo)
            {
                int mid = lo + (hi - lo) / 2;
                float a = At(_order[lo]), b = At(_order[mid]), c = At(_order[hi]);
                float pivot = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (At(_order[i]) < pivot) i++;
                    while (At(_order[j]) > pivot) j--;
                    if (i <= j)
                    {
                        (_order[i], _order[j]) = (_order[j], _order[i]);
                        i++;
                        j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return;
            }
        }

        private bool Blocked(Vec3 origin, Vec3 dir, int[] stack)
        {
            // A plain array as the stack (a Stack<int> per ray was millions of allocations on V12); the tree is
            // balanced, so its depth stays far below the size.
            int top = 0;
            stack[top++] = 0;
            var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
            while (top > 0)
            {
                var node = _nodes[stack[--top]];
                if (!HitsBox(node.Box, origin, inv))
                    continue;
                if (node.Left < 0)
                {
                    for (int i = node.First; i < node.First + node.Count; i++)
                        if (HitsTriangle(origin, dir, _sa[i], _sb[i], _sc[i]))
                            return true;
                    continue;
                }
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
            return false;
        }

        private static bool HitsBox(Aabb box, Vec3 origin, Vec3 inv)
        {
            double t0 = 0, t1 = double.PositiveInfinity;
            for (int a = 0; a < 3; a++)
            {
                double near = (box.Min[a] - origin[a]) * inv[a], far = (box.Max[a] - origin[a]) * inv[a];
                if (near > far) (near, far) = (far, near);
                t0 = Math.Max(t0, near);
                t1 = Math.Min(t1, far);
                if (t0 > t1) return false;
            }
            return true;
        }

        // MÃ¶ller-Trumbore, hits in front of the origin only.
        private static bool HitsTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var p = Cross(d, e2);
            double det = Dot(e1, p);
            if (Math.Abs(det) < 1e-18) return false;
            double inv = 1 / det;
            var s = o - a;
            double u = Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            var q = Cross(s, e1);
            double v = Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            return Dot(e2, q) * inv > 1e-9;
        }

        private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
}
