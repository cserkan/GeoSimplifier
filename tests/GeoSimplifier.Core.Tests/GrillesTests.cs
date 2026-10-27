using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class GrillesTests
    {
        private static Aabb Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
            new Aabb(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

        [Fact]
        public void No_surfaces_find_nothing()
        {
            var found = Grilles.Find(new List<Aabb>(), new List<string>(), Enumerable.Empty<int>(), 200);
            Assert.Empty(found);
        }

        [Fact]
        public void A_part_with_no_small_surfaces_finds_nothing()
        {
            // 200 surfaces of one part, each as long as the part: none under 5 % of it (a TAG3 variant failed here).
            var boxes = Enumerable.Range(0, 200).Select(i => Box(0, i * 0.01, 0, 1, i * 0.01 + 0.005, 1)).ToList();
            var owners = boxes.Select(_ => "plate-1").ToList();
            var found = Grilles.Find(boxes, owners, Enumerable.Range(0, boxes.Count), 200);
            Assert.Empty(found);
        }

        [Fact]
        public void Slats_of_a_small_part_are_no_louvre_but_a_radiators_are()
        {
            // 20 slats 25 mm long stacked 2 mm apart (a connector's fins), and 20 slats 600 mm long (a louvre).
            List<Aabb> Slats(double length) => Enumerable.Range(0, 20).Select(i => Box(0, i * 0.003, 0, length, i * 0.003 + 0.001, 0.02)).ToList();
            foreach (var (length, louvres) in new[] { (0.025, 0), (0.6, 1) })
            {
                var boxes = Slats(length);
                // 200 surfaces make the part big enough to search; the rest are its body, one large surface each.
                boxes.AddRange(Enumerable.Range(0, 180).Select(_ => Box(0, 0, 0, length, 0.1, 0.02)));
                var owners = boxes.Select(_ => "part-1").ToList();
                var found = Grilles.Find(boxes, owners, Enumerable.Range(0, boxes.Count), 200);
                Assert.Equal(louvres, found.Count);
            }
        }

        [Fact]
        public void A_dense_flat_layer_of_small_surfaces_is_one_panel_and_the_frame_stays()
        {
            // A 600 x 600 mm grille of 10 mm squares in the plane x = 0, inside a 2 m frame of four long bars.
            var boxes = new List<Aabb>();
            for (int u = 0; u < 60; u++)
                for (int v = 0; v < 60; v++)
                    boxes.Add(Box(0, u * 0.01, v * 0.01, 0.002, u * 0.01 + 0.008, v * 0.01 + 0.008));
            int grille = boxes.Count;
            boxes.Add(Box(0, -0.7, -0.7, 0.05, 1.3, -0.65));
            boxes.Add(Box(0, -0.7, 1.25, 0.05, 1.3, 1.3));
            boxes.Add(Box(0, -0.7, -0.7, 0.05, -0.65, 1.3));
            boxes.Add(Box(0, 1.25, -0.7, 0.05, 1.3, 1.3));
            var owners = boxes.Select(_ => "radiator-1").ToList();
            var found = Grilles.Find(boxes, owners, Enumerable.Range(0, boxes.Count), 200);
            Assert.Single(found);
            Assert.Equal(grille, found[0].bodies.Count);
            Assert.All(found[0].bodies, i => Assert.True(i < grille));
        }
    }
}
