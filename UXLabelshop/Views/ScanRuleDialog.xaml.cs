using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;
using UXLabelshop.Services;

namespace UXLabelshop.Views;

public partial class ScanRuleDialog : Window
{
    private readonly long _templateId;
    private readonly ScanRuleService _rules = App.Services.GetRequiredService<ScanRuleService>();
    private readonly TemplateService _templates = App.Services.GetRequiredService<TemplateService>();
    private List<LabelScanRule> _list = new();
    private LabelScanRule? _current;

    /// <summary>段号表：样例/分隔符一变就按样例重排，已选变量按段号保留。</summary>
    private readonly ObservableCollection<SeqRow> _rows = new();

    /// <summary>对应变量下拉的数据源：模板变量 + 一个「不映射」空项。</summary>
    public ObservableCollection<VarOption> VarOptions { get; } = new();

    /// <summary>加载规则时批量写控件，避免 TextChanged 把刚读出来的配置冲掉。</summary>
    private bool _loading;

    /// <summary>最近一次加载/保存时的表单快照，关闭时用来判断有没有未保存修改。</summary>
    private string _savedState = "";

    /// <summary>取消切换时把选中项退回，期间忽略 SelectionChanged，别把表单重载成旧规则、抹掉未保存的编辑。</summary>
    private bool _revertingSelection;

    /// <summary>程序主动刷新列表 / 保存后重载时，不做「切换前确认」。</summary>
    private bool _suppressSelectionGuard;

    /// <summary>自检用：替换掉模态确认框，返回 true=继续、false=取消。</summary>
    internal Func<string, bool>? ConfirmDiscardForTest { get; set; }

    public ScanRuleDialog(long templateId)
    {
        InitializeComponent();
        _templateId = templateId;
        DataContext = this;              // 段号表里的下拉要绑到 VarOptions
        SeqGrid.ItemsSource = _rows;
        Closing += OnClosing;

        LoadVarOptions();
        LoadList();
    }

    /// <summary>
    /// 把整张表单（含段号表的变量选择）压成字符串，用于「未保存」判断。
    /// 不含当前选中项：切换规则时 SelectedItem 已经变了，把它算进去会把「没编辑只是点了一下」误判成脏。
    /// </summary>
    private string CaptureState()
    {
        var rows = string.Join("|", _rows.Select(r => $"{r.Seq}\u0002{r.Content}\u0002{r.Key}"));
        return string.Join("\u0001",
            NameBox.Text, KindCombo.SelectedIndex.ToString(), DelimiterBox.Text, MarkerBox.Text,
            PatternBox.Text, MappingBox.Text, PriorityBox.Text,
            EnabledCheck.IsChecked == true ? "1" : "0", SampleBox.Text, rows);
    }

    /// <summary>统一确认框：关闭 / 切换规则 / 新增规则共用一个口径。</summary>
    private bool ConfirmDiscard(string action)
    {
        var msg = $"当前规则「{_current?.Name ?? "新规则"}」有未保存的修改，{action}后会丢失。\n\n确定要继续吗？";
        if (ConfirmDiscardForTest != null) return ConfirmDiscardForTest(msg);
        if (App.IsSelfTest) return true;
        return MessageBox.Show(this, msg, "未保存的修改",
                   MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
               == MessageBoxResult.OK;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsSelfTest) return;
        if (!HasUnsavedChangesForTest) return;
        if (!ConfirmDiscard("关闭")) e.Cancel = true;
    }

    /// <summary>自检用：当前是否有未保存改动（关闭确认框的判断依据）。</summary>
    internal bool HasUnsavedChangesForTest =>
        !string.Equals(CaptureState(), _savedState, StringComparison.Ordinal);

    // ---------------- 变量下拉 ----------------

