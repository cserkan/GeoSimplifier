using System;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Collects the tessellated solid bodies of a part or assembly into one triangle mesh (meters).</summary>
    public static class MeshCollector
    {
        public static TriangleMesh Collect(IModelDoc2 doc)
        {
            var mesh = new TriangleMesh();
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART:
                    var bodies = (object[])((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true);
                    AddBodies(mesh, bodies, null);
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
                        AddBodies(mesh, compBodies, comp.Transform2);
                    }
                    break;

                default:
                    throw new InvalidOperationException("Shrink wrap works on parts and assemblies only.");
            }
            return mesh;
        }

        private static void AddBodies(TriangleMesh mesh, object[] bodies, MathTransform transform)
        {
            if (bodies == null)
                return;
            var m = transform != null ? (double[])transform.ArrayData : null;
            foreach (IBody2 body in bodies)
            {
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
