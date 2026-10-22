using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks
{
    /// <summary>
    /// Puts the wrap into a new configuration of the source document instead of a separate new part:
    /// <list type="bullet">
    /// <item>Assembly: the wrap is saved as a part next to the assembly (&lt;assembly&gt;_ShrinkWrap.SLDPRT) and, in a
    /// new configuration, every other top-level component is suppressed and the wrap is inserted at the origin, fixed.</item>
    /// <item>Part: in a new configuration the part's own features are suppressed and the wrap is added as an imported body.</item>
    /// </list>
    /// In the user's existing configurations the wrap is suppressed and nothing else changes; the new
    /// configuration is active at the end.
    /// </summary>
    internal sealed class ConfigurationOutput
    {
        private readonly ISldWorks _swApp;

        public ConfigurationOutput(ISldWorks swApp)
        {
            _swApp = swApp;
        }

        /// <returns>A sentence saying what was created.</returns>
        public string Create(IModelDoc2 source, IList<IBody2> bodies, string name, Func<IPartDoc> newPart)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = "Simple";
            var existing = ((string[])source.GetConfigurationNames() ?? new string[0]).ToList();
            string config = name;
            for (int i = 2; existing.Contains(config); i++)
                config = $"{name}_{i}";

            return source is IAssemblyDoc
                ? CreateInAssembly(source, bodies, config, existing, newPart)
                : CreateInPart(source, bodies, config, existing);
        }

        private string CreateInAssembly(IModelDoc2 source, IList<IBody2> bodies, string config, List<string> existing, Func<IPartDoc> newPart)
        {
            string assemblyPath = source.GetPathName();
            if (string.IsNullOrEmpty(assemblyPath))
                throw new InvalidOperationException("Konfigürasyon oluşturmak için önce montajı kaydedin.");

            // The wrap as a part file next to the assembly; an existing file is never overwritten.
            string dir = Path.GetDirectoryName(assemblyPath);
            string stem = Path.GetFileNameWithoutExtension(assemblyPath) + "_ShrinkWrap";
            string partPath = Path.Combine(dir, stem + ".SLDPRT");
            for (int i = 2; File.Exists(partPath); i++)
                partPath = Path.Combine(dir, $"{stem}_{i}.SLDPRT");

            var part = newPart();
            AddBodies(part, bodies);
            int errors = 0, warnings = 0;
            if (!((IModelDoc2)part).Extension.SaveAs3(partPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings))
                throw new InvalidOperationException($"Sarma parçası kaydedilemedi ({partPath}, hata {errors}).");
            // AddComponent5 needs the part open; its window is closed once it is in the assembly.
            string partTitle = ((IModelDoc2)part).GetTitle();
            int activateErrors = 0;
            _swApp.ActivateDoc3(source.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);

            var assembly = (IAssemblyDoc)source;
            try
            {
                source.ConfigurationManager.AddConfiguration2(config, "Shrink wrap", "", 0, "", "", false);
                source.ShowConfiguration2(config);
                foreach (IComponent2 comp in (object[])assembly.GetComponents(true) ?? new object[0])
                    comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentSuppressed);
                // Assembly features (cuts, holes) act on the suppressed components and would only show errors here.
                for (var feature = (IFeature)source.FirstFeature(); feature != null; feature = (IFeature)feature.GetNextFeature())
                {
                    if (KeptFeatureTypes.Contains(feature.GetTypeName2()) || feature.GetTypeName2() == "MateGroup" ||
                        feature.GetSpecificFeature2() is IComponent2)
                        continue;
                    feature.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null);
                }

                var wrap = assembly.AddComponent5(partPath, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "", false, "", 0, 0, 0);
                if (wrap == null)
                    throw new InvalidOperationException("Sarma parçası montaja eklenemedi.");
                source.ClearSelection2(true);
                wrap.Select4(false, null, false);
                assembly.FixComponent();
                source.ClearSelection2(true);

                // The wrap is suppressed in every existing configuration ("suppress new components" of a
                // configuration does not keep it out there), one configuration at a time.
                string wrapName = wrap.Name2;
                foreach (string other in existing)
                {
                    source.ShowConfiguration2(other);
                    assembly.GetComponentByName(wrapName)?.SetSuppression2((int)swComponentSuppressionState_e.swComponentSuppressed);
                }
                source.ShowConfiguration2(config);
            }
            finally
            {
                _swApp.CloseDoc(partTitle);
            }
            source.EditRebuild3();
            source.ViewZoomtofit2();
            return $"\"{config}\" konfigürasyonu oluşturuldu; sarma {Path.GetFileName(partPath)} olarak kaydedildi ve orijine sabitlendi.";
        }

        private string CreateInPart(IModelDoc2 source, IList<IBody2> bodies, string config, List<string> existing)
        {
            {
                source.ConfigurationManager.AddConfiguration2(config, "Shrink wrap", "", 0, "", "", false);
                source.ShowConfiguration2(config);
                // The part's own features are suppressed in this configuration; reference geometry and folders stay.
                for (var feature = (IFeature)source.FirstFeature(); feature != null; feature = (IFeature)feature.GetNextFeature())
                {
                    if (KeptFeatureTypes.Contains(feature.GetTypeName2()))
                        continue;
                    feature.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null);
                }
                var added = AddBodies((IPartDoc)source, bodies);

                // The wrap is suppressed in every existing configuration, one configuration at a time.
                foreach (string other in existing)
                {
                    source.ShowConfiguration2(other);
                    foreach (string feature in added)
                        (((IPartDoc)source).FeatureByName(feature) as IFeature)?.SetSuppression2(
                            (int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                }
                source.ShowConfiguration2(config);
            }
            source.EditRebuild3();
            source.ViewZoomtofit2();
            return $"\"{config}\" konfigürasyonu oluşturuldu; parçanın özellikleri bu konfigürasyonda bastırıldı ve sarma eklendi.";
        }

        private static readonly HashSet<string> KeptFeatureTypes = new HashSet<string>
        {
            "OriginProfileFeature", "RefPlane", "RefAxis", "RefPoint", "CoordSys", "MaterialFolder", "SensorFolder",
            "DocsFolder", "HistoryFolder", "DetailCabinet", "SurfaceBodyFolder", "SolidBodyFolder", "CommentsFolder",
            "FavoriteFolder", "SelectionSetFolder", "EqnFolder", "MateReferenceGroupFolder", "InkMarkupFolder",
            "EnvFolder", "LiveSectionFolder", "BlockFolder",
        };

        /// <returns>The names of the features created.</returns>
        private static List<string> AddBodies(IPartDoc part, IList<IBody2> bodies)
        {
            var names = new List<string>();
            int options = (int)(swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck | swCreateFeatureBodyOpts_e.swCreateFeatureBodySimplify);
            foreach (var body in bodies)
                if (part.CreateFeatureFromBody3((IBody2)body.Copy(), false, options) is IFeature feature)
                    names.Add(feature.Name);
            return names;
        }
    }
}
