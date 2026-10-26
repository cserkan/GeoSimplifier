using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GeoSimplifier.Core.Wrap;
using GeoSimplifier.SolidWorks.Geometry;
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
        private const int EmbossLabelId = 9;
        private const int EmbossHeightId = 10;
        private const int ExcludeGroupId = 7;
        private const int ExcludeId = 8;
        private const int OutputGroupId = 11;
        private const int NewPartId = 12;
        private const int NewConfigId = 13;
        private const int ConfigNameLabelId = 14;
        private const int ConfigNameId = 15;
        private const int PreviewId = 16;
        private const int SmallPartsLabelId = 18;
        private const int SmallPartsId = 19;
        private const int SingleBodyId = 22;
        private const int TypeGroupId = 23;
        private const int SolidOptionId = 24;
        private const int ShellOptionId = 25;
        private const int SmallListId = 26;

        // Selection mark of the exclusion box, so its items are told apart from other selections.
        private const int ExcludeMark = 2;

        // Remembered between runs within a SOLIDWORKS session.
        private static bool _closeHoles = true;
        private static double _gapWidth = 0.01; // meters
        private static double _embossHeight = 0; // meters; 0 keeps embossed details
        private static double _smallParts = 5;   // percent of the model's size (Serkan: 5 by default)
        private static bool _removeSmallParts = true;
        private static bool _newConfiguration;
        private static string _configurationName = "Simple";
        private static bool _surfaceShell;
        // The shell's last clean-up of small surface bodies uses the same percentage as 'Bundan küçük parçaları
        // çıkar' (Serkan merged the two options); none when that box is unticked.
        private static double SmallShellPercent => _removeSmallParts ? _smallParts : 0;
        private static bool _forceSingleBody;

        private readonly ISldWorks _swApp;
        private readonly ShrinkWrapService _service;
        private IPropertyManagerPage2 _page;

        // Every object the page hands out is kept: when the garbage collector releases one (here the group box and
        // the label were dropped), SOLIDWORKS crashes on the next call into the page.
        private IPropertyManagerPageGroup _group;
        private IPropertyManagerPageLabel _gapLabel;
        private IPropertyManagerPageCheckbox _closeHolesBox;
        private IPropertyManagerPageNumberbox _gapWidthBox;
        private IPropertyManagerPageLabel _embossLabel;
        private IPropertyManagerPageNumberbox _embossBox;
        private double _embossValue;
        private IPropertyManagerPageCheckbox _smallPartsCheck;
        private IPropertyManagerPageNumberbox _smallPartsBox;
        private double _smallPartsValue;
        private IPropertyManagerPageGroup _excludeGroup;
        private IPropertyManagerPageSelectionbox _excludeBox;
        private Exclusions _exclusions = new Exclusions();
        private IPropertyManagerPageGroup _outputGroup;
        private IPropertyManagerPageOption _newPartOption;
        private IPropertyManagerPageOption _newConfigOption;
        private IPropertyManagerPageLabel _configNameLabel;
        private IPropertyManagerPageTextbox _configNameBox;
        private IPropertyManagerPageCheckbox _previewBox;
        // Page 1 asks only for the kind of result; Next shows page 2 with the settings for that kind.
        private IPropertyManagerPageGroup _typeGroup;
        private IPropertyManagerPageOption _solidOption;
        private IPropertyManagerPageOption _shellOption;
        private bool _shellOutput;
        private int _step = 1;
        private IPropertyManagerPageCheckbox _singleBodyBox;
        private bool _ok;
        private IModelDoc2 _source;
        private WrapResult _preview;
        private string _previewKey;

        private TempBodyPreview _display;

        // Preview colours (COLORREF 0x00BBGGRR): a translucent blue envelope like NX's wrap preview, and the
        // material the wrap adds (plugs, bridges, filled cavities) in solid orange.
        private const int EnvelopeColor = 0xD77800;
        private const int AddedColor = 0x0080FF;
        private const int RemovedColor = 0x1A1AD9; // flattened embossed details, red

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
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_MultiplePages);
            _page = (IPropertyManagerPage2)_swApp.CreatePropertyManagerPage("Shrink Wrap", options, this, ref errors);
            if (_page == null)
                throw new InvalidOperationException("Could not create the PropertyManager page (error " + errors + ").");

            // Page 1: solid or outer skin. The PropertyManager has a single page, so Next and Back show and hide
            // the groups.
            _typeGroup = (IPropertyManagerPageGroup)_page.AddGroupBox(TypeGroupId, "Sonuç türü",
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));
            _solidOption = (IPropertyManagerPageOption)AddControl(_typeGroup, SolidOptionId, swPropertyManagerPageControlType_e.swControlType_Option,
                "Katı", "İçi dolu, delikleri kapalı katı gövde(ler). Ardından İleri ile ayarlara geçin.");
            _shellOption = (IPropertyManagerPageOption)AddControl(_typeGroup, ShellOptionId, swPropertyManagerPageControlType_e.swControlType_Option,
                "Yüzey kabuk (yalnız dış yüzler)",
                "Yalnız dışarıdan görünen yüzler, yüzey gövdesi olarak. En hafif sonuç; yüzeylerden oluşan (STEP) modeller için de. Ardından İleri ile ayarlara geçin.");
            _shellOutput = _surfaceShell;
            (_shellOutput ? _shellOption : _solidOption).Checked = true;

            var group = _group = (IPropertyManagerPageGroup)_page.AddGroupBox(GroupId, "Ayarlar",
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));

            // The preview is computed only while this is checked; it starts unchecked, so opening the command computes nothing.
            _previewBox = (IPropertyManagerPageCheckbox)AddControl(group, PreviewId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Önizleme göster", "İşaretliyken sarmanın önizlemesi hesaplanıp gösterilir ve ayar değişince güncellenir.");
            _previewBox.Checked = false;

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

            // Closed insides are always filled (Serkan: no option for it).

            _embossLabel = (IPropertyManagerPageLabel)AddControl(group, EmbossLabelId, swPropertyManagerPageControlType_e.swControlType_Label,
                "Yok sayılacak kabartma yüksekliği", null);
            _embossBox = (IPropertyManagerPageNumberbox)AddControl(group, EmbossHeightId, swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Yok sayılacak kabartma yüksekliği",
                "Düz yüzlerdeki bu yükseklikten alçak yazı, sembol ve kabartmalar düzlenir; sarma onları takip etmez. 0 hepsini korur.");
            _embossBox.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, 0, 1, true, 0.0005, 0.001, 0.0001);
            _embossBox.Value = _embossHeight;
            _embossValue = _embossHeight;

            _smallPartsCheck = (IPropertyManagerPageCheckbox)AddControl(group, SmallPartsLabelId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Bundan küçük parçaları çıkar (%)", "İşaretliyken aşağıdaki yüzdeden küçük parçalar en baştan sarmaya alınmaz.");
            _smallPartsCheck.Checked = _removeSmallParts;
            _smallPartsBox = (IPropertyManagerPageNumberbox)AddControl(group, SmallPartsId, swPropertyManagerPageControlType_e.swControlType_Numberbox,
                "Bundan küçük parçaları çıkar (%)",
                "Boyu modelin bu yüzdesinden küçük parçalar (vida, somun, pul, küçük bağlantı elemanları) en baştan, dışta olsalar bile sarmaya alınmaz; oturdukları delikler kapatılır. Modeli hafifletir ve hızlandırır. 0 hepsini korur.");
            _smallPartsBox.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, 0, 100, true, 1, 5, 0.5);
            _smallPartsBox.Value = _smallParts;
            _smallPartsValue = _smallParts;
            // The parts that will go, listed and highlighted (selected) as the percentage changes. A box of its own:
            // selections made while "Hariç tut" has the focus would land in that box.
            _smallListBox = (IPropertyManagerPageSelectionbox)AddControl(group, SmallListId, swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                "Çıkarılacak küçük parçalar", "Yüzde değişince, sarmaya alınmayacak küçük parçalar burada listelenir ve modelde vurgulanır.");
            _smallListBox.SingleEntityOnly = false;
            _smallListBox.AllowMultipleSelectOfSameEntity = false;
            _smallListBox.Height = 30;
            _smallListBox.Mark = HighlightMark;
            _smallListBox.SetSelectionFilters(new[] { (int)swSelectType_e.swSelCOMPONENTS });

            _singleBodyBox = (IPropertyManagerPageCheckbox)AddControl(group, SingleBodyId, swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Tek gövdeye zorla",
                "İşaretliyse bütün gövdeler birleştirilip tek gövde yapılmaya çalışılır (büyük montajlarda uzun sürer). İşaretsizse her gövde kendi delikleri ve iç dolgusuyla kalır, birbiriyle birleştirilmez: çok gövdeli ama çok daha hızlı.");
            _singleBodyBox.Checked = _forceSingleBody;

            _excludeGroup = (IPropertyManagerPageGroup)_page.AddGroupBox(ExcludeGroupId, "Hariç tut",
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));
            _excludeBox = (IPropertyManagerPageSelectionbox)AddControl(_excludeGroup, ExcludeId, swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                "Hariç tutulacak bileşen ve gövdeler",
                "Seçilen bileşen ve gövdeler sarılmaz, delikleri kapatılmaz; olduğu gibi, değdikleri yerden tek gövdeye eklenir.");
            _excludeBox.SingleEntityOnly = false;
            _excludeBox.AllowMultipleSelectOfSameEntity = false;
            _excludeBox.Height = 50;
            _excludeBox.Mark = ExcludeMark;
            _excludeBox.SetSelectionFilters(new[] { (int)swSelectType_e.swSelCOMPONENTS, (int)swSelectType_e.swSelSOLIDBODIES,
                                                    (int)swSelectType_e.swSelSURFACEBODIES });

            // Where OK puts the result: a new part, or a new configuration of this document.
            _outputGroup = (IPropertyManagerPageGroup)_page.AddGroupBox(OutputGroupId, "Sonuç",
                (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));
            _newPartOption = (IPropertyManagerPageOption)AddControl(_outputGroup, NewPartId, swPropertyManagerPageControlType_e.swControlType_Option,
                "Yeni parça", "Sarma yeni, kaydedilmemiş bir parça olarak açılır.");
            _newConfigOption = (IPropertyManagerPageOption)AddControl(_outputGroup, NewConfigId, swPropertyManagerPageControlType_e.swControlType_Option,
                "Konfigürasyon oluştur",
                "Montajda: sarma montajın yanına parça olarak kaydedilir, yeni konfigürasyonda diğer bileşenler bastırılıp sarma orijine sabitlenir. " +
                "Parçada: yeni konfigürasyonda parçanın özellikleri bastırılır ve sarma eklenir. Mevcut konfigürasyonlar değişmez.");
            _configNameLabel = (IPropertyManagerPageLabel)AddControl(_outputGroup, ConfigNameLabelId, swPropertyManagerPageControlType_e.swControlType_Label,
                "Konfigürasyon adı", null);
            _configNameBox = (IPropertyManagerPageTextbox)AddControl(_outputGroup, ConfigNameId, swPropertyManagerPageControlType_e.swControlType_Textbox,
                "Konfigürasyon adı", "Bu ad varsa sonuna numara eklenir.");
            _configNameBox.Text = _configurationName;
            (_newConfiguration ? _newConfigOption : _newPartOption).Checked = true;
            _outputNewConfig = _newConfiguration;
            _outputName = _configurationName;

            UpdateEnabled();
            _page.Show2(0);
            ShowStep();
        }

        // Highlighting the parts "Bundan küçük parçaları çıkar" will leave out, as soon as the percentage changes:
        // no wrap is computed, only the components' boxes (read once) are compared with the limit, and the small
        // ones are shown with SOLIDWORKS' own selection highlight in one call (drawing temporary bodies costs
        // about 25 ms a body).
        private const int HighlightMark = 4;
        private Timer _highlightTimer;
        private bool _highlighting;
        private IPropertyManagerPageSelectionbox _smallListBox;
        private System.Collections.Generic.List<(IComponent2 component, Core.Geometry.Aabb box)> _componentBoxes;
        private Core.Geometry.Aabb _modelBox;
        private System.Collections.Generic.List<IComponent2> _highlighted = new System.Collections.Generic.List<IComponent2>();

        private void ScheduleHighlight()
        {
            if (_highlightTimer == null)
            {
                _highlightTimer = new Timer { Interval = 250 };
                _highlightTimer.Tick += (s, e) =>
                {
                    _highlightTimer.Stop();
                    HighlightSmallParts();
                };
            }
            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        private void HighlightSmallParts()
        {
            if (!(_source is IAssemblyDoc assembly))
                return;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (_componentBoxes == null)
                {
                    _componentBoxes = new System.Collections.Generic.List<(IComponent2, Core.Geometry.Aabb)>();
                    _modelBox = Core.Geometry.Aabb.Empty;
                    foreach (IComponent2 comp in (object[])assembly.GetComponents(false) ?? new object[0])
                    {
                        if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed ||
                            comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden ||
                            ((object[])comp.GetChildren())?.Length > 0)
                            continue;
                        if (!(comp.GetBox(false, false) is double[] b) || b.Length < 6)
                            continue;
                        var box = new Core.Geometry.Aabb(new Core.Geometry.Vec3(b[0], b[1], b[2]), new Core.Geometry.Vec3(b[3], b[4], b[5]));
                        _componentBoxes.Add((comp, box));
                        _modelBox = _modelBox.Union(box);
                    }
                }
                double limit = _smallPartsCheck.Checked ? _smallPartsValue / 100 * (_modelBox.Max - _modelBox.Min).Length : 0;
                var small = _componentBoxes.Where(c => (c.box.Max - c.box.Min).Length < limit).Select(c => c.component).ToList();
                // The list box takes the selections; "Hariç tut" gets the focus back afterwards. Taking selections off
                // one by one is slow (DeSelect2 of 3684 took 16 minutes on V12): the whole selection is cleared and the
                // exclusion box's own items are selected again.
                _highlighting = true;
                try
                {
                    var manager = (ISelectionMgr)_source.SelectionManager;
                    var kept = Enumerable.Range(1, manager.GetSelectedObjectCount2(ExcludeMark))
                        .Select(i => manager.GetSelectedObject6(i, ExcludeMark)).Where(o => o != null).ToList();
                    _smallListBox.SetSelectionFocus();
                    _source.ClearSelection2(true);
                    _highlighted = new System.Collections.Generic.List<IComponent2>();
                    if (small.Count > 0)
                    {
                        var data = manager.CreateSelectData();
                        data.Mark = HighlightMark;
                        _source.Extension.MultiSelect2(small.Select(c => new DispatchWrapper(c)).ToArray(), true, data);
                        _highlighted = small;
                    }
                    _excludeBox.SetSelectionFocus();
                    if (kept.Count > 0)
                    {
                        var data = manager.CreateSelectData();
                        data.Mark = ExcludeMark;
                        _source.Extension.MultiSelect2(kept.Select(o => new DispatchWrapper(o)).ToArray(), true, data);
                    }
                }
                finally { _highlighting = false; }
                PageLog.Write($"Küçük parça vurgusu: {small.Count}/{_componentBoxes.Count} bileşen ({watch.Elapsed.TotalSeconds:F2} sn)");
            }
            catch (Exception ex)
            {
                PageLog.Write("Küçük parça vurgusu hatası: " + ex.Message);
            }
        }

        private void ClearHighlight()
        {
            if (_highlighted.Count == 0)
                return;
            // On closing: the whole selection goes (the page and its boxes go with it).
            bool was = _highlighting;
            _highlighting = true;
            try { _source.ClearSelection2(true); }
            finally { _highlighting = was; }
            _highlighted = new System.Collections.Generic.List<IComponent2>();
        }

        /// <summary>Shows page 1 (the kind of result) or page 2 (its settings).</summary>
        private void ShowStep()
        {
            bool settings = _step == 2;
            _typeGroup.Visible = !settings;
            _group.Visible = settings;
            _excludeGroup.Visible = settings;
            _outputGroup.Visible = settings;
            _group.Caption = _shellOutput ? "Ayarlar: yüzey kabuk" : "Ayarlar: katı";
        }

        private static object AddControl(IPropertyManagerPageGroup group, int id, swPropertyManagerPageControlType_e type, string caption, string tip)
        {
            return group.AddControl2(id, (short)type, caption, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge,
                (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled), tip ?? "");
        }

        private bool _outputNewConfig;
        private string _outputName;

        private void UpdateEnabled()
        {
            ((IPropertyManagerPageControl)_gapWidthBox).Enabled = _closeHolesBox.Checked;
            if (_configNameBox != null)
                ((IPropertyManagerPageControl)_configNameBox).Enabled = _outputNewConfig;
            if (_smallPartsBox != null)
                ((IPropertyManagerPageControl)_smallPartsBox).Enabled = _smallPartsCheck.Checked;
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
                _debounce.Tick += (s, e) =>
                {
                    _debounce.Stop();
                    if (_previewBox.Checked)
                        UpdatePreview();
                    else
                    {
                        HidePreview();
                        _previewKey = null;
                    }
                };
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
            double emboss = _embossBox.Value;
            if (emboss >= 0)
                _embossValue = emboss;
            double small = _smallPartsBox.Value;
            if (small >= 0)
                _smallPartsValue = small;
        }

        private ShrinkWrapOptions CurrentOptions() => new ShrinkWrapOptions
        {
            GapWidth = _closeHolesBox.Checked ? _gapValue : 0,
            FillCavities = true,
            MaxEmbossHeight = _embossValue,
            MinPartPercent = _smallPartsCheck.Checked ? _smallPartsValue : 0,
            ForceSingleBody = _singleBodyBox.Checked,
            SurfaceShell = _shellOutput,
        };

        private string Key(ShrinkWrapOptions o) => $"{o.GapWidth:R}|{o.MaxEmbossHeight:R}|{o.MinPartPercent:R}|{o.SurfaceShell}|{_exclusions.Key}";

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
                var watch = System.Diagnostics.Stopwatch.StartNew();
                options.Preview = true;
                _preview = _service.Compute(_source, options, _exclusions);
                double computed = watch.Elapsed.TotalSeconds;
                // The computation leaves many temporary bodies behind; free their COM objects now, on SOLIDWORKS'
                // thread and before anything is drawn, rather than whenever the finalizer gets to them.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                double collected = watch.Elapsed.TotalSeconds;
                _display = _display ?? new TempBodyPreview(_source);
                bool shown = true;
                // Thousands of lids are drawn one by one; the view is redrawn once at the end instead of after each.
                var view = _source.ActiveView as IModelView;
                if (view != null) view.EnableGraphicsUpdate = false;
                // The quick preview's bodies are made for it alone (OK computes again), so they are moved, not copied.
                foreach (var body in _preview.Envelope)
                    shown &= _display.Show(body, EnvelopeColor, new[] { 0.0, 0.47, 0.84 }, transparency: 0.65, inPlace: _preview.IsPreview);
                foreach (var body in _preview.Added)
                    shown &= _display.Show(body, AddedColor, new[] { 1.0, 0.5, 0.0 }, transparency: 0, inPlace: _preview.IsPreview);
                foreach (var body in _preview.Removed)
                    shown &= _display.Show(body, RemovedColor, new[] { 0.85, 0.1, 0.1 }, transparency: 0);
                if (view != null) view.EnableGraphicsUpdate = true;
                double drawnEnvelope = watch.Elapsed.TotalSeconds;
                _source.GraphicsRedraw2();
                PageLog.Write($"  süre: hesap {computed:F2}, bellek {collected - computed:F2}, çizim {drawnEnvelope - collected:F2}, yenileme {watch.Elapsed.TotalSeconds - drawnEnvelope:F2} sn");
                string text = _preview.Summary();
                if (!_display.Supported)
                    text += " Bu belgede önizleme çizilemiyor; sonuç Tamam ile yeni parça olarak oluşturulur.";
                else if (!shown)
                    text += " (Önizleme bu belgede çizilemedi.)";
                else if (_preview.IsSkin)
                    text += "";
                else if (_preview.EnvelopeLeftOut)
                    text += " Büyük model: önizlemede zarf çizilmiyor, eklenecek malzeme turuncu gösteriliyor.";
                else
                    text += _preview.Added.Count == 0
                        ? " Zarf mavi gösteriliyor, eklenen malzeme yok."
                        : " Zarf mavi, eklenecek malzeme turuncu gösteriliyor.";
                if (_display.Supported && _preview.Removed.Count > 0)
                    text += _preview.IsSkin ? " Çıkarılacak küçük parçalar kırmızı." : " Düzlenen kabartmalar kırmızı.";
                // The page shows no summary (users found it too long); it and the hole details go to the log.
                HideMessage();
                PageLog.Write($"Önizleme (çizildi {shown}, hesap {computed:F2} sn, toplam {watch.Elapsed.TotalSeconds:F2} sn): " + text);
                foreach (var line in _preview.Log)
                    PageLog.Write("  " + line);
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

        private void HideMessage()
        {
            _page?.SetMessage3("", (int)swPropertyManagerPageMessageVisibility.swNoMessageBox,
                (int)swPropertyManagerPageMessageExpanded.swMessageBoxMaintainExpandState, "");
        }

        private void SetMessage(string text)
        {
            _page?.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, "Önizleme");
        }

        public void AfterActivation() { }

        /// <summary>
        /// Test hook: types the given limits (meters) into the number box one after another, the way SOLIDWORKS
        /// reports typing (OnNumberboxChanged + OnNumberBoxTrackingCompleted), waits for each preview, then
        /// closes the page with OK or Cancel. Runs from a timer so everything happens in SOLIDWORKS' message loop.
        /// </summary>
        public void Simulate(double[] limits, bool ok, bool forceGc = true)
        {
            int step = 0;
            var timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) =>
            {
                if (step < limits.Length)
                {
                    // A negative limit is typed without turning the preview on (for timing OK alone).
                    double v = Math.Abs(limits[step]);
                    bool preview = limits[step++] > 0;
                    PageLog.Write($"Simülasyon: {v * 1000:F0} mm");
                    // Forces the finalizer to run now, so a crash caused by releasing objects shows up quickly.
                    if (forceGc)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        PageLog.Write("Simülasyon: çöp toplandı");
                    }
                    _gapWidthBox.Value = v;
                    PageLog.Write("Simülasyon: değer yazıldı");
                    OnNumberboxChanged(GapWidthId, v);
                    OnNumberBoxTrackingCompleted(GapWidthId, v);
                    if (preview && !_previewBox.Checked)
                    {
                        _previewBox.Checked = true;
                        OnCheckboxCheck(PreviewId, true);
                    }
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

        /// <summary>Test hook: picks one large component into "Hariç tut", as a click with that box active would.</summary>
        public void ExcludeSelectForTest()
        {
            var pick = _componentBoxes?.OrderByDescending(c => (c.box.Max - c.box.Min).Length).Select(c => c.component).FirstOrDefault();
            if (pick == null) return;
            _excludeBox.SetSelectionFocus();
            var data = ((ISelectionMgr)_source.SelectionManager).CreateSelectData();
            data.Mark = ExcludeMark;
            pick.Select4(true, data, false);
            var manager = (ISelectionMgr)_source.SelectionManager;
            PageLog.Write($"Simülasyon: hariç tutmaya seçildi {pick.Name2}; işaret 4: {manager.GetSelectedObjectCount2(HighlightMark)}, " +
                          $"işaret 2: {manager.GetSelectedObjectCount2(ExcludeMark)}, hariç tutulanlar '{_exclusions.Key}'");
        }

        /// <summary>Test hook: unticks "Bundan küçük parçaları çıkar".</summary>
        public void UntickSmallForTest()
        {
            _smallPartsCheck.Checked = false;
            HighlightSmallParts();
            var manager = (ISelectionMgr)_source.SelectionManager;
            PageLog.Write($"Simülasyon: küçük parça kutusu işaretsiz; işaret 4: {manager.GetSelectedObjectCount2(HighlightMark)}, hariç tutulanlar '{_exclusions.Key}'");
        }

        /// <summary>Test hook: unticks "Delikleri ve boşlukları kapat".</summary>
        public void NoHolesForTest()
        {
            _closeHolesBox.Checked = false;
            UpdateEnabled();
            PageLog.Write("Simülasyon: delik kapatma kapalı");
        }

        /// <summary>Test hook: closes the page with Cancel.</summary>
        public void CloseForTest()
        {
            _page.Close(false);
            PageLog.Write($"Simülasyon: sayfa kapandı; seçili {((ISelectionMgr)_source.SelectionManager).GetSelectedObjectCount2(-1)}");
        }

        /// <summary>Test hook: excludes components by name (Name2) as if they were picked in the selection box.</summary>
        public void ExcludeForTest(string[] componentNames)
        {
            _exclusions = new Exclusions();
            foreach (var name in componentNames)
                _exclusions.AddComponentName(name);
            PageLog.Write("Simülasyon: hariç tutulanlar " + _exclusions.Key);
        }

        public void OnNumberboxChanged(int Id, double Value)
        {
            PageLog.Write($"OnNumberboxChanged {Id}: {Value:R} (kutu {_gapWidthBox.Value:R})");
            if (!SetNumber(Id, Value))
                return;
            SchedulePreview();
        }

        public void OnNumberBoxTrackingCompleted(int Id, double Value)
        {
            PageLog.Write($"OnNumberBoxTrackingCompleted {Id}: {Value:R}");
            if (!SetNumber(Id, Value))
                return;
            SchedulePreview(50);
        }

        /// <returns>False for a control that is not one of the number boxes.</returns>
        private bool SetNumber(int id, double value)
        {
            if (id == GapWidthId)
                _gapValue = value;
            else if (id == EmbossHeightId)
                _embossValue = value;
            else if (id == SmallPartsId)
            {
                _smallPartsValue = value;
                ScheduleHighlight();
            }
            else
                return false;
            return true;
        }

        public void OnLostFocus(int Id)
        {
            if (Id != GapWidthId && Id != EmbossHeightId && Id != SmallPartsId)
                return;
            SyncGapFromBox("OnLostFocus");
            SchedulePreview(50);
        }

        public void OnClose(int Reason)
        {
            _debounce?.Stop();
            _highlightTimer?.Stop();
            ClearHighlight();
            SyncGapFromBox("OnClose");
            _ok = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            if (!_ok)
                return;
            _closeHoles = _closeHolesBox.Checked;
            _newConfiguration = _outputNewConfig;
            _surfaceShell = _shellOutput;
            _configurationName = string.IsNullOrWhiteSpace(_outputName) ? "Simple" : _outputName.Trim();
            _gapWidth = _gapValue;
            _embossHeight = _embossValue;
            _smallParts = _smallPartsValue;
            _removeSmallParts = _smallPartsCheck.Checked;
            _forceSingleBody = _singleBodyBox.Checked;
            // The preview is only a quick, rough result; OK computes the real one.
            _preview = null;
        }

        public void AfterClose()
        {
            _debounce?.Dispose();
            _debounce = null;
            HidePreview();
            if (!_ok)
                return;

            try
            {
                var result = _preview ?? _service.Compute(_source, CurrentOptionsFromSettings(), _exclusions);
                string message = _outputNewConfig
                    ? _service.Commit(result, _source, string.IsNullOrWhiteSpace(_outputName) ? "Simple" : _outputName.Trim(), _surfaceShell, SmallShellPercent)
                    : _service.Commit(result, null, null, _surfaceShell, SmallShellPercent);
                // No message box on success: it only gets in the way. The summary goes to the log.
                PageLog.Write("Tamam: " + message);
            }
            catch (Exception ex)
            {
                PageLog.Write("Tamam hatası: " + ex);
                _swApp.SendMsgToUser2("Shrink wrap başarısız: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        private static ShrinkWrapOptions CurrentOptionsFromSettings() => new ShrinkWrapOptions
        {
            GapWidth = _closeHoles ? _gapWidth : 0,
            FillCavities = true,
            MaxEmbossHeight = _embossHeight,
            MinPartPercent = _removeSmallParts ? _smallParts : 0,
            ForceSingleBody = _forceSingleBody,
            SurfaceShell = _surfaceShell,
        };

        public void OnCheckboxCheck(int Id, bool Checked)
        {
            if (Id == CloseHolesId || Id == SmallPartsLabelId)
                UpdateEnabled();
            if (Id == SmallPartsLabelId)
                ScheduleHighlight();
            SchedulePreview(50);
        }

        // Unused PropertyManager callbacks.
        public bool OnHelp() => false;
        public bool OnPreviousPage()
        {
            _step = 1;
            ShowStep();
            return false;
        }

        public bool OnNextPage()
        {
            _step = 2;
            ShowStep();
            return false;
        }
        public bool OnPreview() => false;
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int Id) => true;
        public void OnGroupExpand(int Id, bool Expanded) { }
        public void OnGroupCheck(int Id, bool Checked) { }
        public void OnOptionCheck(int Id)
        {
            if (Id == SolidOptionId || Id == ShellOptionId)
            {
                _shellOutput = Id == ShellOptionId;
                ShowStep();
                SchedulePreview(50);
                return;
            }
            if (Id != NewPartId && Id != NewConfigId)
                return;
            _outputNewConfig = Id == NewConfigId;
            UpdateEnabled();
        }
        public void OnButtonPress(int Id) { }
        public void OnTextboxChanged(int Id, string Text)
        {
            PageLog.Write($"OnTextboxChanged {Id}: {Text}");
            if (Id == ConfigNameId)
                _outputName = Text;
        }

        /// <summary>Test hook: chooses "Konfigürasyon oluştur" with the given name (null keeps "Yeni parça").</summary>
        public void OutputForTest(string configurationName)
        {
            if (configurationName == null)
                return;
            _newConfigOption.Checked = true;
            _configNameBox.Text = configurationName;
            OnOptionCheck(NewConfigId);
            _outputName = configurationName;
        }
        /// <summary>Test hook: ticks "Yüzey gövde (dış kabuk)".</summary>
        public void ShellForTest() => KindForTest(true);

        /// <summary>Test hook: types a percentage into "Bundan küçük parçaları çıkar" and highlights at once.</summary>
        public void HighlightForTest(double percent)
        {
            _smallPartsBox.Value = percent;
            _smallPartsValue = percent;
            _smallPartsCheck.Checked = true;
            HighlightSmallParts();
            var manager = (ISelectionMgr)_source.SelectionManager;
            PageLog.Write($"Simülasyon: %{percent} vurgusu, işaret 4: {manager.GetSelectedObjectCount2(HighlightMark)}, işaret 2: {manager.GetSelectedObjectCount2(ExcludeMark)}, " +
                          $"hepsi: {manager.GetSelectedObjectCount2(-1)}, kutuya sunulan {_submitted}, hariç tutulanlar '{Exclusions.FromSelection(_source, ExcludeMark).Key}'");
        }

        /// <summary>Test hook: chooses "Yüzey kabuk" or "Katı" on page 1.</summary>
        public void KindForTest(bool shell)
        {
            (shell ? _shellOption : _solidOption).Checked = true;
            OnOptionCheck(shell ? ShellOptionId : SolidOptionId);
            PageLog.Write($"Simülasyon: tür {(shell ? "yüzey kabuk" : "katı")}, sayfa {_step}");
        }

        /// <summary>Test hook: Next or Back, as the user would click them.</summary>
        public void StepForTest(bool next)
        {
            if (next) OnNextPage();
            else OnPreviousPage();
            PageLog.Write($"Simülasyon: {(next ? "İleri" : "Geri")} → sayfa {_step} (tür grubu {(_typeGroup.Visible ? "görünür" : "gizli")}, ayarlar {(_group.Visible ? "görünür" : "gizli")})");
        }

        /// <summary>Test hook: ticks "Tek gövdeye zorla".</summary>
        public void SingleBodyForTest() => _singleBodyBox.Checked = true;

        /// <summary>Test hook: sets "Bundan küçük parçaları çıkar (%)".</summary>
        public void SmallPartsForTest(double size)
        {
            _smallPartsBox.Value = size;
            _smallPartsValue = size;
            _smallPartsCheck.Checked = size > 0;
        }

        public void EmbossForTest(double height)
        {
            _embossBox.Value = height;
            _embossValue = height;
        }

        public void OnComboboxEditChanged(int Id, string Text) { }
        public void OnComboboxSelectionChanged(int Id, int Item) { }
        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public void OnSelectionboxFocusChanged(int Id) { }
        public void OnSelectionboxListChanged(int Id, int Count)
        {
            if (Id != ExcludeId || _highlighting)
                return;
            _exclusions = Exclusions.FromSelection(_source, ExcludeMark);
            PageLog.Write($"Hariç tutulanlar: {Count} seçim, {_exclusions.Key}");
            SchedulePreview(50);
        }
        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }
        // The highlight of small parts is made of selections; the exclusion box must not take them.
        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText)
        {
            if (_highlighting && Id == ExcludeId) _submitted++;
            return true;
        }

        private int _submitted;
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
