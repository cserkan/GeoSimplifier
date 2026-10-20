using System.Collections.Generic;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>
    /// Greedily covers the filled cells with disjoint axis-aligned boxes (grow along X, then Y, then Z),
    /// which keeps the number of boolean operations in the CAD kernel low.
    /// </summary>
    public static class BoxMerger
    {
        public static List<VoxelBox> Merge(VoxelGrid g)
        {
            var boxes = new List<VoxelBox>();
            var used = new bool[g.Length];
            bool Free(int i, int j, int k)
            {
                int idx = g.Index(i, j, k);
                return g[idx] && !used[idx];
            }

            for (int k = 0; k < g.Nz; k++)
            for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                if (!Free(i, j, k))
                    continue;

                int i1 = i + 1;
                while (i1 < g.Nx && Free(i1, j, k))
                    i1++;

                int j1 = j + 1;
                while (j1 < g.Ny && RowFree(i, i1, j1, k))
                    j1++;

                int k1 = k + 1;
                while (k1 < g.Nz && SlabFree(i, i1, j, j1, k1))
                    k1++;

                for (int kk = k; kk < k1; kk++)
                for (int jj = j; jj < j1; jj++)
                for (int ii = i; ii < i1; ii++)
                    used[g.Index(ii, jj, kk)] = true;

                boxes.Add(new VoxelBox(i, j, k, i1, j1, k1));
            }
            return boxes;

            bool RowFree(int i0, int i1, int j, int k)
            {
                for (int i = i0; i < i1; i++)
                    if (!Free(i, j, k)) return false;
                return true;
            }

            bool SlabFree(int i0, int i1, int j0, int j1, int k)
            {
                for (int j = j0; j < j1; j++)
                    if (!RowFree(i0, i1, j, k)) return false;
                return true;
            }
        }
    }
}
