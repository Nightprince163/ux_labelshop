using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using UXLabelshop.Rendering;

namespace UXLabelshop.Views;

public partial class VarDialog : Window
{
    public string VarKey { get; private set; } = "";
    public string VarLabel { get; private set; } = "";
    public string VarSample { get; private set; } = "";
    public string VarDefault { get; private set; } = "";
    public string VarValue { get; private set; } = "";
    public string VarFormula { get; private set; } = "";
    public string VarSource { get; private set; } = "scan";
    public string? OriginalKey { get; private set; }

    private sealed class TestRow
    {
        public string Key { get; init; } = "";
        public string? Value { get; set; }
    }

    private readonly ObservableCollection<TestRow> _testRows = new();
    private readonly List<TemplateVar> _otherVars;
    private bool _suppressEvents;

    /// <summary>新增 / 编辑共用。existing 为 null 表示新增。</summary>
    public VarDialog(TemplateVar? existing, IEnumerable<TemplateVar> allVars)
    {
        InitializeComponent();
        _otherVars = allVars.Where(v => existing == null || v.Key != existing.Key)
            .Where(v => !string.IsNullOrWhiteSpace(v.Key)).ToList();
        TestInputs.ItemsSource = _testRows;
        VarCombo.ItemsSource = _otherVars;

        _suppressEvents = true;
        SourceCombo.SelectedIndex = 0;
        if (existing != null)
        {
            OriginalKey = existing.Key;
            TitleText.Text = $"编辑变量：{existing.Key}";
            KeyBox.Text = existing.Key;
            LabelBox.Text = existing.Label ?? "";
            SampleBox.Text = existing.Sample ?? "";
            DefaultBox.Text = existing.DefaultValue ?? "";
            ValueBox.Text = existing.Value ?? "";
            FormulaBox.Text = existing.Formula ?? "";
            SourceCombo.SelectedIndex = existing.EffectiveSource switch
            {
                "fixed" => 1,
                "computed" => 2,
                _ => 0,
            };
        }
        _suppressEvents = false;

        ApplySourceVisibility();
        RebuildTestRows();
    }

    private string CurrentSource => (SourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "scan";

    private void ApplySourceVisibility()
    {
        var src = CurrentSource;
        var isComputed = src == "computed";
        var isFixed = src == "fixed";
        DefaultBox.IsEnabled = !isComputed && !isFixed;
        ValueBox.IsEnabled = isFixed;
        FormulaBox.IsEnabled = isComputed;
        FuncCombo.IsEnabled = isComputed;
        VarCombo.IsEnabled = isComputed;
        DefaultLabel.Foreground = DefaultBox.IsEnabled ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.Gray;
        ValueLabel.Foreground = ValueBox.IsEnabled ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.Gray;
    }

    private void SourceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        ApplySourceVisibility();
        ResultText.Text = "";
    }

    private void KeyBox_TextChanged(object sender, TextChangedEventArgs e) => ResultText.Text = "";

    /// <summary>按公式里出现的变量引用生成测试值输入框，默认值取该变量的 sample。</summary>
    private void RebuildTestRows()
    {
        var existing = _testRows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        _testRows.Clear();
        foreach (var dep in FormulaEngine.Dependencies(FormulaBox.Text).OrderBy(k => k))
        {
            var v = _otherVars.FirstOrDefault(x => string.Equals(x.Key, dep, StringComparison.OrdinalIgnoreCase));
            var fallback = v?.Sample ?? v?.DefaultValue ?? "";
            _testRows.Add(new TestRow
            {
                Key = dep,
                Value = existing.TryGetValue(dep, out var old) ? old : fallback,
            });
        }
    }

    private void FuncCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || FuncCombo.SelectedItem is not ComboBoxItem item) return;
        var snippet = item.Tag?.ToString() ?? item.Content?.ToString() ?? "";
        InsertAtCursor(snippet);
        _suppressEvents = true;
        FuncCombo.SelectedIndex = -1;
        _suppressEvents = false;
    }

    private void VarCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || VarCombo.SelectedItem is not TemplateVar v) return;
        InsertAtCursor("{{" + v.Key + "}}");
        _suppressEvents = true;
        VarCombo.SelectedIndex = -1;
        _suppressEvents = false;
    }

    private void InsertAtCursor(string text)
    {
        var pos = FormulaBox.SelectionStart;
        var len = FormulaBox.SelectionLength;
        FormulaBox.Text = FormulaBox.Text.Remove(pos, len).Insert(pos, text);
        FormulaBox.SelectionStart = pos + text.Length;
        FormulaBox.Focus();
        RebuildTestRows();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FormulaBox.Text))
        {
            ResultText.Text = "请先填写公式";
            ResultText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }
        RebuildTestRows();
        var values = _testRows.ToDictionary(r => r.Key, r => (string?)r.Value);
        foreach (var v in _otherVars)
            if (!values.ContainsKey(v.Key)) values[v.Key] = v.Sample ?? v.DefaultValue ?? "";
        try
        {
            var result = FormulaEngine.EvaluateStrict(FormulaBox.Text, values);
            ResultText.Text = "结果：" + result;
            ResultText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (FormulaException ex)
        {
            ResultText.Text = "错误：" + ex.Message;
            ResultText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-]+$"))
        {
            MessageBox.Show("变量 key 只能包含字母、数字、下划线、短横线", "提示");
            return;
        }

        VarSource = CurrentSource;
        var formula = FormulaBox.Text.Trim();
        if (VarSource == "computed")
        {
            if (string.IsNullOrWhiteSpace(formula))
            {
                MessageBox.Show("「公式计算」来源的变量必须填写公式。", "提示");
                return;
            }
            var err = FormulaEngine.Validate(formula);
            if (err != null)
            {
                MessageBox.Show("公式语法有误：\n" + err, "公式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            formula = "";
        }

        VarKey = key;
        VarLabel = string.IsNullOrWhiteSpace(LabelBox.Text) ? key : LabelBox.Text.Trim();
        VarSample = SampleBox.Text.Trim();
        VarDefault = DefaultBox.Text.Trim();
        VarValue = ValueBox.Text.Trim();
        VarFormula = formula;
        DialogResult = true;
    }
}
