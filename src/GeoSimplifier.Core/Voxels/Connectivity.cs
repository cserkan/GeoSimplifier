namespace GeoSimplifier.Core.Voxels
{
    public static class Connectivity
    {
        /// <summary>Number of 6-connected components of filled cells.</summary>
        public static int CountComponents(VoxelGrid g)
        {
            var seen = new bool[g.Length];
            var stack = new int[g.Length];
            int count = 0;
            int nx = g.Nx, ny = g.Ny, nz = g.Nz;

            for (int start = 0; start < g.Length; start++)
            {
                if (!g[start] || seen[start])
                    continue;
                count++;
                int top = 0;
                stack[top++] = start;
                seen[start] = true;
                while (top > 0)
                {
                    int idx = stack[--top];
                    int i = idx % nx, j = idx / nx % ny, k = idx / (nx * ny);
                    void Visit(int n)
                    {
                        if (g[n] && !seen[n])
                        {
                            seen[n] = true;
                            stack[top++] = n;
                        }
                    }
                    if (i > 0) Visit(idx - 1);
                    if (i < nx - 1) Visit(idx + 1);
                    if (j > 0) Visit(idx - nx);
                    if (j < ny - 1) Visit(idx + nx);
                    if (k > 0) Visit(idx - nx * ny);
                    if (k < nz - 1) Visit(idx + nx * ny);
                }
            }
            return count;
        }
    }
}
