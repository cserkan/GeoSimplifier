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
        public static List<IBody2> CopyBodies(IModelDoc2 doc, Exclusions exclusions, out List<IBody2> excluded) =>
            CopyBodies(doc, exclusions, out excluded, null);

        /// <summary>
        /// As above; <paramref name="instances"/>, when given, receives for each copy which body of which part (and
        /// configuration) it is and the component's transform (SOLIDWORKS' 16 numbers), so repeated instances of a
        /// part can share work. Bodies of a part document get a null entry.
        /// </summary>
        public static List<IBody2> CopyBodies(IModelDoc2 doc, Exclusions exclusions, out List<IBody2> excluded,
                                              List<(string key, double[] transform)> instances)
        {
            var copies = new List<IBody2>();
            excluded = new List<IBody2>();
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART:
                    var bodies = (object[])((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true);
                    if (bodies != null)
                        foreach (IBody2 body in bodies)
                        {
                            bool skip = exclusions != null && exclusions.Contains(body, null);
                            (skip ? excluded : copies).Add((IBody2)body.Copy());
                            if (!skip) instances?.Add((null, null));
                        }
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
                        string part = instances == null ? null : $"{comp.GetPathName()}|{comp.ReferencedConfiguration}";
                        for (int i = 0; i < compBodies.Length; i++)
                        {
                            var body = (IBody2)compBodies[i];
                            var copy = (IBody2)body.Copy();
                            if (transform != null)
                                copy.ApplyTransform(transform);
                            bool skip = wholeComponent || (exclusions != null && exclusions.Contains(body, comp));
                            (skip ? excluded : copies).Add(copy);
                            if (!skip) instances?.Add(($"{part}|{i}", (double[])((IMathTransform)transform)?.ArrayData));
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
