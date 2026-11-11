using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;
using UXLabelshop.Services;

namespace UXLabelshop.Views;

public partial class VariablesWindow : Window
{
    private readonly long _templateId;
    private readonly TemplateService _templates;
    private readonly ObservableCollection<VarItem> _items = new();
    private readonly List<(string Old, string New)> _renames = new();
    private bool _dirty;

    private sealed class VarItem
    {
        public string Key { get; set; } = "";
        public string? Label { get; set; }
        public string? Short { get; set; }
        public string Source { get; set; } = "scan";
        public string? DefaultValue { get; set; }
        public string? Formula { get; set; }
        public string? Sample { get; set; }
        public string? Value { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
        public string SourceText => Source switch
        {
            "fixed" => "固定值",
            "computed" => "公式计算",
            _ => "扫码录入",
        };
        public string RuleText => Source == "computed" ? (Formula ?? "")
            : Source == "fixed" ? ("固定值：" + (Value ?? "")) : "";
    }

    public VariablesWindow(long templateId, string templateName)
    {
        InitializeComponent();
        _templateId = templateId;
        _templates = App.Services.GetRequiredService<TemplateService>();
        TitleText.Text = $"变量管理 · {templateName}";
        Title = $"变量管理 · {templateName}";
        VarGrid.ItemsSource = _items;
        Load();
    }

    private void Load()
    {
        _items.Clear();
        _renames.Clear();
        foreach (var v in _templates.GetVariables(_templateId))
            _items.Add(new VarItem
            {
                Key = v.Key,
                Label = v.Label,
                Short = v.Short,
                Source = v.EffectiveSource,
                DefaultValue = v.DefaultValue,
                Formula = v.Formula,
                Sample = v.Sample,
                Value = v.Value,
                Extra = v.Extra,
            });
        StatusText.Text = $"共 {_items.Count} 个变量（双击行可编辑）";
        _dirty = false;
    }

    private VarItem? Selected => VarGrid.SelectedItem as VarItem;

    private TemplateVar ToVar(VarItem i) => new()
    {
        Key = i.Key,
        Label = i.Label,
        Short = i.Short,
        Source = i.Source,
        DefaultValue = i.DefaultValue,
        Formula = i.Formula,
        Sample = i.Sample,
        Value = i.Value,
        Extra = i.Extra,
        SortNo = _items.IndexOf(i),
    };

    private List<TemplateVar> AllVars() => _items.Select(ToVar).ToList();

    private void Add_Click(object sender, RoutedEventArgs e) => EditItem(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { MessageBox.Show("请先选中一行"); return; }
        EditItem(Selected);
    }

    private void VarGrid_DoubleClick(object sender, MouseButtonEventArgs e) => EditItem(Selected);

    private void EditItem(VarItem? item)
    {
        var existing = item == null ? null : ToVar(item);
        var dlg = new VarDialog(existing, AllVars()) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // key 唯一性
        var dup = _items.FirstOrDefault(x => !ReferenceEquals(x, item) &&
            string.Equals(x.Key, dlg.VarKey, StringComparison.OrdinalIgnoreCase));
        if (dup != null)
        {
            MessageBox.Show($"变量 key「{dlg.VarKey}」已存在。", "提示");
            return;
        }

        var target = item ?? new VarItem();
        var oldKey = item?.Key;
        target.Key = dlg.VarKey;
        target.Label = dlg.VarLabel;
        target.Sample = dlg.VarSample;
        target.DefaultValue = dlg.VarDefault;
        target.Value = dlg.VarValue;
        target.Formula = dlg.VarFormula;
        target.Source = dlg.VarSource;

        if (item == null) _items.Add(target);

        // 改 key：记录待级联替换，并同步改写其他变量公式里的引用
        if (!string.IsNullOrEmpty(oldKey) && !string.Equals(oldKey, dlg.VarKey, StringComparison.Ordinal))
        {
            _renames.RemoveAll(r => r.New == oldKey);
            _renames.Add((oldKey, dlg.VarKey));
            foreach (var other in _items)
            {
                if (ReferenceEquals(other, target) || string.IsNullOrEmpty(other.Formula)) continue;
                other.Formula = VariableResolver.RenameRefs(other.Formula, oldKey, dlg.VarKey);
            }
        }

        _dirty = true;
        VarGrid.Items.Refresh();
        VarGrid.SelectedItem = target;
        StatusText.Text = $"已{(item == null ? "新增" : "修改")}变量 {dlg.VarKey}（未保存）";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) { MessageBox.Show("请先选中一行"); return; }

