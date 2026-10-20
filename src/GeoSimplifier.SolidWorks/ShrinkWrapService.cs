using System;
using System.Diagnostics;
using GeoSimplifier.Core.ShrinkWrap;
using GeoSimplifier.SolidWorks.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks
{
    /// <summary>Runs the shrink wrap on the active document and puts the result in a new part.</summary>
    public sealed class ShrinkWrapService
    {
        private readonly ISldWorks _swApp;

        public ShrinkWrapService(ISldWorks swApp)
        {
            _swApp = swApp;
        }

        /// <param name="keepOriginalFaces">
        /// True: union the original bodies with the voxel fill (closed holes, cavities) so the exterior
        /// faces stay exact. False: build the whole result from voxels.
        /// </param>
        public string Run(ShrinkWrapOptions options, bool keepOriginalFaces)
        {
            var source = _swApp.IActiveDoc2 ?? throw new InvalidOperationException("Açık bir parça ya da montaj yok.");
            var watch = Stopwatch.StartNew();

            _swApp.GetUserProgressBar(out UserProgressBar progress);
            progress.Start(0, 100, "Shrink wrap: geometri toplanıyor");
            try
            {
                var mesh = MeshCollector.Collect(source);
                if (mesh.Triangles.Count == 0)
                    throw new InvalidOperationException("Görünür katı gövde bulunamadı.");

                progress.UpdateTitle($"Shrink wrap: {mesh.Triangles.Count:N0} üçgen voksellere çevriliyor");
                progress.UpdateProgress(10);
                var result = ShrinkWrapper.Run(mesh, options);

                var boxes = keepOriginalFaces ? result.FillBoxes : result.Boxes;
                var grid = keepOriginalFaces ? result.Fill : result.Solid;
                var seeds = keepOriginalFaces ? MeshCollector.CopyBodies(source) : null;

                progress.UpdateTitle($"Shrink wrap: gövde oluşturuluyor ({boxes.Count:N0} kutu)");
                progress.UpdateProgress(30);
                var builder = new BodyBuilder(_swApp);
                var bodies = builder.Build(boxes, grid, seeds, (done, total) =>
                {
                    if (done % 50 == 0)
                        progress.UpdateProgress(30 + (int)(60L * done / total));
                });

                progress.UpdateTitle("Shrink wrap: parça oluşturuluyor");
                progress.UpdateProgress(95);
                var part = NewPart();
                int created = 0;
                int featureOptions = (int)(swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck | swCreateFeatureBodyOpts_e.swCreateFeatureBodySimplify);
                foreach (var body in bodies)
                {
                    if (part.CreateFeatureFromBody3(body, false, featureOptions) != null)
                        created++;
                }
                ((IModelDoc2)part).ViewZoomtofit2();

                var g = result.Solid;
                string summary =
                    $"Tamamlandı: {watch.Elapsed.TotalSeconds:F1} sn. Voksel {result.VoxelSize * 1000:G4} mm, " +
                    $"ızgara {g.Nx}x{g.Ny}x{g.Nz}, {boxes.Count:N0} kutu, {created} gövde oluşturuldu, " +
                    $"{builder.FailedUnions} başarısız birleştirme.";
                if (created < bodies.Count)
                    summary += " UYARI: bazı parçalar eklenemedi, sonuç eksik olabilir.";
                if (bodies.Count > 1)
                    summary += " Birbirinden uzak parçalar var; tek gövde için 'Delikleri ve boşlukları kapat' genişliğini artırın.";
                return summary;
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
