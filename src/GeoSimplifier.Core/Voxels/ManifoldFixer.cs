namespace GeoSimplifier.Core.Voxels
{
    /// <summary>
    /// Removes non-manifold configurations (cells touching only along an edge or at a vertex) by
    /// filling extra cells. Only ever adds cells, so the result still encloses the input.
    /// A manifold voxel set is required for the box union in SolidWorks to produce one valid solid.
    /// </summary>
    public static class ManifoldFixer
    {
        // Indexed by a 2x2x2 occupancy mask (bit = dx | dy << 1 | dz << 2).
        private static readonly bool[] ManifoldBlock = BuildTable();

        /// <returns>Number of cells added.</returns>
        public static int Fix(VoxelGrid g, int maxPasses = 256)
        {
            int added = 0;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                int before = added;
                added += FixEdges(g);
                added += FixVertices(g);
                if (added == before)
                    break;
            }
            return added;
        }

        public static bool IsManifold(VoxelGrid g)
        {
            var copy = g.Clone();
            return FixEdges(copy) == 0 && FixVertices(copy) == 0;
        }

        private static int FixEdges(VoxelGrid g)
        {
            int added = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                int u = axis == 0 ? 1 : 0;
                int v = axis == 2 ? 1 : 2;
                var c = new int[3];
                for (int t = 0; t < g.Dim(axis); t++)
                for (int b = 0; b < g.Dim(v) - 1; b++)
                for (int a = 0; a < g.Dim(u) - 1; a++)
                {
                    c[axis] = t;
                    int Idx(int da, int db)
                    {
                        c[u] = a + da;
                        c[v] = b + db;
                        return g.Index(c[0], c[1], c[2]);
                    }

                    int i00 = Idx(0, 0), i10 = Idx(1, 0), i01 = Idx(0, 1), i11 = Idx(1, 1);
                    bool f00 = g[i00], f10 = g[i10], f01 = g[i01], f11 = g[i11];
                    if (f00 && f11 && !f10 && !f01)
                    {
                        g[i10] = true;
                        added++;
                    }
                    else if (f10 && f01 && !f00 && !f11)
                    {
                        g[i00] = true;
                        added++;
                    }
                }
            }
            return added;
        }

        private static int FixVertices(VoxelGrid g)
        {
            int added = 0;
            var idx = new int[8];
            for (int k = 0; k < g.Nz - 1; k++)
            for (int j = 0; j < g.Ny - 1; j++)
            for (int i = 0; i < g.Nx - 1; i++)
            {
                int mask = 0;
                for (int b = 0; b < 8; b++)
                {
                    idx[b] = g.Index(i + (b & 1), j + ((b >> 1) & 1), k + ((b >> 2) & 1));
                    if (g[idx[b]])
                        mask |= 1 << b;
                }

                while (!ManifoldBlock[mask])
                {
                    int best = -1, bestScore = -1;
                    for (int b = 0; b < 8; b++)
                    {
                        if ((mask & (1 << b)) != 0)
                            continue;
                        int score = 0;
                        for (int axis = 0; axis < 3; axis++)
                            if ((mask & (1 << (b ^ (1 << axis)))) != 0)
                                score++;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = b;
                        }
                    }
                    mask |= 1 << best;
                    g[idx[best]] = true;
                    added++;
                }
            }
            return added;
        }

        private static bool[] BuildTable()
        {
            var table = new bool[256];
            for (int mask = 0; mask < 256; mask++)
                table[mask] = Components(mask) <= 1 && Components(~mask & 0xFF) <= 1;
            return table;
        }

        /// <summary>Number of face-connected components of the cells in a 2x2x2 mask.</summary>
        internal static int Components(int mask)
        {
            int seen = 0, count = 0;
            for (int start = 0; start < 8; start++)
            {
                if ((mask & (1 << start)) == 0 || (seen & (1 << start)) != 0)
                    continue;
                count++;
                int frontier = 1 << start;
                seen |= frontier;
                while (frontier != 0)
                {
                    int next = 0;
                    for (int b = 0; b < 8; b++)
                    {
                        if ((frontier & (1 << b)) == 0)
                            continue;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int n = b ^ (1 << axis);
                            if ((mask & (1 << n)) != 0 && (seen & (1 << n)) == 0)
                            {
                                seen |= 1 << n;
                                next |= 1 << n;
                            }
                        }
                    }
                    frontier = next;
                }
            }
            return count;
        }
    }
}
