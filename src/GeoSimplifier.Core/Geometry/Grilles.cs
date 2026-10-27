using System;
using System.Collections.Generic;
using System.Linq;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>
    /// Grilles inside one part (a radiator imported as tens of thousands of small surfaces), found from the surfaces'
    /// boxes alone, so each grille can be replaced by a box on its own bounds while the part's frame, plates and pipes
    /// keep their geometry (Serkan: "only the grilles"; one box over TAG3's radiator region of 946x2848x2184 mm was too
    /// coarse).
    /// </summary>
    public static class Grilles
    {
        /// <summary>
        /// Panels and louvres of the parts with at least <paramref name="minPartSurfaces"/> surfaces.
        /// Panels are thin dense layers of small surfaces (under 5 % of the part's longest side): along each axis the
        /// surfaces' middles are counted in 20 mm slices, and a slice holding at least 4 times the median and 300 is a
        /// layer, with the slices next to it (louvres slant). In its plane, the layer's surfaces are put in 100 mm cells;
        /// touching cells of at least 4 form a panel, a grille when it has at least 100 surfaces, both sides at least
        /// 150 mm, one at least 300 mm, and covers at least half its rectangle (a perforated flange or a row of rivets
        /// does not).
        /// Louvres are made of long slats (one of TAG3's radiator panels stayed open, its slats over the small limit):
        /// a slat's box is long one way and under 120 mm the other two; slats with the same long axis that start and
        /// end within 20 mm, sit at the same depth (50 mm) and stack with gaps under 60 mm, at least 10 in a run, are a
        /// louvre panel when it is at least 300 mm long.
        /// Returns the surfaces (indices into <paramref name="boxes"/>) of each.
        /// </summary>
        public static List<(string part, List<int> bodies)> Find(IReadOnlyList<Aabb> boxes, IReadOnlyList<string> owners,
            IEnumerable<int> surfaces, int minPartSurfaces, Action<string> log = null)
        {
            var found = new List<(string, List<int>)>();
            const double slice = 0.02, cell = 0.1;
            foreach (var part in surfaces.Where(i => !string.IsNullOrEmpty(owners[i]) && !boxes[i].IsEmpty).GroupBy(i => owners[i]))
            {
                var members = part.ToList();
                if (members.Count < minPartSurfaces)
                    continue;
                var partBox = members.Aggregate(Aabb.Empty, (a, i) => a.Union(boxes[i]));
                var small = members.Where(i => boxes[i].LongestSide < 0.05 * partBox.LongestSide).ToList();
                var taken = new HashSet<int>();
                int panels = 0;
                double Mid(int i, int a) => (boxes[i].Min[a] + boxes[i].Max[a]) / 2;
                // A part of only large surfaces has no small ones to make a panel of (a TAG3 variant's part of 200
                // surfaces none of them small: the empty list failed the whole result).
                for (int axis = 0; axis < 3 && small.Count > 0; axis++)
                {
                    int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    double start = small.Min(i => Mid(i, axis));
                    var counts = small.GroupBy(i => (int)Math.Floor((Mid(i, axis) - start) / slice)).ToDictionary(g => g.Key, g => g.Count());
                    // The median over every slice of the part's extent, empty ones too: a part that is all grille has
                    // one full slice, which must not be its own median.
                    int lowest = (int)Math.Floor((partBox.Min[axis] - start) / slice), highest = (int)Math.Floor((partBox.Max[axis] - start) / slice);
                    var sorted = Enumerable.Range(lowest, Math.Max(1, highest - lowest + 1)).Select(k => counts.TryGetValue(k, out var n) ? n : 0).OrderBy(v => v).ToList();
                    double limit = Math.Max(300, 4 * sorted[sorted.Count / 2]);
                    var layers = new List<(int lo, int hi)>();
                    foreach (int s in counts.Keys.Where(k => counts[k] >= limit).OrderBy(k => k))
                    {
                        if (layers.Count > 0 && s <= layers[layers.Count - 1].hi + 1) layers[layers.Count - 1] = (layers[layers.Count - 1].lo, s);
                        else layers.Add((s, s));
                    }
                    foreach (var (lo, hi) in layers)
                    {
                        double from = start + (lo - 1) * slice, to = start + (hi + 2) * slice;
                        var inLayer = small.Where(i => !taken.Contains(i) && boxes[i].Min[axis] >= from && boxes[i].Max[axis] <= to).ToList();
                        var cells = inLayer.GroupBy(i => ((long)Math.Floor(Mid(i, ua) / cell), (long)Math.Floor(Mid(i, va) / cell)))
                            .Where(g => g.Count() >= 4).ToDictionary(g => g.Key, g => g.ToList());
                        var seen = new HashSet<(long, long)>();
                        foreach (var first in cells.Keys.ToList())
                        {
                            if (!seen.Add(first)) continue;
                            var panel = new List<int>();
                            int panelCells = 0;
                            var queue = new Queue<(long, long)>();
                            queue.Enqueue(first);
                            while (queue.Count > 0)
                            {
                                var c = queue.Dequeue();
                                panelCells++;
                                panel.AddRange(cells[c]);
                                for (int du = -1; du <= 1; du++)
                                    for (int dv = -1; dv <= 1; dv++)
                                    {
                                        var nb = (c.Item1 + du, c.Item2 + dv);
                                        if (cells.ContainsKey(nb) && seen.Add(nb))
                                            queue.Enqueue(nb);
                                    }
                            }
                            var box = panel.Aggregate(Aabb.Empty, (a, i) => a.Union(boxes[i]));
                            double su = box.Size[ua], sv = box.Size[va];
                            double cover = panelCells / Math.Max(1.0, Math.Ceiling(su / cell) * Math.Ceiling(sv / cell));
                            if (panel.Count < 100 || Math.Min(su, sv) < 0.15 || Math.Max(su, sv) < 0.3 || cover < 0.5)
                                continue;
                            foreach (int i in panel) taken.Add(i);
                            panels++;
                            found.Add((part.Key, panel));
                            var s = box.Size;
                            log?.Invoke($"  ızgara → kutu: {part.Key}, {panel.Count}/{members.Count} yüzey gövdesi, kutu {s.X * 1000:F0}x{s.Y * 1000:F0}x{s.Z * 1000:F0} mm, " +
                                        $"{"XYZ"[axis]} yönünde {s[axis] * 1000:F0} mm, doluluk %{100 * cover:F0}");
                        }
                    }
                }
                var slats = members.Where(i => !taken.Contains(i) && boxes[i].LongestSide >= 0.05 * partBox.LongestSide && MiddleSide(boxes[i]) < 0.12).ToList();
                for (int la = 0; la < 3; la++)
                    foreach (int sa in Enumerable.Range(0, 3).Where(a => a != la))
                    {
                        int ta = 3 - la - sa;
                        var along = slats.Where(i => !taken.Contains(i) && boxes[i].Size[la] == boxes[i].LongestSide);
                        foreach (var line in along.GroupBy(i => (Math.Round(boxes[i].Min[la] / 0.02), Math.Round(boxes[i].Max[la] / 0.02), Math.Round((boxes[i].Min[ta] + boxes[i].Max[ta]) / 2 / 0.05))))
                        {
                            var run = new List<int>();
                            void Flush()
                            {
                                // Radiator-sized only, as the panels: on TAG3 the slats of small electrical parts
                                // made 429 "louvres" of 25 mm, kept as boxes past the last clean-up.
                                var box = run.Aggregate(Aabb.Empty, (a, i) => a.Union(boxes[i]));
                                if (run.Count >= 10 && box.LongestSide >= 0.3)
                                {
                                    foreach (int i in run) taken.Add(i);
                                    panels++;
                                    found.Add((part.Key, run.ToList()));
                                    var s = box.Size;
                                    log?.Invoke($"  panjur → kutu: {part.Key}, {run.Count} çıta, kutu {s.X * 1000:F0}x{s.Y * 1000:F0}x{s.Z * 1000:F0} mm");
                                }
                                run.Clear();
                            }
                            foreach (int i in line.OrderBy(i => boxes[i].Min[sa]))
                            {
                                if (run.Count > 0 && boxes[i].Min[sa] - boxes[run[run.Count - 1]].Max[sa] > 0.06)
                                    Flush();
                                run.Add(i);
                            }
                            Flush();
                        }
                    }
                if (panels == 0)
                    log?.Invoke($"  ızgara arama: {part.Key}, {members.Count} yüzey gövdesi, {small.Count} küçük, ızgara yok");
                else
                    log?.Invoke($"  ızgara arama: {part.Key}, {panels} panel, {taken.Count}/{small.Count} küçük yüzey kutuya döndü, kalanı olduğu gibi");
            }
            return found;
        }

        /// <summary>The middle one of a box's three sides.</summary>
        public static double MiddleSide(Aabb b) => new[] { b.Size.X, b.Size.Y, b.Size.Z }.OrderBy(v => v).ElementAt(1);
    }
}
