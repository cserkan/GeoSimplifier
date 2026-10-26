using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// Components and bodies the user keeps out of the wrap. They are not wrapped or closed; their original
    /// bodies are united with the wrapped body afterwards, so they keep every hole and detail.
    /// Items are matched by name (component Name2, body Name), which stays valid between recomputations.
    /// </summary>
    public sealed class Exclusions
    {
        private readonly HashSet<string> _components = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => _components.Count == 0 && _bodies.Count == 0;

        public int Count => _components.Count + _bodies.Count;

        /// <summary>A stable text for the current set, part of the preview cache key.</summary>
        public string Key => string.Join(";", _components.OrderBy(n => n)) + "|" + string.Join(";", _bodies.OrderBy(n => n));

        public void Clear()
        {
            _components.Clear();
            _bodies.Clear();
        }

        public void AddComponent(IComponent2 component)
        {
            if (component != null)
                _components.Add(component.Name2);
        }

        public void AddComponentName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                _components.Add(name.Trim());
        }

        /// <param name="component">The component the body was selected in; null in a part.</param>
        public void AddBody(IBody2 body, IComponent2 component)
        {
            if (body != null)
                _bodies.Add(BodyKey(component?.Name2, body.Name));
        }

        /// <summary>True for a leaf component that is excluded itself or lies under an excluded sub-assembly.</summary>
        public bool Contains(IComponent2 component)
        {
            string name = component.Name2;
            return _components.Contains(name) || _components.Any(c => name.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True when an excluded component or body lies under the sub-assembly named <paramref name="parent"/>.</summary>
        public bool AnyUnder(string parent) =>
            _components.Any(c => c.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)) ||
            _bodies.Any(b => b.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));

        public bool Contains(IBody2 body, IComponent2 component) => _bodies.Contains(BodyKey(component?.Name2, body.Name));

        private static string BodyKey(string component, string body) => (component ?? "") + "|" + body;

        /// <summary>Reads the items held by a PropertyManager selection box (its selection mark).</summary>
        public static Exclusions FromSelection(IModelDoc2 doc, int mark)
        {
            var result = new Exclusions();
            var selection = (ISelectionMgr)doc.SelectionManager;
            int count = selection.GetSelectedObjectCount2(mark);
            for (int i = 1; i <= count; i++)
            {
                var obj = selection.GetSelectedObject6(i, mark);
                var component = selection.GetSelectedObjectsComponent4(i, mark) as IComponent2;
                switch ((swSelectType_e)selection.GetSelectedObjectType3(i, mark))
                {
                    case swSelectType_e.swSelCOMPONENTS:
                        result.AddComponent(obj as IComponent2 ?? component);
                        break;
                    case swSelectType_e.swSelSOLIDBODIES:
                    case swSelectType_e.swSelSURFACEBODIES:
                        result.AddBody(obj as IBody2, component);
                        break;
                }
            }
            return result;
        }
    }
}
