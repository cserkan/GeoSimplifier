using System;
using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>Surface voxelization: marks every cell that a triangle touches.</summary>
    public static class Voxelizer
    {
        /// <summary>
        /// Builds a grid around <paramref name="mesh"/> with <paramref name="padding"/> empty cells on
        /// every side and marks the cells intersected by the mesh surface.
        /// </summary>
        public static VoxelGrid VoxelizeSurface(TriangleMesh mesh, double voxelSize, int padding, long maxCells)
        {
            if (mesh.Triangles.Count == 0)
                throw new ArgumentException("Mesh has no triangles.", nameof(mesh));

            var bounds = mesh.Bounds;
            var size = bounds.Size;
            int nx = (int)Math.Ceiling(size.X / voxelSize) + 1 + 2 * padding;
            int ny = (int)Math.Ceiling(size.Y / voxelSize) + 1 + 2 * padding;
            int nz = (int)Math.Ceiling(size.Z / voxelSize) + 1 + 2 * padding;
            long total = (long)nx * ny * nz;
            if (total > maxCells)
                throw new InvalidOperationException(
                    $"Voxel grid {nx}x{ny}x{nz} ({total:N0} cells) exceeds the limit of {maxCells:N0}. Use a larger voxel size.");

            // Center the model inside the padded grid.
            var slack = new Vec3(nx * voxelSize - size.X, ny * voxelSize - size.Y, nz * voxelSize - size.Z) * 0.5;
            var origin = bounds.Min - slack;
            var grid = new VoxelGrid(nx, ny, nz, origin, voxelSize);

            var half = new Vec3(voxelSize, voxelSize, voxelSize) * 0.5;
            foreach (var tri in mesh.Triangles)
            {
                var tb = tri.Bounds;
                int i0 = Clamp((int)Math.Floor((tb.Min.X - origin.X) / voxelSize), 0, nx - 1);
                int j0 = Clamp((int)Math.Floor((tb.Min.Y - origin.Y) / voxelSize), 0, ny - 1);
                int k0 = Clamp((int)Math.Floor((tb.Min.Z - origin.Z) / voxelSize), 0, nz - 1);
                int i1 = Clamp((int)Math.Floor((tb.Max.X - origin.X) / voxelSize), 0, nx - 1);
                int j1 = Clamp((int)Math.Floor((tb.Max.Y - origin.Y) / voxelSize), 0, ny - 1);
                int k1 = Clamp((int)Math.Floor((tb.Max.Z - origin.Z) / voxelSize), 0, nz - 1);

                for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int idx = grid.Index(i, j, k);
                    if (grid[idx])
                        continue;
                    var center = grid.CellMin(i, j, k) + half;
                    if (TriangleBoxOverlap.Test(center, half, tri))
                        grid[idx] = true;
                }
            }

            return grid;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
