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

        public int SourceBodies;

        /// <summary>Surface bodies wrapped as thin solids, and those SOLIDWORKS could not thicken.</summary>
        public int Sheets;
        public int FailedSheets;

        /// <summary>Gaps between separate parts spanned like a bag pulled over them.</summary>
        public int Bridges;
        public int HolesClosed;
        public int HolesFailed;
        public int BossesRemoved;

        /// <summary>Small parts left out (<see cref="ShrinkWrapOptions.MinPartSize"/>).</summary>
        public int SmallPartsRemoved;
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
                          (Bridges > 0 ? $", {Bridges} boşluk köprülendi" : "") +
                          (Sheets > 0 ? $", {Sheets} yüzey gövdesi sarıldı" : "") + $" ({Seconds:F1} sn).";
            var notes = new List<string>(Notes);
            if (Bodies.Count > 1 && !IsPreview)
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
                bodies[i] = closer.CloseHoles(bodies[i], width);
                if (k != null)
                    done[k] = (t, bodies[i], p0, closer.Plugs.Count, w0, closer.TooWide.Count, closer.Closed - c0, closer.Failed - f0);
            }
            UI.PageLog.Write($"  delikler: {bodies.Count} gövde, {reused} tanesi tekrar eden parçadan taşındı");
        }

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
            void Report(string title, int from, int to, int done, int total)
            {
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
                var bodies = BodyCollector.CopyBodies(source, exclusions, out var excluded, instances);
                var ops = new BodyOps(_swApp) { Trace = System.Environment.GetEnvironmentVariable("GEOSIMPLIFIER_TRACE") == "1" };

                // Surface bodies are wrapped as they are, as thin solids (0.1 mm); they are not sewn together.
                var sheets = BodyCollector.CopySheets(source, exclusions);
                if (sheets.Count > 0)
                {
                    var thick = ops.ThickenSheets(sheets, 1e-4, out int failed);
                    bodies.AddRange(thick);
                    result.Sheets = thick.Count;
                    result.FailedSheets = failed;
                }
                if (bodies.Count == 0)
                    throw new InvalidOperationException(excluded.Count > 0
                        ? "Sarılacak gövde kalmadı; bütün gövdeler hariç tutulmuş."
                        : result.FailedSheets > 0
                            ? "Katı gövde yok; yüzeyler kalınlaştırılamadı."
                            : "Görünür katı veya yüzey gövdesi bulunamadı.");
                if (options.MinPartSize > 0)
                {
                    // Small parts (screws, nuts, washers) are left out; the holes they sit in are closed instead.
                    int before = bodies.Count;
                    for (int i = bodies.Count - 1; i >= 0; i--)
                        if (BodyOps.TightBounds(bodies[i]).LongestSide < options.MinPartSize && bodies.Count > 1)
                        {
                            bodies.RemoveAt(i);
                            if (i < instances.Count) instances.RemoveAt(i);
                        }
                    result.SmallPartsRemoved = before - bodies.Count;
                    UI.PageLog.Write($"  küçük parçalar: {before - bodies.Count} gövde çıkarıldı");
                }
                Lap("toplama");
                result.SourceBodies = bodies.Count;
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
                    string plugKey = $"{source.GetPathName()}|{source.GetTitle()}|{exclusions?.Key}|{bodies.Count}|{options.MaxEmbossHeight:R}";
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
                        var insides = ops.CoveredInsides(parts[i], bodies, 1e-6);
                        if (insides.Count == 0)
                            continue;
                        bodies.AddRange(insides);
                        cups++;
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
                    ? $"{source.GetPathName()}|{source.GetTitle()}|{exclusions?.Key}|{bodies.Count}|{options.MaxEmbossHeight:R}"
                    : null;
                if (bagOnly)
                    original = bodies;
                else if (unionKey != null && unionKey == _unionKey)
                    original = _union;
                else
                {
                    original = ops.UnionAll(bodies, tolerance, (done, total) => Report("birleştiriliyor", 40, 60, done, total));
                    if (unionKey != null) { _unionKey = unionKey; _union = original; }
                }
                var lumps = new List<IBody2>(original);
                Lap("birleştirme");
                UI.PageLog.Write($"  birleşim: {bodies.Count} gövde → {lumps.Count} parça");
                // What the bridges and cavity fills add, for the preview to draw; null when it is not known exactly
                // (the offset bridge), and then it is found by subtracting the union, which is slow on large models.
                var filledIn = new List<IBody2>();

                if (options.GapWidth > 0 && lumps.Count > 1 && !bagOnly)
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
                // preview skips this, except for surface models: there the filled inside is the result.
                bool bagged = bagBoxes.Count > 0;
                if (options.FillCavities && !bagged && (!options.Preview || result.Sheets > 0))
                {
                    for (int i = 0; i < lumps.Count; i++)
                    {
                        Report("iç dolduruluyor", 70, 85, i, lumps.Count);
                        lumps[i] = ops.FillCavities(lumps[i], filledIn);
                    }
                    Lap("iç boşluk");
                }

                progress.UpdateTitle("Shrink wrap: önizleme hazırlanıyor");
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
                result.Bodies = lumps;
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
            Commit(result, source, configurationName, surfaceShell: false);

        /// <param name="surfaceShell">Put in only the outer skin: each result body becomes one surface body made of
        /// its faces (the insides are filled, so all of them are outer faces), and no solid.</param>
        public string Commit(WrapResult result, IModelDoc2 source, string configurationName, bool surfaceShell)
        {
            var bodies = surfaceShell ? Shells(result) : result.Bodies;
            if (source != null && configurationName != null)
                return "Tamamlandı: " + result.Summary() + " " +
                       new ConfigurationOutput(_swApp).Create(source, bodies, configurationName, NewPart);

            var part = NewPart();
            int created = 0;
            int featureOptions = (int)(swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck | swCreateFeatureBodyOpts_e.swCreateFeatureBodySimplify);
            foreach (var body in bodies)
            {
                if (part.CreateFeatureFromBody3((IBody2)body.Copy(), false, featureOptions) != null)
                    created++;
            }
            ((IModelDoc2)part).ViewZoomtofit2();
            if (created < bodies.Count)
                result.Notes.Add("Bazı gövdeler parçaya eklenemedi, sonuç eksik olabilir.");
            return "Tamamlandı: " + result.Summary();
        }

        /// <summary>
        /// The outer skin of each result body as surface bodies: only the faces that can be seen from outside (the
        /// floors of deep slots, faces pressed against each other and the like are left out), copied as surfaces
        /// and knitted together without forming a solid. A body that cannot be turned into surfaces stays solid.
        /// The visibility is worked out on the tessellation, which needs the bodies in a document; they are put in
        /// a hidden part that is closed afterwards.
        /// </summary>
        private List<IBody2> Shells(WrapResult result)
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
                    progress.UpdateTitle("Shrink wrap: dış kabuk: görünen yüzler bulunuyor");
                    progress.UpdateProgress(10);
                    var feature = ((IPartDoc)temp).CreateFeatureFromBody3((IBody2)body.Copy(), false, 0) as IFeature;
                    var placed = (((IPartDoc)temp).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[])?.OfType<IBody2>().LastOrDefault();
                    var faces = ((placed?.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>().ToList();
                    var visibility = new Core.Geometry.FaceVisibility();
                    for (int i = 0; i < faces.Count; i++)
                        visibility.AddFace(i, (float[])faces[i].GetTessTriangles(true) ?? new float[0], (float[])faces[i].GetTessNorms(), samples: 4);
                    var box = BodyOps.Bounds(placed);
                    var visible = visibility.Visible(Math.Max(1e-5, (box.Max - box.Min).Length * 2e-5));
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
            UI.PageLog.Write($"  dış kabuk: {result.Bodies.Count} gövde → {shells.Count(s => s.GetType() == (int)swBodyType_e.swSheetBody)} yüzey gövdesi, " +
                             $"{kept}/{total} yüz görünüyor ({watch.Elapsed.TotalSeconds:F1} sn)");
            return shells;
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
