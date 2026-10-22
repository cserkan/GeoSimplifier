using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Collects temporary copies of the visible solid bodies of a part or assembly, in model coordinates.</summary>
    public static class BodyCollector
    {
        public static List<IBody2> CopyBodies(IModelDoc2 doc) => CopyBodies(doc, null, out _);

        /// <summary>Copies of the visible surface (sheet) bodies, in model coordinates, skipping excluded ones.</summary>
        public static List<IBody2> CopySheets(IModelDoc2 doc, Exclusions exclusions)
        {
            var copies = new List<IBody2>();
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART:
                    var bodies = (object[])((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSheetBody, true);
                    if (bodies != null)
                        foreach (IBody2 body in bodies)
                            if (exclusions == null || !exclusions.Contains(body, null))
                                copies.Add((IBody2)body.Copy());
                    break;

                case swDocumentTypes_e.swDocASSEMBLY:
                    foreach (IComponent2 comp in (object[])((IAssemblyDoc)doc).GetComponents(false) ?? new object[0])
                    {
                        if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed ||
                            comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden ||
                            (exclusions != null && exclusions.Contains(comp)))
                            continue;
                        var children = (object[])comp.GetChildren();
                        if (children != null && children.Length > 0)
                            continue;
                        var sheets = (object[])comp.GetBodies3((int)swBodyType_e.swSheetBody, out _);
                        if (sheets == null)
                            continue;
                        var transform = comp.Transform2;
                        foreach (IBody2 body in sheets)
                        {
                            if (exclusions != null && exclusions.Contains(body, comp))
                                continue;
                            var copy = (IBody2)body.Copy();
                            if (transform != null)
                                copy.ApplyTransform(transform);
                            copies.Add(copy);
                        }
                    }
                    break;
            }
            return copies;
        }

        /// <summary>As <see cref="CopyBodies(IModelDoc2)"/>; bodies matched by <paramref name="exclusions"/> go to <paramref name="excluded"/>.</summary>
        public static List<IBody2> CopyBodies(IModelDoc2 doc, Exclusions exclusions, out List<IBody2> excluded)
        {
            var copies = new List<IBody2>();
            excluded = new List<IBody2>();
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART:
                    var bodies = (object[])((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true);
                    if (bodies != null)
                        foreach (IBody2 body in bodies)
                            (exclusions != null && exclusions.Contains(body, null) ? excluded : copies).Add((IBody2)body.Copy());
                    break;

                case swDocumentTypes_e.swDocASSEMBLY:
                    var components = (object[])((IAssemblyDoc)doc).GetComponents(false);
                    if (components == null)
                        break;
                    foreach (IComponent2 comp in components)
                    {
                        // IsSuppressed() is also true for lightweight components, whose bodies GetBodies3 still returns.
                        if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed ||
                            comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden)
                            continue;
                        // Leaf components only; sub-assemblies are covered by their children.
                        var children = (object[])comp.GetChildren();
                        if (children != null && children.Length > 0)
                            continue;
                        var compBodies = (object[])comp.GetBodies3((int)swBodyType_e.swSolidBody, out _);
                        if (compBodies == null)
                            continue;
                        var transform = comp.Transform2;
                        bool wholeComponent = exclusions != null && exclusions.Contains(comp);
                        foreach (IBody2 body in compBodies)
                        {
                            var copy = (IBody2)body.Copy();
                            if (transform != null)
                                copy.ApplyTransform(transform);
                            (wholeComponent || (exclusions != null && exclusions.Contains(body, comp)) ? excluded : copies).Add(copy);
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException("Shrink wrap yalnızca parça ve montajlarda çalışır.");
            }
            return copies;
        }
    }
}
