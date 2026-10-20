using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>A block of cells [I0, I1) x [J0, J1) x [K0, K1).</summary>
    public readonly struct VoxelBox
    {
        public readonly int I0, J0, K0, I1, J1, K1;

        public VoxelBox(int i0, int j0, int k0, int i1, int j1, int k1)
        {
            I0 = i0; J0 = j0; K0 = k0;
            I1 = i1; J1 = j1; K1 = k1;
        }

        public int CellCount => (I1 - I0) * (J1 - J0) * (K1 - K0);

        public Aabb ToWorld(VoxelGrid grid) => new Aabb(grid.CellMin(I0, J0, K0), grid.CellMin(I1, J1, K1));
    }
}
