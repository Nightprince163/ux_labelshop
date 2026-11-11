using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class PrintRecordsPage : UserControl
{
    private readonly PrintRecordsViewModel _vm;

    public PrintRecordsPage(PrintRecordsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) =>
        {
            if (_vm.Records.Count == 0) _vm.SearchCommand.Execute(null);
        };
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Records.Count == 0)
        {
            MessageBox.Show("没有可导出的记录，请先查询。");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"打印记录-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        _vm.ExportCsv(dlg.FileName);
        MessageBox.Show("已导出：" + dlg.FileName, "导出完成");
    }
}
