using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>
    /// Draws temporary bodies on a part. Assemblies get no drawn preview: with the assembly as owner Display3
    /// succeeds but draws nothing, and with a component as owner SOLIDWORKS 2026 crashes when the bodies are
    /// hidden again (inside or outside the PropertyManager callbacks).
    /// </summary>
    public sealed class TempBodyPreview
    {
        private readonly IModelDoc2 _doc;
        private readonly List<IBody2> _shown = new List<IBody2>();

        public TempBodyPreview(IModelDoc2 doc)
        {
            _doc = doc;
            Supported = doc is IPartDoc;
        }

        /// <summary>False for assemblies: nothing is drawn.</summary>
        public bool Supported { get; }

        /// <returns>False when nothing was drawn.</returns>
        public bool Show(IBody2 body, int color, double[] rgb, double transparency)
        {
            if (!Supported)
                return false;
            // Colour, ambient, diffuse, specular, shininess, transparency, emission.
            body.MaterialPropertyValues2 = new[] { rgb[0], rgb[1], rgb[2], 1, 1, 0.3, 0.3, transparency, 0 };
            _shown.Add(body);
            return body.Display3(_doc, color, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone) == 0;
        }

        public void Clear()
        {
            foreach (var body in _shown)
            {
                try { body.Hide(_doc); }
                catch (COMException) { }
            }
            _shown.Clear();
            _doc?.GraphicsRedraw2();
        }
    }
}
