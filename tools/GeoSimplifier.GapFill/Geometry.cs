using System;
using System.Collections.Generic;

namespace GeoSimplifier.GapFill
{
    internal struct V
    {
        public double X, Y, Z;
        public V(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator *(V a, double s) => new V(a.X * s, a.Y * s, a.Z * s);
        public double Dot(V b) => X * b.X + Y * b.Y + Z * b.Z;
        public V Cross(V b) => new V(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Len => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V Unit { get { double l = Len; return l > 0 ? this * (1 / l) : new V(0, 0, 1); } }
        public override string ToString() => $"({X * 1000:F1};{Y * 1000:F1};{Z * 1000:F1})";
    }

    /// <summary>Plane fit, area and other shape measures of a closed polyline.</summary>
    internal static class Shape
    {
        /// <summary>Least squares plane through the points: centre and normal (smallest eigenvector).</summary>
        public static (V centre, V normal) FitPlane(IReadOnlyList<V> pts)
        {
            var c = new V();
            foreach (var p in pts) c = c + p;
            c = c * (1.0 / Math.Max(1, pts.Count));
            var m = new double[3, 3];
            foreach (var p in pts)
            {
                var d = p - c;
                double[] a = { d.X, d.Y, d.Z };
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        m[i, j] += a[i] * a[j];
            }
            var (vals, vecs) = Jacobi(m);
            int k = 0;
            for (int i = 1; i < 3; i++) if (vals[i] < vals[k]) k = i;
            var n = new V(vecs[0, k], vecs[1, k], vecs[2, k]).Unit;
            // For a closed polyline the area normal (Newell) is better when the points lie on a line-like set.
            var newell = Newell(pts);
            if (newell.Len > 1e-12 && Math.Abs(newell.Unit.Dot(n)) < 0.5)
                n = newell.Unit;
            return (c, n);
        }

        public static V Newell(IReadOnlyList<V> pts)
        {
            var n = new V();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                n = n + new V((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            return n * 0.5;
        }

        public static (V u, V v) Basis(V n)
        {
            var u = (Math.Abs(n.X) < 0.9 ? new V(1, 0, 0) : new V(0, 1, 0)).Cross(n).Unit;
            return (u, n.Cross(u).Unit);
        }

        public static List<(double x, double y)> Project(IReadOnlyList<V> pts, V c, V u, V v)
        {
            var r = new List<(double, double)>(pts.Count);
            foreach (var p in pts) { var d = p - c; r.Add((d.Dot(u), d.Dot(v))); }
            return r;
        }

        public static double Area(List<(double x, double y)> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                a += p.x * q.y - q.x * p.y;
            }
            return Math.Abs(a) / 2;
        }

        public static bool Inside(List<(double x, double y)> poly, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        public static double DistToPolyline(List<(double x, double y)> poly, double x, double y)
        {
            double best = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((x - a.x) * dx + (y - a.y) * dy) / l2)) : 0;
                double ex = a.x + t * dx - x, ey = a.y + t * dy - y;
                best = Math.Min(best, ex * ex + ey * ey);
            }
            return Math.Sqrt(best);
        }

        private static (double[] vals, double[,] vecs) Jacobi(double[,] a)
        {
            var m = (double[,])a.Clone();
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
                if (off < 1e-30) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-30) continue;
                        double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double mkp = m[k, p], mkq = m[k, q];
                            m[k, p] = c * mkp - s * mkq; m[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double mpk = m[p, k], mqk = m[q, k];
                            m[p, k] = c * mpk - s * mqk; m[q, k] = s * mpk + c * mqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            return (new[] { m[0, 0], m[1, 1], m[2, 2] }, v);
        }
    }

    /// <summary>
    /// Display triangles of all bodies in a uniform grid, for "is there a surface within r of this point" queries.
    /// Used only for classification, never in the result.
    /// </summary>
    internal sealed class TriGrid
    {
        private readonly double _cell;
        private readonly List<float[]> _tris = new List<float[]>();   // per body: 9 floats per triangle
        private readonly Dictionary<long, List<(int body, int tri)>> _cells = new Dictionary<long, List<(int, int)>>();

        public TriGrid(double cell) { _cell = cell; }

        public int Triangles { get; private set; }

