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
        private const int LargeModelFaces = 8000;

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

            _swApp.GetUserProgressBar(out UserProgressBar progress);
            progress.Start(0, 100, "Shrink wrap: gövdeler toplanıyor");
            try
            {
                var bodies = BodyCollector.CopyBodies(source, exclusions, out var excluded);
                var ops = new BodyOps(_swApp);

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
                if (options.GapWidth > 0 && !bagOnly)
                {
                    progress.UpdateTitle("Shrink wrap: delikler kapatılıyor");
                    progress.UpdateProgress(10);
                    for (int i = 0; i < bodies.Count; i++)
                        bodies[i] = closer.CloseHoles(bodies[i], options.GapWidth);
                    Lap("delik");
                }
                var plugs = closer.Plugs;

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
                    original = ops.UnionAll(bodies, tolerance);
                    if (unionKey != null) { _unionKey = unionKey; _union = original; }
                }
                var lumps = new List<IBody2>(original);
                Lap("birleştirme");

                if (options.GapWidth > 0 && lumps.Count > 1 && !bagOnly)
                {
                    progress.UpdateTitle("Shrink wrap: parçalar arası boşluklar köprüleniyor");
                    progress.UpdateProgress(30);
                    // The offset closing follows the parts' shapes; where SOLIDWORKS cannot offset, the gaps are
                    // spanned with boxes instead, like a bag pulled over the parts.
                    var bridged = ops.Bridge(lumps, options.GapWidth, tolerance);
                    if (bridged != null)
                        lumps = bridged;
                    else
                    {
                        lumps = ops.BridgeByBoxes(lumps, options.GapWidth, tolerance, out int count);
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
                    progress.UpdateProgress(60);
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
                    progress.UpdateTitle("Shrink wrap: iç boşluklar dolduruluyor");
                    progress.UpdateProgress(70);
                    for (int i = 0; i < lumps.Count; i++)
                        lumps[i] = ops.FillCavities(lumps[i]);
                    Lap("iç boşluk");
                }

                progress.UpdateTitle("Shrink wrap: önizleme hazırlanıyor");
                progress.UpdateProgress(85);
                // Plugs were taken per body; bridges and filled cavities are what the lumps have beyond the union.
                var changed = lumps.Where(l => !original.Any(o => ReferenceEquals(o, l))).ToList();
                // What is added is only drawn in the preview; the real result goes straight into the document.
                result.Added = !options.Preview ? plugs
                    : bagOnly ? plugs.Concat(bagBoxes).ToList()
                    : plugs.Concat(changed.Count > 0 ? ops.Difference(changed, original) : new List<IBody2>()).ToList();
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
                result.HolesClosed = closer.Closed;
                result.HolesFailed = closer.Failed;
                result.BossesRemoved = closer.BossesRemoved;
                result.TooWide = closer.TooWide;
                result.Log = closer.Log.Concat(result.Log).ToList();
                result.FailedBooleans = ops.FailedBooleans;
                result.Seconds = watch.Elapsed.TotalSeconds;
                result.Timings = string.Join(", ", laps);
                UI.PageLog.Write($"Süreler ({bodies.Count} gövde): {result.Timings}");
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
        public string Commit(WrapResult result, IModelDoc2 source, string configurationName)
        {
            if (source != null && configurationName != null)
                return "Tamamlandı: " + result.Summary() + " " +
                       new ConfigurationOutput(_swApp).Create(source, result.Bodies, configurationName, NewPart);

            var part = NewPart();
            int created = 0;
            int featureOptions = (int)(swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck | swCreateFeatureBodyOpts_e.swCreateFeatureBodySimplify);
            foreach (var body in result.Bodies)
            {
                if (part.CreateFeatureFromBody3((IBody2)body.Copy(), false, featureOptions) != null)
                    created++;
            }
            ((IModelDoc2)part).ViewZoomtofit2();
            if (created < result.Bodies.Count)
                result.Notes.Add("Bazı gövdeler parçaya eklenemedi, sonuç eksik olabilir.");
            return "Tamamlandı: " + result.Summary();
        }

        private IPartDoc NewPart()
        {
            string template = _swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc = _swApp.NewDocument(template, 0, 0, 0) as IPartDoc;
            return doc ?? throw new InvalidOperationException("Şablondan yeni parça oluşturulamadı: " + template);
        }
    }
}
