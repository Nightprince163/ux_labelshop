using System.Windows;
using UXLabelshop.Rendering;
using UXLabelshop.Services;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly PrintService _printService;
    private readonly ConfigService _config;
    private PrintPreviewViewModel? _vm;

    public PrintPreviewWindow(PrintService printService, ConfigService config)
    {
        InitializeComponent();
        _printService = printService;
        _config = config;
    }

    public void Initialize(TemplateDoc doc, long templateId, string templateCode, string templateName,
        List<Dictionary<string, string?>> rows)
    {
        _vm = new PrintPreviewViewModel(_printService, _config, doc, templateId, templateCode, templateName, rows);
        DataContext = _vm;
        _vm.BuildPreviews();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // 两个 RadioButton 绑定各自属性，需手动互斥
        if (_vm.PrintToPrinter) _vm.ExportToFile = false;
        else if (_vm.ExportToFile) _vm.PrintToPrinter = false;

        if (_vm.PrintToPrinter && _vm.SelectedPrinter == null && _vm.Printers.Count > 0)
            _vm.SelectedPrinter = _vm.Printers[0];
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 PNG 导出目录",
            SelectedPath = _vm.ExportDir,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _vm.ExportDir = dlg.SelectedPath;
    }
}
