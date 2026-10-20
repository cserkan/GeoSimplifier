namespace GeoSimplifier.Core.Geometry
{
    public readonly struct Triangle
    {
        public readonly Vec3 A;
        public readonly Vec3 B;
        public readonly Vec3 C;

        public Triangle(Vec3 a, Vec3 b, Vec3 c)
        {
            A = a;
            B = b;
            C = c;
        }

        public Aabb Bounds => Aabb.FromPoints(A, B, C);
    }
}
