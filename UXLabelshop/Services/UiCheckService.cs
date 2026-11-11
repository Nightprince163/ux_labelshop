using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;
using UXLabelshop.ViewModels;
using UXLabelshop.Views;

namespace UXLabelshop.Services;

/// <summary>离屏渲染主要界面为 PNG，用于无交互的布局检查。</summary>
public class UiCheckService
{
    private readonly IServiceProvider _sp;

    public UiCheckService(IServiceProvider sp) => _sp = sp;

    public List<string> Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var lines = new List<string>();

        var login = _sp.GetRequiredService<LoginWindow>();
        // 无边框科技风登录窗：1000×620（与 XAML 根 Border 一致，避免截图被拉伸）
        Save(login.Content as FrameworkElement, 1000, 620, Path.Combine(outputDir, "01-login.png"), lines);

        // 登录失败态：错误提示必须可见
        try
        {
            var loginFail = _sp.GetRequiredService<LoginWindow>();
            var failVm = loginFail.DataContext as LoginViewModel;
            if (failVm != null)
            {
                failVm.Username = "admin";
                failVm.LoginCommand.Execute("wrong-password-123");
            }
            Save(loginFail.Content as FrameworkElement, 1000, 620,
                Path.Combine(outputDir, "01b-login-error.png"), lines);
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 01b-login-error.png: {ex.Message}");
        }

        var main = _sp.GetRequiredService<MainWindow>();
        Save(main.Content as FrameworkElement, 1180, 760, Path.Combine(outputDir, "02-main-scan.png"), lines);

