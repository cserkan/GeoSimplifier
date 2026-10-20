using System;
using System.Runtime.InteropServices;
using GeoSimplifier.Core.ShrinkWrap;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>
    /// The Shrink Wrap PropertyManager page (left panel, green check / red cross), like a native feature.
    /// The wrap runs after the page closes with OK, as SOLIDWORKS recommends for model changes.
    /// </summary>
    [ComVisible(true)]
    public sealed class ShrinkWrapPage : IPropertyManagerPage2Handler9
    {
        private const int GroupId = 1;
        private const int KeepFacesId = 2;
        private const int CloseHolesId = 3;
        private const int GapLabelId = 4;
        private const int GapWidthId = 5;
        private const int ResolutionLabelId = 6;
        private const int ResolutionId = 7;

        // Remembered between runs within a SOLIDWORKS session.
        private static bool _keepFaces = true;
        private static bool _closeHoles = true;
        private static double _gapWidth = 0.02; // meters
        private static int _resolution = 256;

        private readonly ISldWorks _swApp;
        private readonly ShrinkWrapService _service;
        private IPropertyManagerPage2 _page;
        private IPropertyManagerPageCheckbox _keepFacesBox;
        private IPropertyManagerPageCheckbox _closeHolesBox;
        private IPropertyManagerPageNumberbox _gapWidthBox;
        private IPropertyManagerPageNumberbox _resolutionBox;
        private bool _ok;

        public ShrinkWrapPage(ISldWorks swApp, ShrinkWrapService service)
        {
            _swApp = swApp;
            _service = service;
        }

        public void Show()
        {
            int errors = 0;
            int options = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton);
            _page = (IPropertyManagerPage2)_swApp.CreatePropertyManagerPage("Shrink Wrap", options, this, ref errors);
            if (_page == null)
                throw new InvalidOperationException("Could not create the PropertyManager page (error " + errors + ").");

            var group = (IPropertyManagerPageGroup)_page.AddGroupBox(GroupId, "Ayarlar",
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));

            _keepFacesBox = (IPropertyManagerPageCheckbox)AddControl(group, KeepFacesId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Orijinal yüzeyleri koru (hassas)",
                "Dış yüzeyler birebir korunur; voksel yalnızca iç boşlukları ve kapatılan delikleri doldurur. Kapalıysa tüm gövde vokselden oluşturulur.");
            _keepFacesBox.Checked = _keepFaces;

            _closeHolesBox = (IPropertyManagerPageCheckbox)AddControl(group, CloseHolesId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Delikleri ve boşlukları kapat",
                "Aşağıdaki genişlikten dar delikler, kanallar ve gövdeler arası boşluklar kapatılır.");
            _closeHolesBox.Checked = _closeHoles;

            AddControl(group, GapLabelId, swPropertyManagerPageControlType_e.swControlType_Label, "En büyük delik / boşluk genişliği", null);
            _gapWidthBox = (IPropertyManagerPageNumberbox)AddControl(group, GapWidthId, swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "En büyük delik / boşluk genişliği", "Bu genişliğe kadar olan açıklıklar kapatılır.");
            _gapWidthBox.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, 0.0001, 10, true, 0.001, 0.01, 0.0001);
            _gapWidthBox.Value = _gapWidth;

            AddControl(group, ResolutionLabelId, swPropertyManagerPageControlType_e.swControlType_Label,
                "Çözünürlük (en uzun kenardaki voksel sayısı)", null);
            _resolutionBox = (IPropertyManagerPageNumberbox)AddControl(group, ResolutionId, swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Çözünürlük", "Yüksek değer daha hassas sonuç verir; süre ve bellek artar.");
            _resolutionBox.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 16, 2048, true, 16, 128, 1);
            _resolutionBox.Value = _resolution;

            UpdateEnabled();
            _page.Show2(0);
        }

        private static object AddControl(IPropertyManagerPageGroup group, int id, swPropertyManagerPageControlType_e type, string caption, string tip)
        {
            return group.AddControl2(id, (short)type, caption, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge,
                (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled), tip ?? "");
        }

        private void UpdateEnabled()
        {
            ((IPropertyManagerPageControl)_gapWidthBox).Enabled = _closeHolesBox.Checked;
        }

        public void OnClose(int Reason)
        {
            _ok = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            if (!_ok)
                return;
            _keepFaces = _keepFacesBox.Checked;
            _closeHoles = _closeHolesBox.Checked;
            _gapWidth = _gapWidthBox.Value;
            _resolution = (int)Math.Round(_resolutionBox.Value);
        }

        public void AfterClose()
        {
            if (!_ok)
                return;

            var options = new ShrinkWrapOptions
            {
                Resolution = _resolution,
                GapClosingWidth = _closeHoles ? _gapWidth : 0,
            };
            string message;
            int icon;
            try
            {
                message = _service.Run(options, _keepFaces);
                icon = (int)swMessageBoxIcon_e.swMbInformation;
            }
            catch (Exception ex)
            {
                message = "Shrink wrap başarısız: " + ex.Message;
                icon = (int)swMessageBoxIcon_e.swMbStop;
            }
            _swApp.SendMsgToUser2(message, icon, (int)swMessageBoxBtn_e.swMbOk);
        }

        public void OnCheckboxCheck(int Id, bool Checked)
        {
            if (Id == CloseHolesId)
                UpdateEnabled();
        }

        // Unused PropertyManager callbacks.
        public void AfterActivation() { }
        public bool OnHelp() => false;
        public bool OnPreviousPage() => false;
        public bool OnNextPage() => false;
        public bool OnPreview() => false;
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int Id) => true;
        public void OnGroupExpand(int Id, bool Expanded) { }
        public void OnGroupCheck(int Id, bool Checked) { }
        public void OnOptionCheck(int Id) { }
        public void OnButtonPress(int Id) { }
        public void OnTextboxChanged(int Id, string Text) { }
        public void OnNumberboxChanged(int Id, double Value) { }
        public void OnNumberBoxTrackingCompleted(int Id, double Value) { }
        public void OnComboboxEditChanged(int Id, string Text) { }
        public void OnComboboxSelectionChanged(int Id, int Item) { }
        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public void OnSelectionboxFocusChanged(int Id) { }
        public void OnSelectionboxListChanged(int Id, int Count) { }
        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }
        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText) => true;
        public int OnActiveXControlCreated(int Id, bool Status) => 0;
        public int OnWindowFromHandleControlCreated(int Id, bool Status) => 0;
        public void OnSliderPositionChanged(int Id, double Value) { }
        public void OnSliderTrackingCompleted(int Id, double Value) { }
        public bool OnKeystroke(int Wparam, int Message, int Lparam, int Id) => false;
        public void OnPopupMenuItem(int Id) { }
        public void OnPopupMenuItemUpdate(int Id, ref int retval) { }
        public void OnGainedFocus(int Id) { }
        public void OnLostFocus(int Id) { }
    }
}
