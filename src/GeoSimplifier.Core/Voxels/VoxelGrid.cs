using System;
using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>
    /// A dense boolean voxel grid. Cell (i, j, k) covers
    /// [Origin + (i, j, k) * VoxelSize, Origin + (i + 1, j + 1, k + 1) * VoxelSize].
    /// </summary>
    public sealed class VoxelGrid
    {
        private readonly bool[] _cells;

        public VoxelGrid(int nx, int ny, int nz, Vec3 origin, double voxelSize)
        {
            if (nx <= 0 || ny <= 0 || nz <= 0)
                throw new ArgumentOutOfRangeException(nameof(nx), "Grid dimensions must be positive.");
            if (voxelSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(voxelSize));

            Nx = nx;
            Ny = ny;
            Nz = nz;
            Origin = origin;
            VoxelSize = voxelSize;
            _cells = new bool[checked(nx * ny * nz)];
        }

        public int Nx { get; }
        public int Ny { get; }
        public int Nz { get; }
        public Vec3 Origin { get; }
        public double VoxelSize { get; }

        public int Length => _cells.Length;

        public int Index(int i, int j, int k) => i + Nx * (j + Ny * k);

        public bool InRange(int i, int j, int k) =>
            i >= 0 && j >= 0 && k >= 0 && i < Nx && j < Ny && k < Nz;

        public bool this[int i, int j, int k]
        {
            get => InRange(i, j, k) && _cells[Index(i, j, k)];
            set => _cells[Index(i, j, k)] = value;
        }

        public bool this[int index]
        {
            get => _cells[index];
            set => _cells[index] = value;
        }

        public int Dim(int axis) => axis == 0 ? Nx : axis == 1 ? Ny : Nz;

        public Vec3 CellMin(int i, int j, int k) => Origin + new Vec3(i, j, k) * VoxelSize;

        public int Count()
        {
            int n = 0;
            foreach (var c in _cells)
                if (c) n++;
            return n;
        }

        public VoxelGrid CloneEmpty() => new VoxelGrid(Nx, Ny, Nz, Origin, VoxelSize);

        public VoxelGrid Clone()
        {
            var copy = CloneEmpty();
            Array.Copy(_cells, copy._cells, _cells.Length);
            return copy;
        }

        public void Invert()
        {
            for (int n = 0; n < _cells.Length; n++)
                _cells[n] = !_cells[n];
        }
    }
}
