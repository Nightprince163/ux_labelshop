using System.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UXLabelshop.Rendering;
using UXLabelshop.Services;

namespace UXLabelshop.ViewModels;

public partial class ScanPrintViewModel : ObservableObject
{
    private readonly TemplateService _templates;
    private readonly ScanRuleService _rules;
    private readonly LabelRenderer _renderer = new();

    public List<TemplateListItem> Templates { get; private set; } = new();
    /// <summary>清单里显示的列 = scan + computed（fixed 不进清单）</summary>
    public List<TemplateVar> EditableVars { get; private set; } = new();
    public List<TemplateVar> AllVars { get; private set; } = new();
    public TemplateDoc? Doc { get; private set; }
    public TemplateListItem? CurrentTemplate { get; private set; }
    public VariableResolver Resolver { get; private set; } = new(Array.Empty<TemplateVar>());
    private bool _suppressRecalc;

    /// <summary>清单数据（列 = 模板可录入变量）。</summary>
    public DataTable Table { get; } = new();

    [ObservableProperty] private TemplateListItem? _selectedTemplate;
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string _previewInfo = "扫码后在此预览";
    [ObservableProperty] private int _rowCount;

    /// <summary>模板切换通知（页面据此重建 DataGrid 列）。</summary>
    public event Action? TemplateChanged;
    public event Action? RowsChanged;

    public ScanPrintViewModel(TemplateService templates, ScanRuleService rules)
    {
        _templates = templates;
        _rules = rules;
        Table.ColumnChanged += OnColumnChanged;
        LoadTemplates();
    }

