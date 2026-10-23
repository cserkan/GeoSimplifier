using System;
using System.Collections.Generic;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>
    /// The "bag pulled over the model": a morphological closing on a voxel grid. Everything a ball of the given
    /// radius cannot reach from outside is filled: hollow insides, open shells (a fan cover), gaps between parts
    /// and holes narrower than twice the radius.
    ///
    /// The model is given as surface triangles. The result is a set of boxes covering the inside of the closed
    /// outline, so that united with the exact bodies they fill them and join them into one; the boxes stay a little
    /// inside the closed outline, so they never stick out of the model's exact outer faces.
    /// </summary>
    public static class VoxelClosing
    {
        public sealed class Result
        {
            public readonly List<Aabb> Boxes = new List<Aabb>();
            public double Cell;
            public int CellsX, CellsY, CellsZ;
            public int FilledCells;
        }

        /// <summary>
        /// As <see cref="Fill(IReadOnlyList{Vec3}, double, int)"/>, with the grid made coarser until there are at most
        /// <paramref name="maxBoxes"/> boxes (each box is a body to draw and to unite).
        /// </summary>
        public static Result FillLimited(IReadOnlyList<Vec3> triangles, double radius, int maxBoxes = 800, int maxCellsPerAxis = 160)
        {
            // Each try is a full computation; the resolution that was enough for these triangles last time is
            // tried first (the limit usually changes little between previews).
            if (ReferenceEquals(triangles, _lastTriangles) && maxCellsPerAxis == _lastAsked)
                maxCellsPerAxis = _lastUsed;
            else
            {
                _lastTriangles = triangles;
                _lastAsked = maxCellsPerAxis;
            }
            var result = Fill(triangles, radius, maxCellsPerAxis);
            while (result.Boxes.Count > maxBoxes && maxCellsPerAxis > 30)
            {
                maxCellsPerAxis = maxCellsPerAxis * 7 / 10;
                result = Fill(triangles, radius, maxCellsPerAxis);
            }
            _lastUsed = maxCellsPerAxis;
            return result;
        }

        private static IReadOnlyList<Vec3> _lastTriangles;
        private static int _lastAsked, _lastUsed;

        /// <param name="triangles">Surface triangles, three points each.</param>
        /// <param name="radius">Half the widest opening that is closed.</param>
        /// <param name="maxCellsPerAxis">Grid resolution along the longest side of the model.</param>
        public static Result Fill(IReadOnlyList<Vec3> triangles, double radius, int maxCellsPerAxis = 160)
        {
            var result = new Result();
            if (triangles.Count < 3 || radius <= 0)
                return result;

            var bounds = Aabb.Empty;
            foreach (var p in triangles)
                bounds = bounds.Include(p);
            double cell = Math.Max(bounds.LongestSide / maxCellsPerAxis, radius / 60);
            double margin = radius + 3 * cell;
            var origin = bounds.Min - new Vec3(margin, margin, margin);
            int nx = (int)Math.Ceiling((bounds.Size.X + 2 * margin) / cell) + 1;
            int ny = (int)Math.Ceiling((bounds.Size.Y + 2 * margin) / cell) + 1;
            int nz = (int)Math.Ceiling((bounds.Size.Z + 2 * margin) / cell) + 1;
            result.Cell = cell;
            result.CellsX = nx;
            result.CellsY = ny;
            result.CellsZ = nz;
            int count = nx * ny * nz;

            // 1. Voxels the surfaces pass through.
            var surface = new bool[count];
            for (int t = 0; t + 2 < triangles.Count; t += 3)
                Rasterize(triangles[t], triangles[t + 1], triangles[t + 2], origin, cell, nx, ny, nz, surface);

            // 2. Material: everything the outside cannot reach without crossing a surface.
            var outsideRaw = FloodFromBoundary(surface, nx, ny, nz);
            var solid = new bool[count];
            for (int i = 0; i < count; i++)
                solid[i] = !outsideRaw[i];

            // 3. Closing with a cube (not a ball): a cube rests flat on the rim of an opening, so the fill stretches
            //    across it like a bag instead of sagging into it. Grow the material by the radius, find the
            //    outside of that, grow the outside back.
            int k = Math.Max(1, (int)Math.Round(radius / cell));
            var grown = Dilate(solid, k, nx, ny, nz);
            var outside = FloodFromBoundary(grown, nx, ny, nz);

            // 4. Filled: everything inside the closing, less one layer of cells: the outermost cells hold the exact
            //    outer faces, so without them the fill stays inside those faces. Material is filled too: on the grid, air thinner than a cell looks like material, and the
            //    hollows behind it would stay empty; a solid inside merges into a few large boxes anyway.
            var nearOutside = Dilate(outside, k + 1, nx, ny, nz);
            var filled = new bool[count];
            for (int i = 0; i < count; i++)
            {
                filled[i] = !nearOutside[i];
                if (filled[i]) result.FilledCells++;
            }

            // 5. Merge the filled voxels into as few boxes as possible.
            foreach (var (x0, y0, z0, x1, y1, z1) in MergeBoxes(filled, nx, ny, nz))
                result.Boxes.Add(new Aabb(origin + new Vec3(x0 * cell, y0 * cell, z0 * cell),
                                          origin + new Vec3((x1 + 1) * cell, (y1 + 1) * cell, (z1 + 1) * cell)));
            return result;
        }

        /// <summary>Marks every voxel a triangle passes through (by sampling it finer than a cell).</summary>
        private static void Rasterize(Vec3 a, Vec3 b, Vec3 c, Vec3 origin, double cell, int nx, int ny, int nz, bool[] grid)
        {
            double longest = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
            int steps = Math.Max(1, (int)Math.Ceiling(longest / (cell * 0.4)));
            for (int i = 0; i <= steps; i++)
                for (int j = 0; j <= steps - i; j++)
                {
                    double u = (double)i / steps, v = (double)j / steps;
                    var p = a + (b - a) * u + (c - a) * v;
                    int x = (int)((p.X - origin.X) / cell), y = (int)((p.Y - origin.Y) / cell), z = (int)((p.Z - origin.Z) / cell);
                    if (x >= 0 && y >= 0 && z >= 0 && x < nx && y < ny && z < nz)
                        grid[(z * ny + y) * nx + x] = true;
                }
        }

        /// <summary>Voxels not blocked that the grid's boundary reaches through face neighbours.</summary>
        private static bool[] FloodFromBoundary(bool[] blocked, int nx, int ny, int nz)
        {
            var reached = new bool[blocked.Length];
            var stack = new Stack<int>();
            void Push(int x, int y, int z)
            {
                int i = (z * ny + y) * nx + x;
                if (!blocked[i] && !reached[i]) { reached[i] = true; stack.Push(i); }
            }
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++) { Push(x, y, 0); Push(x, y, nz - 1); }
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++) { Push(x, 0, z); Push(x, ny - 1, z); }
            for (int y = 0; y < ny; y++)
                for (int z = 0; z < nz; z++) { Push(0, y, z); Push(nx - 1, y, z); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                if (x > 0) Push(x - 1, y, z);
                if (x < nx - 1) Push(x + 1, y, z);
                if (y > 0) Push(x, y - 1, z);
                if (y < ny - 1) Push(x, y + 1, z);
                if (z > 0) Push(x, y, z - 1);
                if (z < nz - 1) Push(x, y, z + 1);
            }
            return reached;
        }

        /// <summary>Grows the set by <paramref name="k"/> cells in every direction (a cube), one axis at a time.</summary>
        private static bool[] Dilate(bool[] set, int k, int nx, int ny, int nz)
        {
            var a = (bool[])set.Clone();
            // Voxel (x, y, z) is at (z * ny + y) * nx + x.
            a = DilateAxis(a, k, nx, 1, ny * nz, l => l * nx);
            a = DilateAxis(a, k, ny, nx, nx * nz, l => (l / nx) * nx * ny + l % nx);
            a = DilateAxis(a, k, nz, nx * ny, nx * ny, l => l);
            return a;
        }

        /// <summary>
        /// Dilation along one axis: <paramref name="lines"/> lines of <paramref name="n"/> voxels, voxel j of line l
        /// at start(l) + j * stride.
        /// </summary>
        private static bool[] DilateAxis(bool[] a, int k, int n, int stride, int lines, Func<int, int> start)
        {
            var result = new bool[a.Length];
            var prefix = new int[n + 1];
            for (int l = 0; l < lines; l++)
            {
                int s0 = start(l);
                for (int j = 0; j < n; j++)
                    prefix[j + 1] = prefix[j] + (a[s0 + j * stride] ? 1 : 0);
                for (int j = 0; j < n; j++)
                {
                    int lo = Math.Max(0, j - k), hi = Math.Min(n - 1, j + k);
                    result[s0 + j * stride] = prefix[hi + 1] - prefix[lo] > 0;
                }
            }
            return result;
        }

        /// <summary>Greedy merge of set voxels into boxes: runs along x, then rows along y, then slabs along z.</summary>
        private static IEnumerable<(int, int, int, int, int, int)> MergeBoxes(bool[] set, int nx, int ny, int nz)
        {
            var used = new bool[set.Length];
            bool Free(int x, int y, int z) { int i = (z * ny + y) * nx + x; return set[i] && !used[i]; }
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (!Free(x, y, z))
                            continue;
                        int x1 = x;
                        while (x1 + 1 < nx && Free(x1 + 1, y, z)) x1++;
                        int y1 = y;
                        while (y1 + 1 < ny && RowFree(x, x1, y1 + 1, z)) y1++;
                        int z1 = z;
                        while (z1 + 1 < nz && SlabFree(x, x1, y, y1, z1 + 1)) z1++;
                        for (int zz = z; zz <= z1; zz++)
                            for (int yy = y; yy <= y1; yy++)
                                for (int xx = x; xx <= x1; xx++)
                                    used[(zz * ny + yy) * nx + xx] = true;
                        yield return (x, y, z, x1, y1, z1);
                    }

            bool RowFree(int x0, int x1, int y, int z)
            {
                for (int x = x0; x <= x1; x++) if (!Free(x, y, z)) return false;
                return true;
            }
            bool SlabFree(int x0, int x1, int y0, int y1, int z)
            {
                for (int y = y0; y <= y1; y++)
                    if (!RowFree(x0, x1, y, z)) return false;
                return true;
            }
        }
    }
}
