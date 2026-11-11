using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using UXLabelshop.Services;

namespace UXLabelshop.Views;

public partial class NewTemplateDialog : Window
{
    public string Code { get; private set; } = "";
    public string TemplateName { get; private set; } = "";
    public double WidthMm { get; private set; } = 100;
    public double HeightMm { get; private set; } = 50;
    public int Dpi { get; private set; } = 203;
    public string? Remark { get; private set; }

    /// <summary>从 .lsd 导入时的解析结果；null 表示走普通空白模板创建流程。</summary>
    public LsdImportResult? Imported { get; private set; }

    public NewTemplateDialog() => InitializeComponent();

    public void SetDefaults(string code, string name)
    {
        CodeBox.Text = code;
        NameBox.Text = name;
    }

    private void ImportLsd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "LabelShop 模板 (*.lsd)|*.lsd|所有文件 (*.*)|*.*",
            Title = "选择要导入的 .lsd 模板",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var r = LsdImportService.Parse(dlg.FileName);
            Imported = r;
            // 名称取文件名（.lsd 里的 saved_title 通常是保存日期，不适合当模板名），可手动改
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(r.FileName);
            WidthBox.Text = r.WidthMm.ToString("0.##");
            HeightBox.Text = r.HeightMm.ToString("0.##");
            // .lsd 不记录 dpi，默认 203，用户仍可在下拉里改成 300
            DpiCombo.SelectedIndex = r.Dpi == 300 ? 1 : 0;
            if (string.IsNullOrWhiteSpace(CodeBox.Text))
                CodeBox.Text = MakeCodeFromName(NameBox.Text);
            ImportInfo.Text = r.InfoText + "\n已就绪：填好模板编码后点「创建」即可生成模板。";
            ImportInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A));
            CodeBox.Focus();
        }
        catch (System.Exception ex)
        {
            Imported = null;
            ImportInfo.Text = "导入失败：" + ex.Message;
            ImportInfo.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            MessageBox.Show(this, "无法解析该 .lsd 文件：\n" + ex.Message,
                "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>从文件名推一个合法的编码建议值（用户仍可改）。</summary>
    private static string MakeCodeFromName(string name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_\-]", "_");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"_+", "_").Trim('_');
        return cleaned.Length > 0 ? cleaned.ToLowerInvariant() : "";
    }

    /// <summary>自检用（窗口 Show 之后调用）：确认「创建」按钮完整落在窗口客户区内。</summary>
    internal void VerifyButtonsVisibleForTest(List<string> lines)
    {
        UpdateLayout();
        Button? create = null;
        void Walk(DependencyObject node)
        {
            if (create != null) return;
            if (node is Button b && b.Content as string == "创建") { create = b; return; }
            var n = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(this);

        if (create == null) { lines.Add("[FAIL] 新建模板对话框：找不到「创建」按钮"); return; }
        var root = Content as FrameworkElement;
        if (root == null) { lines.Add("[FAIL] 新建模板对话框：内容根为空"); return; }
        // 用同一坐标系（内容根）比较，避免标题栏偏移造成误判
        var pos = create.TransformToVisual(root).Transform(new Point(0, 0));
        var client = root.ActualHeight;
        var visible = pos.Y >= 0 && pos.Y + create.ActualHeight <= client + 1;
        lines.Add($"[CHECK] 「创建」按钮 Y={pos.Y:0}，高={create.ActualHeight:0}，内容高={client:0}：" +
                  $"{(visible ? "完全可见（通过）" : "被裁切（未通过）")}");
        lines.Add($"[INFO] 窗口 ActualWidth={ActualWidth:0}/Height={ActualHeight:0}，" +
                  $"内容根 {root.ActualWidth:0}×{root.ActualHeight:0}，" +
                  $"按钮 X={pos.X:0}");
        if (!visible) lines.Add("[FAIL] 新建模板对话框底部按钮被裁切");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (code.Length == 0 || name.Length == 0)
        {
            MessageBox.Show("请填写编码和名称");
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Za-z0-9_\-]+$"))
        {
            MessageBox.Show("编码只能包含字母、数字、下划线和短横线");
            return;
        }
        if (!double.TryParse(WidthBox.Text, out var w) || w is < 10 or > 300)
        {
            MessageBox.Show("宽度需为 10~300mm 的数字");
            return;
        }
        if (!double.TryParse(HeightBox.Text, out var h) || h is < 10 or > 300)
        {
            MessageBox.Show("高度需为 10~300mm 的数字");
            return;
        }

        Code = code;
        TemplateName = name;
        WidthMm = w;
        HeightMm = h;
        Dpi = ((System.Windows.Controls.ComboBoxItem)DpiCombo.SelectedItem).Content.ToString() == "300" ? 300 : 203;
        Remark = string.IsNullOrWhiteSpace(RemarkBox.Text)
            ? (Imported != null ? $"由 .lsd 导入：{Imported.FileName}" : null)
            : RemarkBox.Text.Trim();
        DialogResult = true;
    }
}