        private long Key(int i, int j, int k) => ((long)(i & 0x1FFFFF) << 42) | ((long)(j & 0x1FFFFF) << 21) | (long)(k & 0x1FFFFF);

        public void AddBody(int body, float[] tris)
        {
            while (_tris.Count <= body) _tris.Add(null);
            _tris[body] = tris;
            for (int t = 0; t + 8 < tris.Length; t += 9)
            {
                double x0 = Math.Min(tris[t], Math.Min(tris[t + 3], tris[t + 6])), x1 = Math.Max(tris[t], Math.Max(tris[t + 3], tris[t + 6]));
                double y0 = Math.Min(tris[t + 1], Math.Min(tris[t + 4], tris[t + 7])), y1 = Math.Max(tris[t + 1], Math.Max(tris[t + 4], tris[t + 7]));
                double z0 = Math.Min(tris[t + 2], Math.Min(tris[t + 5], tris[t + 8])), z1 = Math.Max(tris[t + 2], Math.Max(tris[t + 5], tris[t + 8]));
                int i0 = (int)Math.Floor(x0 / _cell), i1 = (int)Math.Floor(x1 / _cell);
                int j0 = (int)Math.Floor(y0 / _cell), j1 = (int)Math.Floor(y1 / _cell);
                int k0 = (int)Math.Floor(z0 / _cell), k1 = (int)Math.Floor(z1 / _cell);
                long span = (long)(i1 - i0 + 1) * (j1 - j0 + 1) * (k1 - k0 + 1);
                var a = new V(tris[t], tris[t + 1], tris[t + 2]);
                var n = (new V(tris[t + 3], tris[t + 4], tris[t + 5]) - a).Cross(new V(tris[t + 6], tris[t + 7], tris[t + 8]) - a).Unit;
                double half = _cell * 0.87;
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                        for (int k = k0; k <= k1; k++)
                        {
                            if (span > 8)
                            {
                                // Skip cells the triangle's plane does not pass through.
                                var c = new V((i + 0.5) * _cell, (j + 0.5) * _cell, (k + 0.5) * _cell);
                                if (Math.Abs((c - a).Dot(n)) > half) continue;
                            }
                            long key = Key(i, j, k);
                            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<(int, int)>();
                            list.Add((body, t));
                        }
                Triangles++;
            }
        }

        /// <summary>Smallest distance from p to a triangle within r, of any body except <paramref name="skipBody"/>; r+ if none.</summary>
        public double Nearest(V p, double r, int skipBody, out int nearBody)
        {
            nearBody = -1;
            double best = r * 1.0001;
            int i0 = (int)Math.Floor((p.X - r) / _cell), i1 = (int)Math.Floor((p.X + r) / _cell);
            int j0 = (int)Math.Floor((p.Y - r) / _cell), j1 = (int)Math.Floor((p.Y + r) / _cell);
            int k0 = (int)Math.Floor((p.Z - r) / _cell), k1 = (int)Math.Floor((p.Z + r) / _cell);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    for (int k = k0; k <= k1; k++)
                    {
                        if (!_cells.TryGetValue(Key(i, j, k), out var list)) continue;
                        foreach (var (b, t) in list)
                        {
                            if (b == skipBody) continue;
                            var tr = _tris[b];
                            double d = PointTri(p, new V(tr[t], tr[t + 1], tr[t + 2]), new V(tr[t + 3], tr[t + 4], tr[t + 5]), new V(tr[t + 6], tr[t + 7], tr[t + 8]));
                            if (d < best) { best = d; nearBody = b; }
                        }
                    }
            return best;
        }

        // Ericson, Real-Time Collision Detection 5.1.5
        private static double PointTri(V p, V a, V b, V c)
        {
            V ab = b - a, ac = c - a, ap = p - a;
            double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0 && d2 <= 0) return ap.Len;
            V bp = p - b; double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0 && d4 <= d3) return bp.Len;
            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); return (p - (a + ab * v)).Len; }
            V cp = p - c; double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0 && d5 <= d6) return cp.Len;
            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); return (p - (a + ac * w)).Len; }
            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { double w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (p - (b + (c - b) * w)).Len; }
            double denom = 1 / (va + vb + vc);
            double vv = vb * denom, ww = vc * denom;
            return (p - (a + ab * vv + ac * ww)).Len;
        }
    }
}
