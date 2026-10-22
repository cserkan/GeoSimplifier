using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GeoSimplifier.Core.Wrap;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>
    /// The Shrink Wrap PropertyManager page (left panel, green check / red cross), like a native feature.
    /// While the page is open the material the wrap adds is shown on the model as a coloured preview and the
    /// page shows the result summary; OK puts the result into a new part.
    /// </summary>
    [ComVisible(true)]
    public sealed class ShrinkWrapPage : IPropertyManagerPage2Handler9
    {
        private const int GroupId = 1;
        private const int CloseHolesId = 3;
        private const int GapLabelId = 4;
        private const int GapWidthId = 5;
        private const int FillCavitiesId = 6;

        // Remembered between runs within a SOLIDWORKS session.
        private static bool _closeHoles = true;
        private static double _gapWidth = 0.01; // meters
        private static bool _fillCavities = true;

        private readonly ISldWorks _swApp;
        private readonly ShrinkWrapService _service;
        private IPropertyManagerPage2 _page;

        // Every object the page hands out is kept: when the garbage collector releases one (here the group box and
        // the label were dropped), SOLIDWORKS crashes on the next call into the page.
        private IPropertyManagerPageGroup _group;
        private IPropertyManagerPageLabel _gapLabel;
        private IPropertyManagerPageCheckbox _closeHolesBox;
        private IPropertyManagerPageNumberbox _gapWidthBox;
        private IPropertyManagerPageCheckbox _fillCavitiesBox;
        private bool _ok;
        private IModelDoc2 _source;
        private WrapResult _preview;
        private string _previewKey;

        private TempBodyPreview _display;

        // Preview colours (COLORREF 0x00BBGGRR): a translucent blue envelope like NX's wrap preview, and the
        // material the wrap adds (plugs, bridges, filled cavities) in solid orange.
        private const int EnvelopeColor = 0xD77800;
        private const int AddedColor = 0x0080FF;

        public ShrinkWrapPage(ISldWorks swApp, ShrinkWrapService service)
        {
            _swApp = swApp;
            _service = service;
        }

        public void Show()
        {
            int errors = 0;
            _source = _swApp.IActiveDoc2;
            int options = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton);
            _page = (IPropertyManagerPage2)_swApp.CreatePropertyManagerPage("Shrink Wrap", options, this, ref errors);
            if (_page == null)
                throw new InvalidOperationException("Could not create the PropertyManager page (error " + errors + ").");

            var group = _group = (IPropertyManagerPageGroup)_page.AddGroupBox(GroupId, "Ayarlar",
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));

            _closeHolesBox = (IPropertyManagerPageCheckbox)AddControl(group, CloseHolesId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Delikleri ve boşlukları kapat",
                "Ağzı aşağıdaki genişlikten dar delikler, kanallar ve cepler yüzeyle aynı hizada kapatılır; daha yakın parçalar birleştirilir.");
            _closeHolesBox.Checked = _closeHoles;

            _gapLabel = (IPropertyManagerPageLabel)AddControl(group, GapLabelId, swPropertyManagerPageControlType_e.swControlType_Label,
                "Kapatılacak en büyük açıklık", null);
            _gapWidthBox = (IPropertyManagerPageNumberbox)AddControl(group, GapWidthId, swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Kapatılacak en büyük açıklık",
                "Sarma bu genişlikten dar açıklıkların içine girmez. Daha geniş açıklıklar olduğu gibi kalır.");
            _gapWidthBox.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, 0.0001, 10, true, 0.001, 0.01, 0.0001);
            _gapWidthBox.Value = _gapWidth;
            _gapValue = _gapWidth;

            _fillCavitiesBox = (IPropertyManagerPageCheckbox)AddControl(group, FillCavitiesId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "İç boşlukları doldur", "Dışarıdan görünmeyen kapalı iç boşluklar dolu hale getirilir.");
            _fillCavitiesBox.Checked = _fillCavities;

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

        // The number box's Value still holds the old number while OnNumberboxChanged runs, so the new one is kept here.
        private double _gapValue;

        // OnNumberboxChanged fires on every keystroke; computing the wrap there blocks typing, so the preview
        // waits until the user pauses. Every update also runs from this timer rather than inside the
        // PropertyManager callback: hiding the previous preview of an assembly (owned by a component) inside
        // the callback crashes SOLIDWORKS.
        private Timer _debounce;

        private void SchedulePreview(int delay = 600)
        {
            if (_debounce == null)
            {
                _debounce = new Timer();
                _debounce.Tick += (s, e) => { _debounce.Stop(); UpdatePreview(); };
            }
            _debounce.Stop();
            _debounce.Interval = delay;
            _debounce.Start();
        }

        /// <summary>Takes the number box's own value when it differs from the one last reported to us.</summary>
        private void SyncGapFromBox(string when)
        {
            double boxValue = _gapWidthBox.Value;
            PageLog.Write($"{when}: kutu {boxValue:R}, son değer {_gapValue:R}");
            if (boxValue > 0)
                _gapValue = boxValue;
        }

        private ShrinkWrapOptions CurrentOptions() => new ShrinkWrapOptions
        {
            GapWidth = _closeHolesBox.Checked ? _gapValue : 0,
            FillCavities = _fillCavitiesBox.Checked,
        };

        private static string Key(ShrinkWrapOptions o) => $"{o.GapWidth:R}|{o.FillCavities}";

        /// <summary>Recomputes the wrap for the current settings and shows what it adds.</summary>
        private void UpdatePreview()
        {
            var options = CurrentOptions();
            string key = Key(options);
            if (key == _previewKey)
                return;
            HidePreview();
            _previewKey = key;
            PageLog.Write($"Önizleme hesaplanıyor: sınır {options.GapWidth * 1000:F2} mm");
            try
            {
                _preview = _service.Compute(_source, options);
                _display = _display ?? new TempBodyPreview(_source);
                bool shown = true;
                foreach (var body in _preview.Envelope)
                    shown &= _display.Show(body, EnvelopeColor, new[] { 0.0, 0.47, 0.84 }, transparency: 0.65);
                foreach (var body in _preview.Added)
                    shown &= _display.Show(body, AddedColor, new[] { 1.0, 0.5, 0.0 }, transparency: 0);
                _source.GraphicsRedraw2();
                string text = _preview.Summary();
                if (!_display.Supported)
                    text += " Montajlarda önizleme çizilmiyor; sonuç Tamam ile yeni parça olarak oluşturulur.";
                else if (!shown)
                    text += " (Önizleme bu belgede çizilemedi.)";
                else
                    text += _preview.Added.Count == 0
                        ? " Zarf mavi gösteriliyor, eklenen malzeme yok."
                        : " Zarf mavi, eklenecek malzeme turuncu gösteriliyor.";
                if (_preview.Log.Count > 0)
                    text += "\n\nAyrıntı: " + string.Join(" ", _preview.Log);
                SetMessage(text);
                PageLog.Write($"Önizleme (çizildi {shown}): " + _preview.Summary());
            }
            catch (Exception ex)
            {
                PageLog.Write("Önizleme hatası: " + ex);
                _preview = null;
                SetMessage("Önizleme hesaplanamadı: " + ex.Message);
            }
        }

        private void HidePreview()
        {
            if (_display == null)
                return;
            PageLog.Write("Önizleme gizleniyor");
            _display.Clear();
            PageLog.Write("Önizleme gizlendi");
        }

        private void SetMessage(string text)
        {
            _page?.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, "Önizleme");
        }

        public void AfterActivation() => UpdatePreview();

        /// <summary>
        /// Test hook: types the given limits (meters) into the number box one after another, the way SOLIDWORKS
        /// reports typing (OnNumberboxChanged + OnNumberBoxTrackingCompleted), waits for each preview, then
        /// closes the page with OK or Cancel. Runs from a timer so everything happens in SOLIDWORKS' message loop.
        /// </summary>
        public void Simulate(double[] limits, bool ok)
        {
            int step = 0;
            var timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) =>
            {
                if (step < limits.Length)
                {
                    double v = limits[step++];
                    // Forces the finalizer to run now, so a crash caused by releasing objects shows up quickly.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    PageLog.Write($"Simülasyon: {v * 1000:F0} mm");
                    _gapWidthBox.Value = v;
                    OnNumberboxChanged(GapWidthId, v);
                    OnNumberBoxTrackingCompleted(GapWidthId, v);
                    return;
                }
                timer.Stop();
                timer.Dispose();
                PageLog.Write("Simülasyon: sayfa kapatılıyor (" + (ok ? "Tamam" : "İptal") + ")");
                _page.Close(ok);
                PageLog.Write("Simülasyon: bitti");
            };
            timer.Start();
        }

        public void OnNumberboxChanged(int Id, double Value)
        {
            PageLog.Write($"OnNumberboxChanged {Id}: {Value:R} (kutu {_gapWidthBox.Value:R})");
            if (Id != GapWidthId)
                return;
            _gapValue = Value;
            SchedulePreview();
        }

        public void OnNumberBoxTrackingCompleted(int Id, double Value)
        {
            PageLog.Write($"OnNumberBoxTrackingCompleted {Id}: {Value:R}");
            if (Id != GapWidthId)
                return;
            _gapValue = Value;
            SchedulePreview(50);
        }

        public void OnLostFocus(int Id)
        {
            if (Id != GapWidthId)
                return;
            SyncGapFromBox("OnLostFocus");
            SchedulePreview(50);
        }

        public void OnClose(int Reason)
        {
            _debounce?.Stop();
            SyncGapFromBox("OnClose");
            _ok = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            if (!_ok)
                return;
            _closeHoles = _closeHolesBox.Checked;
            _gapWidth = _gapValue;
            _fillCavities = _fillCavitiesBox.Checked;
            // The preview is the result unless it is stale or failed.
            if (Key(CurrentOptions()) != _previewKey)
                _preview = null;
        }

        public void AfterClose()
        {
            _debounce?.Dispose();
            _debounce = null;
            HidePreview();
            if (!_ok)
                return;

            string message;
            int icon;
            try
            {
                var result = _preview ?? _service.Compute(_source, CurrentOptionsFromSettings());
                message = _service.Commit(result);
                PageLog.Write("Tamam: " + message);
                icon = (int)swMessageBoxIcon_e.swMbInformation;
            }
            catch (Exception ex)
            {
                message = "Shrink wrap başarısız: " + ex.Message;
                PageLog.Write("Tamam hatası: " + ex);
                icon = (int)swMessageBoxIcon_e.swMbStop;
            }
            _swApp.SendMsgToUser2(message, icon, (int)swMessageBoxBtn_e.swMbOk);
        }

        private static ShrinkWrapOptions CurrentOptionsFromSettings() => new ShrinkWrapOptions
        {
            GapWidth = _closeHoles ? _gapWidth : 0,
            FillCavities = _fillCavities,
        };

        public void OnCheckboxCheck(int Id, bool Checked)
        {
            if (Id == CloseHolesId)
                UpdateEnabled();
            SchedulePreview(50);
        }

        // Unused PropertyManager callbacks.
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
        public void OnTextboxChanged(int Id, string Text) => PageLog.Write($"OnTextboxChanged {Id}: {Text}");
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
    }
}
