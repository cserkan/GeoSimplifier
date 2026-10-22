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

        /// <summary>
        /// The result grown by a hair, for the translucent preview: drawn exactly on the original faces it would
        /// fight with them for the same pixels and show streaks.
        /// </summary>
        public List<IBody2> Envelope = new List<IBody2>();

        public int SourceBodies;
        public int HolesClosed;
        public int HolesFailed;
        public int FailedBooleans;
        public double Seconds;
        public List<string> Notes = new List<string>();

        /// <summary>Openings left open because they are wider than the limit (meters).</summary>
        public List<double> TooWide = new List<double>();

        /// <summary>Hole detection details, shown in the preview box for debugging.</summary>
        public List<string> Log = new List<string>();

        public string Summary()
        {
            string text = $"{SourceBodies} gövde → {Bodies.Count} gövde, {HolesClosed} delik kapatıldı" +
                          (HolesFailed > 0 ? $", {HolesFailed} delik kapatılamadı" : "") + $" ({Seconds:F1} sn).";
            var notes = new List<string>(Notes);
            if (Bodies.Count > 1)
                notes.Add("Birbirinden uzak parçalar var; tek gövde için kapatma genişliğini artırın.");
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

        public ShrinkWrapService(ISldWorks swApp)
        {
            _swApp = swApp;
        }

        /// <summary>Computes the wrap as temporary bodies without changing any document.</summary>
        public WrapResult Compute(IModelDoc2 source, ShrinkWrapOptions options)
        {
            if (source == null)
                throw new InvalidOperationException("Açık bir parça ya da montaj yok.");
            var watch = Stopwatch.StartNew();
            var result = new WrapResult();

            _swApp.GetUserProgressBar(out UserProgressBar progress);
            progress.Start(0, 100, "Shrink wrap: gövdeler toplanıyor");
            try
            {
                var bodies = BodyCollector.CopyBodies(source);
                if (bodies.Count == 0)
                    throw new InvalidOperationException("Görünür katı gövde bulunamadı.");
                result.SourceBodies = bodies.Count;

                var ops = new BodyOps(_swApp);
                double tolerance = 1e-6;

                progress.UpdateTitle($"Shrink wrap: {bodies.Count} gövde birleştiriliyor");
                progress.UpdateProgress(10);
                var original = ops.UnionAll(bodies, tolerance);
                var lumps = new List<IBody2>(original);

                if (options.GapWidth > 0 && lumps.Count > 1)
                {
                    progress.UpdateTitle("Shrink wrap: parçalar arası boşluklar köprüleniyor");
                    progress.UpdateProgress(30);
                    var bridged = ops.Bridge(lumps, options.GapWidth, tolerance);
                    if (bridged != null)
                        lumps = bridged;
                    else
                        result.Notes.Add("Parçalar arası boşluklar köprülenemedi (SOLIDWORKS ofset oluşturamadı).");
                }

                var closer = new HoleCloser(ops);
                if (options.GapWidth > 0)
                {
                    progress.UpdateTitle("Shrink wrap: delikler kapatılıyor");
                    progress.UpdateProgress(50);
                    for (int i = 0; i < lumps.Count; i++)
                        lumps[i] = closer.CloseHoles(lumps[i], options.GapWidth);
                }

                if (options.FillCavities)
                {
                    progress.UpdateTitle("Shrink wrap: iç boşluklar dolduruluyor");
                    progress.UpdateProgress(70);
                    for (int i = 0; i < lumps.Count; i++)
                        lumps[i] = ops.FillCavities(lumps[i]);
                }

                progress.UpdateTitle("Shrink wrap: önizleme hazırlanıyor");
                progress.UpdateProgress(85);
                result.Added = ops.Difference(lumps, original);

                result.Bodies = lumps;
                // The translucent envelope is lifted off the model's faces when MakeOffset works. On installations where
                // it fails a plain copy is drawn on the faces; it shows streaks, but the envelope stays visible.
                foreach (var lump in lumps)
                {
                    double gap = Math.Max(5e-5, BodyOps.Bounds(lump).LongestSide * 0.0015);
                    result.Envelope.Add(ops.Offset(lump, gap, outward: true) ?? (IBody2)lump.Copy());
                }
                result.HolesClosed = closer.Closed;
                result.HolesFailed = closer.Failed;
                result.TooWide = closer.TooWide;
                result.Log = closer.Log;
                result.FailedBooleans = ops.FailedBooleans;
                result.Seconds = watch.Elapsed.TotalSeconds;
                return result;
            }
            finally
            {
                progress.End();
            }
        }

        /// <summary>Puts the computed bodies into a new part and returns the summary text.</summary>
        public string Commit(WrapResult result)
        {
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
