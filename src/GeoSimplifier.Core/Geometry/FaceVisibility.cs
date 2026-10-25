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
        private readonly List<Vec3> _a = new List<Vec3>(), _b = new List<Vec3>(), _c = new List<Vec3>();
        private readonly List<int> _face = new List<int>();
        private readonly HashSet<int> _unsampled = new HashSet<int>();
        private readonly List<(Vec3 point, Vec3 normal, int face)> _samples = new List<(Vec3, Vec3, int)>();
        private Node[] _nodes;
        private int[] _order;

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
        public void AddFace(int face, IReadOnlyList<float> triangles, IReadOnlyList<float> normals, int samples, bool twoSided = false)
        {
            int count = triangles.Count / 9;
            for (int t = 0; t < count; t++)
            {
                _a.Add(new Vec3(triangles[9 * t], triangles[9 * t + 1], triangles[9 * t + 2]));
                _b.Add(new Vec3(triangles[9 * t + 3], triangles[9 * t + 4], triangles[9 * t + 5]));
                _c.Add(new Vec3(triangles[9 * t + 6], triangles[9 * t + 7], triangles[9 * t + 8]));
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
                var centre = (_a[i] + _b[i] + _c[i]) * (1.0 / 3);
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
                foreach (var dir in Directions(n))
                    if (!Blocked(start, dir))
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

        /// <summary>Faces added without normals; they are always counted as visible.</summary>
        public IReadOnlyCollection<int> Unsampled => _unsampled;

        public int Triangles => _a.Count;
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
            var centres = new Vec3[count];
            var boxes = new Aabb[count];
            for (int i = 0; i < count; i++)
            {
                _order[i] = i;
                boxes[i] = Aabb.Empty.Include(_a[i]).Include(_b[i]).Include(_c[i]);
                centres[i] = (_a[i] + _b[i] + _c[i]) * (1.0 / 3);
            }
            var nodes = new List<Node>();
            Split(nodes, boxes, centres, 0, count);
            _nodes = nodes.ToArray();
        }

        private int Split(List<Node> nodes, Aabb[] boxes, Vec3[] centres, int first, int count)
        {
            var box = Aabb.Empty;
            for (int i = first; i < first + count; i++)
                box = box.Union(boxes[_order[i]]);
            int index = nodes.Count;
            nodes.Add(new Node { Box = box, Left = -1, Right = -1, First = first, Count = count });
            if (count <= 8)
                return index;
            var size = box.Size;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            Array.Sort(_order, first, count, Comparer<int>.Create((p, q) => centres[p][axis].CompareTo(centres[q][axis])));
            int half = count / 2;
            int left = Split(nodes, boxes, centres, first, half);
            int right = Split(nodes, boxes, centres, first + half, count - half);
            nodes[index] = new Node { Box = box, Left = left, Right = right, First = first, Count = count };
            return index;
        }

        private bool Blocked(Vec3 origin, Vec3 dir)
        {
            var stack = new Stack<int>();
            stack.Push(0);
            var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
            while (stack.Count > 0)
            {
                var node = _nodes[stack.Pop()];
                if (!HitsBox(node.Box, origin, inv))
                    continue;
                if (node.Left < 0)
                {
                    for (int i = node.First; i < node.First + node.Count; i++)
                    {
                        int t = _order[i];
                        if (HitsTriangle(origin, dir, _a[t], _b[t], _c[t]))
                            return true;
                    }
                    continue;
                }
                stack.Push(node.Left);
                stack.Push(node.Right);
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

        // Möller-Trumbore, hits in front of the origin only.
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