        var refs = _templates.FindVariableReferences(_templateId, item.Key);
        var msg = $"确定删除变量「{item.Key}」吗？";
        if (refs.Count > 0)
            msg += $"\n\n它被以下位置引用（删除后这些引用会显示为空）：\n· " +
                   string.Join("\n· ", refs.Take(10)) + (refs.Count > 10 ? $"\n· …另有 {refs.Count - 10} 处" : "");
        if (MessageBox.Show(msg, "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _items.Remove(item);
        _dirty = true;
        StatusText.Text = $"已删除变量 {item.Key}（未保存）";
    }

    private void Move(int delta)
    {
        var item = Selected;
        if (item == null) return;
        var i = _items.IndexOf(item);
        var j = i + delta;
        if (j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        _dirty = true;
        VarGrid.SelectedItem = item;
        StatusText.Text = $"已调整「{item.Key}」顺序（未保存）";
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        foreach (var v in _items.Where(x => x.Source == "computed"))
        {
            if (string.IsNullOrWhiteSpace(v.Formula)) { errors.Add($"{v.Key}: 公式为空"); continue; }
            var err = FormulaEngine.Validate(v.Formula);
            if (err != null) errors.Add($"{v.Key}: {err}");
        }
        var resolver = new VariableResolver(AllVars());
        if (resolver.ValidationError != null) errors.Add(resolver.ValidationError);

        // 引用了不存在的变量也提示
        var keys = new HashSet<string>(_items.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var v in _items.Where(x => x.Source == "computed"))
            foreach (var dep in FormulaEngine.Dependencies(v.Formula))
                if (!keys.Contains(dep)) errors.Add($"{v.Key}: 公式引用了不存在的变量 {{{{{dep}}}}}");

        StatusText.Text = errors.Count == 0
            ? $"校验通过：{_items.Count(x => x.Source == "computed")} 条公式，无语法错误与循环依赖"
            : "发现问题：\n" + string.Join("\n", errors);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 保存前统一校验
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _items)
        {
            if (!Regex.IsMatch(v.Key, @"^[A-Za-z0-9_\-]+$"))
            { MessageBox.Show($"变量 key「{v.Key}」不合法", "提示"); return; }
            if (!keys.Add(v.Key))
            { MessageBox.Show($"变量 key「{v.Key}」重复", "提示"); return; }
            if (v.Source == "computed")
            {
                var err = FormulaEngine.Validate(v.Formula);
                if (err != null)
                { MessageBox.Show($"{v.Key} 公式有误：{err}", "公式错误"); return; }
            }
        }
        var resolver = new VariableResolver(AllVars());
        if (resolver.ValidationError != null)
        {
            MessageBox.Show(resolver.ValidationError, "公式错误");
            return;
        }

        try
        {
            var userId = App.Services.GetRequiredService<AppSession>().CurrentUser?.Id;
            _templates.SaveVariables(_templateId, AllVars());

            // 改 key 的级联：画布正文 + 扫码规则映射
            var renamed = 0;
            foreach (var (oldKey, newKey) in _renames)
                renamed += _templates.ApplyVariableRename(_templateId, oldKey, newKey, userId);

            Load();
            StatusText.Text = $"已保存（{DateTime.Now:HH:mm:ss}）" + (renamed > 0 ? $"，同步替换 {renamed} 处引用" : "");
            MessageBox.Show("变量已保存。" + (renamed > 0 ? $"\n已同步替换 {renamed} 处正文/规则引用。" : ""),
                "保存成功");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && MessageBox.Show("有未保存的改动，确定放弃吗？", "提示",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        DialogResult = false;
    }
}
