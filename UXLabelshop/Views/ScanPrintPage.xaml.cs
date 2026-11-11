using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.ViewModels;
using UXLabelshop.Views;

namespace UXLabelshop.Views;

public partial class ScanPrintPage : UserControl
{
    private readonly ScanPrintViewModel _vm;
    private bool _previewVisible = true;

    /// <summary>单元格变更后延迟 120ms 重绘预览，避免逐字输入时每敲一个键都渲染一次标签。</summary>
    private readonly DispatcherTimer _previewTimer;
    private DataRow? _pendingPreviewRow;

    /// <summary>自检用：分别统计「切模板后请求聚焦扫码框」与「预览实际重绘」次数。</summary>
    internal int ScanFocusRequestsForTest { get; private set; }
    internal int PreviewRefreshCountForTest { get; private set; }
    internal DataRow? LastPreviewRowForTest { get; private set; }

    public ScanPrintPage(ScanPrintViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.TemplateChanged += () =>
        {
            RebuildColumns();
            FocusScanBox();          // 切完模板立刻把光标放回扫码框，可以直接扫下一张
        };
        _vm.RowsChanged += () => DataGridRows.ItemsSource = _vm.Table.DefaultView;

        // 单元格值一变（手输、公式重算、批量填充）就排队刷新预览，不依赖切换行
        _vm.Table.ColumnChanged += (_, e) => SchedulePreviewRefresh(e.Row);
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            var row = _pendingPreviewRow;
            _pendingPreviewRow = null;
            RefreshPreview(row);
        };

        if (!App.IsSelfTest)
        {
            _vm.ConfirmTemplateSwitch = msg => MessageBox.Show(
                Window.GetWindow(this), msg, "切换模板",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
                == MessageBoxResult.OK;
        }
        Loaded += (_, _) =>
        {
            RebuildColumns();
            DataGridRows.ItemsSource = _vm.Table.DefaultView;
            ScanBox.Focus();
        };

        // Ctrl+点击列标题 → 向下填充（与原 Web 版交互一致）
        var headerStyle = new Style(typeof(DataGridColumnHeader))
        {
            BasedOn = (Style)FindResource(typeof(DataGridColumnHeader)),
        };
        headerStyle.Setters.Add(new EventSetter(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnHeaderMouseLeftButtonDown)));
        DataGridRows.ColumnHeaderStyle = headerStyle;

