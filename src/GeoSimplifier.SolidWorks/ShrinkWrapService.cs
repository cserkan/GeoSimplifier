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

        public string Run(ShrinkWrapOptions options, Action<string> status)
        {
            var source = _swApp.IActiveDoc2 ?? throw new InvalidOperationException("No active document.");
            var watch = Stopwatch.StartNew();

            status("Collecting geometry...");
            var mesh = MeshCollector.Collect(source);
            if (mesh.Triangles.Count == 0)
                throw new InvalidOperationException("No visible solid bodies found.");

            status($"Voxelizing {mesh.Triangles.Count:N0} triangles...");
            var result = ShrinkWrapper.Run(mesh, options);

            status($"Building solid from {result.Boxes.Count:N0} boxes...");
            var builder = new BodyBuilder(_swApp);
            var bodies = builder.Build(result, (done, total) =>
            {
                if (done % 100 == 0) status($"Building solid: {done:N0}/{total:N0} boxes");
            });

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
                $"Done in {watch.Elapsed.TotalSeconds:F1} s. Voxel {result.VoxelSize * 1000:G4} mm, " +
                $"grid {g.Nx}x{g.Ny}x{g.Nz}, {result.Boxes.Count:N0} boxes, " +
                $"{bodies.Count} lump(s), {created} body(ies) created, {builder.FailedUnions} failed union(s).";
            if (created < bodies.Count)
                summary += " WARNING: some lumps could not be added to the part; the result is incomplete.";
            if (result.BodyCount > 1)
                summary += " The model has parts farther apart than the gap closing distance; increase gap closing to get one body.";
            return summary;
        }

        private IPartDoc NewPart()
        {
            string template = _swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc = _swApp.NewDocument(template, 0, 0, 0) as IPartDoc;
            return doc ?? throw new InvalidOperationException("Could not create a new part from template: " + template);
        }
    }
}
