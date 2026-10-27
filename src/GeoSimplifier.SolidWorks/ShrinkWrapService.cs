using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GeoSimplifier.Core.Wrap;
using GeoSimplifier.SolidWorks.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks
{
    /// <summary>The computed wrap: temporary bodies, what was added to the original, and statistics.</summary>
    public sealed class WrapResult
    {
        public List<IBody2> Bodies = new List<IBody2>();

        /// <summary>Material the wrap adds to the original bodies (plugs, bridges, filled cavities), for the preview.</summary>
        public List<IBody2> Added = new List<IBody2>();

        /// <summary>Material the wrap removes (flattened embossed details), for the preview.</summary>
        public List<IBody2> Removed = new List<IBody2>();

        /// <summary>
        /// The result grown by a hair, for the translucent preview: drawn exactly on the original faces it would
        /// fight with them for the same pixels and show streaks.
        /// </summary>
        public List<IBody2> Envelope = new List<IBody2>();

        /// <summary>Bodies kept out of the wrap and united with it as they are.</summary>
        public int ExcludedBodies;

        /// <summary>Bodies the last small-body clean-up leaves alone however small (excluded parts, cage boxes).</summary>
        public HashSet<IBody2> KeepAlways = new HashSet<IBody2>();

        public int SourceBodies;

        /// <summary>Surface bodies wrapped as thin solids, and those SOLIDWORKS could not thicken.</summary>
        public int Sheets;
        public int FailedSheets;

        /// <summary>Gaps between separate parts spanned like a bag pulled over them.</summary>
        public int Bridges;
        public int HolesClosed;
        public int HolesFailed;
        public int BossesRemoved;

        /// <summary>Small parts left out (<see cref="ShrinkWrapOptions.MinPartPercent"/>).</summary>
        public int SmallPartsRemoved;

        /// <summary>Cable bundles deleted.</summary>
        public int BundlesRemoved;

        /// <summary>The bodies already are the outer skin of a surface model (see Skin); Commit puts them in as they are.</summary>
        public bool IsSkin;

        /// <summary>"Tek gövdeye zorla" was off: many bodies are expected.</summary>
        public bool ManyBodiesWanted;

        /// <summary>Parts no ray from outside reaches, left out of the wrap (their space is filled).</summary>
        public int InsideParts;

        /// <summary>Threaded regions filled (<see cref="ShrinkWrapOptions.FillThreads"/>).</summary>
        public int ThreadsFilled;
        public int FailedBooleans;
        public double Seconds;
        public List<string> Notes = new List<string>();

        /// <summary>Openings left open because they are wider than the limit (meters).</summary>
        public List<double> TooWide = new List<double>();

        /// <summary>Hole detection details, shown in the preview box for debugging.</summary>
        public List<string> Log = new List<string>();

        /// <summary>A quick preview result (<see cref="ShrinkWrapOptions.Preview"/>): not to be committed.</summary>
        public bool IsPreview;

        /// <summary>The preview of a large model draws no envelope (it would take seconds to redraw).</summary>
        public bool EnvelopeLeftOut;

        /// <summary>Seconds spent in each step, for finding what makes the preview slow.</summary>
        public string Timings = "";

        public string Summary()
        {
            string text = $"{SourceBodies} gövde" + (ExcludedBodies > 0 ? $" (+{ExcludedBodies} hariç tutulan)" : "") +
                          (IsPreview ? " (hızlı önizleme)" : $" → {Bodies.Count} gövde") + $", {HolesClosed} delik kapatıldı" +
                          (HolesFailed > 0 ? $", {HolesFailed} delik kapatılamadı" : "") +
                          (BossesRemoved > 0 ? $", {BossesRemoved} kabartma düzlendi" : "") +
                          (SmallPartsRemoved > 0 ? $", {SmallPartsRemoved} küçük parça çıkarıldı" : "") +
                          (BundlesRemoved > 0 ? $", {BundlesRemoved} kablo demeti silindi" : "") +
                          (InsideParts > 0 ? $", {InsideParts} iç parça atlandı" : "") +
                          (ThreadsFilled > 0 ? $", {ThreadsFilled} vida dişi dolduruldu" : "") +
                          (Bridges > 0 ? $", {Bridges} boşluk köprülendi" : "") +
                          (Sheets > 0 ? $", {Sheets} yüzey gövdesi sarıldı" : "") + $" ({Seconds:F1} sn).";
            var notes = new List<string>(Notes);
            if (Bodies.Count > 1 && !IsPreview && !ManyBodiesWanted)
                notes.Add("Birbirinden uzak parçalar var; tek gövde için kapatma genişliğini artırın.");
            if (FailedSheets > 0)
                notes.Add($"{FailedSheets} yüzey gövdesi kalınlaştırılamadığı için sarmaya katılmadı.");
            if (FailedBooleans > 0)
                notes.Add($"{FailedBooleans} boolean işlemi başarısız oldu.");
            if (TooWide.Count > 0)
                notes.Add($"{TooWide.Count} açıklık sınırdan geniş olduğu için açık bırakıldı. Kapatmak için sınırı en az {Math.Ceiling(TooWide.Min() * 1000 + 0.5)} mm yapın" +
                          (TooWide.Count > 1 ? $" (hepsi için {Math.Ceiling(TooWide.Max() * 1000 + 0.5)} mm)." : "."));
            return notes.Count == 0 ? text : text + " " + string.Join(" ", notes);
        }
    }

    /// <summary>
    /// Exact shrink wrap of a part or assembly: union all bodies, bridge nearby bodies, close holes flush and
    /// fill internal cavities. Faces that stay visible are the original ones.
    /// </summary>
    public sealed class ShrinkWrapService
    {
        private readonly ISldWorks _swApp;

        private const int LargeModelTriangles = 200000;
        // Above this many faces the quick preview draws no translucent envelope: on a 43-body motor (about 5000
        // faces) redrawing it took 5 s of a preview's 11.
        private const int LargeModelFaces = 3000;

        /// <summary>
        /// In the outer skin of a surface model, a part showing more separate surfaces than this is replaced by its
        /// box when it is grid-like (0: never). Serkan agreed to boxes for radiator- and grille-like parts.
        /// </summary>
        public static int MaxSheetsPerPart { get; set; } = 150;

        /// <summary>
        /// In the outer skin of a surface model, a surface body is kept when more than this share of its sample
        /// points is seen from outside, and its area is above SkinMinArea times the square of the model's size.
        /// </summary>
        public static double SkinMinSeen { get; set; } = 0.2;

        /// <summary>Whether the outer skin of a surface model gets flat lids on the holes of its surfaces.</summary>
        public static bool SkinLids { get; set; }
        // 1e-6 of the model's size squared: 38 mm² on V12 (6.1 m). With 20 % seen, V12 went from 10,591 to
        // 5,843 surface bodies and looked complete; Serkan took that.
        public static double SkinMinArea { get; set; } = 1e-6;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// Whether SOLIDWORKS' main window is on the screen. ISldWorks.Visible stays true in a test SOLIDWORKS whose
        /// window was hidden, so the frame's own window is asked.
        /// </summary>
        private bool MainWindowShown()
        {
            try
            {
                // A minimized window counts as visible to Windows; nobody sees it either (zooming the hidden test
                // SOLIDWORKS onto TAG3's 8,457 bodies took it to 38.7 GB and it never came back).
                return _swApp.Visible && _swApp.Frame() is IFrame frame &&
                       IsWindowVisible(new IntPtr(frame.GetHWndx64())) && !IsIconic(new IntPtr(frame.GetHWndx64()));
            }
            catch (Exception) { return _swApp.Visible; }
        }

        /// <summary>Whether the solids seen in a surface model's skin get holes plugged, threads filled and embossed details flattened.</summary>
        public static bool SkinSolidWork { get; set; } = true;

        /// <summary>How long the solids of a surface model's skin are worked on in all, most visible first.</summary>
        public const double SkinSolidSeconds = 300;

        /// <summary>
        /// Thin-part groups whose members' median middle/longest box side is below this are cable bundles (TAG3's
        /// bundles 0.42 to 0.63, V12's fan cages 0.98 and 1.0).
        /// </summary>
        private const double BundleSquat = 0.8;

        /// <summary>Whether grilles inside one part get flat lids in a surface skin (a trial; off).</summary>
        public static bool SkinGrilleCaps { get; set; }

        /// <summary>A trial: leave the new part's view as it is instead of zooming to fit.</summary>
        public static bool SkipZoom { get; set; }

        /// <summary>Whether surface bodies are knitted per component before wrapping (for trials; see Compute).</summary>
        public static bool KnitSheets { get; set; }

        // Lids the preview draws at most (the largest ones).
        private const int MaxPreviewLids = 800;

        // Exact preview plugs of the previewed document for each limit tried.
        private string _plugKey;
        private readonly Dictionary<double, (List<IBody2> plugs, List<double> tooWide)> _plugsByLimit =
            new Dictionary<double, (List<IBody2> plugs, List<double> tooWide)>();

        // The union of the source bodies does not depend on the limit, so plugs-only previews reuse it.
        private string _unionKey;
        private List<IBody2> _union;

        // The surface triangles of the source bodies do not depend on the settings, so previews reuse them.
        private string _trianglesKey;
        private List<Core.Geometry.Vec3> _triangles;

        public ShrinkWrapService(ISldWorks swApp)
        {
            _swApp = swApp;
        }

        /// <summary>
        /// Closes the holes of each body. A body that is another instance of a part body already closed, placed
        /// with the same rotation (a row of screws), gets that body's result and plugs moved into place instead of
        /// finding its holes again; the result is the same, since only the position differs.
        /// </summary>
        private void CloseHolesPerInstance(HoleCloser closer, List<IBody2> bodies, List<(string key, double[] transform)> instances,
                                           double width, Action<int, int> report)
        {
            var math = (IMathUtility)_swApp.GetMathUtility();
            var done = new Dictionary<string, (double[] t, IBody2 result, int plugs0, int plugs1, int wide0, int wide1, int closed, int failed)>();
            int reused = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                report(i, bodies.Count);
                var (key, t) = i < instances.Count ? instances[i] : (null, null);
                string k = key == null || t == null || t.Length < 13 ? null
                    : key + "|" + string.Join(",", t.Take(9).Concat(new[] { t[12] }).Select(v => Math.Round(v, 9).ToString("R")));
                if (k != null && done.TryGetValue(k, out var d) &&
                    math.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, t[9] - d.t[9], t[10] - d.t[10], t[11] - d.t[11], 1, 0, 0, 0 }) is MathTransform shift)
                {
                    if (!closer.PlugsOnly)
                    {
                        var moved = (IBody2)d.result.Copy();
                        moved.ApplyTransform(shift);
                        bodies[i] = moved;
                    }
                    for (int p = d.plugs0; p < d.plugs1; p++)
                    {
                        var plug = (IBody2)closer.Plugs[p].Copy();
                        plug.ApplyTransform(shift);
                        closer.Plugs.Add(plug);
                        closer.PlugWidths.Add(closer.PlugWidths[p]);
                    }
                    for (int w = d.wide0; w < d.wide1; w++)
                        closer.TooWide.Add(closer.TooWide[w]);
                    closer.CountRepeat(d.closed, d.failed);
                    reused++;
                    continue;
                }
                int p0 = closer.Plugs.Count, w0 = closer.TooWide.Count, c0 = closer.Closed, f0 = closer.Failed;
                var original = bodies[i];
                bodies[i] = closer.CloseHoles(bodies[i], width);
                // A plug fills a hole of this body, so it lies within the body's box. One that reaches out of it was
                // made from a misread hole (a huge block once swallowed a motor): the body is left as it was.
                var limit = Grown(BodyOps.AnyBounds(original));
                if (!limit.IsEmpty && (Outside(BodyOps.AnyBounds(bodies[i]), limit) ||
                                       closer.Plugs.Skip(p0).Any(p => Outside(BodyOps.AnyBounds(p), limit))))
                {
                    UI.PageLog.Write($"  delik kapatma kutudan taştı, gövde {i} olduğu gibi bırakıldı");
                    bodies[i] = original;
                    closer.Plugs.RemoveRange(p0, closer.Plugs.Count - p0);
                    closer.PlugWidths.RemoveRange(p0, closer.PlugWidths.Count - p0);
                }
                if (k != null)
                    done[k] = (t, bodies[i], p0, closer.Plugs.Count, w0, closer.TooWide.Count, closer.Closed - c0, closer.Failed - f0);
            }
            UI.PageLog.Write($"  delikler: {bodies.Count} gövde, {reused} tanesi tekrar eden parçadan taşındı");
        }

        /// <summary>
        /// Leaving hidden faces out splits the visible ones into islands, each a surface body of its own (126 on a
        /// motor). A hidden face that touches two or more islands is kept, joining them, as long as that helps.
        /// </summary>
        private static void ConnectIslands(List<IFace2> faces, HashSet<int> visible)
        {
            var index = new Dictionary<IFace2, int>();
            for (int i = 0; i < faces.Count; i++)
                index[faces[i]] = i;
            var neighbours = new List<int>[faces.Count];
            for (int i = 0; i < faces.Count; i++)
            {
                neighbours[i] = new List<int>();
                foreach (IEdge edge in (object[])faces[i].GetEdges() ?? new object[0])
                    foreach (IFace2 other in (object[])edge.GetTwoAdjacentFaces2() ?? new object[0])
                        if (other != null && index.TryGetValue(other, out int j) && j != i && !neighbours[i].Contains(j))
                            neighbours[i].Add(j);
            }
            var parent = Enumerable.Range(0, faces.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (int i in visible)
                foreach (int j in neighbours[i])
                    if (visible.Contains(j)) parent[Find(i)] = Find(j);
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int h = 0; h < faces.Count; h++)
                {
                    if (visible.Contains(h)) continue;
                    var islands = neighbours[h].Where(visible.Contains).Select(Find).Distinct().ToList();
                    if (islands.Count < 2) continue;
                    visible.Add(h);
                    foreach (int r in islands) parent[r] = Find(h);
                    changed = true;
                }
            }
        }

        /// <summary>
        /// The lids of the preview, per body; a body that is another instance of a part body already done, with the
        /// same rotation, gets that body's lids moved into place.
        /// </summary>
        private List<IBody2> MouthCaps(BodyOps ops, List<IBody2> bodies, List<(string key, double[] transform)> instances,
                                       double width, Action<int, int> report)
        {
            var math = (IMathUtility)_swApp.GetMathUtility();
            var finder = new HoleCloser(ops) { PlugsOnly = true };
            var done = new Dictionary<string, (double[] t, List<IBody2> caps)>();
            var all = new List<IBody2>();
            for (int i = 0; i < bodies.Count; i++)
            {
                report(i, bodies.Count);
                var (key, t) = i < instances.Count ? instances[i] : (null, null);
                string k = key == null || t == null || t.Length < 13 ? null
                    : key + "|" + string.Join(",", t.Take(9).Concat(new[] { t[12] }).Select(v => Math.Round(v, 9).ToString("R")));
                if (k != null && done.TryGetValue(k, out var d) &&
                    math.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, t[9] - d.t[9], t[10] - d.t[10], t[11] - d.t[11], 1, 0, 0, 0 }) is MathTransform shift)
                {
                    foreach (var cap in d.caps)
                    {
                        var moved = (IBody2)cap.Copy();
                        moved.ApplyTransform(shift);
                        all.Add(moved);
                    }
                    continue;
                }
                var caps = finder.MouthCaps(bodies[i], width);
                all.AddRange(caps);
                if (k != null)
                    done[k] = (t, caps);
            }
            UI.PageLog.Write($"  önizleme kapakları: {all.Count} yuvarlak delik ağzı ({finder.SkippedCaps} yuvarlak olmayan çizilmedi), {bodies.Count} gövde");
            return all;
        }

        /// <summary>
        /// The bodies some ray from outside reaches first. Repeated instances of a part (same rotation) reuse its
        /// triangles, moved into place, so the tessellation is read once per part.
        /// </summary>
        private HashSet<int> SeenFromOutside(List<IBody2> bodies, List<(string key, double[] transform)> instances, Action<int, int> report,
                                             List<List<Core.Geometry.Vec3>> trianglesOut = null)
        {
            var rays = new Core.Geometry.OutsideRays();
            var cache = new Dictionary<string, (double[] t, List<Core.Geometry.Vec3> triangles)>();
            var extent = Core.Geometry.Aabb.Empty;
            // A body of more faces than any real part (TAG3's imported castings) is not tessellated: SOLIDWORKS took
            // all 32 GB of memory on one. It counts as seen, so it stays as it is, and hides nothing behind it.
            var huge = new HashSet<int>();
            for (int i = 0; i < bodies.Count; i++)
            {
                report(i, bodies.Count);
                if (bodies[i].GetFaceCount() > Geometry.ThreadFiller.MaxFaces)
                {
                    huge.Add(i);
                    trianglesOut?.Add(new List<Core.Geometry.Vec3>());
                    extent = extent.Union(BodyOps.Bounds(bodies[i]));
                    continue;
                }
                var (key, t) = i < instances.Count ? instances[i] : (null, null);
                string k = key == null || t == null || t.Length < 13 ? null
                    : key + "|" + string.Join(",", t.Take(9).Concat(new[] { t[12] }).Select(v => Math.Round(v, 9).ToString("R")));
                List<Core.Geometry.Vec3> triangles;
                if (k != null && cache.TryGetValue(k, out var c))
                {
                    var shift = new Core.Geometry.Vec3(t[9] - c.t[9], t[10] - c.t[10], t[11] - c.t[11]);
                    triangles = c.triangles.Select(p => p + shift).ToList();
                }
                else
                {
                    triangles = BodyOps.Triangles(new[] { bodies[i] });
                    if (k != null) cache[k] = (t, triangles);
                }
                rays.AddBody(i, triangles);
                trianglesOut?.Add(triangles);
                extent = extent.Union(BodyOps.Bounds(bodies[i]));
            }
            UI.PageLog.Write($"  dışarıdan görünen: {bodies.Count} gövdenin üçgenleri okundu, {rays.Triangles} üçgen" +
                             (huge.Count > 0 ? $"; {huge.Count} gövde {Geometry.ThreadFiller.MaxFaces} yüzden fazla, okunmadı, görünür sayıldı" : ""));
            // Coarser than for the shell: a part missed here only goes without lids in the preview.
            var seen = rays.Seen(directions: 32, spacing: Math.Max(extent.LongestSide / 250, 5e-4), maxRaysPerSide: 250);
            seen.UnionWith(huge);
            return seen;
        }

        /// <summary>
        /// Quick preview for <see cref="Skin"/>: the small parts that will be left out, the largest of them drawn
        /// in red (drawing costs about 25 ms a body, so at most MaxPreviewLids).
        /// </summary>
        private void SkinPreview(WrapResult result, ShrinkWrapOptions options, List<IBody2> solids, List<string> solidOwners,
                                 List<IBody2> sheets, List<string> sheetOwners)
        {
            var bodies = solids.Concat(sheets).ToList();
            var owners = solidOwners.Take(solids.Count).Concat(sheetOwners).ToList();
            var boxes = bodies.Select(BodyOps.AnyBounds).ToList();
            var model = boxes.Aggregate(Core.Geometry.Aabb.Empty, (a, b) => a.Union(b));
            double limit = options.MinPartPercent / 100 * (model.Max - model.Min).Length;
            var partBoxes = new Dictionary<string, Core.Geometry.Aabb>();
            for (int i = 0; i < bodies.Count; i++)
                partBoxes[owners[i] ?? ""] = partBoxes.TryGetValue(owners[i] ?? "", out var pb) ? pb.Union(boxes[i]) : boxes[i];
            var small = new HashSet<string>(partBoxes.Where(kv => kv.Key != "" && (kv.Value.Max - kv.Value.Min).Length < limit).Select(kv => kv.Key));
            var removed = Enumerable.Range(0, bodies.Count).Where(i => options.MinPartPercent > 0 && small.Contains(owners[i] ?? "")).ToList();
            result.IsPreview = true;
            result.IsSkin = true;
            result.EnvelopeLeftOut = true;
            result.SourceBodies = bodies.Count;
            result.SmallPartsRemoved = small.Count;
            result.Removed = removed.OrderByDescending(i => (boxes[i].Max - boxes[i].Min).Length).Take(MaxPreviewLids).Select(i => bodies[i]).ToList();
            result.Notes.Add($"Yüzey modeli: {partBoxes.Count - small.Count} parça kalıyor; dışarıdan görünen yüzeyleri Tamam'da seçilir.");
            UI.PageLog.Write($"  yüzey modeli önizlemesi: {bodies.Count} gövde, {small.Count} küçük parça ({removed.Count} gövde), en büyük {result.Removed.Count} tanesi kırmızı");
        }

        /// <summary>
        /// The outer skin of a model made mostly of surface bodies, without making solids: small parts are left
        /// out (each part measured as a whole), then every face of every body, solid or surface, is tested with
        /// rays from outside. Surface bodies with a face seen from outside are kept as they are; of the solids only
        /// the faces seen are kept, copied as surfaces. With "Tek gövdeye zorla" each part's kept surfaces are knitted
        /// where SOLIDWORKS manages to.
        /// </summary>
        private void Skin(WrapResult result, ShrinkWrapOptions options, List<IBody2> solids, List<string> solidOwners,
                          List<IBody2> sheets, List<string> sheetOwners, Action<int, int> report,
                          List<(string key, double[] transform)> solidInstances, List<(string key, double[] transform)> sheetInstances,
                          Exclusions exclusions)
        {
            // Which part body each body is and where it sits, for reusing the triangles of repeated parts.
            var instances = solidInstances.Take(solids.Count).Concat(Enumerable.Repeat(((string)null, (double[])null), Math.Max(0, solids.Count - solidInstances.Count)))
                .Concat(sheetInstances.Count == sheets.Count ? sheetInstances : Enumerable.Repeat(((string)null, (double[])null), sheets.Count)).ToList();
            var watch = Stopwatch.StartNew();
            var bodies = solids.Concat(sheets).ToList();
            var owners = solidOwners.Take(solids.Count).Concat(sheetOwners).ToList();
            int solidCount = solids.Count;
            result.SourceBodies = bodies.Count;
            result.IsSkin = true;
            result.ManyBodiesWanted = !options.ForceSingleBody;

            var boxes = bodies.Select(BodyOps.AnyBounds).ToList();
            var model = boxes.Aggregate(Core.Geometry.Aabb.Empty, (a, b) => a.Union(b));
            var keep = Enumerable.Range(0, bodies.Count).ToList();
            // Cages and grilles of many thin parts become one box each, before anything else looks at their parts;
            // cable bundles are deleted.
            var cageBoxes = new List<(string part, IBody2 sheet, List<float> triangles)>();
            var bundles = new List<(string group, List<int> members, Core.Geometry.Aabb box)>();
            var cages = Cages(boxes, owners, keep, exclusions, bundles);
            var bundled = new HashSet<int>(bundles.SelectMany(b => b.members));
            keep = keep.Where(i => !bundled.Contains(i)).ToList();
            result.BundlesRemoved = bundles.Count;
            foreach (var cage in cages)
            {
                var members = new HashSet<int>(cage.members);
                keep = keep.Where(i => !members.Contains(i)).ToList();
                var skin = BoxSkin(cage.box);
                if (skin == null) continue;
                var tri = new List<float>();
                foreach (var p in BoxTriangles(cage.box))
                    tri.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
                cageBoxes.Add((cage.group, skin, tri));
            }
            // Grilles inside one part (a radiator's fin pack imported as thousands of small surfaces) are deleted and
            // a box stands in their place, on the grille's own bounds (Serkan): dense regions of the part's small
            // surfaces, so the frame, plates and pipes around it, all large, stay as they are.
            foreach (var (part, region) in GrilleRegions(boxes, owners, keep.Where(i => i >= solidCount).ToList()))
            {
                var gone = new HashSet<int>(region);
                keep = keep.Where(i => !gone.Contains(i)).ToList();
                var regionBox = region.Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i]));
                var skin = BoxSkin(regionBox);
                if (skin == null) continue;
                var tri = new List<float>();
                foreach (var p in BoxTriangles(regionBox))
                    tri.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
                cageBoxes.Add((part, skin, tri));
            }
            if (options.MinPartPercent > 0)
            {
                double limit = options.MinPartPercent / 100 * (model.Max - model.Min).Length;
                var partBoxes = new Dictionary<string, Core.Geometry.Aabb>();
                for (int i = 0; i < bodies.Count; i++)
                    partBoxes[owners[i] ?? ""] = partBoxes.TryGetValue(owners[i] ?? "", out var pb) ? pb.Union(boxes[i]) : boxes[i];
                var small = new HashSet<string>(partBoxes.Where(kv => kv.Key != "" && (kv.Value.Max - kv.Value.Min).Length < limit).Select(kv => kv.Key));
                keep = keep.Where(i => !small.Contains(owners[i] ?? "")).ToList();
                result.SmallPartsRemoved = small.Count;
                UI.PageLog.Write($"  küçük parçalar: {small.Count} parça ({bodies.Count - keep.Count} gövde) çıkarıldı (%{options.MinPartPercent:0.#} = {limit * 1000:F0} mm altı)");
            }
            // The copies of the bodies left out are released now: SOLIDWORKS keeps them until their COM objects go,
            // and TAG3's skin at 5 % ran out of memory (33 GB) with all 98,281 kept.
            {
                var kept = new HashSet<int>(keep);
                for (int i = 0; i < bodies.Count; i++)
                    if (!kept.Contains(i))
                        bodies[i] = null;
                solids.Clear();
                sheets.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            // Every face of the kept bodies goes into one ray caster; surfaces have no inside, so rays leave them
            // on both sides.
            var visibility = new Core.Geometry.FaceVisibility();
            var faceOf = new List<(int body, IFace2 face)>();
            // The area of each face from its triangles (asking SOLIDWORKS for 60,000 face areas took 26 s on V12).
            var faceArea = new List<double>();
            // (Reusing the triangles of repeated parts, moved into place, saved little and changed the result by
            // one surface on V12: each copy is tessellated a little differently. Each body is read itself.)
            // Grilles inside one part (a radiator core, imported as thousands of small surfaces): flat faces square
            // to an axis, per part and plane, from their triangles (no extra SOLIDWORKS calls).
            var surfacesPerPart = keep.Where(i => i >= solidCount).GroupBy(i => owners[i] ?? "").ToDictionary(g => g.Key, g => g.Count());
            var flatFaces = new List<(string part, int axis, double at, Core.Geometry.Aabb box)>();
            // Large faces get rays from a grid over them, about one point per 2 % of the model's size squared.
            double sampleSpacing = 0.02 * (model.Max - model.Min).Length;
            int done = 0;
            foreach (int i in keep)
            {
                report(done++, keep.Count);
                bool sheet = i >= solidCount;
                bool busy = sheet && (owners[i] ?? "") != "" && surfacesPerPart[owners[i] ?? ""] >= GrillePartSurfaces;
                foreach (IFace2 face in (object[])bodies[i].GetFaces() ?? new object[0])
                {
                    var triangles = face.GetTessTriangles(true) as float[];
                    if (triangles == null || triangles.Length == 0)
                        continue;
                    var normals = face.GetTessNorms() as float[];
                    visibility.AddFace(faceOf.Count, triangles, normals, samples: 4, twoSided: sheet, spacing: sampleSpacing);
                    faceOf.Add((i, face));
                    faceArea.Add(TriangleArea(triangles));
                    if (busy)
                    {
                        var fb = Core.Geometry.Aabb.Empty;
                        for (int t = 0; t + 2 < triangles.Length; t += 3)
                            fb = fb.Include(new Core.Geometry.Vec3(triangles[t], triangles[t + 1], triangles[t + 2]));
                        var extent = fb.Size;
                        for (int axis = 0; axis < 3; axis++)
                            if (extent[axis] < 2e-5)
                            {
                                flatFaces.Add((owners[i], axis, (fb.Min[axis] + fb.Max[axis]) / 2, fb));
                                break;
                            }
                    }
                }
            }
            // Off: on V12 the caps over the radiator grille hid the radiator's frame and shroud too, leaving flat
            // plates in the air.
            var caps = SkinGrilleCaps ? GrilleCaps(flatFaces, keep.Where(i => i >= solidCount).ToList(), owners, boxes)
                : new List<(string part, IBody2 sheet, Core.Geometry.Vec3[] corners, Core.Geometry.Vec3 normal)>();
            double readSeconds = watch.Elapsed.TotalSeconds;
            // Lids on the holes of the surfaces (at most the gap width across). They go into the ray test too,
            // so the walls of a closed hole are no longer seen, and are kept where they are seen themselves.
            var lids = new List<(string owner, IBody2 lid)>();
            int firstLid = faceOf.Count;
            // Off by default: on V12 they added 5742 lid bodies and hid few walls (a through hole's walls are
            // still seen from the other side).
            if (options.GapWidth > 0 && SkinLids)
            {
                var closer = new HoleCloser(new BodyOps(_swApp));
                foreach (int i in keep.Where(i => i >= solidCount))
                {
                    foreach (var (lid, outline, normal) in closer.SheetLids(bodies[i], options.GapWidth))
                    {
                        var centre = outline.Aggregate(new Core.Geometry.Vec3(0, 0, 0), (a, p) => a + p) * (1.0 / outline.Count);
                        var triangles = new List<float>();
                        var normals = new List<float>();
                        for (int k = 0; k < outline.Count; k++)
                            foreach (var p in new[] { centre, outline[k], outline[(k + 1) % outline.Count] })
                            {
                                triangles.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
                                normals.AddRange(new[] { (float)normal.X, (float)normal.Y, (float)normal.Z });
                            }
                        visibility.AddFace(firstLid + lids.Count, triangles, normals, samples: 2, twoSided: true);
                        lids.Add((owners[i], lid));
                    }
                }
                result.HolesClosed = lids.Count;
                UI.PageLog.Write($"  delik kapakları: {lids.Count} ({closer.SkippedCaps} düzensiz ağız kapatılmadı)");
            }

            // The grille caps go into the ray test as well (so what lies behind them is no longer seen) and are
            // always kept.
            int firstCap = firstLid + lids.Count;
            for (int k = 0; k < caps.Count; k++)
            {
                var corners = caps[k].corners;
                var normal = caps[k].normal;
                var tri = new List<float>();
                var nor = new List<float>();
                foreach (int c in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    tri.AddRange(new[] { (float)corners[c].X, (float)corners[c].Y, (float)corners[c].Z });
                    nor.AddRange(new[] { (float)normal.X, (float)normal.Y, (float)normal.Z });
                }
                visibility.AddFace(firstCap + k, tri, nor, samples: 2, twoSided: true);
            }

            // The cage boxes too: what is inside or behind them is no longer seen.
            int firstCage = firstCap + caps.Count;
            for (int k = 0; k < cageBoxes.Count; k++)
                visibility.AddFace(firstCage + k, cageBoxes[k].triangles, null, samples: 0);

            double size = (model.Max - model.Min).Length;
            var escapes = visibility.Escapes(Math.Max(1e-5, size * 2e-5));
            var visible = new HashSet<int>(visibility.Unsampled.Concat(escapes.Where(kv => kv.Value.escaped > 0).Select(kv => kv.Key)));
            UI.PageLog.Write($"  dış yüzler: {visible.Count}/{faceOf.Count} yüz görünüyor, {visibility.Triangles} üçgen " +
                             $"(okuma {readSeconds:F1} sn, ışınlar {watch.Elapsed.TotalSeconds - readSeconds:F1} sn)");

            // Per surface body: the share of its sample points seen from outside, and its area. A surface seen
            // only at a point or two through a gap, or a tiny one, is dropped (SkinMinSeen, SkinMinArea).
            var seenShare = new Dictionary<int, (int escaped, int samples, double area)>();
            for (int f = 0; f < faceOf.Count; f++)
            {
                var (b, face) = faceOf[f];
                if (b < solidCount) continue;
                seenShare.TryGetValue(b, out var c);
                var e = escapes.TryGetValue(f, out var x) ? x : (visibility.Unsampled.Contains(f) ? (1, 1) : (0, 0));
                seenShare[b] = (c.escaped + e.Item1, c.samples + e.Item2, c.area + faceArea[f]);
            }
            double modelArea = size * size;
            // The triangles' area is a little less than the true one on curved faces; near the limit SOLIDWORKS is
            // asked for the exact area, so the choice is the same as with exact areas everywhere.
            double areaLimit = SkinMinArea * modelArea;
            var near = new HashSet<int>(seenShare.Where(kv => kv.Value.area > 0.7 * areaLimit && kv.Value.area < 1.5 * areaLimit).Select(kv => kv.Key));
            var exact = new Dictionary<int, double>();
            for (int f = 0; f < faceOf.Count; f++)
                if (near.Contains(faceOf[f].body))
                    exact[faceOf[f].body] = (exact.TryGetValue(faceOf[f].body, out var a) ? a : 0) + faceOf[f].face.GetArea();
            foreach (var kv in exact)
                seenShare[kv.Key] = (seenShare[kv.Key].escaped, seenShare[kv.Key].samples, kv.Value);
            int asked = near.Count;
            UI.PageLog.Write($"  seyrek kural: {seenShare.Values.Count(v => v.escaped > 0 && v.escaped > SkinMinSeen * v.samples && v.area > areaLimit)} yüzey kalıyor " +
                             $"({asked} yüzeyin alanı SOLIDWORKS'e soruldu, {watch.Elapsed.TotalSeconds:F1} sn)");

            var seenBodies = new HashSet<int>();
            var seenSolidFaces = new List<(int body, IFace2 face)>();
            foreach (int f in visible)
            {
                if (f >= firstLid)
                    continue;
                var (b, face) = faceOf[f];
                if (b >= solidCount)
                {
                    var v = seenShare[b];
                    if (v.escaped > SkinMinSeen * v.samples && v.area > SkinMinArea * modelArea)
                        seenBodies.Add(b);
                }
                else seenSolidFaces.Add((b, face));
            }
            // Large surfaces dropped here, for tracing a part that goes missing (TAG3's radiator front plates).
            foreach (int b in keep.Where(b => b >= solidCount && !seenBodies.Contains(b) && (boxes[b].Max - boxes[b].Min).Length > 0.1 * size))
            {
                seenShare.TryGetValue(b, out var v);
                var s = boxes[b].Size;
                // What a ray straight out of its middle hits first, on each side.
                string blockers = "";
                if (s.X * s.Y + s.Y * s.Z + s.X * s.Z > 0.01 * size * size &&
                    ((object[])bodies[b].GetFaces())?.OfType<IFace2>().FirstOrDefault() is IFace2 first &&
                    first.GetTessTriangles(true) is float[] tri && first.GetTessNorms() is float[] nor && tri.Length >= 9)
                {
                    var c = new Core.Geometry.Vec3((tri[0] + tri[3] + tri[6]) / 3, (tri[1] + tri[4] + tri[7]) / 3, (tri[2] + tri[5] + tri[8]) / 3);
                    var n = new Core.Geometry.Vec3(nor[0] + nor[3] + nor[6], nor[1] + nor[4] + nor[7], nor[2] + nor[5] + nor[8]);
                    n = n * (1 / Math.Max(n.Length, 1e-12));
                    foreach (double side in new[] { 1.0, -1.0 })
                    {
                        var (hit, distance) = visibility.FirstHit(c + n * (side * size * 2e-5), n * side);
                        string what = hit < 0 ? "dışarı" : hit < faceOf.Count ? $"{owners[faceOf[hit].body] ?? "?"} ({(faceOf[hit].body >= solidCount ? "yüzey" : "katı")})" : "kafes/kapak kutusu";
                        blockers += $"; {(side > 0 ? "+" : "-")}normal: {what}{(hit < 0 ? "" : $" {distance * 1000:F0} mm")}";
                    }
                    blockers += $"; orta {c.X * 1000:F0},{c.Y * 1000:F0},{c.Z * 1000:F0} mm";
                }
                UI.PageLog.Write($"  büyük yüzey düştü: {owners[b]}, kutu {s.X * 1000:F0}x{s.Y * 1000:F0}x{s.Z * 1000:F0} mm, {bodies[b].GetFaceCount()} yüz, " +
                                 $"görülen {v.escaped}/{v.samples} örnek, alan {v.area * 1e6:F0} mm²{blockers}");
            }
            var output = new List<(string owner, IBody2 body)>();
            foreach (int b in seenBodies.OrderBy(b => b))
                output.Add((owners[b], bodies[b]));
            // The seen faces of each solid are knitted back into one surface body, as in Shells (faces of a real
            // solid knit; a 1.2 GB STEP with 8,500 solids gave 111,000 single-face bodies otherwise).
            int solidFailed = 0, solidsWhole = 0;
            var knitModeler = (IModeler)_swApp.GetModeler();
            // The solids seen from outside get the solid path's simplification (Serkan: the bolt holes of TAG3's
            // flywheel housing stayed open): holes plugged, threads filled, embossed details flattened; then they go
            // in whole. (Their seen faces did not knit on TAG3 anyway, so they went in whole before too.)
            if (SkinSolidWork && (options.GapWidth > 0 || options.FillThreads || options.MaxEmbossHeight > 0))
            {
                var solidWatch = Stopwatch.StartNew();
                var ops = new BodyOps(_swApp);
                var closer = new HoleCloser(ops);
                var threads = new ThreadFiller(ops);
                // The most visible solids first, within a time budget: TAG3's 414 seen solids (imported castings)
                // needed 40 s each; those left over go in as they are.
                var seenSolids = seenSolidFaces.GroupBy(f => f.body).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                int n = 0, skipped = 0;
                foreach (int b in seenSolids)
                {
                    report(n++, seenSolids.Count);
                    var body = bodies[b];
                    if (solidWatch.Elapsed.TotalSeconds > SkinSolidSeconds)
                    {
                        output.Add((owners[b], body));
                        skipped++;
                        continue;
                    }
                    if (options.FillThreads)
                        body = threads.Fill(body);
                    if (options.MaxEmbossHeight > 0)
                        body = closer.RemoveBosses(body, options.MaxEmbossHeight);
                    if (options.GapWidth > 0)
                        body = closer.CloseHoles(body, options.GapWidth) ?? body;
                    output.Add((owners[b], body));
                }
                result.HolesClosed = closer.Closed;
                result.HolesFailed = closer.Failed;
                result.ThreadsFilled = threads.Filled;
                result.BossesRemoved = closer.BossesRemoved;
                UI.PageLog.Write($"  görünen katılar: {seenSolids.Count} katı sadeleştirildi ({closer.Closed} delik kapandı, {closer.Failed} kapanmadı, " +
                                 $"{threads.Filled} diş, {closer.BossesRemoved} kabartma; {skipped} tanesi {SkinSolidSeconds:F0} sn bütçesi dolduğu için olduğu gibi; " +
                                 $"{solidWatch.Elapsed.TotalSeconds:F1} sn)");
                seenSolidFaces.Clear();
            }
            foreach (var group in seenSolidFaces.GroupBy(f => f.body))
            {
                var faceSheets = new List<IBody2>();
                foreach (var (_, face) in group)
                {
                    if (face.CreateSheetBody() is IBody2 copy) faceSheets.Add(copy);
                    else solidFailed++;
                }
                if (faceSheets.Count == 0)
                    continue;
                int error = 0;
                List<IBody2> knit = null;
                if (faceSheets.Count > 1)
                {
                    try { knit = ((object[])knitModeler.CreateBodiesFromSheets2(faceSheets.Select(f => (object)f.Copy()).ToArray(), SheetBodiesOnly, 1e-4, ref error))?.OfType<IBody2>().ToList(); }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (knit != null && knit.Count > 0)
                    output.AddRange(knit.Select(k => (owners[group.Key], k)));
                else if (faceSheets.Count == 1)
                    output.Add((owners[group.Key], faceSheets[0]));
                else
                {
                    // Faces of imported solids do not knit here (TAG3: 111,000 single faces): the solid goes in whole.
                    output.Add((owners[group.Key], bodies[group.Key]));
                    solidsWhole++;
                }
            }
            if (solidFailed > 0)
                result.Notes.Add($"{solidFailed} katı yüzü yüzeye çevrilemedi.");
            UI.PageLog.Write($"  katılar: {seenSolidFaces.Select(f => f.body).Distinct().Count()} katının görünen yüzleri alındı, {solidsWhole} tanesi dikilemediği için bütün olarak kondu");
            var seenLids = lids.Where((l, k) => visible.Contains(firstLid + k)).ToList();
            output.AddRange(seenLids);
            output.AddRange(caps.Select(c => (c.part, c.sheet)));
            output.AddRange(cageBoxes.Select(c => (c.part, c.sheet)));
            result.KeepAlways.UnionWith(cageBoxes.Select(c => c.sheet));
            if (lids.Count > 0)
                UI.PageLog.Write($"  görünen kapak: {seenLids.Count}/{lids.Count}");
            var perPart = output.GroupBy(o => o.owner ?? "").Select(g => (part: g.Key, count: g.Count())).OrderByDescending(p => p.count).ToList();
            UI.PageLog.Write($"  parça başına yüzey: {perPart.Count} parça; en çok: " +
                             string.Join(", ", perPart.Take(15).Select(p => $"{p.part} {p.count}")) +
                             $"; 100+ yüzeyli {perPart.Count(p => p.count >= 100)} parça toplam {perPart.Where(p => p.count >= 100).Sum(p => p.count)} yüzey");

            // A part that shows very many separate surfaces (a radiator core, a grille: SOLIDWORKS cannot knit or
            // unite imported surfaces) is replaced by the surfaces of its box, which also closes its openings.
            if (MaxSheetsPerPart > 0)
            {
                var ops = new BodyOps(_swApp);
                var modeler = (IModeler)_swApp.GetModeler();
                // Only grid-like parts: most of their surfaces are flat and square to the axes, and the part is
                // plate-like (its box much thinner one way). An engine block is neither and stays exact.
                bool GridLike(string part)
                {
                    var faces = output.Where(o => o.owner == part).SelectMany(o => ((object[])o.body.GetFaces() ?? new object[0]).OfType<IFace2>()).ToList();
                    int square = faces.Count(f =>
                    {
                        var surface = (ISurface)f.GetSurface();
                        if (surface == null || !surface.IsPlane()) return false;
                        var p = (double[])surface.PlaneParams;
                        double m = Math.Max(Math.Abs(p[0]), Math.Max(Math.Abs(p[1]), Math.Abs(p[2])));
                        return m > 0.999;
                    });
                    var box = Enumerable.Range(0, bodies.Count).Where(i => owners[i] == part)
                        .Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i]));
                    var s = box.Size;
                    double thin = Math.Min(s.X, Math.Min(s.Y, s.Z)), wide = Math.Max(s.X, Math.Max(s.Y, s.Z));
                    bool grid = faces.Count > 0 && square >= 0.8 * faces.Count && thin <= 0.25 * wide;
                    UI.PageLog.Write($"  çok yüzeyli parça {part}: {faces.Count} yüz, %{100.0 * square / Math.Max(1, faces.Count):F0} eksene dik düz, " +
                                     $"kutu {s.X * 1000:F0}x{s.Y * 1000:F0}x{s.Z * 1000:F0} mm → {(grid ? "kutu" : "olduğu gibi")}");
                    return grid;
                }
                var boxed = new HashSet<string>(perPart.Where(p => p.part != "" && p.count > MaxSheetsPerPart && GridLike(p.part)).Select(p => p.part));
                var replaced = new List<(string owner, IBody2 body)>();
                int boxedSheets = 0;
                foreach (var part in boxed)
                {
                    var box = Enumerable.Range(0, bodies.Count).Where(i => owners[i] == part && keep.Contains(i))
                        .Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i]));
                    var solid = ops.Box(box);
                    var faces = ((object[])solid?.GetFaces() ?? new object[0]).OfType<IFace2>().Select(f => f.CreateSheetBody() as IBody2).Where(s => s != null).ToArray();
                    int error = 0;
                    var skin = faces.Length == 6
                        ? ((object[])modeler.CreateBodiesFromSheets2(faces, SheetBodiesOnly, 1e-5, ref error))?.OfType<IBody2>().FirstOrDefault()
                        : null;
                    if (skin == null && solid == null)
                        continue;
                    boxedSheets += output.Count(o => o.owner == part);
                    replaced.Add((part, skin ?? solid));
                }
                output = output.Where(o => !boxed.Contains(o.owner ?? "")).Concat(replaced).ToList();
                UI.PageLog.Write($"  çok yüzeyli parçalar kutuya çevrildi: {replaced.Count} parça, {boxedSheets} yüzey yerine (sınır {MaxSheetsPerPart})");
            }

            if (options.ForceSingleBody)
            {
                var modeler = (IModeler)_swApp.GetModeler();
                var knitted = new List<(string, IBody2)>();
                int joined = 0;
                foreach (var group in output.GroupBy(o => o.owner ?? ""))
                {
                    var parts = group.Select(g => g.body).ToList();
                    int error = 0;
                    List<IBody2> sewn = null;
                    if (parts.Count > 1)
                    {
                        try { sewn = ((object[])modeler.CreateBodiesFromSheets2(parts.Select(p => (object)p.Copy()).ToArray(), SheetBodiesOnly, 1e-4, ref error))?.OfType<IBody2>().ToList(); }
                        catch (System.Runtime.InteropServices.COMException) { }
                    }
                    if (sewn != null && sewn.Count > 0 && sewn.Count < parts.Count)
                    {
                        joined += parts.Count - sewn.Count;
                        knitted.AddRange(sewn.Select(s => (group.Key, s)));
                    }
                    else
                        knitted.AddRange(parts.Select(p => (group.Key, p)));
                }
                UI.PageLog.Write($"  dikme: {output.Count} yüzey → {knitted.Count} ({joined} birleşti)");
                output = knitted;
            }
            // Large parts with nothing in the result, and why: for tracing a part that goes missing.
            {
                var inOutput = new HashSet<string>(output.Select(o => o.owner ?? ""));
                var keptSet = new HashSet<int>(keep);
                var lost = Enumerable.Range(0, bodies.Count).Where(i => !string.IsNullOrEmpty(owners[i]))
                    .GroupBy(i => owners[i]).Select(g => (part: g.Key, box: g.Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i])), members: g.ToList()))
                    .Where(p => (p.box.Max - p.box.Min).Length > 0.1 * size && !inOutput.Contains(p.part)).ToList();
                foreach (var p in lost.Take(30))
                {
                    var s = p.box.Size;
                    bool kept = p.members.Any(keptSet.Contains);
                    UI.PageLog.Write($"  büyük parça sonuçta yok: {p.part}, kutu {s.X * 1000:F0}x{s.Y * 1000:F0}x{s.Z * 1000:F0} mm, {p.members.Count} gövde " +
                                     $"({p.members.Count(i => i >= solidCount)} yüzey), {(kept ? "ışın/seyrek kuralda düştü" : "kafes ya da küçük parça olarak çıkarıldı")}");
                }
                UI.PageLog.Write($"  büyük parça sonuçta yok: toplam {lost.Count}");
            }
            result.Bodies = output.Select(o => o.body).ToList();
            result.Sheets = seenBodies.Count;
            // Everything else is let go before the result goes into a part: the faces read for the ray test (355,000
            // on TAG3 at 5 %) and the copies of the bodies not kept. With them held, inserting TAG3's skin took
            // SOLIDWORKS to 38 GB and the machine ran out of memory.
            {
                var used = new HashSet<IBody2>(result.Bodies);
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i] != null && !used.Contains(bodies[i]))
                        bodies[i] = null;
                faceOf.Clear();
                faceOf.TrimExcess();
                visibility = null;
                escapes = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            UI.PageLog.Write($"  dış kabuk (yüzey modeli): {keep.Count} gövde → {result.Bodies.Count} yüzey gövdesi, " +
                             $"{result.Bodies.Sum(b => b.GetFaceCount())} yüz ({watch.Elapsed.TotalSeconds:F1} sn)");
        }

        /// <summary>
        /// Cages and cable bundles: sub-assemblies made of many separate thin parts with mostly air between them. Per
        /// sub-assembly (the parts' common parent), it counts when it has at least 20 parts, nearly all thin (their two
        /// smaller sides under 3 % of the group's longest), together filling under 10 % of the group's box, none of
        /// them a big block, and nothing in it excluded. A group whose box holds many other parts, or whose members
        /// are elongated, is a cable bundle and is deleted (Serkan); the others are cages and grilles, each boxed with
        /// the few parts inside. Returns the members (body indices) and the group's box of each.
        /// </summary>
        private static List<(string group, List<int> members, Core.Geometry.Aabb box)> Cages(
            List<Core.Geometry.Aabb> boxes, List<string> owners, IEnumerable<int> candidates, Exclusions exclusions,
            List<(string group, List<int> members, Core.Geometry.Aabb box)> bundles)
        {
            var cages = new List<(string, List<int>, Core.Geometry.Aabb)>();
            var parts = candidates.Where(i => !string.IsNullOrEmpty(owners[i]) && !boxes[i].IsEmpty)
                .GroupBy(i => owners[i]).Select(g => (part: g.Key, members: g.ToList(), box: g.Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i])))).ToList();
            int allParts = parts.Count;
            var taken = new HashSet<string>();
            // First the parts' own parent assemblies, whole: cages and grilles become boxes, bundles are deleted
            // with everything in them (V12's fan cages and engine harness, TAG3's six SCHEME harnesses).
            foreach (var group in parts.Where(p => p.part.Contains("/")).GroupBy(p => p.part.Substring(0, p.part.LastIndexOf('/'))))
            {
                var list = group.ToList();
                if (list.Count < 20 || list.Count > allParts / 2)
                    continue;
                if (exclusions != null && exclusions.AnyUnder(group.Key))
                    continue;
                var box = list.Aggregate(Core.Geometry.Aabb.Empty, (a, p) => a.Union(p.box));
                double longest = box.LongestSide, volume = box.Size.X * box.Size.Y * box.Size.Z;
                if (longest <= 0 || volume <= 0)
                    continue;
                // Rods and wires: thin in two directions (a sheet-metal housing of thin plates is not a cage).
                int thin = list.Count(p => MiddleSide(p.box) < 0.03 * longest);
                double filled = list.Sum(p => p.box.Size.X * p.box.Size.Y * p.box.Size.Z);
                bool block = list.Any(p => p.box.Size.X * p.box.Size.Y * p.box.Size.Z > 0.1 * volume);
                if (thin < 0.9 * list.Count || block)
                    continue;
                // Rods together fill much of their box when they are a dense grid, a radiator's grille (TAG3's
                // 6425356: 33 %); a housing of plates never gets here, its plates are not thin both ways.
                if (filled > 0.5 * volume)
                {
                    UI.PageLog.Write($"  kafes/ızgara değil: {group.Key}, {list.Count} ince parça ama kutusunun %{100 * filled / volume:F0}'ini dolduruyor");
                    continue;
                }
                // Cage or cable bundle. A bundle runs across the machine, so its box holds many other parts (V12's
                // engine wiring: 643 in 89; TAG3's SCHEME groups 550 to 1049), or its members are elongated pieces
                // of a curved run (median middle/longest side of the members' boxes 0.42 to 0.63 on TAG3). A guard
                // or grille holds few parts and is made of short, squat pieces (V12's fan cages: 0.98 and 1.0).
                var own = new HashSet<string>(list.Select(p => p.part));
                double diag = (box.Max - box.Min).Length;
                var inside = parts.Where(p => !own.Contains(p.part) && (p.box.Max - p.box.Min).Length > 0.05 * diag &&
                                              Inside(box, (p.box.Min + p.box.Max) * 0.5)).ToList();
                var ratios = list.Select(p => Squat(p.box)).OrderBy(v => v).ToList();
                double squat = ratios[ratios.Count / 2];
                var size = box.Size;
                foreach (var p in list) taken.Add(p.part);
                if (inside.Count > 0.25 * list.Count || squat < BundleSquat)
                {
                    bundles.Add((group.Key, list.SelectMany(p => p.members).ToList(), box));
                    UI.PageLog.Write($"  kablo demeti: {group.Key}, {list.Count} ince parça, kutu {size.X * 1000:F0}x{size.Y * 1000:F0}x{size.Z * 1000:F0} mm, " +
                                     $"içinde {inside.Count} başka parça, üye oranı {squat:F2} → silinir");
                    continue;
                }
                // The parts inside the cage go with it.
                foreach (var p in inside) taken.Add(p.part);
                cages.Add((group.Key, list.SelectMany(p => p.members).Concat(inside.SelectMany(p => p.members)).ToList(), box));
                UI.PageLog.Write($"  kafes/ızgara: {group.Key}, {list.Count} ince parça, kutu {size.X * 1000:F0}x{size.Y * 1000:F0}x{size.Z * 1000:F0} mm, " +
                                 $"doluluk %{100 * filled / volume:F0}, içinde {inside.Count} parça, üye oranı {squat:F2} → tek kutu");
            }
            // Then the wires left in harnesses the rule above does not take whole (TAG3's wiring along the cylinder
            // heads: harness assemblies holding connectors, brackets and boxes too, their wires split over
            // sub-assemblies of a few each). At every assembly level, deepest first: a group with at least 20
            // wires, at least half its parts, running across the machine or curved, loses its wires only. A wire is
            // thin in two directions for its group, under 40 mm thick (pipes are thicker) and at least 50 mm long
            // (bolts and clips are short); connectors, blocks and pipes stay.
            var levels = new Dictionary<string, List<(string part, List<int> members, Core.Geometry.Aabb box)>>();
            foreach (var p in parts.Where(p => !taken.Contains(p.part)))
                for (int slash = p.part.IndexOf('/'); slash > 0; slash = p.part.IndexOf('/', slash + 1))
                {
                    string key = p.part.Substring(0, slash);
                    if (!levels.TryGetValue(key, out var l)) levels[key] = l = new List<(string, List<int>, Core.Geometry.Aabb)>();
                    l.Add(p);
                }
            int nearMisses = 0;
            foreach (var level in levels.OrderByDescending(kv => kv.Key.Count(c => c == '/')).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var list = level.Value.Where(p => !taken.Contains(p.part)).ToList();
                if (list.Count < 20 || list.Count > allParts / 2)
                    continue;
                if (exclusions != null && exclusions.AnyUnder(level.Key))
                    continue;
                var box = list.Aggregate(Core.Geometry.Aabb.Empty, (a, p) => a.Union(p.box));
                double longest = box.LongestSide;
                if (longest <= 0)
                    continue;
                var wires = list.Where(p => MiddleSide(p.box) < Math.Min(0.03 * longest, WireThickness) && p.box.LongestSide >= WireLength).ToList();
                if (wires.Count < 20)
                    continue;
                var own = new HashSet<string>(list.Select(p => p.part));
                double diag = (box.Max - box.Min).Length;
                int inside = parts.Count(p => !own.Contains(p.part) && (p.box.Max - p.box.Min).Length > 0.05 * diag && Inside(box, (p.box.Min + p.box.Max) * 0.5));
                var ratios = wires.Select(p => Squat(p.box)).OrderBy(v => v).ToList();
                double squat = ratios[ratios.Count / 2];
                var size = box.Size;
                if (wires.Count < 0.5 * list.Count || !(inside > 0.25 * wires.Count || squat < BundleSquat))
                {
                    if (nearMisses++ < 40)
                        UI.PageLog.Write($"  tel grubu değil: {level.Key}, {list.Count} parça, {wires.Count} tel, içinde {inside} başka parça, üye oranı {squat:F2}, " +
                                         $"kutu {size.X * 1000:F0}x{size.Y * 1000:F0}x{size.Z * 1000:F0} mm, orta {(box.Min.X + box.Max.X) * 500:F0},{(box.Min.Y + box.Max.Y) * 500:F0},{(box.Min.Z + box.Max.Z) * 500:F0}");
                    continue;
                }
                foreach (var p in wires) taken.Add(p.part);
                var wireBox = wires.Aggregate(Core.Geometry.Aabb.Empty, (a, p) => a.Union(p.box));
                bundles.Add((level.Key, wires.SelectMany(p => p.members).ToList(), wireBox));
                UI.PageLog.Write($"  kablo telleri: {level.Key}, {list.Count} parçanın {wires.Count} teli silinir, kutu {size.X * 1000:F0}x{size.Y * 1000:F0}x{size.Z * 1000:F0} mm, " +
                                 $"içinde {inside} başka parça, üye oranı {squat:F2}, orta {(box.Min.X + box.Max.X) * 500:F0},{(box.Min.Y + box.Max.Y) * 500:F0},{(box.Min.Z + box.Max.Z) * 500:F0}");
            }
            return cages;
        }

        /// <summary>
        /// Grilles inside one part (a radiator imported as tens of thousands of small surfaces): its panels are thin
        /// dense layers of small surfaces (under 5 % of the part's longest side). Along each axis the surfaces'
        /// middles are counted in 20 mm slices; a slice holding at least 4 times the median and 300 is a layer,
        /// with the slices next to it (louvres slant). In its plane, the layer's surfaces are put in 100 mm cells;
        /// touching cells of at least 4 form a panel, a grille when it has at least 100 surfaces, both sides at
        /// least 150 mm, one at least 300 mm, and covers at least half its rectangle (a perforated flange or a row
        /// of rivets does not). Everything else of the part, its frame, plates and pipes, keeps its geometry
        /// (Serkan: "only the grilles"; one box over TAG3's radiator region of 946x2848x2184 mm was too coarse).
        /// Returns the surfaces of each panel.
        /// </summary>
        private static List<(string part, List<int> bodies)> GrilleRegions(List<Core.Geometry.Aabb> boxes, List<string> owners, List<int> sheets)
        {
            var found = new List<(string, List<int>)>();
            const double slice = 0.02, cell = 0.1;
            foreach (var part in sheets.Where(i => !string.IsNullOrEmpty(owners[i]) && !boxes[i].IsEmpty).GroupBy(i => owners[i]))
            {
                if (part.Count() < GrillePartSurfaces)
                    continue;
                var partBox = part.Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i]));
                var small = part.Where(i => boxes[i].LongestSide < 0.05 * partBox.LongestSide).ToList();
                // GEOSIMPLIFIER_DUMP (tests only): the boxes of a big part's small surfaces, to tune grille finding
                // without SOLIDWORKS.
                if (System.Environment.GetEnvironmentVariable("GEOSIMPLIFIER_DUMP") is string dump && dump != "" && small.Count >= 5000)
                    try
                    {
                        System.IO.File.WriteAllLines(System.IO.Path.Combine(dump, $"grille_{Math.Abs(part.Key.GetHashCode())}.csv"),
                            new[] { "# " + part.Key }.Concat(small.Select(i => string.Join(",", new[] { boxes[i].Min.X, boxes[i].Min.Y, boxes[i].Min.Z, boxes[i].Max.X, boxes[i].Max.Y, boxes[i].Max.Z }
                                .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));
                    }
                    catch (System.IO.IOException) { }
                var taken = new HashSet<int>();
                int panels = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    double Mid(int i, int a) => (boxes[i].Min[a] + boxes[i].Max[a]) / 2;
                    double start = small.Min(i => Mid(i, axis));
                    var counts = small.GroupBy(i => (int)Math.Floor((Mid(i, axis) - start) / slice)).ToDictionary(g => g.Key, g => g.Count());
                    var sorted = counts.Values.OrderBy(v => v).ToList();
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
                            var box = panel.Aggregate(Core.Geometry.Aabb.Empty, (a, i) => a.Union(boxes[i]));
                            double su = box.Size[ua], sv = box.Size[va];
                            double cover = panelCells / Math.Max(1.0, Math.Ceiling(su / cell) * Math.Ceiling(sv / cell));
                            if (panel.Count < 100 || Math.Min(su, sv) < 0.15 || Math.Max(su, sv) < 0.3 || cover < 0.5)
                                continue;
                            foreach (int i in panel) taken.Add(i);
                            panels++;
                            found.Add((part.Key, panel));
                            var s = box.Size;
                            UI.PageLog.Write($"  ızgara → kutu: {part.Key}, {panel.Count}/{part.Count()} yüzey gövdesi, kutu {s.X * 1000:F0}x{s.Y * 1000:F0}x{s.Z * 1000:F0} mm, " +
                                             $"{"XYZ"[axis]} yönünde {s[axis] * 1000:F0} mm, doluluk %{100 * cover:F0}");
                        }
                    }
                }
                if (panels == 0)
                    UI.PageLog.Write($"  ızgara arama: {part.Key}, {part.Count()} yüzey gövdesi, {small.Count} küçük, ızgara yok");
                else
                    UI.PageLog.Write($"  ızgara arama: {part.Key}, {panels} panel, {taken.Count}/{small.Count} küçük yüzey kutuya döndü, kalanı olduğu gibi");
            }
            return found;
        }

        // A wire of a harness: thinner than a pipe, longer than a bolt or clip.
        private const double WireThickness = 0.04;
        private const double WireLength = 0.05;

        /// <summary>The middle one of a box's three sides.</summary>
        private static double MiddleSide(Core.Geometry.Aabb b) => new[] { b.Size.X, b.Size.Y, b.Size.Z }.OrderBy(v => v).ElementAt(1);

        /// <summary>A box's middle side over its longest: low for rods and wires, near 1 for squat pieces.</summary>
        private static double Squat(Core.Geometry.Aabb b) => MiddleSide(b) / Math.Max(b.LongestSide, 1e-12);

        private static bool Inside(Core.Geometry.Aabb box, Core.Geometry.Vec3 p) =>
            p.X >= box.Min.X && p.X <= box.Max.X && p.Y >= box.Min.Y && p.Y <= box.Max.Y && p.Z >= box.Min.Z && p.Z <= box.Max.Z;

        /// <summary>Whether two boxes touch or overlap, within 0.5 mm.</summary>
        private static bool Touch(Core.Geometry.Aabb a, Core.Geometry.Aabb b) =>
            a.Min.X <= b.Max.X + 5e-4 && b.Min.X <= a.Max.X + 5e-4 && a.Min.Y <= b.Max.Y + 5e-4 && b.Min.Y <= a.Max.Y + 5e-4 &&
            a.Min.Z <= b.Max.Z + 5e-4 && b.Min.Z <= a.Max.Z + 5e-4;

        /// <summary>A box's six faces as one surface body (or the solid box when they do not knit).</summary>
        private IBody2 BoxSkin(Core.Geometry.Aabb box)
        {
            var solid = new BodyOps(_swApp).Box(box);
            if (solid == null)
                return null;
            var faces = ((object[])solid.GetFaces() ?? new object[0]).OfType<IFace2>().Select(f => f.CreateSheetBody() as IBody2).Where(s => s != null).ToArray();
            int error = 0;
            IBody2 skin = null;
            if (faces.Length == 6)
            {
                try { skin = ((object[])((IModeler)_swApp.GetModeler()).CreateBodiesFromSheets2(faces, SheetBodiesOnly, 1e-5, ref error))?.OfType<IBody2>().FirstOrDefault(); }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            return skin ?? solid;
        }

        // A part showing at least this many surfaces is looked at for grilles, and a dense slab of at least
        // GrilleMinFaces small flat faces in it is capped.
        private const int GrillePartSurfaces = 200;
        private const int GrilleMinFaces = 300;

        /// <summary>
        /// Flat lids over grilles inside one part. V12's radiator grille is a grid of 2 mm slats 60 mm deep: thousands
        /// of small flat faces square to the axes, on many planes, filling a thin slab. Per part, those faces are
        /// put in 100 mm cells; touching cells holding at least 4 faces form a region (the radiator grille has about 8 a cell). A region of many faces whose
        /// box is a slab (its thinnest side under 15 % of the next) gets a rectangle on each of its two broad sides,
        /// 0.5 mm outside. Only small faces count, so a part's large flat faces never make a slab.
        /// </summary>
        private List<(string part, IBody2 sheet, Core.Geometry.Vec3[] corners, Core.Geometry.Vec3 normal)> GrilleCaps(
            List<(string part, int axis, double at, Core.Geometry.Aabb box)> flatFaces, List<int> sheetBodies, List<string> owners,
            List<Core.Geometry.Aabb> boxes)
        {
            var caps = new List<(string, IBody2, Core.Geometry.Vec3[], Core.Geometry.Vec3)>();
            if (flatFaces.Count == 0)
                return caps;
            var modeler = (IModeler)_swApp.GetModeler();
            const double cell = 0.1;
            foreach (var part in flatFaces.GroupBy(f => f.part))
            {
                var partBox = part.Aggregate(Core.Geometry.Aabb.Empty, (a, f) => a.Union(f.box));
                // Small faces: under 5 % of the part's longest side both ways.
                var small = part.Where(f => { var s = f.box.Size; return Math.Max(s.X, Math.Max(s.Y, s.Z)) < 0.05 * partBox.LongestSide; }).ToList();
                if (small.Count < GrilleMinFaces)
                {
                    if (part.Count() >= GrilleMinFaces)
                        UI.PageLog.Write($"  ızgara arama: {part.Key}, {part.Count()} düz yüz ama yalnız {small.Count} tanesi küçük");
                    continue;
                }
                (long, long, long) Cell(Core.Geometry.Vec3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
                var cells = small.GroupBy(f => Cell((f.box.Min + f.box.Max) * 0.5)).Where(g => g.Count() >= 4).ToDictionary(g => g.Key, g => g.ToList());
                UI.PageLog.Write($"  ızgara arama: {part.Key}, {part.Count()} düz yüz, {small.Count} küçük, {cells.Count} yoğun hücre");
                var seen = new HashSet<(long, long, long)>();
                foreach (var startCell in cells.Keys)
                {
                    if (!seen.Add(startCell)) continue;
                    var region = new List<(string part, int axis, double at, Core.Geometry.Aabb box)>();
                    var queue = new Queue<(long, long, long)>();
                    queue.Enqueue(startCell);
                    while (queue.Count > 0)
                    {
                        var c = queue.Dequeue();
                        region.AddRange(cells[c]);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    var nb = (c.Item1 + dx, c.Item2 + dy, c.Item3 + dz);
                                    if (cells.ContainsKey(nb) && seen.Add(nb))
                                        queue.Enqueue(nb);
                                }
                    }
                    if (region.Count < GrilleMinFaces)
                        continue;
                    var slab = region.Aggregate(Core.Geometry.Aabb.Empty, (a, f) => a.Union(f.box));
                    var order = Enumerable.Range(0, 3).OrderBy(a => slab.Size[a]).ToArray();
                    int axis = order[0], ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    if (slab.Size[axis] > 0.15 * slab.Size[order[1]] || slab.Size[order[1]] < 0.2)
                    {
                        UI.PageLog.Write($"  ızgara değil: {part.Key}, {region.Count} küçük yüz ama katman değil " +
                                         $"({slab.Size.X * 1000:F0}x{slab.Size.Y * 1000:F0}x{slab.Size.Z * 1000:F0} mm)");
                        continue;
                    }
                    foreach (double side in new[] { -1.0, 1.0 })
                    {
                        double capAt = side < 0 ? slab.Min[axis] - 5e-4 : slab.Max[axis] + 5e-4;
                        Core.Geometry.Vec3 Point(double u, double v)
                        {
                            var p = new double[3];
                            p[axis] = capAt; p[ua] = u; p[va] = v;
                            return new Core.Geometry.Vec3(p[0], p[1], p[2]);
                        }
                        var corners = new[] { Point(slab.Min[ua], slab.Min[va]), Point(slab.Max[ua], slab.Min[va]),
                                              Point(slab.Max[ua], slab.Max[va]), Point(slab.Min[ua], slab.Max[va]) };
                        var n = new double[3]; n[axis] = side;
                        var normal = new Core.Geometry.Vec3(n[0], n[1], n[2]);
                        var u = new double[3]; u[ua] = 1;
                        var centre = Point((slab.Min[ua] + slab.Max[ua]) / 2, (slab.Min[va] + slab.Max[va]) / 2);
                        double vSign = Core.Geometry.Vec3.Cross(normal, new Core.Geometry.Vec3(u[0], u[1], u[2]))[va];
                        var surface = modeler.CreatePlanarSurface2(new[] { centre.X, centre.Y, centre.Z }, n, u) as Surface;
                        var sheet = surface == null ? null : modeler.CreateSheetFromSurface(surface, new[]
                        {
                            -slab.Size[ua] / 2, slab.Size[ua] / 2,
                            Math.Min(-slab.Size[va] / 2 * vSign, slab.Size[va] / 2 * vSign), Math.Max(-slab.Size[va] / 2 * vSign, slab.Size[va] / 2 * vSign),
                        }) as IBody2;
                        if (sheet != null)
                            caps.Add((part.Key, sheet, corners, normal));
                    }
                    UI.PageLog.Write($"  ızgara kapağı: {part.Key}, {region.Count} küçük yüz, {slab.Size.X * 1000:F0}x{slab.Size.Y * 1000:F0}x{slab.Size.Z * 1000:F0} mm katman, " +
                                     $"iki yüzü {"XYZ"[axis]} yönünde kapatıldı");
                }
            }
            return caps;
        }
        /// <summary>The area of triangles given as nine numbers each.</summary>
        private static double TriangleArea(float[] t)
        {
            double area = 0;
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                double ux = t[i + 3] - t[i], uy = t[i + 4] - t[i + 1], uz = t[i + 5] - t[i + 2];
                double vx = t[i + 6] - t[i], vy = t[i + 7] - t[i + 1], vz = t[i + 8] - t[i + 2];
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                area += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
            }
            return area;
        }

        /// <summary>The twelve triangles of a box's faces.</summary>
        private static List<Core.Geometry.Vec3> BoxTriangles(Core.Geometry.Aabb b)
        {
            Core.Geometry.Vec3 P(int i) => new Core.Geometry.Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
            var t = new List<Core.Geometry.Vec3>();
            void Quad(int a, int c, int d, int e) => t.AddRange(new[] { P(a), P(c), P(d), P(a), P(d), P(e) });
            Quad(0, 1, 3, 2); Quad(4, 5, 7, 6); Quad(0, 1, 5, 4); Quad(2, 3, 7, 6); Quad(0, 2, 6, 4); Quad(1, 3, 7, 5);
            return t;
        }

        /// <summary>
        /// For a solid result of a large assembly: the parts no ray from outside reaches are taken out of the wrap's
        /// work (hole closing, insides, uniting) and stand in as their boxes, which unite quickly. A box is used only
        /// where it stays hidden too (rays from outside still reach nothing of it); otherwise the part stays as it is.
        /// So the inside stays solid where they were and the outside does not change. Returns the boxes.
        /// </summary>
        private List<IBody2> HiddenPartsAsBoxes(BodyOps ops, List<IBody2> bodies, List<(string key, double[] transform)> instances,
                                                Action<int, int> report)
        {
            int solids = instances.Count;
            var triangles = new List<List<Core.Geometry.Vec3>>();
            var watch = Stopwatch.StartNew();
            var seen = SeenFromOutside(bodies.Take(solids).ToList(), instances, report, triangles);
            UI.PageLog.Write($"  iç parçalar: ışınlar {watch.Elapsed.TotalSeconds:F1} sn ({solids} gövde, {triangles.Sum(t => t.Count) / 3} üçgen)");
            var hidden = Enumerable.Range(0, solids).Where(i => !seen.Contains(i)).ToList();
            if (hidden.Count == 0)
                return new List<IBody2>();
            var check = new Core.Geometry.OutsideRays();
            var extent = Core.Geometry.Aabb.Empty;
            foreach (int i in seen)
            {
                check.AddBody(i, triangles[i]);
                extent = extent.Union(BodyOps.Bounds(bodies[i]));
            }
            var boxes = hidden.Select(i => BodyOps.TightBounds(bodies[i])).ToList();
            for (int j = 0; j < hidden.Count; j++)
                check.AddBody(solids + j, BoxTriangles(boxes[j]));
            var seenBoxes = check.Seen(directions: 32, spacing: Math.Max(extent.LongestSide / 250, 5e-4), maxRaysPerSide: 250);
            UI.PageLog.Write($"  iç parçalar: kutu denetimi bitti ({watch.Elapsed.TotalSeconds:F1} sn)");
            var proxies = new List<IBody2>();
            var replaced = new List<int>();
            for (int j = 0; j < hidden.Count; j++)
            {
                if (seenBoxes.Contains(solids + j))
                    continue;
                var box = ops.Box(boxes[j]);
                if (box == null)
                    continue;
                proxies.Add(box);
                replaced.Add(hidden[j]);
            }
            foreach (int i in replaced.OrderByDescending(i => i))
            {
                bodies.RemoveAt(i);
                instances.RemoveAt(i);
            }
            UI.PageLog.Write($"  iç parçalar: {hidden.Count} parça dışarıdan görünmüyor; {proxies.Count} tanesi kutusuyla temsil ediliyor, {hidden.Count - proxies.Count} tanesinin kutusu dışarıdan görüneceği için kendisi kaldı");
            return proxies;
        }

        /// <summary>A box grown by a small margin: 0.5 mm or 2 % of its longest side.</summary>
        private static Core.Geometry.Aabb Grown(Core.Geometry.Aabb box)
        {
            if (box.IsEmpty) return box;
            double m = Math.Max(5e-4, box.LongestSide * 0.02);
            var g = new Core.Geometry.Vec3(m, m, m);
            return new Core.Geometry.Aabb(box.Min - g, box.Max + g);
        }

        /// <summary>Whether <paramref name="box"/> reaches out of <paramref name="limit"/>.</summary>
        private static bool Outside(Core.Geometry.Aabb box, Core.Geometry.Aabb limit) =>
            !box.IsEmpty && (box.Min.X < limit.Min.X || box.Min.Y < limit.Min.Y || box.Min.Z < limit.Min.Z ||
                             box.Max.X > limit.Max.X || box.Max.Y > limit.Max.Y || box.Max.Z > limit.Max.Z);

        /// <summary>The status bar's progress bar and, in the middle of the SOLIDWORKS window, a progress window.</summary>
        private sealed class Progress
        {
            private readonly UserProgressBar _bar;
            private readonly UI.ProgressWindow _window;
            private string _title;
            private int _percent;

            public Progress(ISldWorks swApp, string title)
            {
                _title = title;
                swApp.GetUserProgressBar(out _bar);
                _bar?.Start(0, 100, title);
                // No window over other programs while SOLIDWORKS itself is hidden (it runs in the background).
                if (swApp.Visible)
                    try { _window = new UI.ProgressWindow(new IntPtr(((IFrame)swApp.Frame()).GetHWndx64()), title); }
                catch (Exception ex) { UI.PageLog.Write("İlerleme penceresi açılamadı: " + ex.Message); }
            }

            public void UpdateTitle(string title)
            {
                _title = title;
                _bar?.UpdateTitle(title);
                _window?.Update(title, _percent);
            }

            public void UpdateProgress(int percent)
            {
                _percent = percent;
                _bar?.UpdateProgress(percent);
                _window?.Update(_title, percent);
            }

            public void End()
            {
                _bar?.End();
                _window?.Dispose();
            }
        }

        /// <summary>Computes the wrap as temporary bodies without changing any document.</summary>
        public WrapResult Compute(IModelDoc2 source, ShrinkWrapOptions options) => Compute(source, options, null);

        /// <summary>
        /// Computes the wrap without the bodies in <paramref name="exclusions"/>, then unites those bodies as they
        /// are with the wrapped body where they touch it, so they keep all their detail.
        /// </summary>
        public WrapResult Compute(IModelDoc2 source, ShrinkWrapOptions options, Exclusions exclusions)
        {
            if (source == null)
                throw new InvalidOperationException("Açık bir parça ya da montaj yok.");
            var watch = Stopwatch.StartNew();
            var result = new WrapResult();
            var laps = new List<string>();
            double last = 0;
            void Lap(string step)
            {
                double now = watch.Elapsed.TotalSeconds;
                laps.Add($"{step} {now - last:F2}");
                UI.PageLog.Write($"  adım: {step} {now - last:F2} sn");
                last = now;
            }

            var progress = new Progress(_swApp, "Shrink wrap: gövdeler toplanıyor");
            // Progress within a long step, from one percentage to the next: once per body, which costs nothing next
            // to the booleans.
            int reports = 0;
            double lastBeat = 0;
            void Report(string title, int from, int to, int done, int total)
            {
                // A heartbeat in the log once a minute, so a long run can be told apart from a stuck one.
                if (watch.Elapsed.TotalSeconds - lastBeat >= 60)
                {
                    lastBeat = watch.Elapsed.TotalSeconds;
                    UI.PageLog.Write($"  nabız: {title} {done}/{total}, {lastBeat:F0} sn");
                }
                // The booleans leave temporary bodies behind that SOLIDWORKS keeps until their COM objects are
                // released (19.7 GB on a 117-body motor); collecting now and then releases them on the way.
                if (++reports % 20 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                progress.UpdateProgress(from + (to - from) * done / Math.Max(1, total));
                progress.UpdateTitle($"Shrink wrap: {title} ({done}/{total})");
            }
            try
            {
                var instances = new List<(string key, double[] transform)>();
                // Which component each body comes from, so small parts are judged as whole parts.
                var bodyOwners = new List<string>();
                var bodies = BodyCollector.CopyBodies(source, exclusions, out var excluded, instances, bodyOwners);
                UI.PageLog.Write($"  katı gövdeler toplandı: {bodies.Count} ({watch.Elapsed.TotalSeconds:F1} sn)");
                var ops = new BodyOps(_swApp) { Trace = System.Environment.GetEnvironmentVariable("GEOSIMPLIFIER_TRACE") == "1" };

                // Surface bodies are wrapped as thin solids (0.1 mm). Knitting those of each component first
                // (KnitSheets) closed nothing on the STEP imports tried (V12, 3300155310: SOLIDWORKS refuses to knit
                // them, in memory and as a Knit feature alike), so it is only done when asked for.
                // The lid preview does not use them, so it leaves them out (1015 surfaces took seconds).
                bool lidPreview = options.Preview && options.GapWidth > 0 && !options.Bag;
                var owners = new List<string>();
                var excludedSheets = new List<IBody2>();
                var sheetInstances = new List<(string key, double[] transform)>();
                var sheets = lidPreview ? new List<IBody2>() : BodyCollector.CopySheets(source, exclusions, owners, excludedSheets, sheetInstances);
                UI.PageLog.Write($"  yüzey gövdeleri toplandı: {sheets.Count} ({watch.Elapsed.TotalSeconds:F1} sn)");
                if (lidPreview)
                {
                    // The preview of a surface model's outer skin: which parts go as small (red). Seeing which
                    // surfaces stay takes the ray test of OK (two minutes on V12).
                    var previewSheets = BodyCollector.CopySheets(source, exclusions, owners);
                    if (previewSheets.Count > bodies.Count)
                    {
                        SkinPreview(result, options, bodies, bodyOwners, previewSheets, owners);
                        result.Seconds = watch.Elapsed.TotalSeconds;
                        return result;
                    }
                }
                if (options.SurfaceShell && !options.Preview && sheets.Count > bodies.Count)
                {
                    // A model made of surfaces (STEP imports): its outer skin is taken from the surfaces as they
                    // are, with no solids made.
                    Skin(result, options, bodies, bodyOwners, sheets, owners, (d, t) => Report("dış yüzler", 10, 90, d, t), instances, sheetInstances, exclusions);
                    // Excluded parts come through whole, with every face ("Hariç tut": keep these as they are).
                    result.Bodies.AddRange(excluded.Concat(excludedSheets));
                    result.KeepAlways.UnionWith(excluded.Concat(excludedSheets));
                    result.ExcludedBodies = excluded.Count + excludedSheets.Count;
                    if (result.ExcludedBodies > 0)
                        UI.PageLog.Write($"  hariç tutulan: {excluded.Count} katı + {excludedSheets.Count} yüzey gövdesi olduğu gibi eklendi");
                    result.Seconds = watch.Elapsed.TotalSeconds;
                    return result;
                }
                if (sheets.Count > 0 && KnitSheets)
                {
                    var knitWatch = System.Diagnostics.Stopwatch.StartNew();
                    var thick = ops.KnitSheets(sheets, owners, 1e-4, out int closed, out int knitted, out int failed);
                    bodies.AddRange(thick);
                    bodyOwners.AddRange(thick.Select(t => (string)null));
                    result.Sheets = thick.Count;
                    result.FailedSheets = failed;
                    UI.PageLog.Write($"  yüzey dikme: {sheets.Count} yüzey → {closed} kapalı katı + {knitted} dikili yüzey + " +
                                     $"{thick.Count - closed - knitted} tek yüzey, {failed} kalınlaşmadı ({knitWatch.Elapsed.TotalSeconds:F1} sn)");
                }
                // Otherwise surface bodies are not thickened (Serkan: no thickening attempts): they skip the wrap
                // and come through as they are, apart from small ones (same limit as for parts, below).
                var asIs = KnitSheets ? new List<IBody2>() : sheets;
                if (bodies.Count == 0 && asIs.Count > 0)
                {
                    result.Bodies = asIs;
                    result.Sheets = asIs.Count;
                    result.Seconds = watch.Elapsed.TotalSeconds;
                    return result;
                }
                if (bodies.Count == 0)
                    throw new InvalidOperationException(excluded.Count > 0
                        ? "Sarılacak gövde kalmadı; bütün gövdeler hariç tutulmuş."
                        : result.FailedSheets > 0
                            ? "Katı gövde yok; yüzeyler kalınlaştırılamadı."
                            : "Görünür katı veya yüzey gövdesi bulunamadı.");
                // Cages and grilles of many thin parts become one solid box each, before the small-part filter (which
                // would otherwise drop their thin bars one by one).
                if (!options.Preview && bodyOwners.Count == bodies.Count)
                {
                    // Solids and the surfaces kept as they are, together (a bundle's wires may be either); cable
                    // bundles are deleted.
                    bool sheetOwnersKnown = owners.Count == asIs.Count;
                    var allBoxes = bodies.Select(BodyOps.AnyBounds).Concat(sheetOwnersKnown ? asIs.Select(BodyOps.AnyBounds) : Enumerable.Empty<Core.Geometry.Aabb>()).ToList();
                    var allOwners = bodyOwners.Concat(sheetOwnersKnown ? owners : Enumerable.Empty<string>()).ToList();
                    var bundles = new List<(string group, List<int> members, Core.Geometry.Aabb box)>();
                    var cageBoxes = Cages(allBoxes, allOwners, Enumerable.Range(0, allBoxes.Count), exclusions, bundles);
                    result.BundlesRemoved = bundles.Count;
                    var gone = new HashSet<int>(cageBoxes.SelectMany(c => c.members).Concat(bundles.SelectMany(b => b.members)));
                    int solidsBefore = bodies.Count;
                    if (sheetOwnersKnown)
                        asIs = asIs.Where((s, k) => !gone.Contains(solidsBefore + k)).ToList();
                    for (int i = bodies.Count - 1; i >= 0; i--)
                        if (gone.Contains(i))
                        {
                            bodies.RemoveAt(i);
                            bodyOwners.RemoveAt(i);
                            if (i < instances.Count) instances.RemoveAt(i);
                        }
                    foreach (var cage in cageBoxes)
                    {
                        var box = ops.Box(cage.box);
                        if (box == null) continue;
                        bodies.Add(box);
                        bodyOwners.Add(cage.group);
                        instances.Add((null, null));
                    }
                }
                if (options.MinPartPercent > 0 && bodies.Count + asIs.Count > 1)
                {
                    // First of all, small parts (screws, nuts, washers, small fittings) are left out, wherever they
                    // are, even on the outside: below MinPartPercent of the model's size (the diagonal of its box,
                    // as for the shell's small surfaces). They get no work at all; holes they sat in close as usual.
                    // Each body is measured alone here, as before. (Measuring whole parts kept a 974-patch surface
                    // part of 7300002 that used to drop out, and made its shell 2.7 times slower; the surface path,
                    // Skin, measures whole parts.)
                    var boxes = bodies.Select(BodyOps.AnyBounds).ToList();
                    var sheetBoxes = asIs.Select(BodyOps.AnyBounds).ToList();
                    var model = boxes.Concat(sheetBoxes).Aggregate(Core.Geometry.Aabb.Empty, (a, b) => a.Union(b));
                    double limit = options.MinPartPercent / 100 * (model.Max - model.Min).Length;
                    int before = bodies.Count + asIs.Count;
                    for (int i = bodies.Count - 1; i >= 0; i--)
                        if (!boxes[i].IsEmpty && (boxes[i].Max - boxes[i].Min).Length < limit && bodies.Count > 1)
                        {
                            bodies.RemoveAt(i);
                            if (i < instances.Count) instances.RemoveAt(i);
                        }
                    asIs = asIs.Where((s, i) => sheetBoxes[i].IsEmpty || (sheetBoxes[i].Max - sheetBoxes[i].Min).Length >= limit).ToList();
                    result.SmallPartsRemoved = before - bodies.Count - asIs.Count;
                    UI.PageLog.Write($"  küçük parçalar: {result.SmallPartsRemoved} gövde çıkarıldı (%{options.MinPartPercent:0.#} = {limit * 1000:F0} mm altı)");
                    // The copies of the parts left out are released now: SOLIDWORKS keeps them until their COM
                    // objects go, and TAG3 (98,281 bodies, 20 GB open) ran out of memory with them kept.
                    sheets = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                if (asIs.Count > 0)
                    UI.PageLog.Write($"  yüzey gövdeleri kalınlaştırılmadan olduğu gibi kalıyor: {asIs.Count}");
                result.Sheets = asIs.Count;                if (options.SurfaceShell && !options.Preview && instances.Count > 1)
                {
                    // For the outer skin, parts the wrap never touches (no ray from outside reaches them: bearings,
                    // a rotor) are left out of hole closing and uniting. Not for a solid result: where the housing is
                    // open the space they took would stay empty (a motor lost 0.23 million mm3 inside).
                    // Only the parts as collected are measured (thickened surfaces are kept as they are).
                    int solids = instances.Count;
                    var rays = new Core.Geometry.OutsideRays();
                    var extent = Core.Geometry.Aabb.Empty;
                    for (int i = 0; i < solids; i++)
                    {
                        Report("dışarıdan görünen parçalar bulunuyor", 1, 3, i, solids);
                        rays.AddBody(i, BodyOps.Triangles(new[] { bodies[i] }));
                        extent = extent.Union(BodyOps.Bounds(bodies[i]));
                    }
                    var seen = rays.Seen(directions: 64, spacing: Math.Max(extent.LongestSide / 300, 5e-4));
                    int before = bodies.Count;
                    for (int i = solids - 1; i >= 0; i--)
                        if (!seen.Contains(i) && seen.Count > 0)
                        {
                            bodies.RemoveAt(i);
                            instances.RemoveAt(i);
                        }
                    result.InsideParts = before - bodies.Count;
                    UI.PageLog.Write($"  iç parçalar: {before - bodies.Count} gövde dışarıdan görünmüyor, sarmaya alınmadı ({rays.Triangles} üçgen)");
                    Lap("görünürlük");
                }

                Lap("toplama");
                result.SourceBodies = bodies.Count;
                // Nothing the wrap adds may reach out of the model: the result is checked against this at the end.
                var sourceLimit = Grown(bodies.Aggregate(Core.Geometry.Aabb.Empty, (box, b) => box.Union(BodyOps.AnyBounds(b))));
                var insideBoxes = new List<IBody2>();
                if (!options.Preview && !options.SurfaceShell && options.FillCavities && instances.Count > 50)
                {
                    insideBoxes = HiddenPartsAsBoxes(ops, bodies, instances, (i, n) => Report("dışarıdan görünmeyen parçalar bulunuyor", 1, 8, i, n));
                    result.InsideParts = insideBoxes.Count;
                    Lap("iç parçalar");
                }
                if (lidPreview)
                {
                    // The preview does no boolean at all (on a 3145-body assembly it took 46 minutes): it draws a
                    // flat lid on the mouth of every hole the wrap closes, bounded by the mouth's own edges. Bridges
                    // between parts and filled insides are only made on OK.
                    // Every part gets lids: leaving out the parts no ray from outside reaches took longer (97 s to read
                    // the tessellation of 2130 bodies) than it saved in drawing.
                    var caps = MouthCaps(ops, bodies, instances, options.GapWidth, (i, n) => Report("delik ağızları bulunuyor", 10, 90, i, n));
                    Lap("kapaklar");
                    // Drawing a lid costs SOLIDWORKS about 25 ms whatever its size (4000 lids took 100 s), and on a
                    // large assembly most are too small to be seen: only the largest MaxPreviewLids are drawn.
                    if (caps.Count > MaxPreviewLids)
                    {
                        UI.PageLog.Write($"  önizlemede en büyük {MaxPreviewLids} kapak çiziliyor ({caps.Count} kapaktan)");
                        caps = caps.OrderByDescending(c => (BodyOps.AnyBounds(c).Max - BodyOps.AnyBounds(c).Min).Length).Take(MaxPreviewLids).ToList();
                    }
                    result.IsPreview = true;
                    result.Added = caps;
                    result.HolesClosed = caps.Count;
                    result.EnvelopeLeftOut = true;
                    result.Notes.Add("Hızlı önizleme: yalnız delik kapakları gösteriliyor; parçalar arası köprüler ve iç dolgu Tamam'da hesaplanır.");
                    result.Seconds = watch.Elapsed.TotalSeconds;
                    result.Timings = string.Join(", ", laps);
                    UI.PageLog.Write($"Süreler ({bodies.Count} gövde): {result.Timings}");
                    return result;
                }
                if (options.FillThreads)
                {
                    var threads = new ThreadFiller(ops);
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        Report("vida dişleri dolduruluyor", 2, 8, i, bodies.Count);
                        bodies[i] = threads.Fill(bodies[i]);
                    }
                    result.ThreadsFilled = threads.Filled;
                    UI.PageLog.Write($"  vida dişleri: {threads.Filled} dişli bölge dolduruldu");
                    Lap("diş");
                }
                result.ExcludedBodies = excluded.Count;

                double tolerance = 1e-6;

                // Holes are closed in each body before the bodies are united: a plug is cut from and united with
                // that one small body instead of the whole assembly, which made this step the slowest by far.
                // The bag is computed from the bodies as they were collected: tessellating the temporary bodies that
                // booleans produce crashed SOLIDWORKS.
                var collected = new List<IBody2>(bodies);
                var closer = new HoleCloser(ops) { PlugsOnly = options.Preview };
                result.IsPreview = options.Preview;
                // With the bag, the quick preview is only the bag over the bodies as they are: no exact plugs, no
                // flattening, no union and no box bridges (the bag fills holes and gaps itself); all of that is
                // done on OK. Without it the preview finds the plugs and reuses the union of the bodies.
                bool bagOnly = options.Preview && options.Bag;
                if (options.MaxEmbossHeight > 0 && !bagOnly)
                {
                    progress.UpdateTitle("Shrink wrap: kabartmalar düzleniyor");
                    progress.UpdateProgress(5);
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        int before = closer.BossesRemoved;
                        var flat = closer.RemoveBosses(bodies[i], options.MaxEmbossHeight);
                        if (closer.BossesRemoved > before)
                            result.Removed.AddRange(ops.Difference(new[] { bodies[i] }, new[] { flat }));
                        bodies[i] = flat;
                    }
                    Lap("kabartma");
                }
                List<IBody2> previewPlugs = null;
                if (options.GapWidth > 0 && options.Preview && !bagOnly)
                {
                    // The preview shows the plugs cut exactly to the holes, as OK makes them (rough boxes in their
                    // place looked like slabs much larger than the model). They are kept per limit, so going back to
                    // a value tried before is instant.
                    string plugKey = $"{source.GetPathName()}|{source.GetTitle()}|{exclusions?.Key}|{bodies.Count}|{options.MaxEmbossHeight:R}|{options.MinPartPercent:R}|{options.SurfaceShell}";
                    if (plugKey != _plugKey)
                    {
                        _plugsByLimit.Clear();
                        _plugKey = plugKey;
                    }
                    if (!_plugsByLimit.TryGetValue(options.GapWidth, out var cached))
                    {
                        var exact = new HoleCloser(ops) { PlugsOnly = true };
                        // The preview leaves the bodies as they are, so the closer works on a list of its own.
                        CloseHolesPerInstance(exact, new List<IBody2>(bodies), instances, options.GapWidth,
                            (i, n) => Report("delikler kapatılıyor", 10, 25, i, n));
                        cached = (exact.Plugs.ToList(), exact.TooWide.ToList());
                        _plugsByLimit[options.GapWidth] = cached;
                    }
                    previewPlugs = cached.plugs.Select(p => (IBody2)p.Copy()).ToList();
                    result.TooWide.AddRange(cached.tooWide);
                    Lap("delik");
                }
                else if (options.GapWidth > 0 && !bagOnly)
                {
                    CloseHolesPerInstance(closer, bodies, instances, options.GapWidth,
                        (i, n) => Report("delikler kapatılıyor", 10, 25, i, n));
                    Lap("delik");
                }
                var plugs = previewPlugs ?? closer.Plugs;

                // The insides of parts their neighbours close off (a terminal box between lid and housing) are filled
                // exactly, flat in the plane of their rims; their holes and slots were plugged above.
                if (options.FillCavities && !options.Preview)
                {
                    int cups = 0;
                    var parts = bodies.ToList();
                    for (int i = 0; i < parts.Count; i++)
                    {
                        Report("kap içleri dolduruluyor", 25, 40, i, parts.Count);
                        // Only parts that can hold an inside: hollow ones (much less volume than their box) and not
                        // tiny ones. A plate, a block or a bolt has none worth filling; trying every part took 24
                        // minutes on a 2130-body press.
                        var partBox = BodyOps.AnyBounds(parts[i]);
                        double boxVolume = partBox.Size.X * partBox.Size.Y * partBox.Size.Z;
                        double partVolume = ((double[])parts[i].GetMassProperties(1))?[3] ?? 0;
                        if (boxVolume <= 0 || partVolume > 0.6 * boxVolume || partBox.LongestSide < 0.01 * sourceLimit.LongestSide)
                            continue;
                        var partLimit = Grown(partBox);
                        double cupStart = watch.Elapsed.TotalSeconds;
                        var insides = ops.CoveredInsides(parts[i], bodies, 1e-6)
                            .Where(p => !Outside(BodyOps.AnyBounds(p), partLimit)).ToList();
                        double cupTime = watch.Elapsed.TotalSeconds - cupStart;
                        if (cupTime > 5)
                            UI.PageLog.Write($"  yavaş kap içi: {cupTime:F1} sn, kutu {partBox.Size.X * 1000:F0}x{partBox.Size.Y * 1000:F0}x{partBox.Size.Z * 1000:F0} mm, doluluk %{100 * partVolume / boxVolume:F0}, {insides.Count} dolgu");
                        if (insides.Count == 0)
                            continue;
                        cups++;
                        if (options.ForceSingleBody)
                        {
                            bodies.AddRange(insides);
                            continue;
                        }
                        // Without the union of all bodies an inside is united with its own part only.
                        var filled = bodies[i];
                        foreach (var inside in insides)
                        {
                            var united = ops.Union(filled, inside);
                            if (united != null) filled = united;
                            else bodies.Add(inside);
                        }
                        bodies[i] = filled;
                    }
                    result.Log.Add($"Kap içi dolgusu: {cups} parça.");
                    UI.PageLog.Write($"  kap içi dolgusu: {cups} parça");
                    Lap("kap içleri");
                }

                progress.UpdateTitle($"Shrink wrap: {bodies.Count} gövde birleştiriliyor");
                progress.UpdateProgress(30);
                // A plugs-only preview leaves the bodies unplugged (only flattened), so their union can be reused.
                List<IBody2> original;
                string unionKey = options.Preview && !bagOnly
                    ? $"{source.GetPathName()}|{source.GetTitle()}|{exclusions?.Key}|{bodies.Count}|{options.MaxEmbossHeight:R}|{options.MinPartPercent:R}|{options.SurfaceShell}"
                    : null;
                if (bagOnly)
                    original = bodies;
                else if (!options.ForceSingleBody && !options.Preview)
                {
                    result.ManyBodiesWanted = true;
                    // "Tek gövdeye zorla" off: the bodies are not united with each other (much faster on a large
                    // assembly); each keeps its own plugs and filled insides, and the result has many bodies.
                    original = bodies.Concat(insideBoxes).ToList();
                }
                else if (unionKey != null && unionKey == _unionKey)
                    original = _union;
                else
                {
                    original = ops.UnionAll(bodies, tolerance, (done, total) => Report("birleştiriliyor", 40, 60, done, total));
                    if (insideBoxes.Count > 0)
                    {
                        // The boxes of the hidden parts: united with each other first (boxes unite quickly), then
                        // the few blocks that leaves with the rest.
                        var blocks = ops.UnionMany(insideBoxes);
                        UI.PageLog.Write($"  iç kutular: {insideBoxes.Count} → {blocks.Count} blok");
                        original = ops.UnionAll(original.Concat(blocks), tolerance, (done, total) => Report("iç bloklar ekleniyor", 55, 60, done, total));
                    }
                    if (unionKey != null) { _unionKey = unionKey; _union = original; }
                }
                var lumps = new List<IBody2>(original);
                Lap("birleştirme");
                UI.PageLog.Write($"  birleşim: {bodies.Count} gövde → {lumps.Count} parça");
                // What the bridges and cavity fills add, for the preview to draw; null when it is not known exactly
                // (the offset bridge), and then it is found by subtracting the union, which is slow on large models.
                var filledIn = new List<IBody2>();

                if (options.GapWidth > 0 && lumps.Count > 1 && !bagOnly && (options.ForceSingleBody || options.Preview))
                {
                    progress.UpdateTitle("Shrink wrap: köprüler kuruluyor");
                    progress.UpdateProgress(60);
                    // The offset closing follows the parts' shapes; where SOLIDWORKS cannot offset, the gaps are
                    // spanned with boxes instead, like a bag pulled over the parts.
                    var bridged = ops.Bridge(lumps, options.GapWidth, tolerance);
                    if (bridged != null)
                    {
                        lumps = bridged;
                        filledIn = null;
                    }
                    else
                    {
                        lumps = ops.BridgeByBoxes(lumps, options.GapWidth, tolerance, out int count, filledIn);
                        result.Bridges = count;
                    }
                    Lap("köprü");
                }


                // The bag: a closing on a voxel grid fills what a cube of the limit's size cannot reach from outside
                // (open shells such as a fan cover, hollow insides, gaps between parts). Its boxes are united with
                // the exact bodies, so outside the bridged gaps the faces stay the original ones.
                var bagBoxes = new List<IBody2>();
                int triangleCount = 0;
                if (options.GapWidth > 0 && options.Bag)
                {
                    progress.UpdateTitle("Shrink wrap: poşet geçiriliyor");
                    progress.UpdateProgress(65);
                    // The surfaces do not depend on the limit, so previews reuse them.
                    string key = $"{source.GetPathName()}|{source.GetTitle()}|{exclusions?.Key}|{bodies.Count}";
                    List<Core.Geometry.Vec3> triangles;
                    if (key == _trianglesKey)
                        triangles = _triangles;
                    else
                    {
                        triangles = BodyOps.Triangles(collected);
                        _trianglesKey = key;
                        _triangles = triangles;
                    }
                    Lap("üçgenler");
                    triangleCount = triangles.Count / 3;
                    // The same grid as the preview: a finer one gave several times more boxes, and uniting them took minutes.
                    var bag = Core.Geometry.VoxelClosing.FillLimited(triangles, options.GapWidth / 2);
                    foreach (var box in bag.Boxes)
                        if (ops.Box(box) is IBody2 b)
                            bagBoxes.Add(b);
                    result.Log.Add($"Poşet: {triangles.Count / 3} üçgen, ızgara {bag.CellsX}x{bag.CellsY}x{bag.CellsZ} " +
                                   $"({bag.Cell * 1000:F1} mm), {bag.FilledCells} dolu hücre, {bagBoxes.Count} kutu.");
                    Lap("poşet");
                    if (!options.Preview && bagBoxes.Count > 0)
                    {
                        // Pieces the pairwise union left are small; uniting them with each other first is cheap,
                        // uniting each of them with the whole model is not.
                        var fill = ops.UnionAll(ops.UnionMany(bagBoxes), tolerance);
                        Lap("poşet birleşimi");
                        UI.PageLog.Write($"  poşet: {bagBoxes.Count} kutu → {fill.Count} parça");
                        lumps = ops.UnionAll(lumps.Concat(fill), tolerance);
                        Lap("poşet ekleme");
                    }
                }

                // The bag already fills closed hollows. Without it (no limit) cavities are filled here; the quick
                // preview skips this, except for models made mostly of surfaces: there the filled inside is the
                // result. (A few surfaces in a large assembly made every preview fill its cavities.)
                bool bagged = bagBoxes.Count > 0;
                if (options.FillCavities && !bagged && (!options.Preview || result.Sheets * 2 > result.SourceBodies))
                {
                    for (int i = 0; i < lumps.Count; i++)
                    {
                        Report("iç dolduruluyor", 70, 85, i, lumps.Count);
                        lumps[i] = ops.FillCavities(lumps[i], filledIn);
                    }
                    Lap("iç boşluk");
                }

                // On OK nothing is previewed here: only what was added is listed (the plugs).
                progress.UpdateTitle(options.Preview ? "Shrink wrap: önizleme hazırlanıyor" : "Shrink wrap: sonuç hazırlanıyor");
                progress.UpdateProgress(85);
                // Plugs were taken per body; bridges and filled cavities are what the lumps have beyond the union.
                var changed = lumps.Where(l => !original.Any(o => ReferenceEquals(o, l))).ToList();
                // What is added is only drawn in the preview; the real result goes straight into the document.
                result.Added = !options.Preview ? plugs
                    : bagOnly ? plugs.Concat(bagBoxes).ToList()
                    : plugs.Concat(changed.Count == 0 ? new List<IBody2>() : filledIn ?? ops.Difference(changed, original)).ToList();
                Lap("fark");

                if (excluded.Count > 0)
                {
                    progress.UpdateTitle("Shrink wrap: hariç tutulan gövdeler ekleniyor");
                    progress.UpdateProgress(90);
                    int before = lumps.Count;
                    lumps = ops.UnionAll(lumps.Concat(excluded), tolerance);
                    // Excluded bodies that stayed apart are kept by the last clean-up however small.
                    result.KeepAlways.UnionWith(lumps.Where(l => excluded.Any(e => ReferenceEquals(e, l))));
                    if (lumps.Count > before)
                        result.Notes.Add("Bazı hariç tutulan gövdeler sarılan gövdeye değmediği için ayrı kaldı.");
                    Lap("hariç");
                }

                // Slivers the fills leave apart (a few mm³ that did not merge) are no part of the result.
                if (!options.Preview && lumps.Count > 1)
                {
                    var volumes = lumps.Select(l => ((double[])l.GetMassProperties(1))?[3] ?? 0).ToList();
                    double largest = volumes.Max();
                    var kept = lumps.Where((l, i) => volumes[i] >= largest * 1e-5).ToList();
                    if (kept.Count < lumps.Count)
                        UI.PageLog.Write($"  {lumps.Count - kept.Count} küçük parça atıldı");
                    lumps = kept;
                }
                var outside = lumps.Where(l => Outside(BodyOps.AnyBounds(l), sourceLimit)).ToList();
                if (outside.Count > 0)
                {
                    UI.PageLog.Write($"  UYARI: {outside.Count} sonuç gövdesi modelin kutusunun dışına taşıyor");
                    result.Notes.Add("Uyarı: sonuç modelin dışına taşıyor; lütfen kontrol edin.");
                }
                result.Bodies = lumps.Concat(asIs).ToList();
                // The translucent envelope is lifted off the model's faces when MakeOffset works. On installations where
                // it fails a plain copy is drawn on the faces; it shows streaks, but the envelope stays visible.
                // A translucent envelope of a large model takes SOLIDWORKS seconds to redraw (3.5 s for a motor of
                // 550 000 triangles) and looks just like the model; the quick preview leaves it out there.
                int faceCount = collected.Sum(b => ((object[])b.GetFaces())?.Length ?? 0);
                bool envelope = !options.Preview || (triangleCount < LargeModelTriangles && faceCount < LargeModelFaces);
                result.EnvelopeLeftOut = !envelope;
                foreach (var lump in envelope ? lumps : new List<IBody2>())
                {
                    double gap = Math.Max(5e-5, BodyOps.Bounds(lump).LongestSide * 0.0015);
                    // The preview's envelope may be the cached union, which the preview moves: give it a copy.
                    result.Envelope.Add(options.Preview ? (IBody2)lump.Copy() : ops.Offset(lump, gap, outward: true) ?? (IBody2)lump.Copy());
                }
                Lap("zarf");
                result.HolesClosed = previewPlugs?.Count ?? closer.Closed;
                result.HolesFailed = closer.Failed;
                result.BossesRemoved = closer.BossesRemoved;
                if (previewPlugs == null) result.TooWide = closer.TooWide;
                result.Log = closer.Log.Concat(result.Log).ToList();
                result.FailedBooleans = ops.FailedBooleans;
                result.Seconds = watch.Elapsed.TotalSeconds;
                result.Timings = string.Join(", ", laps);
                UI.PageLog.Write($"Süreler ({bodies.Count} gövde): {result.Timings}");
                using (var self = Process.GetCurrentProcess())
                    UI.PageLog.Write($"  bellek: şu an {self.WorkingSet64 / 1e9:F1} GB, en çok {self.PeakWorkingSet64 / 1e9:F1} GB");
                return result;
            }
            finally
            {
                progress.End();
            }
        }

        /// <summary>Puts the computed bodies into a new part and returns the summary text.</summary>
        public string Commit(WrapResult result) => Commit(result, null, null);

        /// <summary>
        /// With <paramref name="configurationName"/>, puts the result into a new configuration of
        /// <paramref name="source"/> (see <see cref="ConfigurationOutput"/>) instead of a new part.
        /// </summary>
        public string Commit(WrapResult result, IModelDoc2 source, string configurationName) =>
            Commit(result, source, configurationName, surfaceShell: false, smallShellPercent: 0);

        /// <param name="surfaceShell">Put in only the outer skin: each result body becomes one surface body made of
        /// its faces (the insides are filled, so all of them are outer faces), and no solid.</param>
        /// <param name="smallShellPercent">Surface bodies of the shell smaller than this percentage of the model's
        /// size are deleted; 0 keeps them all.</param>
        public string Commit(WrapResult result, IModelDoc2 source, string configurationName, bool surfaceShell, double smallShellPercent)
        {
            var bodies = surfaceShell && !result.IsSkin ? Shells(result, smallShellPercent / 100) : result.Bodies;
            // The skin of a surface model and a solid result get the same last clean-up (Serkan: "Select Bodies By
            // Size" at the same percentage still picked 183 bodies of a TAG3 result).
            if (smallShellPercent > 0 && (result.IsSkin || !surfaceShell) && !result.IsPreview)
                bodies = DropSmall(bodies, smallShellPercent / 100, result.KeepAlways);
            if (source != null && configurationName != null)
                return "Tamamlandı: " + result.Summary() + " " +
                       new ConfigurationOutput(_swApp).Create(source, bodies, configurationName, NewPart);

            var commitWatch = Stopwatch.StartNew();
            // Asked before the bodies go in: asking after took 52 s on TAG3 (11,236 bodies) and once never returned.
            bool zoom = !SkipZoom && MainWindowShown();
            var part = NewPart();
            double newPart = commitWatch.Elapsed.TotalSeconds;
            int created = 0;
            // Checking and simplifying each body took most of the 8.5 minutes 1753 bodies needed; with many bodies
            // they are put in as they are.
            int featureOptions = bodies.Count > 50 ? 0
                : (int)(swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck | swCreateFeatureBodyOpts_e.swCreateFeatureBodySimplify);
            // The feature tree and the view are not updated after each of possibly a thousand bodies, only once.
            var partDoc = (IModelDoc2)part;
            var view = partDoc.ActiveView as IModelView;
            partDoc.FeatureManager.EnableFeatureTree = false;
            if (view != null) view.EnableGraphicsUpdate = false;
            int total = bodies.Count;
            try
            {
                // Each body is let go once it is in the part: holding all of them besides their copies in the part
                // took SOLIDWORKS to 44 GB on TAG3, and the result could then not be saved (save error 1).
                var queue = new List<IBody2>(bodies);
                bodies = null;
                // The count stays for the summary; the bodies themselves go.
                result.Bodies = new List<IBody2>(new IBody2[queue.Count]);
                result.KeepAlways.Clear();
                for (int i = 0; i < queue.Count; i++)
                {
                    if (part.CreateFeatureFromBody3((IBody2)queue[i].Copy(), false, featureOptions) != null)
                        created++;
                    queue[i] = null;
                    if (i % 1000 == 999)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
            finally
            {
                double inserted = commitWatch.Elapsed.TotalSeconds;
                partDoc.FeatureManager.EnableFeatureTree = true;
                double tree = commitWatch.Elapsed.TotalSeconds;
                if (view != null) view.EnableGraphicsUpdate = true;
                UI.PageLog.Write($"  ekleme: yeni parça {newPart:F1}, {total} gövde {inserted - newPart:F1}, ağaç {tree - inserted:F1}, " +
                                 $"görüntü {commitWatch.Elapsed.TotalSeconds - tree:F1} sn");
            }
            double beforeZoom = commitWatch.Elapsed.TotalSeconds;
            // Most of this is SOLIDWORKS drawing the new part for the first time: zooming to the bodies' known box
            // instead took as long (26 s on V12).
            // With SOLIDWORKS hidden (batch runs) nobody looks at the view, so it is not fitted.
            if (zoom)
                partDoc.ViewZoomtofit2();
            UI.PageLog.Write($"  sığdırma {commitWatch.Elapsed.TotalSeconds - beforeZoom:F1} sn");
            if (created < total)
                result.Notes.Add("Bazı gövdeler parçaya eklenemedi, sonuç eksik olabilir.");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return "Tamamlandı: " + result.Summary();
        }

        /// <summary>
        /// The outer skin of each result body as surface bodies: only the faces that can be seen from outside (the
        /// floors of deep slots, faces pressed against each other and the like are left out), copied as surfaces
        /// and knitted together without forming a solid. A body that cannot be turned into surfaces stays solid.
        /// The visibility is worked out on the tessellation, which needs the bodies in a document; they are put in
        /// a hidden part that is closed afterwards.
        /// </summary>
        private List<IBody2> Shells(WrapResult result, double smallPart)
        {
            var modeler = (IModeler)_swApp.GetModeler();
            var shells = new List<IBody2>();
            var watch = Stopwatch.StartNew();
            var progress = new Progress(_swApp, "Shrink wrap: dış kabuk çıkarılıyor");
            IModelDoc2 temp = null;
            int kept = 0, total = 0;
            try
            {
                _swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                try { temp = (IModelDoc2)NewPart(); }
                finally { _swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); }
                foreach (var body in result.Bodies)
                {
                    // Surface bodies of the model came through the wrap as they are; they are already surfaces.
                    if (body.GetType() == (int)swBodyType_e.swSheetBody)
                    {
                        shells.Add(body);
                        continue;
                    }
                    progress.UpdateTitle("Shrink wrap: dış kabuk: görünen yüzler bulunuyor");
                    progress.UpdateProgress(10);
                    var feature = ((IPartDoc)temp).CreateFeatureFromBody3((IBody2)body.Copy(), false, 0) as IFeature;
                    var placed = (((IPartDoc)temp).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[])?.OfType<IBody2>().LastOrDefault();
                    var faces = ((placed?.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>().ToList();
                    var visibility = new Core.Geometry.FaceVisibility();
                    for (int i = 0; i < faces.Count; i++)
                        visibility.AddFace(i, faces[i].GetTessTriangles(true) as float[] ?? new float[0], faces[i].GetTessNorms() as float[], samples: 4);
                    var box = BodyOps.Bounds(placed);
                    var visible = visibility.Visible(Math.Max(1e-5, (box.Max - box.Min).Length * 2e-5));
                    // Islands of visible faces are joined into one surface only when one body is wanted.
                    if (!result.ManyBodiesWanted)
                        ConnectIslands(faces, visible);
                    total += faces.Count;
                    var sheets = new List<IBody2>();
                    for (int i = 0; i < faces.Count; i++)
                    {
                        if (i % 100 == 0)
                        {
                            progress.UpdateProgress(20 + 70 * i / Math.Max(1, faces.Count));
                            progress.UpdateTitle($"Shrink wrap: dış kabuk ({i}/{faces.Count} yüz)");
                        }
                        if (visible.Contains(i) && faces[i].CreateSheetBody() is IBody2 sheet)
                            sheets.Add(sheet);
                    }
                    kept += sheets.Count;
                    int error = 0;
                    var knitted = sheets.Count == 0 ? null
                        : ((object[])modeler.CreateBodiesFromSheets2(sheets.ToArray(), SheetBodiesOnly, 1e-5, ref error))?.OfType<IBody2>().ToList();
                    if (knitted == null || knitted.Count == 0)
                    {
                        result.Notes.Add("Bir gövde yüzey gövdesine çevrilemedi; katı olarak bırakıldı.");
                        shells.Add(body);
                        continue;
                    }
                    shells.AddRange(knitted);
                    // The next body is measured alone.
                    if (feature != null)
                    {
                        feature.Select2(false, 0);
                        temp.EditDelete();
                    }
                }
            }
            finally
            {
                if (temp != null)
                    _swApp.CloseDoc(temp.GetTitle());
                progress.End();
            }
            // Small surface bodies left over (bits of screw heads and rings seen through gaps) are dropped: below
            // smallPart of the model's size (Serkan picked them with "select bodies by size" at 14 %). The
            // largest one always stays.
            var model = shells.Aggregate(Core.Geometry.Aabb.Empty, (b, s) => b.Union(BodyOps.AnyBounds(s)));
            double modelSize = (model.Max - model.Min).Length;
            var largest = shells.OrderByDescending(s => (BodyOps.AnyBounds(s).Max - BodyOps.AnyBounds(s).Min).Length).FirstOrDefault();
            int before = shells.Count;
            shells = shells.Where(s => s == largest || s.GetType() != (int)swBodyType_e.swSheetBody ||
                                       (BodyOps.AnyBounds(s).Max - BodyOps.AnyBounds(s).Min).Length >= smallPart * modelSize).ToList();
            UI.PageLog.Write($"  dış kabuk: {result.Bodies.Count} gövde → {shells.Count(s => s.GetType() == (int)swBodyType_e.swSheetBody)} yüzey gövdesi " +
                             $"({before - shells.Count} küçük parça atıldı), {kept}/{total} yüz görünüyor ({watch.Elapsed.TotalSeconds:F1} sn)");
            return shells;
        }
        /// <summary>
        /// The result's bodies smaller than <paramref name="fraction"/> of its size (box diagonals, as SOLIDWORKS'
        /// "Select Bodies By Size" measures) are dropped. A surface touching a surface that stays is kept: a large
        /// imported panel is often many small patches, and dropping them would open holes in it. The largest body
        /// and <paramref name="keep"/> always stay.
        /// </summary>
        private static List<IBody2> DropSmall(List<IBody2> bodies, double fraction, ICollection<IBody2> keep)
        {
            if (bodies.Count < 2)
                return bodies;
            var watch = Stopwatch.StartNew();
            var boxes = bodies.Select(BodyOps.AnyBounds).ToList();
            var model = boxes.Aggregate(Core.Geometry.Aabb.Empty, (a, b) => a.Union(b));
            double limit = fraction * (model.Max - model.Min).Length;
            int largest = Enumerable.Range(0, bodies.Count).OrderByDescending(i => (boxes[i].Max - boxes[i].Min).Length).First();
            bool Sheet(int i) => bodies[i].GetType() == (int)swBodyType_e.swSheetBody;
            var small = new HashSet<int>(Enumerable.Range(0, bodies.Count).Where(i => i != largest && !keep.Contains(bodies[i]) &&
                !boxes[i].IsEmpty && (boxes[i].Max - boxes[i].Min).Length < limit));
            // Small surfaces joined to a large one, directly or through other small ones, stay with it.
            var stays = new HashSet<int>();
            var bigSheets = Enumerable.Range(0, bodies.Count).Where(i => !small.Contains(i) && Sheet(i)).ToList();
            var grid = new Dictionary<(long, long, long), List<int>>();
            double cell = Math.Max(limit, 1e-3);
            (long, long, long) Key(Core.Geometry.Vec3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            foreach (int i in small.Where(Sheet))
            {
                var k = Key((boxes[i].Min + boxes[i].Max) * 0.5);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(i);
            }
            bool Touch(Core.Geometry.Aabb a, Core.Geometry.Aabb b) =>
                a.Min.X <= b.Max.X + 5e-4 && b.Min.X <= a.Max.X + 5e-4 && a.Min.Y <= b.Max.Y + 5e-4 && b.Min.Y <= a.Max.Y + 5e-4 &&
                a.Min.Z <= b.Max.Z + 5e-4 && b.Min.Z <= a.Max.Z + 5e-4;
            IEnumerable<int> SmallNear(Core.Geometry.Aabb box)
            {
                var lo = Key(box.Min - new Core.Geometry.Vec3(cell, cell, cell));
                var hi = Key(box.Max + new Core.Geometry.Vec3(cell, cell, cell));
                // A large box spans many cells; it is checked against every small sheet instead.
                if ((hi.Item1 - lo.Item1 + 1) * (hi.Item2 - lo.Item2 + 1) * (hi.Item3 - lo.Item3 + 1) > grid.Count)
                    return grid.Values.SelectMany(v => v).Where(j => Touch(box, boxes[j]));
                var found = new List<int>();
                for (long x = lo.Item1; x <= hi.Item1; x++)
                    for (long y = lo.Item2; y <= hi.Item2; y++)
                        for (long z = lo.Item3; z <= hi.Item3; z++)
                            if (grid.TryGetValue((x, y, z), out var list))
                                found.AddRange(list.Where(j => Touch(box, boxes[j])));
                return found;
            }
            var queue = new Queue<int>();
            foreach (int b in bigSheets)
                foreach (int j in SmallNear(boxes[b]))
                    if (stays.Add(j)) queue.Enqueue(j);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (int j in SmallNear(boxes[i]))
                    if (stays.Add(j)) queue.Enqueue(j);
            }
            var result = Enumerable.Range(0, bodies.Count).Where(i => !small.Contains(i) || stays.Contains(i)).Select(i => bodies[i]).ToList();
            UI.PageLog.Write($"  son temizlik: {bodies.Count} gövde → {result.Count} (%{fraction * 100:0.#} = {limit * 1000:F0} mm altı {small.Count} küçük gövde, " +
                             $"{stays.Count} tanesi büyük bir yüzeye bağlı olduğu için kaldı; {watch.Elapsed.TotalSeconds:F1} sn)");
            return result;
        }

        // CreateBodiesFromSheets2 option that knits into sheet bodies instead of closing them into solids
        // (found by trying: 0 and 2 gave solids).
        private const int SheetBodiesOnly = 1;

        private IPartDoc NewPart()
        {
            string template = _swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc = _swApp.NewDocument(template, 0, 0, 0) as IPartDoc;
            return doc ?? throw new InvalidOperationException("Şablondan yeni parça oluşturulamadı: " + template);
        }
    }
}