        // 每行单独建 ContextMenu：放进 XAML 的 RowStyle 里会因共享元素导致 XamlParseException
        DataGridRows.LoadingRow += (_, e) => e.Row.ContextMenu = BuildRowMenu(e.Row);
    }

    private ContextMenu BuildRowMenu(DataGridRow row)
    {
        var menu = new ContextMenu();
        var one = new MenuItem { Header = "重算本行公式列" };
        one.Click += (_, _) => RecomputeOne(row.DataContext as DataRowView);
        var all = new MenuItem { Header = "重算全部行的公式列" };
        all.Click += (_, _) =>
        {
            _vm.RecomputeAllRows();
            _vm.StatusMessage = "已按公式重算全部行的计算列";
        };
        menu.Items.Add(one);
        menu.Items.Add(all);
        return menu;
    }

    private void RecomputeOne(DataRowView? drv)
    {
        if (drv == null) return;
        _vm.RecomputeRow(drv.Row, suppressEvents: true);
        _vm.StatusMessage = "已按公式重算该行计算列";
        _vm.PreviewRow(drv.Row);
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridColumnHeader header || header.Column is not DataGridTextColumn column) return;

        var key = (column.Binding as Binding)?.Path.Path;
        if (key == null) return;
        key = key.TrimStart('[').TrimEnd(']');

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            var label = _vm.EditableVars.FirstOrDefault(v => v.Key == key)?.Label ?? key;
            var filled = _vm.FillBlanks(key, BatchFillMode.CopyPrevious, "", "", 1, 1, 0);
            _vm.StatusMessage = filled > 0
                ? $"「{label}」已向下填充 {filled} 个空白格"
                : $"「{label}」没有可填充的空白格";
        }
        // 不带 Ctrl：不处理（列排序已禁用，单击标题不做任何事）
    }

    private void RebuildColumns()
    {
        DataGridRows.Columns.Clear();
        foreach (var v in _vm.EditableVars)
        {
            var col = new DataGridTextColumn
            {
                Header = (string.IsNullOrEmpty(v.Label) ? v.Key : v.Label) + (v.IsComputed ? " · 公式" : ""),
                Binding = new Binding($"[{v.Key}]") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells),
                MinWidth = 90,
                // 列绑定是 DataTable 的 [key] 路径，排序会去反射同名属性而抛异常，
                // 清单本身也不需要排序（顺序即打印顺序），这里彻底关掉
                CanUserSort = false,
                // 编辑用的 TextBox 单独关输入法：InputMethod.IsInputMethodEnabled 不会从 DataGrid 继承下去
                EditingElementStyle = NoImeEditStyle,
            };
            if (v.IsComputed)
            {
                // 计算列用蓝底区分「公式生成」与「扫码录入」，但仍可手动编辑。
                // 选中整行时数据行会把前景刷成白字，所以选中态必须换成更深一档的蓝，
                // 既保住白字的可读性，又比行选中色再深一点，公式列不至于淹没在选中行里；
                // 但网格失去焦点时行选中色会退成浅灰，这时让单元格完全跟随行色，免得突兀。
                col.MinWidth = 140; // 容纳「· 公式」标记，避免表头被截断
                col.CellStyle = new Style(typeof(DataGridCell))
                {
                    Setters =
                    {
                        new Setter(Control.BackgroundProperty, Frozen(Color.FromRgb(0xBB, 0xD4, 0xF0))),
                    },
                    Triggers =
                    {
                        new DataTrigger
                        {
                            Binding = Ancestor(nameof(DataGridRow.IsSelected), typeof(DataGridRow)),
                            Value = true,
                            Setters =
                            {
                                new Setter(Control.BackgroundProperty, Frozen(Color.FromRgb(0x1F, 0x5F, 0xA8))),
                                new Setter(Control.ForegroundProperty, Frozen(Colors.White)),
                            },
                        },
                        // 后写的触发器优先级更高：网格失去焦点时，行选中色会退成浅灰，
                        // 这时单元格也用同样的浅灰，免得一整行浅灰里孤零零一个深蓝格
                        new MultiDataTrigger
                        {
                            Conditions =
                            {
                                new Condition
                                {
                                    Binding = Ancestor(nameof(DataGridRow.IsSelected), typeof(DataGridRow)),
                                    Value = true,
                                },
                                new Condition
                                {
                                    Binding = Ancestor(nameof(UIElement.IsKeyboardFocusWithin), typeof(DataGrid)),
                                    Value = false,
                                },
                            },
                            Setters =
                            {
                                new Setter(Control.BackgroundProperty, Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0))),
                                new Setter(Control.ForegroundProperty, Frozen(Colors.Black)),
                            },
                        },
                    },
                };
            }
            DataGridRows.Columns.Add(col);
        }
        DataGridRows.ItemsSource = _vm.Table.DefaultView;
    }

    /// <summary>已冻结的画刷：避免每帧新建对象，也满足 WPF 跨线程使用的要求。</summary>
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>清单单元格编辑框：禁用输入法（标签内容不会有中文，中文输入法会吞掉扫码枪字符）。</summary>
    private static readonly Style NoImeEditStyle = CreateNoImeEditStyle();

    private static Style CreateNoImeEditStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(InputMethod.IsInputMethodEnabledProperty, false));
        style.Seal();
        return style;
    }

    /// <summary>绑定到可视化祖先（DataGridRow / DataGrid）上的属性。</summary>
    private static Binding Ancestor(string path, Type ancestorType) => new(path)
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, ancestorType, 1),
    };

    private void ScanBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _vm.ScanCommand.Execute(null);
        ScanBox.Focus();
        // 只有真正新增了行才把选中行跳到末行；短码补录要保持当前选中行，
        // 否则连扫多个补录码时会被跳走、补错行。
        if (_vm.LastScanAddedRow && DataGridRows.Items.Count > 0)
        {
            var last = DataGridRows.Items[DataGridRows.Items.Count - 1];
            DataGridRows.SelectedItem = last;
            DataGridRows.ScrollIntoView(last);
        }
    }

    /// <summary>切模板后把焦点交给扫码框。延后一拍执行，等下拉框自己的选焦逻辑收尾。</summary>
    private void FocusScanBox()
    {
        ScanFocusRequestsForTest++;
        if (App.IsSelfTest) { ScanBox.Focus(); return; }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ScanBox.Focus()));
    }

    private void SchedulePreviewRefresh(DataRow? editedRow = null)
    {
        if (_previewTimer == null) return;
        if (App.IsSelfTest) { RefreshPreview(editedRow); return; }   // 自检不走计时器，立刻刷新便于断言
        _pendingPreviewRow = editedRow;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshSelectedPreview() => RefreshPreview(null);

    /// <summary>
    /// 重绘预览：优先用「刚被编辑的行」，其次当前选中行，最后退回第一行
    /// （清单只有一行且从未选中时也能刷新，正是原来预览刷不出来的场景）。
    /// </summary>
    private void RefreshPreview(DataRow? edited)
    {
        PreviewRefreshCountForTest++;
        var row = edited
                  ?? (DataGridRows.SelectedItem as DataRowView)?.Row
                  ?? (DataGridRows.Items.Count > 0 ? (DataGridRows.Items[0] as DataRowView)?.Row : null);
        LastPreviewRowForTest = row;
        _vm.PreviewRow(row);
    }

    private void DataGridRows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedRow = (DataGridRows.SelectedItem as DataRowView)?.Row;
        RefreshSelectedPreview();
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        _vm.DeleteRow(DataGridRows.SelectedItem as DataRowView);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Table.Rows.Count == 0) return;
        if (MessageBox.Show($"确认清空 {_vm.Table.Rows.Count} 行清单？", "清空", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) == MessageBoxResult.OK)
            _vm.ClearAllCommand.Execute(null);
    }

    private void BatchFill_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.EditableVars.Count == 0)
        {
            _vm.StatusMessage = "请先选择模板";
            return;
        }
        var dlg = new BatchFillDialog(_vm.EditableVars) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.ColumnKey == null) return;

        var filled = _vm.FillBlanks(dlg.ColumnKey, dlg.Mode, dlg.FixedValue, dlg.Prefix, dlg.Start, dlg.Step, dlg.Digits);
        _vm.StatusMessage = filled == 0 ? "该列没有空白单元格，未做修改。" : $"已填充 {filled} 个空白单元格。";
        DataGridRows.Items.Refresh();
    }

    private void TogglePreview_Click(object sender, RoutedEventArgs e)
    {
        _previewVisible = !_previewVisible;
        if (_previewVisible)
        {
            PreviewPanel.Visibility = Visibility.Visible;
            PreviewSplitter.IsEnabled = true;
            PreviewColumn.MinWidth = 240;
            PreviewColumn.Width = new GridLength(360);
            PreviewSplitterCol.Width = new GridLength(6);
            PreviewToggleButton.Content = "隐藏预览";
        }
        else
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewSplitter.IsEnabled = false;
            PreviewColumn.MinWidth = 0;
            PreviewColumn.Width = new GridLength(0);
            PreviewSplitterCol.Width = new GridLength(0);
            PreviewToggleButton.Content = "显示预览";
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Doc == null || _vm.CurrentTemplate == null)
        {
            _vm.StatusMessage = "请先选择模板";
            return;
        }
        if (_vm.Table.Rows.Count == 0)
        {
            _vm.StatusMessage = "清单为空，请先扫码";
            return;
        }

        var win = App.Services.GetRequiredService<PrintPreviewWindow>();
        win.Initialize(_vm.Doc, _vm.CurrentTemplate.Id, _vm.CurrentTemplate.Code, _vm.CurrentTemplate.Name,
            _vm.BuildRows());
        win.Owner = Window.GetWindow(this);
        win.ShowDialog();
    }
}
