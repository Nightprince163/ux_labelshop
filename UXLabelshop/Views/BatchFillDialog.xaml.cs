using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UXLabelshop.Rendering;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class BatchFillDialog : Window
{
    private class ColumnItem
    {
        public string Key { get; init; } = "";
        public string Header { get; init; } = "";
    }

    public string? ColumnKey { get; private set; }
    public BatchFillMode Mode { get; private set; } = BatchFillMode.Fixed;
    public string FixedValue { get; private set; } = "";
    public string Prefix { get; private set; } = "";
    public int Start { get; private set; } = 1;
    public int Step { get; private set; } = 1;
    public int Digits { get; private set; } = 3;

    public BatchFillDialog(List<TemplateVar> vars)
    {
        InitializeComponent();
        ColumnCombo.ItemsSource = vars
            .Select(v => new ColumnItem { Key = v.Key, Header = string.IsNullOrEmpty(v.Label) ? v.Key : v.Label })
            .ToList();
        ColumnCombo.SelectedIndex = 0;
        Mode_Changed(null, null!);
    }

    private void Mode_Changed(object? sender, RoutedEventArgs e)
    {
        if (FixedValueBox == null) return;
        var seq = ModeSeq.IsChecked == true;
        var fixedMode = ModeFixed.IsChecked == true;
        FixedValueBox.IsEnabled = fixedMode;
        PrefixBox.IsEnabled = seq;
        StartBox.IsEnabled = seq;
        StepBox.IsEnabled = seq;
        DigitsBox.IsEnabled = seq;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnCombo.SelectedItem is not ColumnItem col)
        {
            MessageBox.Show("请选择填充列");
            return;
        }
        ColumnKey = col.Key;

        if (ModeFixed.IsChecked == true)
        {
            Mode = BatchFillMode.Fixed;
            FixedValue = FixedValueBox.Text;
            if (FixedValue.Length == 0)
            {
                MessageBox.Show("请输入固定值");
                return;
            }
        }
        else if (ModeSeq.IsChecked == true)
        {
            Mode = BatchFillMode.Sequence;
            Prefix = PrefixBox.Text;
            if (!int.TryParse(StartBox.Text, out var start) || !int.TryParse(StepBox.Text, out var step) ||
                !int.TryParse(DigitsBox.Text, out var digits) || digits is < 1 or > 12)
            {
                MessageBox.Show("起始值/步长/位数需为数字（位数 1~12）");
                return;
            }
            Start = start;
            Step = step;
            Digits = digits;
        }
        else
        {
            Mode = BatchFillMode.CopyPrevious;
        }

        DialogResult = true;
    }
}
