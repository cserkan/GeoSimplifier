using System.Collections.Generic;

namespace GeoSimplifier.Core.Voxels
{
    public static class ExteriorFill
    {
        /// <summary>
        /// Flood-fills empty space from the grid border (6-connected) and returns everything the fill
        /// could not reach: the shell plus all enclosed cavities, i.e. a solid without internal voids.
        /// </summary>
        public static VoxelGrid SolidFromShell(VoxelGrid shell)
        {
            var outside = shell.CloneEmpty();
            var stack = new Stack<int>();

            void Seed(int i, int j, int k)
            {
                int idx = shell.Index(i, j, k);
                if (!shell[idx] && !outside[idx])
                {
                    outside[idx] = true;
                    stack.Push(idx);
                }
            }

            int nx = shell.Nx, ny = shell.Ny, nz = shell.Nz;
            for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                if (i == 0 || j == 0 || k == 0 || i == nx - 1 || j == ny - 1 || k == nz - 1)
                    Seed(i, j, k);
            }

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                int i = idx % nx;
                int j = idx / nx % ny;
                int k = idx / (nx * ny);
                if (i > 0) Seed(i - 1, j, k);
                if (i < nx - 1) Seed(i + 1, j, k);
                if (j > 0) Seed(i, j - 1, k);
                if (j < ny - 1) Seed(i, j + 1, k);
                if (k > 0) Seed(i, j, k - 1);
                if (k < nz - 1) Seed(i, j, k + 1);
            }

            outside.Invert();
            return outside;
        }
    }
}
