using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;
using UXLabelshop.Services;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class TemplatesPage : UserControl
{
    private readonly TemplatesViewModel _vm;
    private TemplateService Templates => App.Services.GetRequiredService<TemplateService>();
    private ScanRuleService Rules => App.Services.GetRequiredService<ScanRuleService>();

    public TemplatesPage(TemplatesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) => _vm.Load();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewTemplateDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var userId = App.Services.GetRequiredService<AppSession>().CurrentUser?.Id;
            var id = Templates.CreateTemplate(dlg.Code, dlg.TemplateName, dlg.WidthMm, dlg.HeightMm, dlg.Dpi,
                userId, dlg.Remark);

            string createdMsg;
            if (dlg.Imported != null)
            {
                // .lsd 导入：先把变量写进子表（唯一权威来源），再把元素/画布写成一个新版本
                var built = LsdImportService.BuildDoc(dlg.Imported, dlg.Code, dlg.TemplateName,
                    dlg.WidthMm, dlg.HeightMm, dlg.Dpi);
                Templates.SaveVariables(id, built.Variables);
                Templates.SaveVersion(id, built, userId, $"从 .lsd 导入：{dlg.Imported.FileName}");
                var doc = Templates.LoadDoc(id);
                Rules.EnsureDefaultRule(id, doc.Variables.Select(v => v.Key).ToList(), userId);
                createdMsg = $"已从 .lsd 导入：{built.Variables.Count} 个变量、{built.Elements.Count} 个元素。\n" +
                             "文字内容被 .lsd 压缩块封装，正文按实测幅面重新排版，请在编辑器中按实物微调。\n" +
                             "扫码规则默认禁用，需校准后启用。";
            }
            else
            {
                var doc = Templates.LoadDoc(id);
                Rules.EnsureDefaultRule(id, doc.Variables.Select(v => v.Key).ToList(), userId);
                createdMsg = "模板已创建为草稿，并自动生成了扫码规则（默认禁用，需校准后启用）。";
            }

            _vm.Load();
            _vm.Selected = _vm.Rows.FirstOrDefault(r => r.Id == id);
            MessageBox.Show(createdMsg, "新建模板", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenEditor(id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "新建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        OpenEditor(_vm.Selected.Id);
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Selected != null) OpenEditor(_vm.Selected.Id);
    }

    private void Variables_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        var win = new VariablesWindow(_vm.Selected.Id, _vm.Selected.Name)
        {
            Owner = Window.GetWindow(this),
        };
        win.ShowDialog();
        _vm.Load();
    }

    private void OpenEditor(long templateId)
    {
        var win = new TemplateEditorWindow(templateId) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
        _vm.Load();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        var dlg = new NewTemplateDialog
        {
            Owner = Window.GetWindow(this),
            Title = "复制模板",
        };
        dlg.SetDefaults(_vm.Selected.Code + "_copy", _vm.Selected.Name + " 副本");
        if (dlg.ShowDialog() != true) return;

        try
        {
            Templates.CopyTemplate(_vm.Selected.Id, dlg.Code, dlg.TemplateName,
                App.Services.GetRequiredService<AppSession>().CurrentUser?.Id);
            _vm.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        if (MessageBox.Show($"确认发布模板「{_vm.Selected.Name}」？发布后可扫码打印。", "发布",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        // 发布前要求至少有一条可用规则，且有规则启用
        var rules = Rules.GetByTemplate(_vm.Selected.Id);
        if (rules.Count == 0 || rules.All(r => !r.Enabled))
        {
            MessageBox.Show("该模板还没有启用的扫码规则，请先在「扫码规则」中校准并启用。",
                "无法发布", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Templates.SetStatus(_vm.Selected.Id, "published");
        _vm.Load();
        RefreshScanTemplates();
    }

    /// <summary>同步刷新扫码页的模板下拉（页面是单例，不重建，必须主动通知）。</summary>
    private static void RefreshScanTemplates() =>
        App.Services.GetRequiredService<ScanPrintViewModel>().RefreshTemplates();

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        Templates.SetStatus(_vm.Selected.Id, "disabled");
        _vm.Load();
        RefreshScanTemplates();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        if (_vm.Selected.Status != "draft")
        {
            MessageBox.Show("只有草稿模板可以删除；已发布模板请使用「停用」。");
            return;
        }
        if (MessageBox.Show($"确认删除草稿「{_vm.Selected.Name}」及其全部版本？", "删除",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Templates.DeleteDraft(_vm.Selected.Id);
        _vm.Load();
    }

    private void Rules_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected == null) { MessageBox.Show("请选择模板"); return; }
        ScanRuleDialog dlg;
        try
        {
            dlg = new ScanRuleDialog(_vm.Selected.Id) { Owner = Window.GetWindow(this) };
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "打开规则失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        dlg.ShowDialog();
        _vm.Load();
    }
}
