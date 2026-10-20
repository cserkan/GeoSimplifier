using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Collects the solid bodies of a part or assembly, as a triangle mesh or as body copies (meters).</summary>
    public static class MeshCollector
    {
        /// <summary>Tessellates every visible solid body into one mesh in model coordinates.</summary>
        public static TriangleMesh Collect(IModelDoc2 doc)
        {
            var mesh = new TriangleMesh();
            foreach (var (body, transform) in EnumerateBodies(doc))
            {
                var m = transform != null ? (double[])transform.ArrayData : null;
                var faces = (object[])body.GetFaces();
                if (faces == null)
                    continue;
                foreach (IFace2 face in faces)
                {
                    var tris = (float[])face.GetTessTriangles(true);
                    if (tris == null)
                        continue;
                    for (int i = 0; i + 8 < tris.Length; i += 9)
                    {
                        mesh.Add(ToAssembly(tris[i], tris[i + 1], tris[i + 2], m),
                                 ToAssembly(tris[i + 3], tris[i + 4], tris[i + 5], m),
                                 ToAssembly(tris[i + 6], tris[i + 7], tris[i + 8], m));
                    }
                }
            }
            return mesh;
        }

        /// <summary>Temporary copies of every visible solid body, moved into model coordinates.</summary>
        public static List<IBody2> CopyBodies(IModelDoc2 doc)
        {
            var copies = new List<IBody2>();
            foreach (var (body, transform) in EnumerateBodies(doc))
            {
                var copy = (IBody2)body.Copy();
                if (transform != null)
                    copy.ApplyTransform(transform);
                copies.Add(copy);
            }
            return copies;
        }

        private static IEnumerable<(IBody2 Body, MathTransform Transform)> EnumerateBodies(IModelDoc2 doc)
        {
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART:
                    var bodies = (object[])((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true);
                    if (bodies != null)
                        foreach (IBody2 body in bodies)
                            yield return (body, null);
                    break;

                case swDocumentTypes_e.swDocASSEMBLY:
                    var components = (object[])((IAssemblyDoc)doc).GetComponents(false);
                    if (components == null)
                        break;
                    foreach (IComponent2 comp in components)
                    {
                        if (comp.IsSuppressed() || comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden)
                            continue;
                        // Leaf components only; sub-assemblies are covered by their children.
                        var children = (object[])comp.GetChildren();
                        if (children != null && children.Length > 0)
                            continue;
                        var compBodies = (object[])comp.GetBodies3((int)swBodyType_e.swSolidBody, out _);
                        if (compBodies == null)
                            continue;
                        var transform = comp.Transform2;
                        foreach (IBody2 body in compBodies)
                            yield return (body, transform);
                    }
                    break;

                default:
                    throw new InvalidOperationException("Shrink wrap works on parts and assemblies only.");
            }
        }

        /// <summary>
        /// Applies a component transform. SOLIDWORKS stores it as a row-vector 3x3 rotation (indices 0-8),
        /// translation (9-11) and scale (12): p' = p * R * s + t.
        /// </summary>
        private static Vec3 ToAssembly(double x, double y, double z, double[] m)
        {
            if (m == null)
                return new Vec3(x, y, z);
            double s = m[12];
            return new Vec3(
                (x * m[0] + y * m[3] + z * m[6]) * s + m[9],
                (x * m[1] + y * m[4] + z * m[7]) * s + m[10],
                (x * m[2] + y * m[5] + z * m[8]) * s + m[11]);
        }
    }
}