    private void LoadVarOptions()
    {
        VarOptions.Clear();
        VarOptions.Add(new VarOption { Key = "", Label = "（不映射）" });
        try
        {
            var doc = _templates.LoadDoc(_templateId);
            foreach (var v in doc.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Key)))
                VarOptions.Add(new VarOption
                {
                    Key = v.Key,
                    Label = string.IsNullOrWhiteSpace(v.Label) ? v.Key : $"{v.Key}（{v.Label}）",
                });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取模板变量失败，变量下拉为空");
        }
    }

    /// <summary>规则里存了、但模板上已经没有的变量，补一条提示项，避免保存时被悄悄丢掉。</summary>
    private void EnsureOption(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (VarOptions.Any(o => o.Key == key)) return;
        VarOptions.Add(new VarOption { Key = key, Label = key + "（变量已删除）" });
    }

    // ---------------- 规则列表 ----------------

    private void LoadList(long? selectId = null)
    {
        // 保存/删除后的主动重载会改 ItemsSource 和 SelectedItem，这几次不算「用户切换规则」
        _suppressSelectionGuard = true;
        try
        {
            _list = _rules.GetByTemplate(_templateId);
            RuleList.ItemsSource = null;
            RuleList.ItemsSource = _list;
            if (_list.Count == 0)
            {
                ClearForm();
                return;
            }
            RuleList.SelectedItem = _list.FirstOrDefault(r => r.Id == selectId) ?? _list[0];
        }
        finally
        {
            _suppressSelectionGuard = false;
        }
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection) return;                 // 退回选中项引发的事件：表单保持原样
        if (RuleList.SelectedItem is not LabelScanRule rule) return;

        // 从规则 A 切到 B 之前（含「新增规则」草稿切回已有规则），有未保存改动就先确认
        if (!_suppressSelectionGuard && (_current == null || _current.Id != rule.Id)
            && HasUnsavedChangesForTest && !ConfirmDiscard("切换规则"))
        {
            var keep = _current;
            _revertingSelection = true;
            try { RuleList.SelectedItem = keep; }
            finally { _revertingSelection = false; }
            return;
        }

        _current = rule;

        _loading = true;
        try
        {
            NameBox.Text = rule.Name;
            KindCombo.SelectedIndex = rule.Kind == "regex" ? 1 : 0;
            DelimiterBox.Text = rule.Delimiter ?? "";
            MarkerBox.Text = rule.Marker ?? "";
            PatternBox.Text = rule.Pattern ?? "";
            MappingBox.Text = MapToText(rule.Mapping);
            PriorityBox.Text = rule.Priority.ToString();
            EnabledCheck.IsChecked = rule.Enabled;
            SampleBox.Text = rule.SampleCodes is null
                ? ""
                : (JsonSerializer.Deserialize<List<string>>(rule.SampleCodes)?.FirstOrDefault() ?? "");
        }
        finally
        {
            _loading = false;
        }

        TestResult.Text = "";
        Kind_Changed(null, null!);

        LoadRowsFromJson(rule.SeqMap);   // 先还原已存的映射（可能没有样例）
        RefreshFromSample();             // 有样例再按样例补齐测试内容
        _savedState = CaptureState();    // 刚加载完的状态就是「已保存」
    }

    private void Kind_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (DelimitedPanel == null) return;
        var isRegex = KindCombo.SelectedIndex == 1;
        DelimitedPanel.Visibility = isRegex ? Visibility.Collapsed : Visibility.Visible;
        RegexPanel.Visibility = isRegex ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 段号表 ----------------

    private void LoadRowsFromJson(string? json)
    {
        _rows.Clear();
        if (string.IsNullOrEmpty(json)) return;
        Dictionary<string, string>? map;
        try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return; }
        if (map == null) return;

        foreach (var kv in map.OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : int.MaxValue))
        {
            if (!int.TryParse(kv.Key, out var seq) || seq <= 0) continue;
            EnsureOption(kv.Value);
            _rows.Add(new SeqRow { Seq = seq, Key = kv.Value });
        }
    }

    /// <summary>样例 / 分隔符 / 标记变动时重建段号表；已选变量按段号沿用。</summary>
    private void RefreshFromSample()
    {
        if (_loading) return;

        var sample = SampleBox.Text.Trim();
        var delimiter = DelimiterBox.Text;
        var marker = MarkerBox.Text.Trim();

        if (sample.Length == 0 || delimiter.Length == 0)
        {
            SeqHint.Foreground = Brushes.Gray;
            SeqHint.Text = _rows.Count == 0
                ? "填写「测试样例扫码内容」和「分隔符」后，这里会自动按段拆分。"
                : $"当前有 {_rows.Count} 条段号映射；填上样例后会自动按分隔符拆分。";
            return;
        }

        var old = _rows.GroupBy(r => r.Seq).ToDictionary(g => g.Key, g => g.First().Key);
        var (parts, markerOk) = SplitSample(sample, delimiter, marker);

        _rows.Clear();
        for (var i = 0; i < parts.Count; i++)
        {
            _rows.Add(new SeqRow
            {
                Seq = i + 1,
                Content = parts[i].Trim(),
                FromSample = true,
                Key = old.TryGetValue(i + 1, out var key) ? key : "",
            });
        }

        var mapped = _rows.Count(r => r.Key.Length > 0);
        if (markerOk)
        {
            var tail = marker.Length > 0 ? $"（已去掉首段标记「{marker}」）" : "";
            SeqHint.Foreground = Brushes.Gray;
            SeqHint.Text = $"样例按「{delimiter}」拆出 {parts.Count} 段{tail}，已选变量 {mapped} 个。";
        }
        else
        {
            SeqHint.Foreground = Brushes.OrangeRed;
            SeqHint.Text = $"首段是「{(parts.Count > 0 ? parts[0] : "")}」，不等于标记「{marker}」，" +
                           "按当前配置不会匹配——请核对该标记或样例。";
        }

        if (_rows.Count > 0) SeqGrid.ScrollIntoView(_rows[0]);
    }

    /// <summary>与 ScanRuleService.MatchDelimited 保持同一套拆法：去首尾空段 → 校验并移除标记。</summary>
    private static (List<string> Parts, bool MarkerOk) SplitSample(string raw, string delimiter, string marker)
    {
        var parts = raw.Split(new[] { delimiter }, StringSplitOptions.None).ToList();
        while (parts.Count > 0 && parts[0].Length == 0) parts.RemoveAt(0);
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        if (marker.Length == 0) return (parts, true);
        if (parts.Count == 0 || parts[0].Trim() != marker) return (parts, false);
        parts.RemoveAt(0);
        return (parts, true);
    }

    private void SampleOrDelimiter_Changed(object sender, TextChangedEventArgs e) => RefreshFromSample();

    // ---------------- 表单读写 ----------------

    private static string MapToText(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map == null ? "" : string.Join(Environment.NewLine, map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch
        {
            return "";
        }
    }

    private static string? TextToJson(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || !s.Contains('=')) continue;
            var idx = s.IndexOf('=');
            map[s[..idx].Trim()] = s[(idx + 1)..].Trim();
        }
        return map.Count == 0 ? null : JsonSerializer.Serialize(map);
    }

    /// <summary>段号表 → seq_map JSON（只收已选变量的段）。</summary>
    private string? BuildSeqMapJson()
    {
        var map = new Dictionary<string, string>();
        foreach (var row in _rows.Where(r => r.Key.Length > 0).OrderBy(r => r.Seq))
            map[row.Seq.ToString()] = row.Key;
        return map.Count == 0 ? null : JsonSerializer.Serialize(map);
    }

    private void ClearForm()
    {
        _current = null;
        _loading = true;
        try
        {
            NameBox.Text = "新规则";
            KindCombo.SelectedIndex = 0;
            DelimiterBox.Text = "|";
            MarkerBox.Text = "";
            PatternBox.Text = "";
            MappingBox.Text = "";
            PriorityBox.Text = "50";
            EnabledCheck.IsChecked = false;
            SampleBox.Text = "";
            _rows.Clear();
        }
        finally
        {
            _loading = false;
        }
        TestResult.Text = "";
        RefreshFromSample();
        _savedState = CaptureState();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // 点「新增规则」同样会丢掉当前未保存的编辑，先确认
        if (HasUnsavedChangesForTest && !ConfirmDiscard("新增规则")) return;

        _suppressSelectionGuard = true;
        try { RuleList.SelectedItem = null; }
        finally { _suppressSelectionGuard = false; }
        ClearForm();
        NameBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _current.Id == 0) return;
        if (MessageBox.Show($"删除规则「{_current.Name}」？", "删除", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _rules.DeleteRule(_current.Id);
        LoadList();
    }

    private LabelScanRule BuildFromForm()
    {
        var rule = _current ?? new LabelScanRule { TemplateId = _templateId };
        rule.TemplateId = _templateId;
        rule.Name = NameBox.Text.Trim();
        rule.Kind = KindCombo.SelectedIndex == 1 ? "regex" : "delimited";
        rule.Delimiter = string.IsNullOrEmpty(DelimiterBox.Text) ? null : DelimiterBox.Text;
        rule.Marker = string.IsNullOrWhiteSpace(MarkerBox.Text) ? null : MarkerBox.Text.Trim();
        rule.SeqMap = BuildSeqMapJson();
        rule.Pattern = string.IsNullOrWhiteSpace(PatternBox.Text) ? null : PatternBox.Text.Trim();
        rule.Mapping = TextToJson(MappingBox.Text);
        rule.Priority = int.TryParse(PriorityBox.Text, out var p) ? p : 50;
        rule.Enabled = EnabledCheck.IsChecked == true;
        rule.StripWrap = true;
        rule.SampleCodes = string.IsNullOrWhiteSpace(SampleBox.Text)
            ? null
            : JsonSerializer.Serialize(new List<string> { SampleBox.Text.Trim() });
        return rule;
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        var rule = BuildFromForm();
        var result = _rules.TestMatch(rule, SampleBox.Text.Trim());
        TestResult.Text = result.Matched
            ? "匹配成功：" + string.Join(", ", result.Values.Select(kv => $"{kv.Key}={kv.Value}"))
            : "未匹配：" + result.Message;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("请填写规则名称");
            return;
        }

        var rule = BuildFromForm();

        if (rule.Kind == "delimited")
        {
            if (string.IsNullOrEmpty(rule.Delimiter))
            {
                MessageBox.Show("请填写分隔符");
                return;
            }
            var mapped = _rows.Where(r => r.Key.Length > 0).ToList();
            if (mapped.Count == 0)
            {
                MessageBox.Show("请至少为一个段号选择对应变量");
                return;
            }
            var dup = mapped.GroupBy(r => r.Key).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}（段 {string.Join("、", g.Select(r => r.Seq))}）").ToList();
            if (dup.Count > 0)
            {
                MessageBox.Show("以下变量被多个段号同时使用，请调整：\n" + string.Join("\n", dup),
                    "变量重复", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (rule.Kind == "regex" && string.IsNullOrEmpty(rule.Pattern))
        {
            MessageBox.Show("请填写正则表达式");
            return;
        }

        _rules.SaveRule(rule, App.Services.GetRequiredService<AppSession>().CurrentUser?.Id);
        MessageBox.Show("规则已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadList(rule.Id);
        _savedState = CaptureState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- 行模型 ----------------

    public sealed class SeqRow
    {
        public int Seq { get; init; }
        public string Content { get; init; } = "";
        /// <summary>该行是否来自样例拆分（用来区分「空段」和「还没填样例」）。</summary>
        public bool FromSample { get; init; }
        public string Key { get; set; } = "";

        public string Display => Content.Length > 0 ? Content : FromSample ? "（空段）" : "";
    }

    public sealed class VarOption
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
    }
}