    /// <summary>依赖格变动时自动重算该行公式列（用户确认：依赖一变就无条件覆盖）。</summary>
    private void OnColumnChanged(object sender, DataColumnChangeEventArgs e)
    {
        if (_suppressRecalc || Resolver.ComputedOrdered.Count == 0 || e.Row == null) return;
        var key = e.Column.ColumnName;
        // 改的是计算列本身时不回算，避免覆盖用户手动改的结果
        if (Resolver.ComputedOrdered.Any(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))) return;
        if (!Resolver.ShouldRecompute(key)) return;
        RecomputeRow(e.Row, suppressEvents: true);
    }

    /// <summary>重算单行的计算列（无条件覆盖）。</summary>
    public void RecomputeRow(DataRow row, bool suppressEvents = false)
    {
        if (Resolver.ComputedOrdered.Count == 0) return;
        var values = EditableVars.ToDictionary(v => v.Key, v => (string?)row[v.Key]?.ToString());
        Resolver.Recompute(values);
        var prev = _suppressRecalc;
        if (suppressEvents) _suppressRecalc = true;
        try
        {
            foreach (var v in Resolver.ComputedOrdered)
                if (!Equals(row[v.Key]?.ToString(), values[v.Key] ?? "")) row[v.Key] = values[v.Key] ?? "";
        }
        finally { _suppressRecalc = prev; }
    }

    /// <summary>行右键「重算公式列」。</summary>
    public void RecomputeAllRows()
    {
        _suppressRecalc = true;
        try
        {
            foreach (DataRow row in Table.Rows) RecomputeRow(row);
        }
        finally { _suppressRecalc = false; }
    }

    public void LoadTemplates()
    {
        Templates = _templates.GetPublished();
        OnPropertyChanged(nameof(Templates));
        if (Templates.Count > 0)
            SelectedTemplate = Templates[0];
        else
            StatusMessage = "没有已发布的模板，请先在「模板管理」中发布模板";
    }

    /// <summary>刷新下拉时抑制选中项副作用：不重载文档、不清空未打印清单。</summary>
    private bool _suppressTemplateChange;

    /// <summary>
    /// 模板发布/停用后由模板管理页 / 编辑器调用：只刷新扫码页的模板下拉，
    /// 尽量保留当前选中模板；不重新加载文档、不清空清单里的扫描结果。
    /// </summary>
    public void RefreshTemplates()
    {
        var currentId = CurrentTemplate?.Id;
        var list = _templates.GetPublished();
        var keep = currentId == null ? null : list.FirstOrDefault(t => t.Id == currentId);

        _suppressTemplateChange = true;
        try
        {
            Templates = list;
            SelectedTemplate = keep;
        }
        finally
        {
            _suppressTemplateChange = false;
        }
        OnPropertyChanged(nameof(Templates));

        if (currentId != null && keep == null)
            StatusMessage = "当前模板已停用，请在下拉中重新选择模板";
        else if (list.Count == 0)
            StatusMessage = "没有已发布的模板，请先在「模板管理」中发布模板";
        else
            StatusMessage = "模板列表已更新";
    }

    /// <summary>
    /// 模板切换前的确认回调：已有扫描结果时调用，返回 false 表示取消切换。
    /// 由页面注入（自检不注入，避免弹模态框）。
    /// </summary>
    public Func<string, bool>? ConfirmTemplateSwitch { get; set; }

    /// <summary>短码阈值：≤20 字符视为「补录码」（如 PO 号条码），不走扫码规则解析。</summary>
    public const int ShortScanMaxLength = 20;

    /// <summary>页面在选中行变化时写入；短码补录用它定位要补的行。</summary>
    public DataRow? SelectedRow { get; set; }

    /// <summary>最近一次扫码是否新增了行；页面据此决定要不要把选中行跳到最后一行。</summary>
    public bool LastScanAddedRow { get; private set; }

    /// <summary>取消切换时临时压制 OnSelectedTemplateChanged，避免回写旧值又触发一次清空。</summary>
    private bool _revertingTemplate;

    partial void OnSelectedTemplateChanged(TemplateListItem? value)
    {
        if (value == null || _revertingTemplate || _suppressTemplateChange) return;

        // 换模板会重建列并清空 Table，先确认，用户取消就把选择退回原模板
        if (Table.Rows.Count > 0 && CurrentTemplate != null && CurrentTemplate.Id != value.Id)
        {
            var msg = $"切换模板会清空当前清单里的 {Table.Rows.Count} 行扫描结果（尚未打印）。\n\n" +
                      $"当前：{CurrentTemplate.Name}\n切换到：{value.Name}\n\n确定要继续吗？";
            if (ConfirmTemplateSwitch != null && !ConfirmTemplateSwitch(msg))
            {
                _revertingTemplate = true;
                try { SelectedTemplate = CurrentTemplate; }
                finally { _revertingTemplate = false; }
                StatusMessage = "已取消切换模板，当前扫描结果保留";
                return;
            }
        }

        try
        {
            CurrentTemplate = value;
            Doc = _templates.LoadDoc(value.Id); // 变量以 label_template_var 子表为准
            AllVars = Doc.Variables.Where(v => !string.IsNullOrEmpty(v.Key)).ToList();
            EditableVars = AllVars.Where(v => !v.IsFixed).ToList();
            Resolver = new VariableResolver(AllVars);

            _suppressRecalc = true;
            try
            {
                Table.Rows.Clear();
                Table.Columns.Clear();
                SelectedRow = null;
                foreach (var v in EditableVars)
                    Table.Columns.Add(v.Key, typeof(string));
            }
            finally { _suppressRecalc = false; }

            RowCount = 0;
            TemplateChanged?.Invoke();
            RowsChanged?.Invoke();
            PreviewImage = null;
            PreviewInfo = $"已选择模板：{value.Name}（{Doc.WidthMm}×{Doc.HeightMm}mm，{Doc.Elements.Count} 个元素）";
            StatusMessage = Resolver.ValidationError != null
                ? "模板公式错误：" + Resolver.ValidationError
                : "就绪：扫码枪扫码，或点「新增行」手动录入";
        }
        catch (Exception ex)
        {
            StatusMessage = "模板加载失败：" + ex.Message;
        }
    }

    [RelayCommand]
    public void Scan()
    {
        var raw = ScanText?.Trim() ?? "";
        if (raw.Length == 0) return;
        ScanText = "";
        LastScanAddedRow = false;

        if (Doc == null || CurrentTemplate == null)
        {
            StatusMessage = "请先选择模板";
            return;
        }

        // 短码补录：二维码常不带 PO 号，用户接着扫一下 PO 条码即可补进当前行的空字段
        if (raw.Length <= ShortScanMaxLength)
        {
            FillFirstEmptyScanField(raw);
            return;
        }

        // 只把 scan 列交给扫码规则解析；computed 列由公式算出
        var keys = EditableVars.Where(v => !v.IsComputed).Select(v => v.Key).ToList();
        var result = _rules.Parse(raw, CurrentTemplate.Id, keys);
        var row = AddRow(result.Values);
        RowCount = Table.Rows.Count;
        LastScanAddedRow = true;
        RowsChanged?.Invoke();

        StatusMessage = result.Matched
            ? $"已解析（{result.RuleName}）"
            : $"{result.Message}，已新增空行，请手动填写";
        PreviewRow(row);
    }

    /// <summary>
    /// 短码（≤20 字符）不走规则解析：补到「当前选中行」第一个空白的扫码录入字段。
    /// 没有行、或当前行没有空白的 scan 字段时直接丢弃本次输入，不新增空行。
    /// 未选中任何行时兜底用最后一行（单行清单最常见）。
    /// </summary>
    private void FillFirstEmptyScanField(string raw)
    {
        if (Table.Rows.Count == 0)
        {
            StatusMessage = $"已忽略短码「{raw}」：清单为空，请先扫二维码";
            return;
        }

        var row = SelectedRow;
        if (row == null || row.Table != Table || row.RowState == DataRowState.Detached)
            row = Table.Rows[Table.Rows.Count - 1];

        var target = EditableVars.FirstOrDefault(v =>
            v.EffectiveSource == "scan" &&
            string.IsNullOrEmpty(row[v.Key]?.ToString()?.Trim()));
        if (target == null)
        {
            StatusMessage = $"已忽略短码「{raw}」：当前行没有空白的扫码录入字段";
            return;
        }

        // 赋值会触发 Table.ColumnChanged：公式列自动重算、预览自动重绘（页面监听了同一事件）
        row[target.Key] = raw;
        StatusMessage = $"已把「{raw}」填入 {target.DisplayName}";
    }

    /// <summary>
    /// 建行统一入口：填解析值 → 套缺省值 → 按拓扑序算计算列。
    /// 用 _suppressRecalc 包住，避免逐格赋值时反复触发自动重算。
    /// </summary>
    private DataRow AddRow(IReadOnlyDictionary<string, string?> parsed)
    {
        var row = Table.NewRow();
        var values = EditableVars.ToDictionary(
            v => v.Key,
            v => parsed.TryGetValue(v.Key, out var val) ? val : (string?)"");
        Resolver.Apply(values);

        _suppressRecalc = true;
        try
        {
            foreach (var v in EditableVars) row[v.Key] = values[v.Key] ?? "";
            Table.Rows.Add(row);
        }
        finally { _suppressRecalc = false; }
        return row;
    }

    /// <summary>手动新增一行空记录（同样套缺省值并计算）。</summary>
    [RelayCommand]
    public void AddBlankRow()
    {
        if (Doc == null) return;
        AddRow(new Dictionary<string, string?>());
        RowCount = Table.Rows.Count;
        RowsChanged?.Invoke();
    }

    public void DeleteRow(DataRowView? rowView)
    {
        if (rowView == null) return;
        Table.Rows.Remove(rowView.Row);
        SelectedRow = null;
        RowCount = Table.Rows.Count;
        RowsChanged?.Invoke();
        PreviewImage = null;
        PreviewInfo = "已删除选中行";
    }

    [RelayCommand]
    public void ClearAll()
    {
        Table.Rows.Clear();
        SelectedRow = null;
        RowCount = 0;
        RowsChanged?.Invoke();
        PreviewImage = null;
        PreviewInfo = "清单已清空";
    }

    /// <summary>空白单元格批量填充：固定值 / 序列号 / 复制上一行。</summary>
    public int FillBlanks(string columnKey, BatchFillMode mode, string fixedValue, string prefix, int start, int step, int digits)
    {
        if (!Table.Columns.Contains(columnKey)) return 0;
        var filled = 0;
        var seq = start;
        string? lastValue = null;

        foreach (DataRow row in Table.Rows)
        {
            var current = row[columnKey]?.ToString();
            var isBlank = string.IsNullOrWhiteSpace(current);
            if (!isBlank)
            {
                lastValue = current;
                continue;
            }

            switch (mode)
            {
                case BatchFillMode.Fixed:
                    row[columnKey] = fixedValue;
                    break;
                case BatchFillMode.Sequence:
                    row[columnKey] = prefix + seq.ToString().PadLeft(Math.Max(1, digits), '0');
                    seq += step;
                    break;
                case BatchFillMode.CopyPrevious:
                    if (lastValue != null) row[columnKey] = lastValue;
                    else continue;
                    break;
            }
            filled++;
            lastValue = row[columnKey]?.ToString();
        }
        return filled;
    }

    public void PreviewRow(DataRowView? rowView) => PreviewRow(rowView?.Row);

    public void PreviewRow(DataRow? row)
    {
        if (Doc == null) return;
        if (row == null)
        {
            PreviewImage = null;
            PreviewInfo = "选中一行以预览";
            return;
        }

        try
        {
            var values = EditableVars.ToDictionary(v => v.Key, v => (string?)row[v.Key]?.ToString());
            using var mono = _renderer.RenderMono(Doc, values);
            PreviewImage = ImageUtil.ToBitmapSource(mono);
            PreviewInfo = $"{Doc.WidthMm}×{Doc.HeightMm}mm · {mono.Width}×{mono.Height}px · 203dpi";
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            PreviewInfo = "预览失败：" + ex.Message;
        }
    }

    public List<Dictionary<string, string?>> BuildRows()
    {
        var list = new List<Dictionary<string, string?>>();
        foreach (DataRow row in Table.Rows)
            list.Add(EditableVars.ToDictionary(v => v.Key, v => (string?)row[v.Key]?.ToString()));
        return list;
    }
}

public enum BatchFillMode
{
    Fixed,
    Sequence,
    CopyPrevious,
}
