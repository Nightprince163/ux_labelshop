using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;
using UXLabelshop.Services;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class TemplateEditorWindow : Window
{
    private readonly long _templateId;
    private readonly TemplateService _templates;
    private readonly ScanRuleService _rules;
    private readonly LabelRenderer _renderer = new();
    private TemplateDoc _doc;
    private double _scale;

    /// <summary>当前选中的元素（有序）。单选时集合里只有一个。</summary>
    private readonly List<TemplateElement> _selection = new();
    /// <summary>主选中元素 = 集合第一个；拖拽基准、自检钩子用它。空选时为 null。</summary>
    private TemplateElement? _selected;
    private readonly Dictionary<TemplateElement, Border> _boxes = new();

    private bool _dragging, _resizing, _rubberBanding;
    private Point _dragStart, _rubberStart;
    private double _origX, _origY, _origW, _origH, _origX2, _origY2;
    private readonly Dictionary<TemplateElement, (double X, double Y, double? X2, double? Y2)> _dragOrigins = new();

    private record VarRow(string Key, string Display);

    /// <summary>撤销/重做：整份模板 JSON 快照（元素少、快照小，比逐操作反向差分可靠）。</summary>
    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private const int UndoLimit = 60;

    /// <summary>拖动/缩放开始前的快照，松手时若真变了才入栈，避免点一下就产生一条空撤销。</summary>
    private string? _dragSnapshot;

    /// <summary>最近一次保存时的界面状态；关闭时与当前状态比较，判断是否提示「未保存」。</summary>
    private string _savedState = "";

    public TemplateEditorWindow(long templateId)
    {
        InitializeComponent();
        _templateId = templateId;
        _templates = App.Services.GetRequiredService<TemplateService>();
        _rules = App.Services.GetRequiredService<ScanRuleService>();
        _doc = _templates.LoadDoc(templateId);
        NameBox.Text = _doc.Name ?? "";

        // 导入的二维码只有 size_mm、没有 w/h，补上后编辑器才能正确画选择框
        NormalizeQrSize();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        UpdateUndoButtons();
        _savedState = CurrentState();

        BuildCanvas();
    }

    /// <summary>把模板内容和名称一起算成「已保存基线」；撤回到原状时也应视为没有未保存改动。</summary>
    private string CurrentState() => _doc.ToJson() + "\u0001" + NameBox.Text.Trim();

    /// <summary>自检用：当前是否有未保存改动（关闭确认框的判断依据）。</summary>
    internal bool HasUnsavedChangesForTest =>
        !string.Equals(CurrentState(), _savedState, StringComparison.Ordinal);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsSelfTest) return;
        if (!HasUnsavedChangesForTest) return;

        var answer = MessageBox.Show(this,
            "当前模板有未保存的修改，关闭后会丢失。\n确定要关闭吗？",
            "未保存的修改", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }

    private void NormalizeQrSize()
    {
        foreach (var el in _doc.Elements)
        {
            if (el.Type != "qrcode") continue;
            var size = el.SizeMm ?? (el.W > 0 ? el.W : 15);
            el.SizeMm = size;
            if (el.W <= 0) el.W = size;
            if (el.H <= 0) el.H = size;
        }
    }

    private void BuildCanvas()
    {
        _scale = _doc.Dpi / 25.4;
        var w = _doc.WidthMm * _scale;
        var h = _doc.HeightMm * _scale;

        EditorCanvas.Width = w;
        EditorCanvas.Height = h;
        PreviewImage.Width = w;
        PreviewImage.Height = h;
        OverlayLayer.Width = w;
        OverlayLayer.Height = h;

        LoadVarList();
        RebuildOverlays();
        RenderPreview();
        StatusText.Text = $"{_doc.WidthMm}×{_doc.HeightMm}mm · {_doc.Dpi}dpi · {_doc.Elements.Count} 个元素";
    }

    private void LoadVarList()
    {
        VarList.ItemsSource = _doc.Variables
            .Where(v => !string.IsNullOrEmpty(v.Key))
            .Select(v => new VarRow(v.Key, string.IsNullOrEmpty(v.Label) ? v.Key : $"{v.Label} ({v.Key})"))
            .ToList();
    }

    // ---------- 渲染 ----------

    private Dictionary<string, string?> SampleValues()
    {
        var values = _doc.Variables.Where(v => !string.IsNullOrEmpty(v.Key))
            .ToDictionary(v => v.Key, v => (string?)(string.IsNullOrEmpty(v.Sample) ? v.Key : v.Sample));
        // 按公式算出计算列，让编辑器预览能看到真实的派生结果
        new VariableResolver(_doc.Variables).Recompute(values);
        return values;
    }

    private void RenderPreview()
    {
        try
        {
            using var bmp = _renderer.RenderGray(_doc, SampleValues());
            PreviewImage.Source = ImageUtil.ToBitmapSource(bmp);
        }
        catch (Exception ex)
        {
            StatusText.Text = "渲染失败：" + ex.Message;
        }
    }

    // ---------- 画布元素 ----------

    private static Rect BoundsOf(TemplateElement el, double s)
    {
        if (el.Type == "line")
        {
            var x1 = Math.Min(el.X, el.X2 ?? el.X) * s;
            var y1 = Math.Min(el.Y, el.Y2 ?? el.Y) * s;
            var x2 = Math.Max(el.X, el.X2 ?? el.X) * s;
            var y2 = Math.Max(el.Y, el.Y2 ?? el.Y) * s;
            return new Rect(x1, y1, Math.Max(6, x2 - x1), Math.Max(6, y2 - y1));
        }
        // 二维码按边长（size_mm）画框：导入的模板没有 w/h，只按 w×h 会缩成 6×6 点，点不中
        if (el.Type == "qrcode")
        {
            var size = el.SizeMm ?? (el.W > 0 ? el.W : 15);
            return new Rect(el.X * s, el.Y * s, Math.Max(10, size * s), Math.Max(10, size * s));
        }
        return new Rect(el.X * s, el.Y * s, Math.Max(6, el.W * s), Math.Max(6, el.H * s));
    }

    private void RebuildOverlays()
    {
        OverlayLayer.Children.Clear();
        _boxes.Clear();

        foreach (var el in _doc.Elements)
        {
            var rect = BoundsOf(el, _scale);
            var box = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Tag = el,
            };
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            box.Width = rect.Width;
            box.Height = rect.Height;

            var grid = new Grid();
            var thumb = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Tag = el,
                Visibility = Visibility.Collapsed,
            };
            grid.Children.Add(thumb);
            box.Child = grid;
            box.Tag = el;
            box.ToolTip = $"{el.Type} {(string.IsNullOrEmpty(el.Text) ? "" : el.Text)}";

            box.MouseLeftButtonDown += Box_MouseDown;
            box.MouseMove += Box_MouseMove;
            box.MouseLeftButtonUp += Box_MouseUp;
            thumb.MouseLeftButtonDown += Thumb_MouseDown;

            _boxes[el] = box;
            OverlayLayer.Children.Add(box);
        }
        ApplySelectionVisual();
    }

    private void ApplySelectionVisual()
    {
        var set = new HashSet<TemplateElement>(_selection);
        foreach (var (el, box) in _boxes)
        {
            var selected = set.Contains(el);
            box.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0xE6, 0x7E, 0x22) : Color.FromRgb(0x3B, 0x82, 0xF6));
            box.BorderThickness = new Thickness(selected ? 2 : 0);
            // 缩放手柄只在单选时有意义（多选时缩放语义不明确）
            if (box.Child is Grid g && g.Children.Count > 0 && g.Children[0] is Rectangle thumb)
                thumb.Visibility = selected && _selection.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (SelectionHint != null)
        {
            SelectionHint.Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SelectionHint.Text = _selection.Count > 1
                ? $"已选中 {_selection.Count} 个元素：值相同的属性显示值，值不同的留空。" +
                  " 修改任一属性会应用到全部选中元素（一次撤销）。"
                : "";
        }
    }

    private void Box_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border box || box.Tag is not TemplateElement el) return;

        // Ctrl+点击：切换该元素的选中状态，不开始拖动，也不清空其它选中项
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleSelection(el);
            e.Handled = true;
            return;
        }

        // 点已选中的元素：保持整组选中，拖动时整体移动
        if (!_selection.Contains(el)) Select(el);
        else CommitPanel();

        BeginDrag(e.GetPosition(EditorCanvas), resize: false);
        box.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>记录拖动开始时的整组原始坐标；缩放仍只作用于主选中元素。</summary>
    private void BeginDrag(Point start, bool resize)
    {
        if (_selection.Count == 0) return;
        _dragging = !resize;
        _resizing = resize;
        _dragStart = start;
        _dragSnapshot = _doc.ToJson();
        _dragOrigins.Clear();
        foreach (var el in _selection)
            _dragOrigins[el] = (el.X, el.Y, el.X2, el.Y2);

        var primary = _selected!;
        _origX = primary.X;
        _origY = primary.Y;
        _origW = primary.Type == "qrcode" ? (primary.SizeMm ?? primary.W) : primary.W;
        _origH = primary.Type == "qrcode" ? (primary.SizeMm ?? primary.H) : primary.H;
        _origX2 = primary.X2 ?? primary.X;
        _origY2 = primary.Y2 ?? primary.Y;
    }

    private void Thumb_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle thumb || thumb.Tag is not TemplateElement el) return;
        if (_selection.Count != 1 || !ReferenceEquals(_selection[0], el)) Select(el);
        else CommitPanel();
        BeginDrag(e.GetPosition(EditorCanvas), resize: true);
        ((UIElement)thumb.Parent).CaptureMouse();
        e.Handled = true;
    }

    private void Box_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selected == null) return;
        if (!_dragging && !_resizing) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var p = e.GetPosition(EditorCanvas);
        var dxMm = (p.X - _dragStart.X) / _scale;
        var dyMm = (p.Y - _dragStart.Y) / _scale;

        if (_dragging)
        {
            // 整组按同一个位移批量移动，line 的两端一起搬
            foreach (var (el, o) in _dragOrigins)
            {
                el.X = Math.Round(Math.Max(0, o.X + dxMm), 2);
                el.Y = Math.Round(Math.Max(0, o.Y + dyMm), 2);
                if (el.Type == "line")
                {
                    if (o.X2.HasValue) el.X2 = Math.Round(Math.Max(0, o.X2.Value + dxMm), 2);
                    if (o.Y2.HasValue) el.Y2 = Math.Round(Math.Max(0, o.Y2.Value + dyMm), 2);
                }
            }
            foreach (var el in _selection) UpdateBoxPosition(el);
        }
        else if (_resizing)
        {
            if (_selected.Type == "line")
            {
                _selected.X2 = Math.Round(Math.Max(0, p.X / _scale), 2);
                _selected.Y2 = Math.Round(Math.Max(0, p.Y / _scale), 2);
            }
            else if (_selected.Type == "qrcode")
            {
                // 二维码是正方形，取拖拽方向里较大的一边，三个尺寸字段一起改
                var size = Math.Round(Math.Max(3, Math.Max(_origW + dxMm, _origH + dyMm)), 2);
                _selected.SizeMm = size;
                _selected.W = size;
                _selected.H = size;
            }
            else
            {
                _selected.W = Math.Round(Math.Max(1, _origW + dxMm), 2);
                _selected.H = Math.Round(Math.Max(1, _origH + dyMm), 2);
            }
        }

        if (!_dragging) UpdateBoxPosition(_selected!);
        RefreshPanelFromSelection(keepContent: true);
    }

    private void UpdateBoxPosition(TemplateElement el)
    {
        if (!_boxes.TryGetValue(el, out var box)) return;
        var rect = BoundsOf(el, _scale);
        Canvas.SetLeft(box, rect.X);
        Canvas.SetTop(box, rect.Y);
        box.Width = rect.Width;
        box.Height = rect.Height;
    }

    private void Box_MouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging && !_resizing) return;
        _dragging = false;
        _resizing = false;
        _dragOrigins.Clear();
        Mouse.Capture(null);

        // 只有真的动过才记一条撤销，避免"点一下元素"就污染撤销栈。
        // 批量移动也只压这一条快照，整组位移合并为一次撤销。
        if (_dragSnapshot != null)
        {
            var after = _doc.ToJson();
            if (!string.Equals(after, _dragSnapshot, StringComparison.Ordinal))
            {
                _undoStack.Add(_dragSnapshot);
                if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
                _redoStack.Clear();
            }
            _dragSnapshot = null;
        }
        UpdateUndoButtons();
        RefreshPanelFromSelection(keepContent: true);
        RenderPreview();
    }

    // ---------- 选择 ----------

    /// <summary>单选：先提交待生效的面板编辑，再切换选中集合。</summary>
    private void Select(TemplateElement el) => SetSelection(new[] { el });

    /// <summary>设置选中集合（Ctrl 多选 / 圈选 / 全选 / 切换元素都走这里）。</summary>
    private void SetSelection(IEnumerable<TemplateElement> elements)
    {
        // 点击画布元素不会让 TextBox/ComboBox 失焦（画布不可聚焦），
        // 所以切换选中元素前必须主动提交，否则刚改的字号会被新元素的灌值覆盖掉。
        CommitPanel();

        var valid = elements.Where(_doc.Elements.Contains).Distinct().ToList();
        _selection.Clear();
        _selection.AddRange(valid);
        _selected = valid.FirstOrDefault();
        RefreshPanelFromSelection();
        ApplySelectionVisual();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();

    /// <summary>全选（Ctrl+A / 工具栏按钮）。</summary>
    public void SelectAll() => SetSelection(_doc.Elements.ToList());

    /// <summary>Ctrl+点击：把元素加入/移出选中集合，保持其它选中项不变。</summary>
    private void ToggleSelection(TemplateElement el)
    {
        CommitPanel();
        var copy = _selection.ToList();
        if (!copy.Remove(el)) copy.Add(el);
        SetSelection(copy);
    }

    /// <summary>自检用：模拟 Ctrl+点击切换，返回切换后选中数。</summary>
    internal int ToggleSelectionForTest(TemplateElement el)
    {
        ToggleSelection(el);
        return _selection.Count;
    }

    /// <summary>自检用：判断元素是否在当前选中集合里。</summary>
    internal bool IsSelectedForTest(TemplateElement el) => _selection.Contains(el);

    // ---------- 画布圈选（橡皮筋） ----------

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border || e.OriginalSource is Rectangle r && ReferenceEquals(r, RubberBand)) return;
        CommitPanel();
        _rubberStart = e.GetPosition(EditorCanvas);
        _rubberBanding = true;
        Canvas.SetLeft(RubberBand, _rubberStart.X);
        Canvas.SetTop(RubberBand, _rubberStart.Y);
        RubberBand.Width = 0;
        RubberBand.Height = 0;
        RubberBand.Visibility = Visibility.Visible;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_rubberBanding) return;
        var p = e.GetPosition(EditorCanvas);
        var rect = new Rect(_rubberStart, p);
        Canvas.SetLeft(RubberBand, rect.X);
        Canvas.SetTop(RubberBand, rect.Y);
        RubberBand.Width = rect.Width;
        RubberBand.Height = rect.Height;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rubberBanding) return;
        _rubberBanding = false;
        Mouse.Capture(null);
        RubberBand.Visibility = Visibility.Collapsed;

        var p = e.GetPosition(EditorCanvas);
        ApplyRubberBand(new Rect(_rubberStart, p),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    /// <summary>圈选命中：橡皮筋与元素包围盒相交即选中；拖得太小视为点空白清空选择。</summary>
    private void ApplyRubberBand(Rect band, bool additive)
    {
        var hits = band.Width < 3 || band.Height < 3
            ? new List<TemplateElement>()
            : _doc.Elements.Where(el => band.IntersectsWith(BoundsOf(el, _scale))).ToList();

        if (additive && hits.Count > 0) hits = _selection.Union(hits).ToList();
        SetSelection(hits);
    }

    /// <summary>灌值期间抑制所有面板变更事件，避免「选中元素/拖动刷新」被当成用户编辑。</summary>
    private bool _suppressPanel;

    /// <summary>面板字段快照，用于 diff 出「用户到底改了哪几个字段」。</summary>
    private sealed class PanelSnapshot
    {
        public string X = "", Y = "", W = "", H = "", Content = "", Font = "", FontSize = "";
        public bool? Bold, Italic, Wrap;
        public string Align = "", Valign = "";
    }

    /// <summary>灌值完成时的基线；CommitPanel 只把与它不同的字段写入所有选中元素。</summary>
    private PanelSnapshot _baseline = new();

    private PanelSnapshot ReadPanel() => new()
    {
        X = XBox.Text, Y = YBox.Text, W = WBox.Text, H = HBox.Text,
        Content = ContentBox.Text, Font = FontCombo.Text, FontSize = FontSizeCombo.Text,
        Bold = BoldToggle.IsChecked, Italic = ItalicToggle.IsChecked, Wrap = WrapToggle.IsChecked,
        Align = AlignRight.IsChecked == true ? "right"
            : AlignCenter.IsChecked == true ? "center"
            : AlignLeft.IsChecked == true ? "left" : "",
        Valign = ValignTop.IsChecked == true ? "top"
            : ValignBottom.IsChecked == true ? "bottom"
            : ValignMiddle.IsChecked == true ? "middle" : "",
    };

    private static HashSet<string> Diff(PanelSnapshot cur, PanelSnapshot baseline)
    {
        var d = new HashSet<string>(StringComparer.Ordinal);
        void Cmp(string name, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal)) d.Add(name);
        }
        Cmp("x", cur.X, baseline.X);
        Cmp("y", cur.Y, baseline.Y);
        Cmp("w", cur.W, baseline.W);
        Cmp("h", cur.H, baseline.H);
        Cmp("content", cur.Content, baseline.Content);
        Cmp("font", cur.Font, baseline.Font);
        Cmp("fontsize", cur.FontSize, baseline.FontSize);
        Cmp("align", cur.Align, baseline.Align);
        Cmp("valign", cur.Valign, baseline.Valign);
        if (cur.Bold != baseline.Bold) d.Add("bold");
        if (cur.Italic != baseline.Italic) d.Add("italic");
        if (cur.Wrap != baseline.Wrap) d.Add("wrap");
        return d;
    }

    /// <summary>多值语义：所有选中元素该属性相同则显示该值，不同则留空。</summary>
    private static string Common(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? distinct[0] : "";
    }

    private static bool? CommonBool(IEnumerable<bool> values)
    {
        var distinct = values.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private void RefreshPanelFromSelection(bool keepContent = false) =>
        FillPropertyPanel(_selection, keepContent);

    /// <summary>自检钩子用的单元素重载。</summary>
    private void FillPropertyPanel(TemplateElement el, bool keepContent = false) =>
        FillPropertyPanel(new[] { el }, keepContent);

    private void FillPropertyPanel(IReadOnlyList<TemplateElement> els, bool keepContent = false)
    {
        _suppressPanel = true;
        try
        {
            var empty = els.Count == 0;
            TypeText.Text = empty ? "未选中元素" : els.Count == 1
                ? (els[0].Type == "symbol" ? $"类型：symbol（{Symbols.Title(els[0].Text)}）" : $"类型：{els[0].Type}")
                : $"已选中 {els.Count} 个元素";

            XBox.Text = empty ? "" : Common(els.Select(e => e.X.ToString("0.##")));
            YBox.Text = empty ? "" : Common(els.Select(e => e.Y.ToString("0.##")));
            WBox.Text = empty ? "" : Common(els.Select(e => WidthText(e)));
            HBox.Text = empty ? "" : Common(els.Select(e => HeightText(e)));
            if (!keepContent) ContentBox.Text = empty ? "" : Common(els.Select(e => e.Text ?? ""));
            FontCombo.Text = empty ? "" : Common(els.Select(e => e.Font ?? "微软雅黑"));

            // 字号：只要选中里有 text 元素就允许批量设置（只作用于其中的 text）；
            // 值不同留空，未改动就不写 font_size，保证旧模板不走样
            var texts = els.Where(e => e.Type == "text").ToList();
            FontSizeCombo.IsEnabled = texts.Count > 0;
            FontSizeCombo.Text = texts.Count == 0 ? "" : Common(texts.Select(FontSizeText));

            var align = empty ? "" : Common(els.Select(e => e.Align ?? "left"));
            var valign = empty ? "" : Common(els.Select(e =>
                e.Valign == "top" ? "top" : e.Valign == "bottom" ? "bottom" : "middle"));
            AlignLeft.IsChecked = align == "left";
            AlignCenter.IsChecked = align == "center";
            AlignRight.IsChecked = align == "right";
            ValignTop.IsChecked = valign == "top";
            ValignMiddle.IsChecked = valign == "middle";
            ValignBottom.IsChecked = valign == "bottom";

            if (empty)
            {
                BoldToggle.IsChecked = ItalicToggle.IsChecked = WrapToggle.IsChecked = null;
            }
            else
            {
                BoldToggle.IsChecked = CommonBool(els.Select(e => e.Bold == true));
                ItalicToggle.IsChecked = CommonBool(els.Select(e => e.Italic == true));
                WrapToggle.IsChecked = CommonBool(els.Select(e => e.WordWrap == true));
            }
        }
        finally
        {
            _suppressPanel = false;
            _baseline = ReadPanel();   // 灌值后重设基线，之前未提交的编辑已在 SetSelection 时提交
        }
    }

    private static string WidthText(TemplateElement e) =>
        e.Type == "qrcode" ? (e.SizeMm ?? (e.W > 0 ? e.W : 15)).ToString("0.##") : e.W.ToString("0.##");

    private static string HeightText(TemplateElement e) =>
        e.Type == "qrcode" ? (e.SizeMm ?? (e.H > 0 ? e.H : 15)).ToString("0.##") : e.H.ToString("0.##");

    private static string FontSizeText(TemplateElement e)
    {
        var pt = e.FontSize ?? (e.SizeMm.HasValue ? e.SizeMm.Value * 72.0 / 25.4 : 11.0);
        return Math.Round(pt, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>把面板里用户改动的字段应用到一个元素；未改动的字段保持该元素原值。</summary>
    private static void ApplyChangedFields(TemplateElement el, PanelSnapshot s, HashSet<string> changed)
    {
        static double? Num(string text) => double.TryParse(text, out var v) ? v : null;

        if (changed.Contains("x") && Num(s.X) is { } x) el.X = x;
        if (changed.Contains("y") && Num(s.Y) is { } y) el.Y = y;

        if (changed.Contains("w") || changed.Contains("h"))
        {
            if (el.Type == "qrcode")
            {
                var size = Math.Max(3, Num(s.W) ?? Num(s.H) ?? el.SizeMm ?? 15);
                el.SizeMm = size;
                el.W = size;
                el.H = size;
            }
            else if (el.Type != "line")
            {
                if (changed.Contains("w") && Num(s.W) is { } w) el.W = Math.Max(1, w);
                if (changed.Contains("h") && Num(s.H) is { } h) el.H = Math.Max(1, h);
            }
        }

        if (changed.Contains("content") && el.Type != "symbol") el.Text = s.Content;
        if (changed.Contains("font")) el.Font = string.IsNullOrWhiteSpace(s.Font) ? null : s.Font;

        // 只有用户确实改了字号才写 font_size；旧模板保持无该字段，继续用 size_mm
        if (changed.Contains("fontsize") && el.Type == "text"
            && double.TryParse(s.FontSize, out var pt) && pt > 0)
        {
            el.FontSize = Math.Round(pt, 1);
        }

        if (changed.Contains("bold")) el.Bold = s.Bold == true;
        if (changed.Contains("italic")) el.Italic = s.Italic == true ? true : null;
        if (changed.Contains("wrap")) el.WordWrap = s.Wrap == true ? true : null;
        if (changed.Contains("align") && s.Align.Length > 0) el.Align = s.Align;
        if (changed.Contains("valign") && s.Valign.Length > 0) el.Valign = s.Valign;
    }

    /// <summary>
    /// 提交一次属性变更：用灌值基线 diff 出用户改过的字段，把这些字段应用到
    /// <b>全部</b>选中元素，未改动的混合值字段保持各自原值。整批改动只压一条撤销。
    /// 数值/文本在光标离开输入框时触发，开关/单选/下拉点击即触发。
    /// </summary>
    private void CommitPanel()
    {
        if (_suppressPanel || _selection.Count == 0) return;

        var current = ReadPanel();
        var changed = Diff(current, _baseline);
        if (changed.Count == 0) return;

        var before = _doc.ToJson();
        foreach (var el in _selection) ApplyChangedFields(el, current, changed);
        var after = _doc.ToJson();
        _baseline = current;
        if (string.Equals(before, after, StringComparison.Ordinal)) return;

        PushUndoSnapshot(before);   // 批量属性改动合并为一次撤销
        foreach (var el in _selection) UpdateBoxPosition(el);
        RenderPreview();
        StatusText.Text = _selection.Count > 1
            ? $"已批量修改 {_selection.Count} 个元素（未保存）"
            : "已修改（未保存）";
    }

    private void Panel_LostFocus(object sender, RoutedEventArgs e) => CommitPanel();

    private void Panel_Changed(object sender, RoutedEventArgs e) => CommitPanel();

    private void Panel_EnterKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitPanel();
        // 移开焦点，避免随后 LostFocus 再触发一次（无变化会自动跳过，这里只是让行为更干脆）
        if (sender is UIElement el) el.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    // ---------- 增删 ----------

    private void AddElement(TemplateElement el)
    {
        PushUndo();
        _doc.Elements.Add(el);
        RebuildOverlays();
        Select(el);
        RenderPreview();
        StatusText.Text = $"已添加 {el.Type}（未保存）";
    }

    private void AddText_Click(object sender, RoutedEventArgs e) => AddElement(new TemplateElement
    {
        Type = "text", X = 4, Y = 4, W = 30, H = 6, Text = "文本", SizeMm = 3, Align = "left", Valign = "middle",
    });

    private void AddBarcode_Click(object sender, RoutedEventArgs e) => AddElement(new TemplateElement
    {
        Type = "barcode", X = 4, Y = 4, W = 40, H = 12, Text = "{{barcode}}", Symbology = "code128",
    });

    private void AddQr_Click(object sender, RoutedEventArgs e) => AddElement(new TemplateElement
    {
        Type = "qrcode", X = 4, Y = 4, W = 15, H = 15, SizeMm = 15, Text = "{{barcode}}",
    });

    private void AddRect_Click(object sender, RoutedEventArgs e) => AddElement(new TemplateElement
    {
        Type = "rect", X = 2, Y = 2, W = 40, H = 25, ThicknessMm = 0.15,
    });

    private void AddLine_Click(object sender, RoutedEventArgs e) => AddElement(new TemplateElement
    {
        Type = "line", X = 2, Y = 10, X2 = 40, Y2 = 10, ThicknessMm = 0.15,
    });

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            Title = "选择图片（Logo）",
        };
        if (dlg.ShowDialog() != true) return;
        AddElement(new TemplateElement
        {
            Type = "image", X = 2, Y = 2, W = 20, H = 10, Src = dlg.FileName,
        });
    }

    /// <summary>插入内置矢量 Pb 无铅标志：默认放在标签右下部（.lsd 里该标志的常见位置）。</summary>
    private void AddPbMark_Click(object sender, RoutedEventArgs e) => AddElement(new TemplateElement
    {
        Type = "symbol",
        Text = "pb-free",
        X = Math.Round(_doc.WidthMm * 0.65, 2),
        Y = Math.Round(_doc.HeightMm * 0.72, 2),
        W = 7.09,
        H = 6.87,
    });

    private void DeleteElement_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        CommitPanel();
        var count = _selection.Count;
        PushUndo();
        foreach (var el in _selection) _doc.Elements.Remove(el);
        _selection.Clear();
        _selected = null;
        RebuildOverlays();
        RefreshPanelFromSelection();
        RenderPreview();
        StatusText.Text = $"已删除 {count} 个元素（未保存）";
    }

    private void Front_Click(object sender, RoutedEventArgs e) => MoveZ(1);

    private void Back_Click(object sender, RoutedEventArgs e) => MoveZ(-1);

    private void MoveZ(int delta)
    {
        if (_selected == null) return;
        CommitPanel();
        var i = _doc.Elements.IndexOf(_selected);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _doc.Elements.Count) return;
        PushUndo();
        _doc.Elements.RemoveAt(i);
        _doc.Elements.Insert(j, _selected);
        RebuildOverlays();
        RenderPreview();
    }

    // ---------- 撤销 / 重做 ----------

    /// <summary>把当前状态压入撤销栈（在改动之前调用）。</summary>
    private void PushUndo() => PushUndoSnapshot(_doc.ToJson());

    /// <summary>压入指定快照：属性即时提交时，改动已发生，需显式传入改动前的 JSON。</summary>
    private void PushUndoSnapshot(string snapshot)
    {
        _undoStack.Add(snapshot);
        if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        if (UndoButton != null) UndoButton.IsEnabled = _undoStack.Count > 0;
        if (RedoButton != null) RedoButton.IsEnabled = _redoStack.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undoStack.Count == 0) { StatusText.Text = "没有可撤销的操作"; return; }
        _redoStack.Add(_doc.ToJson());
        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        ApplySnapshot(snapshot, "已撤销");
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) { StatusText.Text = "没有可重做的操作"; return; }
        _undoStack.Add(_doc.ToJson());
        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        ApplySnapshot(snapshot, "已重做");
    }

    private void ApplySnapshot(string json, string message)
    {
        TemplateDoc doc;
        try { doc = TemplateDoc.Parse(json); }
        catch (Exception ex) { StatusText.Text = "恢复失败：" + ex.Message; return; }

        doc.Name = NameBox.Text.Trim();      // 名称以输入框为准，撤销不该把名称改回去
        _doc = doc;
        _selection.Clear();
        _selected = null;
        NormalizeQrSize();
        RebuildOverlays();
        RefreshPanelFromSelection();
        RenderPreview();
        UpdateUndoButtons();
        StatusText.Text = message + "（未保存）";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        // 焦点在文本框里就交给文本框自己撤销输入，别抢
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.A) { SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Y) { Redo(); e.Handled = true; }
    }

    // ---------- 自检hook（uicheck 用，不参与业务逻辑） ----------

    internal TemplateDoc DocForTest => _doc;

    /// <summary>模拟一次拖动：不依赖鼠标状态，直接改坐标并走与松手相同的收尾逻辑。</summary>
    internal bool SimulateDragForTest(string type, double dxMm, double dyMm)
    {
        var el = _doc.Elements.FirstOrDefault(e => e.Type == type);
        if (el == null) return false;
        Select(el);
        _dragSnapshot = _doc.ToJson();
        _dragging = true;
        el.X = Math.Round(Math.Max(0, el.X + dxMm), 2);
        el.Y = Math.Round(Math.Max(0, el.Y + dyMm), 2);
        UpdateBoxPosition(el);
        EndDrag();
        return true;
    }

    internal void UndoForTest() => Undo();
    internal void RedoForTest() => Redo();

    /// <summary>自检用：当前选中元素数量。</summary>
    internal int SelectionCountForTest => _selection.Count;

    /// <summary>自检用：清空选择。</summary>
    internal void SetSelectionForTest() => SetSelection(Array.Empty<TemplateElement>());

    /// <summary>自检用：强制刷新布局，便于截取多选状态快照。</summary>
    internal void PerformLayoutForTest()
    {
        ApplySelectionVisual();
        UpdateLayout();
    }

    /// <summary>自检用：Ctrl+A 全选。</summary>
    internal int SelectAllForTest()
    {
        SelectAll();
        return _selection.Count;
    }

    /// <summary>自检用：模拟圈选（画布坐标，单位 px），走与真实鼠标相同的命中路径。</summary>
    internal int RubberBandForTest(double x, double y, double w, double h)
    {
        ApplyRubberBand(new Rect(x, y, w, h), additive: false);
        return _selection.Count;
    }

    /// <summary>自检用：Ctrl+圈选追加命中。</summary>
    internal int RubberBandAddForTest(double x, double y, double w, double h)
    {
        ApplyRubberBand(new Rect(x, y, w, h), additive: true);
        return _selection.Count;
    }

    /// <summary>自检用：读取面板当前显示值（验证混合值留空）。</summary>
    internal string PanelTextForTest(string field) => field switch
    {
        "x" => XBox.Text, "y" => YBox.Text, "w" => WBox.Text, "h" => HBox.Text,
        "font" => FontCombo.Text, "fontsize" => FontSizeCombo.Text,
        "content" => ContentBox.Text, "type" => TypeText.Text,
        _ => "",
    };

    /// <summary>
    /// 自检用：模拟「选中两个文本元素 → 面板改字号 → 失焦批量提交」，
    /// 返回可断言的行为串：混合值显示、全部生效、只压一条撤销。
    /// </summary>
    internal string SimulateMultiEditForTest(string newFontSize)
    {
        var texts = _doc.Elements.Where(e => e.Type == "text").Take(2).ToList();
        if (texts.Count < 2) return "skip(<2 text elements)";
        SetSelection(texts);
        var sizesBefore = string.Join(",", texts.Select(t => t.FontSize?.ToString("0.#") ?? "-"));
        var xShown = XBox.Text;               // 两个元素 X 通常不同 → 应为空
        var fontShown = FontCombo.Text;
        var undo0 = _undoStack.Count;

        FontSizeCombo.Text = newFontSize;
        Panel_LostFocus(FontSizeCombo, new RoutedEventArgs());
        var undo1 = _undoStack.Count;
        var sizesAfter = string.Join(",", texts.Select(t => t.FontSize?.ToString("0.#") ?? "-"));
        var xUnchanged = string.Join(",", texts.Select(t => t.X.ToString("0.##")));

        // 再提交一次相同值：不应重复压撤销
        Panel_LostFocus(FontSizeCombo, new RoutedEventArgs());
        var undo2 = _undoStack.Count;

        return $"count:{_selection.Count};xShown:'{xShown}';fontShown:'{fontShown}';" +
               $"sizes:{sizesBefore}->{sizesAfter};xAfter:{xUnchanged};" +
               $"undo1:+{undo1 - undo0};undo2:+{undo2 - undo1}";
    }

    /// <summary>自检用：批量设置字体，验证所有选中元素都生效且只压一条撤销。</summary>
    internal string SimulateBatchFontForTest(string fontName)
    {
        var texts = _doc.Elements.Where(e => e.Type == "text").Take(2).ToList();
        if (texts.Count < 2) return "skip(<2 text elements)";
        SetSelection(texts);
        var undo0 = _undoStack.Count;
        FontCombo.Text = fontName;
        Panel_LostFocus(FontCombo, new RoutedEventArgs());
        var undo1 = _undoStack.Count;
        // 再提交一次相同值：不应重复压撤销
        Panel_LostFocus(FontCombo, new RoutedEventArgs());
        var undo2 = _undoStack.Count;
        var fonts = string.Join(",", texts.Select(t => t.Font ?? "-"));
        return $"count:{_selection.Count};fonts:{fonts};undo1:+{undo1 - undo0};undo2:+{undo2 - undo1}";
    }

    /// <summary>自检用：模拟整组批量拖动，验证所有元素位移一致且只压一条撤销。</summary>
    internal string SimulateMultiDragForTest(double dxMm, double dyMm)
    {
        var els = _doc.Elements.Take(2).ToList();
        if (els.Count < 2) return "skip(<2 elements)";
        SetSelection(els);
        var before = els.Select(e => (e.X, e.Y)).ToList();
        var undo0 = _undoStack.Count;

        BeginDrag(new Point(0, 0), resize: false);
        _dragging = true;
        foreach (var (el, o) in _dragOrigins)
        {
            el.X = Math.Round(Math.Max(0, o.X + dxMm), 2);
            el.Y = Math.Round(Math.Max(0, o.Y + dyMm), 2);
            if (el.Type == "line")
            {
                if (o.X2.HasValue) el.X2 = Math.Round(Math.Max(0, o.X2.Value + dxMm), 2);
                if (o.Y2.HasValue) el.Y2 = Math.Round(Math.Max(0, o.Y2.Value + dyMm), 2);
            }
        }
        foreach (var el in els) UpdateBoxPosition(el);
        EndDrag();
        var undo1 = _undoStack.Count;
        var moved = string.Join(",", els.Select(e => $"{e.X:0.##},{e.Y:0.##}"));
        var deltas = string.Join(",", els.Select((e, i) =>
            $"{(e.X - before[i].X):0.##}/{(e.Y - before[i].Y):0.##}"));
        return $"count:{_selection.Count};deltas:{deltas};pos:{moved};undo:+{undo1 - undo0}";
    }

    /// <summary>
    /// 自检用：模拟「改了 A 的字号但不失焦，直接点画布上的 B，再回到 A」，
    /// 验证 Select 切换元素前会先提交改动（画布不可聚焦，不会触发 LostFocus）。
    /// </summary>
    internal string SimulateSwitchCommitForTest(string newFontSize)
    {
        var texts = _doc.Elements.Where(e => e.Type == "text").ToList();
        if (texts.Count < 2) return "skip(<2 text elements)";
        var a = texts[0];
        var b = texts[1];

        Select(a);                       // 灌入 A 的真实值
        var sizeBefore = a.FontSize;
        FontSizeCombo.Text = newFontSize;   // 只改面板，不触发失焦/提交
        Select(b);                       // 直接点 B：应先提交 A 的改动，再灌 B 的值
        var sizeAfterSwitch = a.FontSize;
        Select(a);                       // 回到 A，此时应显示已经生效的新字号
        var shownOnReturn = FontSizeCombo.Text;

        return $"size:{sizeBefore}->{sizeAfterSwitch};shown:{shownOnReturn};" +
               $"selectedA:{ReferenceEquals(_selected, a)}";
    }

    /// <summary>自检用：选中第一个文本元素，让属性面板截图能展示字号/字体等真实值。</summary>
    internal bool SelectFirstTextForTest()
    {
        var el = _doc.Elements.FirstOrDefault(e => e.Type == "text");
        if (el == null) return false;
        Select(el);
        return true;
    }

    /// <summary>
    /// 自检用：模拟「灌值 → 改数值 → 失焦提交」，返回可断言的行为串。
    /// 验证：灌值不触发提交、失焦即时生效、一次编辑只压一条撤销、无变化不压撤销。
    /// </summary>
    internal string SimulatePanelEditForTest(string newX, string newFontSize)
    {
        var el = _doc.Elements.FirstOrDefault(e => e.Type == "text") ?? _doc.Elements.First();
        Select(el);
        var undo0 = _undoStack.Count;
        var x0 = el.X;

        // 灌值本身不应产生任何提交
        var undoAfterFill = _undoStack.Count;

        XBox.Text = newX;
        Panel_LostFocus(XBox, new RoutedEventArgs());
        var x1 = el.X;
        var undo1 = _undoStack.Count;

        // 相同值再提交一次：不应再压撤销
        XBox.Text = newX;
        Panel_LostFocus(XBox, new RoutedEventArgs());
        var undo2 = _undoStack.Count;

        // 字号：只有文本元素能改
        var sizeBefore = el.FontSize;
        FontSizeCombo.Text = newFontSize;
        Panel_LostFocus(FontSizeCombo, new RoutedEventArgs());
        var sizeAfter = el.FontSize;
        var undo3 = _undoStack.Count;

        return $"x:{x0:0.##}->{x1:0.##};fill:+{undoAfterFill - undo0};" +
               $"edit1:+{undo1 - undoAfterFill};same:+{undo2 - undo1};" +
               $"fontSize:{sizeBefore}->{sizeAfter};edit2:+{undo3 - undo2}";
    }

    /// <summary>自检用：把元素属性灌进面板，读回图标按钮状态，验证双向映射。</summary>
    internal string FormatPanelStateForTest(TemplateElement el)
    {
        FillPropertyPanel(el);
        return PanelStateString();
    }

    private string PanelStateString() =>
        $"B={BoldToggle.IsChecked},I={ItalicToggle.IsChecked},W={WrapToggle.IsChecked}," +
        $"H={(AlignLeft.IsChecked == true ? "left" : AlignCenter.IsChecked == true ? "center" : AlignRight.IsChecked == true ? "right" : "none")}," +
        $"V={(ValignTop.IsChecked == true ? "top" : ValignMiddle.IsChecked == true ? "middle" : ValignBottom.IsChecked == true ? "bottom" : "none")}";

    /// <summary>自检用：读当前面板状态并应用到一个临时元素，验证「控件 → 字段」方向。</summary>
    internal TemplateElement PanelToElementForTest()
    {
        var temp = new TemplateElement { Type = "text", SizeMm = 3 };
        var s = ReadPanel();
        var all = new HashSet<string>(StringComparer.Ordinal)
            { "x", "y", "w", "h", "content", "font", "fontsize", "bold", "italic", "wrap", "align", "valign" };
        ApplyChangedFields(temp, s, all);
        return new TemplateElement
        {
            Bold = temp.Bold, Italic = temp.Italic, WordWrap = temp.WordWrap,
            Align = temp.Align, Valign = temp.Valign, FontSize = temp.FontSize,
        };
    }

    // ---------- 变量 ----------

    private void VarList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VarList.SelectedItem is not VarRow row) return;
        var insert = "{{" + row.Key + "}}";
        var pos = ContentBox.SelectionStart;
        ContentBox.Text = ContentBox.Text.Insert(pos, insert);
        ContentBox.SelectionStart = pos + insert.Length;
        ContentBox.Focus();
    }

    /// <summary>打开独立变量管理窗口；保存后重新从子表加载变量并刷新预览。</summary>
    private void AddVar_Click(object sender, RoutedEventArgs e)
    {
        var win = new VariablesWindow(_templateId, NameBox.Text.Trim()) { Owner = this };
        if (win.ShowDialog() != true) return;

        _doc = _templates.LoadDoc(_templateId);
        _doc.Name = NameBox.Text.Trim();
        // 变量表是子表存的，撤销快照里的变量已过期，直接清空历史，免得出错
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateUndoButtons();
        _savedState = CurrentState();
        LoadVarList();
        RenderPreview();
        StatusText.Text = "变量已更新（子表已保存）";
    }

    // ---------- 保存 / 发布 / 试打 ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitPanel();   // 焦点仍在输入框时，保存前先把待提交的编辑落下来
        try
        {
            _doc.Name = NameBox.Text.Trim();
            var userId = App.Services.GetRequiredService<AppSession>().CurrentUser?.Id;
            _templates.SaveVersion(_templateId, _doc, userId, "编辑器保存");
            _rules.EnsureDefaultRule(_templateId, _doc.Variables.Select(v => v.Key).ToList(), userId);
            _savedState = CurrentState();
            StatusText.Text = $"已保存（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        Save_Click(sender, e);
        var rules = _rules.GetByTemplate(_templateId);
        if (rules.Count == 0 || rules.All(r => !r.Enabled))
        {
            MessageBox.Show("发布前需先在「模板管理 → 扫码规则」中校准并启用至少一条规则。",
                "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _templates.SetStatus(_templateId, "published");
        // 扫码页是单例页面，发布后主动刷新它的模板下拉，否则要等重启才看得到
        App.Services.GetRequiredService<ScanPrintViewModel>().RefreshTemplates();
        StatusText.Text = "模板已发布";
        MessageBox.Show("模板已发布，可在扫码打印页选择使用。", "发布成功");
    }

    private void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        CommitPanel();
        var rows = new List<Dictionary<string, string?>> { SampleValues() };
        var win = App.Services.GetRequiredService<PrintPreviewWindow>();
        win.Initialize(_doc, _templateId, NameBox.Text, NameBox.Text, rows);
        win.Owner = this;
        win.ShowDialog();
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomTransform == null) return;
        ZoomTransform.ScaleX = e.NewValue;
        ZoomTransform.ScaleY = e.NewValue;
    }
}
