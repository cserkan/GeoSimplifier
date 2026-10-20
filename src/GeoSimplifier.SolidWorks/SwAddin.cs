using System;
using System.Runtime.InteropServices;
using GeoSimplifier.SolidWorks.UI;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace GeoSimplifier.SolidWorks
{
    /// <summary>
    /// SOLIDWORKS entry point. Registered with "RegAsm /codebase" (see scripts/register.cmd), which runs
    /// <see cref="RegisterFunction"/> and makes the add-in show up under Tools > Add-Ins.
    /// </summary>
    [ComVisible(true)]
    [Guid("6F3E1B52-8C4D-4E0A-9B7A-2D5C1E9F4A30")]
    [ProgId(ProgId)]
    public class SwAddin : ISwAddin
    {
        public const string ProgId = "GeoSimplifier.SwAddin";
        private const string Title = "GeoSimplifier";
        private const string Description = "Shrink wrap: wraps a part or assembly from the outside into a single simplified solid.";

        private const int MainCommandGroupId = 0x5A11;
        private const int ShrinkWrapCommandId = 1;

        private ISldWorks _swApp;
        private ICommandManager _commandManager;
        private int _cookie;
        private ITaskpaneView _taskpaneView;
        private ShrinkWrapTaskPane _taskPane;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _cookie = Cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _cookie);
            _commandManager = _swApp.GetCommandManager(_cookie);

            AddCommands();
            CreateTaskPane();
            return true;
        }

        public bool DisconnectFromSW()
        {
            _commandManager?.RemoveCommandGroup(MainCommandGroupId);
            _taskpaneView?.DeleteView();
            _taskPane?.Dispose();

            ReleaseCom(_taskpaneView);
            ReleaseCom(_commandManager);
            ReleaseCom(_swApp);
            _taskpaneView = null;
            _commandManager = null;
            _swApp = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private void AddCommands()
        {
            int errors = 0;
            ICommandGroup group = _commandManager.CreateCommandGroup2(
                MainCommandGroupId, Title, Description, Description, -1, true, ref errors);

            int menuToolbar = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            group.AddCommandItem2("Shrink Wrap", -1, "Wrap the active model into one simplified solid",
                "Shrink Wrap", 0, nameof(OnShrinkWrap), nameof(OnShrinkWrapEnable), ShrinkWrapCommandId, menuToolbar);

            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();
        }

        private void CreateTaskPane()
        {
            _taskpaneView = _swApp.CreateTaskpaneView2("", Title);
            _taskPane = new ShrinkWrapTaskPane(new ShrinkWrapService(_swApp));
            _taskPane.CreateControl();
            _taskpaneView.DisplayWindowFromHandlex64(_taskPane.Handle.ToInt64());
        }

        // Command callbacks: SOLIDWORKS resolves these by name, so they must stay public.

        public void OnShrinkWrap()
        {
            _taskpaneView?.ShowView();
            _taskPane?.RunFromCommand();
        }

        /// <returns>1 = enabled, 0 = disabled.</returns>
        public int OnShrinkWrapEnable()
        {
            var doc = _swApp?.IActiveDoc2;
            if (doc == null)
                return 0;
            int type = doc.GetType();
            return type == (int)swDocumentTypes_e.swDocPART || type == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
        }

        private static void ReleaseCom(object o)
        {
            if (o != null && Marshal.IsComObject(o))
                Marshal.ReleaseComObject(o);
        }

        #region COM registration

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString() + "}";
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid))
            {
                key.SetValue(null, 0); // 0 = not loaded at startup until the user enables it
                key.SetValue("Description", Description);
                key.SetValue("Title", Title);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
            {
                key.SetValue(null, 1);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string guid = "{" + t.GUID.ToString() + "}";
            Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\" + guid, false);
            Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\" + guid, false);
        }

        #endregion
    }
}
