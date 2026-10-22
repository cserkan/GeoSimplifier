using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>
    /// Draws temporary bodies (in model coordinates) on a part or assembly. In a part the part owns them. An
    /// assembly cannot own temporary bodies: Display3 succeeds but nothing is drawn, so one part component owns
    /// them and the bodies are moved into its coordinates first.
    /// </summary>
    public sealed class TempBodyPreview
    {
        private readonly IModelDoc2 _doc;
        private readonly object _owner;
        private readonly MathTransform _toOwner;
        private readonly List<IBody2> _shown = new List<IBody2>();

        public TempBodyPreview(IModelDoc2 doc)
        {
            _doc = doc;
            _owner = doc;
            if (doc is IAssemblyDoc assembly)
            {
                _owner = null;
                foreach (IComponent2 comp in (object[])assembly.GetComponents(false) ?? new object[0])
                {
                    var children = (object[])comp.GetChildren();
                    if ((children != null && children.Length > 0) ||
                        comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                        continue;
                    _owner = comp;
                    _toOwner = (MathTransform)comp.Transform2?.Inverse();
                    break;
                }
            }
        }

        /// <summary>Description of the owner, for the log.</summary>
        public string OwnerName => _owner is IComponent2 c ? c.Name2 : _owner == null ? "yok" : "belge";

        /// <returns>False when SOLIDWORKS could not draw the body.</returns>
        public bool Show(IBody2 body, int color, double[] rgb, double transparency)
        {
            if (_owner == null)
                return false;
            var shown = body;
            if (_toOwner != null)
            {
                shown = (IBody2)body.Copy();
                shown.ApplyTransform(_toOwner);
            }
            // Colour, ambient, diffuse, specular, shininess, transparency, emission.
            shown.MaterialPropertyValues2 = new[] { rgb[0], rgb[1], rgb[2], 1, 1, 0.3, 0.3, transparency, 0 };
            _shown.Add(shown);
            return shown.Display3(_owner, color, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone) == 0;
        }

        public void Clear()
        {
            foreach (var body in _shown)
            {
                try { body.Hide(_owner); }
                catch (COMException) { }
            }
            _shown.Clear();
            _doc?.GraphicsRedraw2();
        }
    }
}
