using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>
    /// Which bodies of a model can be seen from outside: parallel rays are shot at the model from many directions,
    /// and the body each ray hits first is seen. A body no ray reaches (a bearing inside a housing, a rotor) lies
    /// wholly inside the others, so the wrap never touches it. Works on the tessellation, with a bounding volume
    /// hierarchy, on all processor cores.
    /// </summary>
    public sealed class OutsideRays
    {
        private readonly List<Vec3> _a = new List<Vec3>(), _b = new List<Vec3>(), _c = new List<Vec3>();
        private readonly List<int> _owner = new List<int>();
        private Node[] _nodes;
        private int[] _order;

        private struct Node
        {
            public Aabb Box;
            public int Left, Right;
            public int First, Count;
        }

        /// <summary>Adds the triangles (three points each) of one body.</summary>
        public void AddBody(int body, IReadOnlyList<Vec3> triangles)
        {
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                _a.Add(triangles[i]);
                _b.Add(triangles[i + 1]);
                _c.Add(triangles[i + 2]);
                _owner.Add(body);
            }
        }

        public int Triangles => _a.Count;

        /// <summary>The bodies some ray hits first.</summary>
        /// <param name="directions">How many directions rays come from, spread over the sphere.</param>
        /// <param name="spacing">Distance between neighbouring parallel rays (model units).</param>
        /// <param name="maxRaysPerSide">Cap on rays across the model in one direction.</param>
        public HashSet<int> Seen(int directions, double spacing, int maxRaysPerSide = 400)
        {
            var seen = new HashSet<int>();
            if (_a.Count == 0)
                return seen;
            Build();
            var box = _nodes[0].Box;
            var centre = (box.Min + box.Max) * 0.5;
            double radius = (box.Max - box.Min).Length / 2 + 1e-6;
            int n = Math.Max(2, Math.Min(maxRaysPerSide, (int)Math.Ceiling(2 * radius / spacing)));
            var hitBy = new bool[directions][];
            Parallel.For(0, directions, d =>
            {
                // Fibonacci sphere.
                double z = 1 - 2 * (d + 0.5) / directions, r = Math.Sqrt(1 - z * z), phi = d * Math.PI * (3 - Math.Sqrt(5));
                var dir = new Vec3(r * Math.Cos(phi), r * Math.Sin(phi), z);
                var u = Math.Abs(dir.X) < 0.9 ? new Vec3(0, -dir.Z, dir.Y) : new Vec3(dir.Z, 0, -dir.X);
                u = u * (1 / u.Length);
                var v = new Vec3(dir.Y * u.Z - dir.Z * u.Y, dir.Z * u.X - dir.X * u.Z, dir.X * u.Y - dir.Y * u.X);
                var local = new bool[_owner.Count == 0 ? 0 : MaxOwner() + 1];
                var stack = new Stack<int>();
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double s = -radius + 2 * radius * (i + 0.5) / n, t = -radius + 2 * radius * (j + 0.5) / n;
                        if (s * s + t * t > radius * radius) continue;
                        var origin = centre - dir * (radius * 1.01) + u * s + v * t;
                        int owner = FirstHit(origin, dir, stack);
                        if (owner >= 0) local[owner] = true;
                    }
                hitBy[d] = local;
            });
            foreach (var local in hitBy)
                for (int i = 0; i < local.Length; i++)
                    if (local[i]) seen.Add(i);
            return seen;
        }

        private int _maxOwner = -1;
        private int MaxOwner()
        {
            if (_maxOwner < 0)
                foreach (var o in _owner) _maxOwner = Math.Max(_maxOwner, o);
            return _maxOwner;
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
            MaxOwner();
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

        /// <summary>The owner of the nearest triangle the ray hits, or -1.</summary>
        private int FirstHit(Vec3 origin, Vec3 dir, Stack<int> stack)
        {
            stack.Clear();
            stack.Push(0);
            var inv = new Vec3(1 / dir.X, 1 / dir.Y, 1 / dir.Z);
            double best = double.PositiveInfinity;
            int owner = -1;
            while (stack.Count > 0)
            {
                var node = _nodes[stack.Pop()];
                if (!HitsBox(node.Box, origin, inv, best))
                    continue;
                if (node.Left < 0)
                {
                    for (int i = node.First; i < node.First + node.Count; i++)
                    {
                        int t = _order[i];
                        double d = HitDistance(origin, dir, _a[t], _b[t], _c[t]);
                        if (d < best) { best = d; owner = _owner[t]; }
                    }
                    continue;
                }
                stack.Push(node.Left);
                stack.Push(node.Right);
            }
            return owner;
        }

        private static bool HitsBox(Aabb box, Vec3 origin, Vec3 inv, double maxT)
        {
            double t0 = 0, t1 = maxT;
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
            return t > 0 ? t : double.PositiveInfinity;
        }

        private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
}
