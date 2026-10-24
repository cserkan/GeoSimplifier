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
        private ShrinkWrapService _service;

        // The open page is kept referenced for as long as SOLIDWORKS may call it.
        private ShrinkWrapPage _page;

        // Created on SOLIDWORKS' main thread; test hooks called from another process arrive on an RPC thread and
        // are moved onto the main thread through it.
        private System.Windows.Forms.Control _mainThread;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _cookie = Cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _cookie);
            _commandManager = _swApp.GetCommandManager(_cookie);

            _service = new ShrinkWrapService(_swApp);
            _mainThread = new System.Windows.Forms.Control();
            _mainThread.CreateControl();
            AddCommands();
            return true;
        }

        public bool DisconnectFromSW()
        {
            _mainThread?.Dispose();
            _mainThread = null;
            RemoveCommandTabs();
            _commandManager?.RemoveCommandGroup(MainCommandGroupId);

            ReleaseCom(_commandManager);
            ReleaseCom(_swApp);
            _commandManager = null;
            _swApp = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private static readonly int[] DocTypes = { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY };

        private void AddCommands()
        {
            int errors = 0;
            ICommandGroup group = _commandManager.CreateCommandGroup2(
                MainCommandGroupId, Title, Description, Description, -1, true, ref errors);

            var icons = IconFactory.CreateIconStrips();
            group.IconList = icons;
            group.MainIconList = icons;

            int menuToolbar = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            int index = group.AddCommandItem2("Shrink Wrap", -1, "Modeli dışarıdan sararak tek, basitleştirilmiş bir gövde oluşturur",
                "Shrink Wrap", 0, nameof(OnShrinkWrap), nameof(OnShrinkWrapEnable), ShrinkWrapCommandId, menuToolbar);

            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();

            // A "GeoSimplifier" tab in the CommandManager ribbon for parts and assemblies.
            int commandId = group.get_CommandID(index);
            foreach (int docType in DocTypes)
            {
                var existing = _commandManager.GetCommandTab(docType, Title);
                if (existing != null)
                    _commandManager.RemoveCommandTab(existing);
                var tab = _commandManager.AddCommandTab(docType, Title);
                var box = tab.AddCommandTabBox();
                box.AddCommands(new[] { commandId }, new[] { (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow });
            }
        }

        private void RemoveCommandTabs()
        {
            if (_commandManager == null)
                return;
            foreach (int docType in DocTypes)
            {
                var tab = _commandManager.GetCommandTab(docType, Title);
                if (tab != null)
                    _commandManager.RemoveCommandTab(tab);
            }
        }

        // Command callbacks: SOLIDWORKS resolves these by name, so they must stay public.

        public void OnShrinkWrap()
        {
            try
            {
                _page = new ShrinkWrapPage(_swApp, _service);
                _page.Show();
            }
            catch (Exception ex)
            {
                _swApp.SendMsgToUser2("Shrink wrap açılamadı: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
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

        /// <summary>
        /// Test hook for tools\GeoSimplifier.Probe (via ISldWorks.GetAddInObject): opens the Shrink Wrap page on the
        /// active document and types the given limits (mm, separated by ';') into it, then closes it with OK or
        /// Cancel. Returns at once; the page logs every step to %TEMP%\GeoSimplifier.log.
        /// </summary>
        public string TestPage(string limitsMm, bool ok) => TestPageExcluding(limitsMm, ok, "");

        /// <summary>As <see cref="TestPage"/>, with the components named in <paramref name="excludeNames"/> (';'-separated Name2) excluded.</summary>
        public string TestPageExcluding(string limitsMm, bool ok, string excludeNames) =>
            TestPageOutput(limitsMm, ok, excludeNames, "");

        /// <summary>As <see cref="TestPageExcluding"/>; a non-empty <paramref name="configurationName"/> chooses "Konfigürasyon oluştur".</summary>
        public string TestPageOutput(string limitsMm, bool ok, string excludeNames, string configurationName)
        {
            // A leading "nogc:" runs the steps without forcing garbage collections.
            bool forceGc = !limitsMm.StartsWith("nogc:");
            limitsMm = limitsMm.Replace("nogc:", "");
            // A leading "shell:" ticks "Yüzey gövde (dış kabuk)".
            bool shell = limitsMm.StartsWith("shell:");
            limitsMm = limitsMm.Replace("shell:", "");
            var limits = Array.ConvertAll(limitsMm.Split(';'),
                v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) / 1000);
            _mainThread.BeginInvoke(new Action(() =>
            {
                _page = new ShrinkWrapPage(_swApp, _service);
                _page.Show();
                if (!string.IsNullOrEmpty(excludeNames))
                    _page.ExcludeForTest(excludeNames.Split(';'));
                if (!string.IsNullOrEmpty(configurationName))
                    _page.OutputForTest(configurationName);
                if (shell)
                    _page.ShellForTest();
                _page.Simulate(limits, ok, forceGc);
            }));
            return "başladı";
        }

        /// <summary>
        /// Test hook: on SOLIDWORKS' main thread, tries IBody2.MakeOffset and IBody2.DeleteFaces5 on a copy of the
        /// first body of the active part (or of the first part component) and logs the results.
        /// </summary>
        public string TestLocalOps()
        {
            _mainThread.BeginInvoke(new Action(() =>
            {
                try
                {
                    var bodies = Geometry.BodyCollector.CopyBodies(_swApp.IActiveDoc2);
                    foreach (var body in bodies)
                    {
                        var offset = body.Copy() is IBody2 c1 ? c1.MakeOffset(0.001, true) : null;
                        PageLog.Write($"Yerel test: MakeOffset {(offset == null ? "null" : "çalıştı")}");
                        var copy = (IBody2)body.Copy();
                        var cylinders = new System.Collections.Generic.List<object>();
                        foreach (IFace2 f in (object[])copy.GetFaces())
                            if (((ISurface)f.GetSurface()).IsCylinder()) { cylinders.Add(f); break; }
                        bool ok = copy.DeleteFaces5(cylinders.ToArray(), (int)swHealActionType_e.swHealAction_GrowParent,
                            (int)swLoopProcessOption_e.swLoopProcess_Auto, true, out _, out bool valid);
                        PageLog.Write($"Yerel test: DeleteFaces5({cylinders.Count} silindir) {ok}/{valid}, yüz {((object[])copy.GetFaces()).Length}");
                    }
                }
                catch (Exception ex)
                {
                    PageLog.Write("Yerel test hatası: " + ex);
                }
            }));
            return "başladı";
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