        // 页面单独快照：新建实例，避免仍被主窗口 ContentControl 约束宽度
        var scan = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<ScanPrintPage>(_sp);
        Load(scan);
        // 造几行代表性数据：既验证状态栏文案，也验证预览栏与"共 N 张"计数
        try
        {
            var svm = scan.DataContext as ScanPrintViewModel;
            if (svm != null)
            {
                // 切到带公式计算列与缺省值的 ux3320_t，让快照验证「计算列显示 + 自动重算」
                var demo = svm.Templates.FirstOrDefault(t => t.Code == "ux3320_t");
                if (demo != null) svm.SelectedTemplate = demo;
            }
            if (svm != null && svm.EditableVars.Count >= 4)
            {
                var keys = svm.EditableVars.Select(v => v.Key).ToList();
                var samples = new[]
                {
                    new[] { "UX-2026-001", "B2401", "LOT8891", "2026-11-04" },
                    new[] { "UX-2026-002", "B2401", "",         "2026-11-04" },
                    new[] { "UX-2026-003", "B2401", "",         ""           },
                };
                foreach (var s in samples)
                {
                    svm.AddBlankRow();
                    var row = svm.Table.Rows[svm.Table.Rows.Count - 1];
                    for (var i = 0; i < keys.Count && i < s.Length; i++) row[keys[i]] = s[i];
                    // 用真实型号让公式列（SPLIT 取第 1 段）算出可辨认的结果
                    if (keys.Contains("part_no")) row["part_no"] = s[0] == "UX-2026-002" ? "UX3320S_A0_P2_1" : "UX3261_D2_H";
                }
                // 模拟 Ctrl+点击列标题：向下填充空白格
                if (keys.Count >= 3)
                {
                    var filled = svm.FillBlanks(keys[2], BatchFillMode.CopyPrevious, "", "", 1, 1, 0);
                    svm.StatusMessage = $"「{svm.EditableVars[2].Label}」已向下填充 {filled} 个空白格";
                }
                svm.PreviewRow(svm.Table.Rows[0]);
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[WARN] 扫码页示例数据: {ex.Message}");
        }
        Save(scan, 1180, 740, Path.Combine(outputDir, "03-scan.png"), lines);
        var sortable = scan.DataGridRows.Columns.Count(c => c.CanUserSort);
        lines.Add($"[CHECK] 扫码清单排序已禁用：CanUserSortColumns={scan.DataGridRows.CanUserSortColumns}，" +
                  $"可排序列={sortable}/{scan.DataGridRows.Columns.Count}");
        // 中文输入法会吞掉扫码枪的连续字符，扫码框与清单单元格必须关掉输入法
        var scanIme = InputMethod.GetIsInputMethodEnabled(scan.ScanBox);
        var editImeOff = scan.DataGridRows.Columns.OfType<DataGridTextColumn>().All(c =>
            c.EditingElementStyle != null && c.EditingElementStyle.Setters.OfType<Setter>().Any(
                x => x.Property == InputMethod.IsInputMethodEnabledProperty && Equals(x.Value, false)));
        lines.Add($"[CHECK] 扫码页输入法已禁用：扫码框={(scanIme ? "未禁用" : "已禁用")}，" +
                  $"单元格编辑框={(editImeOff ? "已禁用" : "未禁用")}");
        lines.Add($"[INFO] scan ActualSize={scan.ActualWidth}x{scan.ActualHeight}");

        // 计算列特写：ux3320_t 有 12 列，默认视口会把最右侧的公式列裁掉，
        // 这里临时只保留几个代表列，验证计算列浅蓝底 + 公式结果 + BIN1 缺省值
        try
        {
            if (scan.DataContext is ScanPrintViewModel csv && csv.Resolver.ComputedOrdered.Count > 0)
            {
                var first = csv.Table.Rows.Count > 0 ? csv.Table.Rows[0] : null;
                if (first != null)
                {
                    foreach (var cv in csv.Resolver.ComputedOrdered)
                        lines.Add($"[CHECK] 计算列 {cv.Key} = {first[cv.Key]}（公式 {cv.Formula}）");
                    foreach (var dv in csv.AllVars.Where(v => !string.IsNullOrEmpty(v.DefaultValue)))
                        lines.Add($"[CHECK] 缺省值 {dv.Key} = {first[dv.Key]}（配置 {dv.DefaultValue}）");
                }

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "po", "part_no", "bin", "part_no2" };
                for (var i = scan.DataGridRows.Columns.Count - 1; i >= 0; i--)
                {
                    var col = scan.DataGridRows.Columns[i];
                    var key = (col as System.Windows.Controls.DataGridTextColumn)?.Binding is System.Windows.Data.Binding b
                        ? b.Path?.Path?.Trim('[', ']')
                        : null;
                    if (key == null || !keep.Contains(key)) scan.DataGridRows.Columns.RemoveAt(i);
                }
                // 选中首行再截图：计算列在「选中整行」时必须是深蓝底白字，否则白字压浅底看不清
                scan.DataGridRows.SelectedIndex = 0;
                scan.DataGridRows.Focus();   // 有焦点才是现场真实观感（无焦点时行选中色会退成浅灰）
                scan.DataGridRows.UpdateLayout();
                DoEvents();
                var computedCol = -1;
                for (var i = 0; i < scan.DataGridRows.Columns.Count; i++)
                    if ((scan.DataGridRows.Columns[i].Header as string)?.Contains("公式") == true)
                    { computedCol = i; break; }

                var cell = computedCol < 0
                    ? null
                    : FindCell(scan.DataGridRows.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow,
                        computedCol);
                var bg = (cell?.Background as SolidColorBrush)?.Color;
                var fg = (cell?.Foreground as SolidColorBrush)?.Color;
                // 关键不是「必须是白字」，而是背景与文字亮度差够大——原来的毛病就是白字(#FFF)压在淡蓝(#E3F0FC)上，差只有十几
                static double Lum(System.Windows.Media.Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                var contrast = bg is { } b0 && fg is { } f0 ? Math.Abs(Lum(b0) - Lum(f0)) : -1;
                lines.Add($"[CHECK] 计算列选中态：行已选中={scan.DataGridRows.SelectedIndex == 0}，" +
                          $"底色=#{bg?.R:X2}{bg?.G:X2}{bg?.B:X2} 字色=#{fg?.R:X2}{fg?.G:X2}{fg?.B:X2}，" +
                          $"明暗差={contrast:0}（>90 才算看得清）");

                var cell2 = computedCol < 0
                    ? null
                    : FindCell(scan.DataGridRows.ItemContainerGenerator.ContainerFromIndex(1) as DataGridRow,
                        computedCol);
                var bg2 = (cell2?.Background as SolidColorBrush)?.Color;
                if (bg2 is { } b1)
                    lines.Add($"[CHECK] 计算列未选中态：底色=#{b1.R:X2}{b1.G:X2}{b1.B:X2}（浅蓝底标识公式列）");

                // 离屏窗口拿不到键盘焦点，聚焦选中色只能从样式触发器里读（现场点行时生效的就是它）
                if (computedCol >= 0 &&
                    scan.DataGridRows.Columns[computedCol].CellStyle is { } cs)
                {
                    var focusBrush = cs.Triggers.OfType<System.Windows.DataTrigger>()
                        .SelectMany(t => t.Setters.OfType<Setter>())
                        .FirstOrDefault(s => s.Property == Control.BackgroundProperty)?.Value
                        as SolidColorBrush;
                    if (focusBrush != null)
                        lines.Add($"[CHECK] 计算列聚焦选中色=#{focusBrush.Color.R:X2}" +
                                  $"{focusBrush.Color.G:X2}{focusBrush.Color.B:X2}（配白字，现场点行时生效）");
                }
                Save(scan, 1180, 740, Path.Combine(outputDir, "03b-scan-computed.png"), lines);
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[WARN] 计算列特写快照: {ex.Message}");
        }

        var templates = ActivatorUtilities.CreateInstance<TemplatesPage>(_sp);
        _sp.GetRequiredService<TemplatesViewModel>().Load();
        Load(templates);
        Save(templates, 1000, 700, Path.Combine(outputDir, "04-templates.png"), lines);

        var records = ActivatorUtilities.CreateInstance<PrintRecordsPage>(_sp);
        _sp.GetRequiredService<PrintRecordsViewModel>().Search();
        Load(records);
        Save(records, 1000, 700, Path.Combine(outputDir, "05-records.png"), lines);

        var users = ActivatorUtilities.CreateInstance<UsersPage>(_sp);
        _sp.GetRequiredService<UsersViewModel>().Load();
        Load(users);
        Save(users, 1000, 700, Path.Combine(outputDir, "06-users.png"), lines);

        // 模板编辑器（画布 + 属性面板）+ 二维码可选中 + 撤销
        try
        {
            var tplSvc = _sp.GetRequiredService<TemplateService>();
            var tplRow = tplSvc.GetRows().FirstOrDefault(t => t.Code == "tia_new")
                         ?? tplSvc.GetRows().First();
            var editor = new TemplateEditorWindow(tplRow.Id);
            Load(editor);
            // 选中一个文本元素，属性面板截图才能展示字号等真实值（否则是空面板）
            var hasText = editor.SelectFirstTextForTest();
            lines.Add($"[CHECK] 编辑器选中文本元素用于属性面板展示：{(hasText ? "通过" : "未通过（该模板无文本元素）")}");
            DoEvents();
            Save(editor.Content as FrameworkElement, 1240, 760,
                Path.Combine(outputDir, "07-editor.png"), lines);

            // 导入的二维码只有 size_mm，叠框必须按边长撑开，否则缩成 6×6 点、鼠标点不中
            var qrBox = editor.OverlayLayer.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag is TemplateElement qe && qe.Type == "qrcode");
            if (qrBox == null)
            {
                lines.Add("[FAIL] 编辑器：找不到二维码元素");
            }
            else
            {
                var w = Math.Round(qrBox.Width);
                var h = Math.Round(qrBox.Height);
                lines.Add($"[CHECK] 编辑器二维码可点区域 {w}×{h} 点（≥100 才算点得中）：{(w >= 100 && h >= 100 ? "通过" : "未通过")}");
            }

            // 未保存改动判定：初始无 → 拖动后有 → 撤销回原状后又无 → 重做后再有
            var dirty0 = editor.HasUnsavedChangesForTest;
            var before = editor.DocForTest.Elements.FirstOrDefault(e => e.Type == "qrcode")?.X;
            var dragged = editor.SimulateDragForTest("qrcode", 5, 3);
            var moved = editor.DocForTest.Elements.FirstOrDefault(e => e.Type == "qrcode")?.X;
            var dirty1 = editor.HasUnsavedChangesForTest;
            editor.UndoForTest();
            var afterUndo = editor.DocForTest.Elements.FirstOrDefault(e => e.Type == "qrcode")?.X;
            var dirty2 = editor.HasUnsavedChangesForTest;
            var undoOk = dragged && before.HasValue && moved.HasValue && afterUndo.HasValue
                         && Math.Abs(moved.Value - before.Value) > 0.5
                         && Math.Abs(afterUndo.Value - before.Value) < 0.01;
            lines.Add($"[CHECK] 编辑器撤销：X {before:0.##} → 拖动 {moved:0.##} → 撤销 {afterUndo:0.##}：{(undoOk ? "通过" : "未通过")}");
            lines.Add($"[CHECK] 编辑器未保存标记：初始={dirty0} 拖动后={dirty1} 撤销后={dirty2}" +
                      $"（期望 False/True/False）：{(!dirty0 && dirty1 && !dirty2 ? "通过" : "未通过")}");

            editor.RedoForTest();
            var afterRedo = editor.DocForTest.Elements.FirstOrDefault(e => e.Type == "qrcode")?.X;
            lines.Add($"[CHECK] 编辑器重做：X {afterRedo:0.##}：{(afterRedo.HasValue && moved.HasValue && Math.Abs(afterRedo.Value - moved.Value) < 0.01 ? "通过" : "未通过")}");

            // 文字样式图标按钮：元素 → 面板、面板 → 元素，以及旧元素默认值
            var styled = new TemplateElement
            {
                Type = "text", Bold = true, Italic = true, WordWrap = true,
                Align = "right", Valign = "bottom",
            };
            var state = editor.FormatPanelStateForTest(styled);
            lines.Add($"[CHECK] 图标按钮·元素→面板 = {state}：" +
                      $"{(state == "B=True,I=True,W=True,H=right,V=bottom" ? "通过" : "未通过")}");
            var back = editor.PanelToElementForTest();
            var backOk = back.Bold == true && back.Italic == true && back.WordWrap == true
                         && back.Align == "right" && back.Valign == "bottom";
            lines.Add($"[CHECK] 图标按钮·面板→元素：{(backOk ? "通过" : "未通过")}");
            var legacy = editor.FormatPanelStateForTest(new TemplateElement { Type = "text" });
            lines.Add($"[CHECK] 旧元素（无新字段）默认值 = {legacy}：" +
                      $"{(legacy == "B=False,I=False,W=False,H=left,V=middle" ? "通过" : "未通过")}");

            // 属性即时生效：灌值不提交、失焦提交、一次编辑一条撤销、无变化不压栈、字号写入 font_size
            try
            {
                var sim = editor.SimulatePanelEditForTest("12.5", "18");
                lines.Add($"[INFO] 即时生效模拟 = {sim}");
                var ok = sim.StartsWith("x:") && sim.Contains("->12.5")
                         && sim.Contains("fill:+0") && sim.Contains("edit1:+1")
                         && sim.Contains("same:+0") && sim.Contains("fontSize:->18")
                         && sim.Contains("edit2:+1");
                lines.Add($"[CHECK] 失焦即时生效 / 撤销不合并 / 字号生效：{(ok ? "通过" : "未通过")}");
                if (!ok) lines.Add($"[FAIL] 即时生效断言不符：{sim}");
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] 即时生效模拟：{ex.Message}");
            }

            // 改了 A 的值后直接点画布上的 B（画布不可聚焦，不会触发 LostFocus），再回 A
            try
            {
                var sw = editor.SimulateSwitchCommitForTest("21");
                lines.Add($"[INFO] 切换元素提交模拟 = {sw}");
                var ok = !sw.StartsWith("skip") && sw.Contains("->21")
                         && sw.Contains("shown:21") && sw.Contains("selectedA:True");
                lines.Add($"[CHECK] 切换选中元素前先提交改动：{(ok ? "通过" : "未通过")}");
                if (!ok) lines.Add($"[FAIL] 切换提交断言不符：{sw}");
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] 切换元素提交模拟：{ex.Message}");
            }

