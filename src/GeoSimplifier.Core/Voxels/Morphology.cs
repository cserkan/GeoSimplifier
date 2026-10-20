namespace GeoSimplifier.Core.Voxels
{
    /// <summary>Binary dilation/erosion with a cubic structuring element (Chebyshev radius).</summary>
    public static class Morphology
    {
        public static VoxelGrid Dilate(VoxelGrid grid, int radius)
        {
            if (radius <= 0)
                return grid.Clone();
            var result = grid;
            for (int axis = 0; axis < 3; axis++)
                result = DilateAxis(result, axis, radius);
            return result;
        }

        /// <summary>Erosion; cells outside the grid count as empty.</summary>
        public static VoxelGrid Erode(VoxelGrid grid, int radius)
        {
            if (radius <= 0)
                return grid.Clone();
            var inverted = grid.Clone();
            inverted.Invert();
            var result = Dilate(inverted, radius);
            result.Invert();
            ClearBorder(result, radius);
            return result;
        }

        private static VoxelGrid DilateAxis(VoxelGrid src, int axis, int r)
        {
            var dst = src.CloneEmpty();
            int n = src.Dim(axis);
            int u = axis == 0 ? 1 : 0;
            int v = axis == 2 ? 1 : 2;
            int nu = src.Dim(u), nv = src.Dim(v);
            var prefix = new int[n + 1];
            var c = new int[3];

            for (int b = 0; b < nv; b++)
            for (int a = 0; a < nu; a++)
            {
                c[u] = a;
                c[v] = b;
                for (int t = 0; t < n; t++)
                {
                    c[axis] = t;
                    prefix[t + 1] = prefix[t] + (src[src.Index(c[0], c[1], c[2])] ? 1 : 0);
                }
                for (int t = 0; t < n; t++)
                {
                    int lo = t - r < 0 ? 0 : t - r;
                    int hi = t + r + 1 > n ? n : t + r + 1;
                    if (prefix[hi] - prefix[lo] > 0)
                    {
                        c[axis] = t;
                        dst[dst.Index(c[0], c[1], c[2])] = true;
                    }
                }
            }
            return dst;
        }

        private static void ClearBorder(VoxelGrid g, int r)
        {
            for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                if (i < r || j < r || k < r || i >= g.Nx - r || j >= g.Ny - r || k >= g.Nz - r)
                    g[i, j, k] = false;
            }
        }
    }
}
