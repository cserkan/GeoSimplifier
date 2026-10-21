using System;
using System.Collections.Generic;
using System.Diagnostics;
using GeoSimplifier.Core.Wrap;
using GeoSimplifier.SolidWorks.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks
{
    /// <summary>
    /// Exact shrink wrap of the active part or assembly into a new part: union all bodies, bridge nearby
    /// bodies, close holes flush and fill internal cavities. Faces that stay visible are the original ones.
    /// </summary>
    public sealed class ShrinkWrapService
    {
        private readonly ISldWorks _swApp;

        public ShrinkWrapService(ISldWorks swApp)
        {
            _swApp = swApp;
        }

        public string Run(ShrinkWrapOptions options)
        {
            var source = _swApp.IActiveDoc2 ?? throw new InvalidOperationException("Açık bir parça ya da montaj yok.");
            var watch = Stopwatch.StartNew();
            var notes = new List<string>();

            _swApp.GetUserProgressBar(out UserProgressBar progress);
            progress.Start(0, 100, "Shrink wrap: gövdeler toplanıyor");
            try
            {
                var bodies = BodyCollector.CopyBodies(source);
                if (bodies.Count == 0)
                    throw new InvalidOperationException("Görünür katı gövde bulunamadı.");

                var ops = new BodyOps(_swApp);
                double tolerance = 1e-6;

                progress.UpdateTitle($"Shrink wrap: {bodies.Count} gövde birleştiriliyor");
                progress.UpdateProgress(10);
                var lumps = ops.UnionAll(bodies, tolerance);

                if (options.GapWidth > 0 && lumps.Count > 1)
                {
                    progress.UpdateTitle("Shrink wrap: parçalar arası boşluklar köprüleniyor");
                    progress.UpdateProgress(30);
                    var bridged = ops.Bridge(lumps, options.GapWidth, tolerance);
                    if (bridged != null)
                        lumps = bridged;
                    else
                        notes.Add("Parçalar arası boşluklar köprülenemedi (SOLIDWORKS ofset oluşturamadı).");
                }

                var closer = new HoleCloser();
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
                    progress.UpdateProgress(75);
                    for (int i = 0; i < lumps.Count; i++)
                        lumps[i] = ops.FillCavities(lumps[i]);
                }

                progress.UpdateTitle("Shrink wrap: parça oluşturuluyor");
                progress.UpdateProgress(90);
                var part = NewPart();
                int created = 0;
                int featureOptions = (int)(swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck | swCreateFeatureBodyOpts_e.swCreateFeatureBodySimplify);
                foreach (var body in lumps)
                {
                    if (part.CreateFeatureFromBody3(body, false, featureOptions) != null)
                        created++;
                }
                ((IModelDoc2)part).ViewZoomtofit2();

                string summary =
                    $"Tamamlandı: {watch.Elapsed.TotalSeconds:F1} sn. {bodies.Count} gövde → {created} gövde. " +
                    $"{closer.Closed} delik kapatıldı" + (closer.Failed > 0 ? $", {closer.Failed} delik kapatılamadı" : "") + ".";
                if (created < lumps.Count)
                    notes.Add("Bazı gövdeler parçaya eklenemedi, sonuç eksik olabilir.");
                if (lumps.Count > 1)
                    notes.Add("Birbirinden uzak parçalar var; tek gövde için kapatma genişliğini artırın.");
                if (ops.FailedBooleans > 0)
                    notes.Add($"{ops.FailedBooleans} boolean işlemi başarısız oldu.");
                return notes.Count == 0 ? summary : summary + " " + string.Join(" ", notes);
            }
            finally
            {
                progress.End();
            }
        }

        private IPartDoc NewPart()
        {
            string template = _swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc = _swApp.NewDocument(template, 0, 0, 0) as IPartDoc;
            return doc ?? throw new InvalidOperationException("Şablondan yeni parça oluşturulamadı: " + template);
        }
    }
}