            // 元素多选：全选 / 圈选 / 混合值留空 / 批量提交 / 批量拖动各合并为一次撤销
            try
            {
                // Ctrl+点击：先清空 → 点 A → 点 B 加进集合 → 再点 A 移出
                editor.SetSelectionForTest();
                var els = editor.DocForTest.Elements.Take(2).ToList();
                var c1 = editor.ToggleSelectionForTest(els[0]);
                var c2 = editor.ToggleSelectionForTest(els[1]);
                var bothSelected = editor.IsSelectedForTest(els[0]) && editor.IsSelectedForTest(els[1]);
                var c3 = editor.ToggleSelectionForTest(els[0]);
                var toggleOk = c1 == 1 && c2 == 2 && bothSelected && c3 == 1 && !editor.IsSelectedForTest(els[0]);
                lines.Add($"[INFO] Ctrl+点击切换 = {c1}→{c2}→{c3}");
                lines.Add($"[CHECK] Ctrl+点击增减多选：{(toggleOk ? "通过" : "未通过")}");
                if (!toggleOk) lines.Add("[FAIL] Ctrl+点击多选行为不符");

                var all = editor.SelectAllForTest();
                lines.Add($"[INFO] Ctrl+A 全选 = {all} 个元素（文档共 {editor.DocForTest.Elements.Count} 个）");
                lines.Add($"[CHECK] 全选覆盖所有元素：{(all == editor.DocForTest.Elements.Count ? "通过" : "未通过")}");

                // 圈选左上角一小块，应命中部分元素
                var docN = editor.DocForTest;
                var band = editor.RubberBandForTest(0, 0, docN.WidthMm * 3, docN.HeightMm * 3);
                lines.Add($"[INFO] 圈选命中 = {band} 个元素");
                lines.Add($"[CHECK] 圈选返回有效选中数：{(band > 0 && band < all ? "通过" : "未通过")}");

                // Ctrl+圈选追加：选中数应单调不减；点空白（框太小）应清空
                var additive = editor.RubberBandAddForTest(docN.WidthMm * 4, docN.HeightMm * 2,
                    docN.WidthMm * 3, docN.HeightMm * 3);
                lines.Add($"[INFO] Ctrl+圈选追加后 = {additive} 个元素");
                lines.Add($"[CHECK] 追加式圈选不清空原有选中：{(additive >= band ? "通过" : "未通过")}");
                var cleared = editor.RubberBandForTest(0, 0, 1, 1);
                lines.Add($"[CHECK] 点空白清空选择：{(cleared == 0 ? "通过" : "未通过")}");
                if (cleared != 0) lines.Add($"[FAIL] 点空白未清空选择（剩 {cleared} 个）");

                // 多选批量改字号：混合值留空、两个元素都生效、只压一次撤销
                var multi = editor.SimulateMultiEditForTest("16");
                lines.Add($"[INFO] 批量改字号 = {multi}");
                var mOk = multi.Contains("count:2") && multi.Contains("sizes:") && multi.Contains("->16,16")
                          && multi.Contains("undo1:+1") && multi.Contains("undo2:+0");
                lines.Add($"[CHECK] 多选批量提交（全部生效/一次撤销）：{(mOk ? "通过" : "未通过")}");
                if (!mOk) lines.Add($"[FAIL] 批量提交断言不符：{multi}");

                // 多选批量改字体：两个元素都生效、只压一次撤销
                var batchFont = editor.SimulateBatchFontForTest("黑体");
                lines.Add($"[INFO] 批量改字体 = {batchFont}");
                var fOk = batchFont.Contains("count:2") && batchFont.Contains("fonts:黑体,黑体")
                          && batchFont.Contains("undo1:+1") && batchFont.Contains("undo2:+0");
                lines.Add($"[CHECK] 多选批量改字体（全部生效/一次撤销）：{(fOk ? "通过" : "未通过")}");
                if (!fOk) lines.Add($"[FAIL] 批量改字体断言不符：{batchFont}");

                // 多选批量拖动：两个元素位移一致、只压一次撤销
                var drag = editor.SimulateMultiDragForTest(2, 1);
                lines.Add($"[INFO] 批量拖动 = {drag}");
                var dOk = drag.Contains("count:2") && drag.Contains("deltas:2/1,2/1") && drag.Contains("undo:+1");
                lines.Add($"[CHECK] 多选批量拖动（整组位移/一次撤销）：{(dOk ? "通过" : "未通过")}");
                if (!dOk) lines.Add($"[FAIL] 批量拖动断言不符：{drag}");
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] 多选自检：{ex.Message}");
            }

            // 多选状态快照：展示橙色多选框 + 属性面板的多选提示与混合值留空
            try
            {
                editor.SelectAllForTest();
                editor.PerformLayoutForTest();
                Save(editor.Content as FrameworkElement, 1240, 760,
                    Path.Combine(outputDir, "14-multi-select.png"), lines);
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] 14-multi-select.png: {ex.Message}");
            }

            editor.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 07-editor.png: {ex.Message}");
        }

        // 变量管理窗口（独立子表编辑界面）
        try
        {
            var tplSvc = _sp.GetRequiredService<TemplateService>();
            var vt = tplSvc.GetRows().FirstOrDefault(t => t.Code == "ux3320_t") ?? tplSvc.GetRows().First();
            var varsWin = new VariablesWindow(vt.Id, vt.Name);
            SaveWindow(varsWin, 880, 620, Path.Combine(outputDir, "09-variables.png"), lines);
            varsWin.Close();

            var doc = tplSvc.LoadDoc(vt.Id);
            var target = doc.Variables.FirstOrDefault(v => v.IsComputed) ?? doc.Variables.First();
            var dlg = new VarDialog(target, doc.Variables);
            SaveWindow(dlg, 470, 680, Path.Combine(outputDir, "10-var-formula.png"), lines);
            dlg.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 09-variables / 10-var-formula: {ex.Message}");
        }

        // 扫码规则窗口：段号表按样例自动拆分，变量列是下拉
        try
        {
            var tplSvc = _sp.GetRequiredService<TemplateService>();
            var st = tplSvc.GetRows().FirstOrDefault(t => t.Code == "ux3320_t") ?? tplSvc.GetRows().First();
            var ruleWin = new ScanRuleDialog(st.Id);
            var ruleDirty0 = ruleWin.HasUnsavedChangesForTest;
            // 样例留空时表格只有已存映射，快照看不出拆分效果，这里补一条真实样码
            ruleWin.SampleBox.Text = "A|UX2069_H1_D1_B|H1D51SB|P1KS28.00|2534|66428|";
            ruleWin.MarkerBox.Text = "A";
            ruleWin.DelimiterBox.Text = "|";
            DoEvents();
            var segCount = ruleWin.SeqGrid.Items.Count;
            var seg1Key = (ruleWin.SeqGrid.Items[0] as ScanRuleDialog.SeqRow)?.Key ?? "";
            var ruleDirty1 = ruleWin.HasUnsavedChangesForTest;
            lines.Add($"[CHECK] 扫码规则未保存标记：初始={ruleDirty0} 改动后={ruleDirty1}" +
                      $"（期望 False/True）：{(!ruleDirty0 && ruleDirty1 ? "通过" : "未通过")}");
            lines.Add($"[CHECK] 扫码规则段号表：按样例拆出 {segCount} 段（含标记段已去掉），" +
                      $"{ruleWin.VarOptions.Count - 1} 个候选变量");
            SaveWindow(ruleWin, 900, 720, Path.Combine(outputDir, "11-scan-rule.png"), lines);

            // 改样例 → 表格自动重排，且已选变量按段号保留
            ruleWin.SampleBox.Text = "A|ONLY_ONE";
            DoEvents();
            var afterCount = ruleWin.SeqGrid.Items.Count;
            var afterKey = (ruleWin.SeqGrid.Items[0] as ScanRuleDialog.SeqRow)?.Key ?? "";
            lines.Add($"[CHECK] 改样例后自动重排：{afterCount} 段（应为 1），" +
                      $"段 1 变量保留={(afterKey.Length > 0 && afterKey == seg1Key ? "是" : "否")}（{afterKey}）");

            // 改分隔符 → 也重排
            ruleWin.DelimiterBox.Text = "-";
            ruleWin.SampleBox.Text = "A-X1-X2-X3";
            DoEvents();
            lines.Add($"[CHECK] 改分隔符后自动重排：{ruleWin.SeqGrid.Items.Count} 段（应为 3）");

            // 切规则确认：有未保存编辑时取消 → 退回原规则且编辑保留；确认 → 新规则生效
            if (ruleWin.RuleList.Items.Count >= 2)
            {
                var sampleBefore = ruleWin.SampleBox.Text;
                ruleWin.ConfirmDiscardForTest = _ => false;
                ruleWin.RuleList.SelectedIndex = 1;
                DoEvents();
                var cancelled = ruleWin.RuleList.SelectedIndex == 0
                                && ruleWin.SampleBox.Text == sampleBefore
                                && ruleWin.HasUnsavedChangesForTest;
                lines.Add($"[CHECK] 切规则取消：回到原规则、编辑保留：{(cancelled ? "通过" : "未通过")}");

                ruleWin.ConfirmDiscardForTest = _ => true;
                ruleWin.RuleList.SelectedIndex = 1;
                DoEvents();
                var applied = ruleWin.RuleList.SelectedIndex == 1
                              && !ruleWin.HasUnsavedChangesForTest;
                lines.Add($"[CHECK] 切规则确认：新规则生效、基线刷新：{(applied ? "通过" : "未通过")}");
                ruleWin.ConfirmDiscardForTest = null;
            }
            else
            {
                lines.Add("[INFO] 该模板只有 1 条规则，跳过切规则确认测试");
            }
            ruleWin.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 11-scan-rule.png: {ex.Message}");
        }

        // 打印预览窗口（多张缩略图排布，验证标签下方不再有摘要文字）
        try
        {
            var tplSvc = _sp.GetRequiredService<TemplateService>();
            var tpl = tplSvc.GetRows().FirstOrDefault(t => t.Code.Contains("2115")) ?? tplSvc.GetRows().First();
            var doc = tplSvc.LoadDoc(tpl.Id);
            var vars = doc.Variables.Where(v => !string.IsNullOrEmpty(v.Key))
                .Where(v => (v.Source ?? "scan") != "fixed").ToList();
            var sampleRows = new List<Dictionary<string, string?>>();
            for (var r = 0; r < 3; r++)
            {
                var row = new Dictionary<string, string?>();
                foreach (var v in vars)
                    row[v.Key] = SampleValueFor(v.Key, r);
                sampleRows.Add(row);
            }
            var preview = _sp.GetRequiredService<PrintPreviewWindow>();
            preview.Initialize(doc, tpl.Id, tpl.Code, tpl.Name, sampleRows);
            Load(preview);
            SaveWindow(preview, 1000, 760, Path.Combine(outputDir, "08-print-preview.png"), lines);
            preview.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 08-print-preview.png: {ex.Message}");
        }

        // 切模板自动聚焦扫码框 + 只有一行时编辑单元格也能刷新预览
        try
        {
            var p = Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                .CreateInstance<ScanPrintPage>(_sp);
            Load(p);
            var pvm = p.DataContext as ScanPrintViewModel;
            if (pvm != null && pvm.Templates.Count >= 2)
            {
                var focus0 = p.ScanFocusRequestsForTest;
                var other = pvm.Templates.FirstOrDefault(t => t.Id != pvm.SelectedTemplate?.Id);
                if (other != null)
                {
                    pvm.ConfirmTemplateSwitch = _ => true;
                    pvm.SelectedTemplate = other;
                    pvm.ConfirmTemplateSwitch = null;
                }
                lines.Add($"[CHECK] 切模板后聚焦扫码框：请求 {focus0} → {p.ScanFocusRequestsForTest}" +
                          $"（{(p.ScanFocusRequestsForTest > focus0 ? "通过" : "未通过")}）");

                pvm.ClearAll();
                pvm.AddBlankRow();
                var refresh0 = p.PreviewRefreshCountForTest;
                var firstKey = pvm.EditableVars.First().Key;
                pvm.Table.Rows[0][firstKey] = "TEST-EDIT";
                var refresh1 = p.PreviewRefreshCountForTest;
                var imgOk = pvm.PreviewImage != null;
                var editedRowShown = ReferenceEquals(p.LastPreviewRowForTest, pvm.Table.Rows[0]);
                lines.Add($"[CHECK] 单行编辑后预览自动刷新：重绘 {refresh0} → {refresh1}，预览图非空={imgOk}，" +
                          $"重绘的是被编辑行={editedRowShown}" +
                          $"（{(refresh1 > refresh0 && imgOk && editedRowShown ? "通过" : "未通过")}）");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 切模板聚焦 / 预览自动刷新: {ex.Message}");
        }

        // 升级提示条：默认隐藏；单独渲染一张显示态快照
        try
        {
            var win = _sp.GetRequiredService<MainWindow>();
            Load(win);
            var hiddenByDefault = win.UpdateBanner.Visibility == Visibility.Collapsed;
            win.UpdateBannerText.Text = "发现新版本 0.4.2（当前 0.4.1）　·　UXLabelshop_v0.4.2.exe";
            win.UpdateBanner.Visibility = Visibility.Visible;
            Save(win.Content as FrameworkElement, 1180, 760,
                Path.Combine(outputDir, "12-update-banner.png"), lines);
            lines.Add($"[CHECK] 升级提示条默认隐藏={hiddenByDefault}，显示态快照已生成");
            win.SuppressCloseConfirm = true;
            win.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 12-update-banner.png: {ex.Message}");
        }

        // 新建模板对话框：底部按钮必须在可视区内（曾因固定 Height+NoResize 被裁掉）
        try
        {
            var dlg = new NewTemplateDialog();
            Load(dlg);
            var hasImport = dlg.FindName("ImportInfo") is FrameworkElement;
            lines.Add($"[CHECK] 新建模板对话框导入区存在={hasImport}");
            var path13 = Path.Combine(outputDir, "13-new-template.png");
            SaveWindow(dlg, 420, 610, path13, lines);
            // SaveWindow 已 Show 过窗口，此时视觉树与布局才有效
            dlg.VerifyButtonsVisibleForTest(lines);
            // 用显式尺寸重渲染一次，避免按内容实际尺寸渲染时把贴底按钮切掉
            Save(dlg.Content as FrameworkElement, 420, 610, path13, lines);
            dlg.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 13-new-template.png: {ex.Message}");
        }

        return lines;
    }

    /// <summary>给预览快照造像样的示例值，按变量 key 猜字段。</summary>
    private static string SampleValueFor(string key, int row)
    {
        var k = key.ToLowerInvariant();
        if (k.Contains("po")) return $"PO-WX-2410-2{row}";
        if (k.Contains("part") || k.Contains("model")) return "UX2115";
        if (k.Contains("package") || k.Contains("pkg")) return "TSSOP016";
        if (k.Contains("date") || k.Contains("dc")) return "2622";
        if (k.Contains("lot")) return "2WZX33092.1";
        if (k.Contains("qty")) return (row + 1) * 5000 + "pcs";
        if (k.Contains("track")) return "260528A50";
        if (k.Contains("msl")) return "MSL 3";
        return $"S{row + 1}";
    }

    private static void Load(FrameworkElement element)
    {
        element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    }

    /// <summary>
    /// 窗口类界面的快照：真正 Show 一次（挪到屏幕外），按内容实际尺寸渲染。
    /// 直接对未显示的 Window.Content 排版会被窗口客户区二次约束，导致右侧/底部被裁。
    /// </summary>
    private static void SaveWindow(Window window, int width, int height, string path, List<string> lines)
    {
        try
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false;
            window.Left = -4000;
            window.Top = -4000;
            window.Width = width;
            window.Height = height;
            window.Show();
            DoEvents();
            lines.Add($"[CHECK] {window.GetType().Name} 标题栏图标：" +
                      (window.Icon == null ? "未加载" : "已加载"));

            if (window.Content is not FrameworkElement root)
            {
                lines.Add($"[FAIL] {Path.GetFileName(path)}: 窗口无内容");
                return;
            }
            var w = (int)Math.Ceiling(root.ActualWidth);
            var h = (int)Math.Ceiling(root.ActualHeight);
            if (w <= 0 || h <= 0)
            {
                lines.Add($"[FAIL] {Path.GetFileName(path)}: 内容尺寸无效 {w}x{h}");
                return;
            }

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            lines.Add($"[OK] {Path.GetFileName(path)} {w}x{h}");
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] {Path.GetFileName(path)}: {ex.Message}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>让窗口完成一次布局/渲染（不进入 Run 循环）。</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>取某行第 columnIndex 列的单元格（用于检查样式是否真的生效）。</summary>
    private static DataGridCell? FindCell(DataGridRow? row, int columnIndex)
    {
        if (row == null) return null;
        var presenter = FindVisual<DataGridCellsPresenter>(row);
        return presenter?.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell;
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var nested = FindVisual<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void Save(FrameworkElement? element, int width, int height, string path, List<string> lines)
    {
        if (element == null)
        {
            lines.Add($"[FAIL] {Path.GetFileName(path)}: 无内容");
            return;
        }
        try
        {
            element.Width = width;
            element.Height = height;
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
            lines.Add($"[OK] {Path.GetFileName(path)} {width}x{height}");
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
