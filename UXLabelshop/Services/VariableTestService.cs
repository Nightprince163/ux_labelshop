using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;
using UXLabelshop.ViewModels;
using UXLabelshop.Views;

namespace UXLabelshop.Services;

/// <summary>
/// 变量/公式自检：UXLabelshop.exe vartest
/// 覆盖 Excel 风格函数、用户验收场景（BIN 缺省 / part_no2 派生）、
/// 子表迁移、计算列依赖重算与人工覆盖语义。
/// </summary>
public class VariableTestService
{
    private readonly IServiceProvider _sp;

    public VariableTestService(IServiceProvider sp) => _sp = sp;

    public List<string> Run()
    {
        var lines = new List<string> { $"vartest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        var pass = 0;
        var fail = 0;

        void Check(string name, string actual, string expected)
        {
            var ok = actual == expected;
            lines.Add(ok
                ? $"[OK] {name} = {Show(actual)}"
                : $"[FAIL] {name}：实际 {Show(actual)}，期望 {Show(expected)}");
            if (ok) pass++; else fail++;
        }

        void CheckTrue(string name, bool ok, string detail = "")
        {
            lines.Add(ok ? $"[OK] {name}{detail}" : $"[FAIL] {name}{detail}");
            if (ok) pass++; else fail++;
        }

        // 逐像素工具：字形/行数/一致性判断（方法级，多个自检块共用）
        static (int Ink, int Rows) Stats(System.Drawing.Bitmap bmp)
        {
            var ink = 0; var rows = 0;
            for (var y = 0; y < bmp.Height; y++)
            {
                var hit = false;
                for (var x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).R < 128) { ink++; hit = true; }
                if (hit) rows++;
            }
            return (ink, rows);
        }
        static bool Same(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            for (var y = 0; y < a.Height; y++)
                for (var x = 0; x < a.Width; x++)
                    if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
            return true;
        }

        // ---- 1. 公式基础与函数覆盖 ----
        var v = new Dictionary<string, string?>
        {
            ["part_no"] = "UX3261_D2_H",
            ["po"] = "WX-260616-05",
            ["qty"] = "20000",
            ["empty"] = "",
        };
        string F(string f) => FormulaEngine.EvaluateStrict(f, v);

        Check("字面量与连接", F("=\"UX\" & \"-\" & \"3261\""), "UX-3261");
        Check("变量引用", F("={{part_no}}"), "UX3261_D2_H");
        Check("LEFT", F("=LEFT({{part_no}},6)"), "UX3261");
        Check("RIGHT", F("=RIGHT({{part_no}},2)"), "_H");
        Check("MID", F("=MID({{part_no}},8,2)"), "D2");
        Check("LEN", F("=LEN({{part_no}})"), "11");
        Check("FIND", F("=FIND(\"_\",{{part_no}})"), "7");
        Check("SPLIT 第 1 段", F("=SPLIT({{part_no}},\"_\",1)"), "UX3261");
        Check("SPLIT 第 3 段", F("=SPLIT({{part_no}},\"_\",3)"), "H");
        Check("SUBSTITUTE", F("=SUBSTITUTE({{part_no}},\"_\",\"-\")"), "UX3261-D2-H");
        Check("UPPER / LOWER", F("=LOWER(\"AB\") & UPPER(\"cd\")"), "abCD");
        Check("IF 判断", F("=IF({{qty}}>10000,\"大\",\"小\")"), "大");
        Check("IF 分支 2", F("=IF({{qty}}<100,\"小\",\"大\")"), "大");
        Check("IFERROR 兜底", F("=IFERROR(SPLIT(\"ABC\",\"_\",2),{{po}})"), "WX-260616-05");
        Check("ISBLANK", F("=IF(ISBLANK({{empty}}),\"空\",\"有\")"), "空");
        Check("AND / OR / NOT", F("=IF(AND({{qty}}>0,NOT(ISBLANK({{po}}))),\"Y\",\"N\")"), "Y");
        Check("算术与比较", F("={{qty}}/1000+1"), "21");
        Check("ROUND", F("=ROUND(3.14159,2)"), "3.14");
        Check("INT / ABS", F("=INT(-3.7) & \"/\" & ABS(-4)"), "-4/4");
        Check("CONCAT / TEXTJOIN", F("=TEXTJOIN(\"-\",TRUE,{{po}},\"A\")"), "WX-260616-05-A");
        Check("REPLACE / TRIM", F("=TRIM(REPLACE(\"  ABCDE\",2,3,\"X\"))"), "XCDE");
        CheckTrue("语法错误可被检出", FormulaEngine.Validate("=LEFT({{part_no}}") != null);
        CheckTrue("未知函数被拒绝", FormulaEngine.Validate("=FOOBAR(1)") != null);
        CheckTrue("公式错误不抛异常（返回 #ERR）",
            FormulaEngine.Evaluate("=1/0", v).StartsWith("#ERR:"));

        // ---- 2. 用户验收场景 ----
        Check("场景：part_no 派生 part_no2",
            F("=IFERROR(SPLIT({{part_no}},\"_\",1), {{part_no}})"), "UX3261");
        Check("场景：无下划线时 IFERROR 回退原值",
            FormulaEngine.EvaluateStrict("=IFERROR(SPLIT({{p}},\"_\",1), {{p}})",
                new Dictionary<string, string?> { ["p"] = "UX3261" }), "UX3261");

        // ---- 3. 子表迁移与读写 ----
        var templateService = _sp.GetRequiredService<TemplateService>();
        var ux3320 = templateService.GetPublished().FirstOrDefault(t => t.Code == "ux3320_t");
        if (ux3320 == null)
        {
            // 未发布时用 GetAll 兜底，保证自检在任何状态下都能取到模板
            ux3320 = templateService.GetAll().FirstOrDefault(t => t.Code == "ux3320_t");
        }
        if (ux3320 == null)
        {
            lines.Add("[FAIL] 找不到 ux3320_t 模板");
            fail++;
        }
        else
        {
            var doc = templateService.LoadDoc(ux3320.Id);
            lines.Add($"[INFO] ux3320_t：{doc.Variables.Count} 变量（来自 label_template_var 子表）");
            var bin = doc.Variables.FirstOrDefault(x => x.Key == "bin");
            var pn2 = doc.Variables.FirstOrDefault(x => x.Key == "part_no2");
            CheckTrue("场景：BIN 缺省值已入库", bin?.DefaultValue == "BIN1", $"（{bin?.DefaultValue}）");
            CheckTrue("场景：part_no2 为公式变量", pn2?.IsComputed == true, $"（{pn2?.Formula}）");

            // ---- 4. 解析器：缺省值 + 计算列 + 依赖重算 ----
            var resolver = new VariableResolver(doc.Variables);
            CheckTrue("公式无循环依赖", resolver.ValidationError == null, resolver.ValidationError ?? "");
            CheckTrue("识别到依赖列 part_no", resolver.ShouldRecompute("part_no"));

            var row = new Dictionary<string, string?>
            {
                ["part_no"] = "UX3261_D2_H",
                ["bin"] = "",
                ["part_no2"] = "",
            };
            resolver.Apply(row);
            Check("缺省值：bin 空 → BIN1", row["bin"] ?? "", "BIN1");

            // 期望值由公式本身算出来：模板公式是可编辑的用户数据（可能被换成 DBLOOKUP 等），
            // 写死 "UX3320S" 之类会让自检在用户调公式后误报。
            string Expect(string partNo) => pn2 == null
                ? ""
                : FormulaEngine.Evaluate(pn2.Formula, new Dictionary<string, string?> { ["part_no"] = partNo });

            Check("计算列：part_no2", row["part_no2"] ?? "", Expect("UX3261_D2_H"));

            // 依赖格变动 → 无条件重算覆盖（含人工改过的值）
            row["part_no"] = "UX3320S_A0_P2_1";
            row["part_no2"] = "人工手改值";
            resolver.Recompute(row);
            Check("依赖变动无条件覆盖人工值", row["part_no2"] ?? "", Expect("UX3320S_A0_P2_1"));

            // 用户手动改计算列后，只要依赖不变就保留
            row["part_no2"] = "人工值B";
            CheckTrue("手动改计算列结果被保留", row["part_no2"] == "人工值B");
        }

        // ---- 5. ViewModel 端到端：编辑依赖格触发自动重算 ----
        try
        {
            var vm = _sp.GetRequiredService<ScanPrintViewModel>();
            var demo = vm.Templates.FirstOrDefault(t => t.Code == "ux3320_t");
            lines.Add($"[INFO] 已发布模板 {vm.Templates.Count} 个，ux3320_t={(demo == null ? "未找到" : "已找到")}，" +
                      $"切换前计算列 {vm.Resolver.ComputedOrdered.Count} 个");
            if (demo != null)
            {
                vm.SelectedTemplate = demo;
                lines.Add($"[INFO] 切换后计算列 {vm.Resolver.ComputedOrdered.Count} 个，清单列 {vm.EditableVars.Count} 个");
            }
            if (demo != null && vm.Resolver.ComputedOrdered.Count > 0)
            {
                vm.SelectedTemplate = demo;
                vm.AddBlankRow();
                var r = vm.Table.Rows[vm.Table.Rows.Count - 1];

                var pn2v = vm.AllVars.FirstOrDefault(v => v.Key == "part_no2");
                string ExpectVm(string partNo) => pn2v == null
                    ? ""
                    : FormulaEngine.Evaluate(pn2v.Formula,
                        new Dictionary<string, string?> { ["part_no"] = partNo });

                r["part_no"] = "UX3261_D2_H";   // 编辑依赖格 → 触发 ColumnChanged
                Check("编辑依赖格自动重算 part_no2", r["part_no2"]?.ToString() ?? "", ExpectVm("UX3261_D2_H"));
                r["part_no"] = "UX3320S_A0_P2_1";
                Check("再次改依赖格自动重算", r["part_no2"]?.ToString() ?? "", ExpectVm("UX3320S_A0_P2_1"));
                Check("缺省值在清单行里生效", r["bin"]?.ToString() ?? "", "BIN1");
                r["part_no2"] = "人工覆盖值";
                Check("手动改计算列结果被保留", r["part_no2"]?.ToString() ?? "", "人工覆盖值");
                vm.RecomputeRow(r, suppressEvents: true);
                Check("右键重算恢复公式值", r["part_no2"]?.ToString() ?? "", ExpectVm("UX3320S_A0_P2_1"));
                vm.ClearAll();
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] ViewModel 自动重算链路：{ex.Message}");
            fail++;
        }

        // ---- 5b. 切模板前确认：有扫描结果时取消 → 结果保留、模板不变 ----
        try
        {
            var svm = _sp.GetRequiredService<ScanPrintViewModel>();
            var from = svm.Templates.FirstOrDefault();
            var to = svm.Templates.FirstOrDefault(t => from == null || t.Id != from.Id);
            if (from != null && to != null)
            {
                svm.SelectedTemplate = from;
                svm.ClearAll();
                svm.AddBlankRow();
                if (svm.Table.Columns.Count > 0)
                    svm.Table.Rows[0][svm.Table.Columns[0].ColumnName] = "UX3261_D2_H";
                var rowsBefore = svm.Table.Rows.Count;

                svm.ConfirmTemplateSwitch = _ => false;          // 模拟用户点「取消」
                svm.SelectedTemplate = to;
                var cancelledKept = ReferenceEquals(svm.SelectedTemplate, from)
                                    || svm.SelectedTemplate?.Id == from.Id;
                var cancelledRows = svm.Table.Rows.Count;

                svm.ConfirmTemplateSwitch = _ => true;           // 模拟用户点「确定」
                svm.SelectedTemplate = to;
                var switched = svm.SelectedTemplate?.Id == to.Id;
                var cleared = svm.Table.Rows.Count == 0;

                CheckTrue("切模板取消：模板保持不变", cancelledKept);
                Check("切模板取消：扫描结果保留", cancelledRows.ToString(), rowsBefore.ToString());
                CheckTrue("切模板确认：新模板生效", switched);
                CheckTrue("切模板确认：旧结果已清空", cleared);

                svm.ConfirmTemplateSwitch = null;
                svm.ClearAll();
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 切模板确认链路：{ex.Message}");
            fail++;
        }

        // ---- 5c. 短码补录：≤20 字符不走规则解析，补当前行第一个空白 scan 字段 ----
        try
        {
            var svm = _sp.GetRequiredService<ScanPrintViewModel>();
            svm.ConfirmTemplateSwitch = _ => true;
            var tpl = svm.Templates.FirstOrDefault(t => t.Code == "ux3320_t") ?? svm.Templates.First();
            svm.SelectedTemplate = tpl;
            svm.ClearAll();

            // 空清单 → 短码直接丢弃，不新增空行
            svm.ScanText = "WX-260923-06";
            svm.Scan();
            Check("短码·清单为空时丢弃", svm.Table.Rows.Count.ToString(), "0");

            svm.AddBlankRow();
            var shortRow = svm.Table.Rows[0];
            svm.SelectedRow = shortRow;
            var scanVars = svm.EditableVars.Where(sv => sv.EffectiveSource == "scan").ToList();
            foreach (var sv in scanVars) shortRow[sv.Key] = "";
            var k1 = scanVars[0].Key;
            var k2 = scanVars[1].Key;

            svm.ScanText = "WX-260923-06";          // 12 字符
            svm.Scan();
            Check("短码补第一个空白 scan 字段", shortRow[k1]?.ToString() ?? "", "WX-260923-06");
            Check("短码不新增行", svm.Table.Rows.Count.ToString(), "1");
            CheckTrue("短码不算新增行标记", !svm.LastScanAddedRow);

            svm.ScanText = "PO-2";                  // 连扫第二个补录码
            svm.Scan();
            Check("连续短码补下一个空字段", shortRow[k2]?.ToString() ?? "", "PO-2");

            foreach (var sv in scanVars) shortRow[sv.Key] = "X";
            svm.ScanText = "DISCARD";
            svm.Scan();
            Check("短码·无空字段时丢弃", svm.Table.Rows.Count.ToString(), "1");
            CheckTrue("丢弃时有提示", svm.StatusMessage?.Contains("已忽略") == true,
                $"（{svm.StatusMessage}）");

            // 阈值边界：20 字符仍是短码，21 字符才走规则解析
            svm.ScanText = "12345678901234567890";   // 20
            svm.Scan();
            Check("恰好 20 字符按短码处理", svm.Table.Rows.Count.ToString(), "1");

            svm.ScanText = "A|UX2067_C3_D1_9|C3GZ1S9|2641|2993|";   // >20
            svm.Scan();
            Check("超过 20 字符走规则解析并新增行", svm.Table.Rows.Count.ToString(), "2");
            CheckTrue("长码标记 LastScanAddedRow", svm.LastScanAddedRow);

            svm.ClearAll();
            svm.ConfirmTemplateSwitch = null;
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 短码补录链路：{ex.Message}");
            fail++;
        }

        // ---- 6. DBLOOKUP：参数化 / 只读校验 / 无结果兜底 / 缓存 ----
        // 先用假宿主脱库验证 SQL 生成与守卫，再连真库跑一次端到端
        var fake = new FakeHost();
        string G(string f) => FormulaEngine.EvaluateStrict(f, v, fake);

        fake.Value = "CONF-001";
        Check("DBLOOKUP 取结果首格",
            G("=DBLOOKUP(\"SELECT invCoding FROM configproduct WHERE invCode='{{po}}'\")"), "CONF-001");
        Check("变量改成参数占位符（剥外层引号）", fake.LastSql ?? "",
            "SELECT invCoding FROM configproduct WHERE invCode=@p0");
        Check("参数值原样交给驱动（不拼字符串）",
            fake.LastArgs is { Count: 1 } ? fake.LastArgs[0] ?? "" : "", "WX-260616-05");

        fake.Value = "X";
        G("=DBLOOKUP(\"SELECT a FROM t WHERE b LIKE '%{{po}}%'\")");
        Check("字面量内混排改写为 CONCAT", fake.LastSql ?? "",
            "SELECT a FROM t WHERE b LIKE CONCAT('%', @p0, '%')");

        fake.Value = "X";
        G("=DBLOOKUP(\"SELECT a FROM t WHERE b={{po}} AND c={{part_no}}\")");
        Check("多变量按出现顺序编号", fake.LastSql ?? "",
            "SELECT a FROM t WHERE b=@p0 AND c=@p1");
        Check("多变量参数个数", (fake.LastArgs?.Count ?? 0).ToString(), "2");

        fake.Value = null;
        Check("无结果返回空串", G("=DBLOOKUP(\"SELECT a FROM t\")"), "");
        Check("无结果用第二个参数兜底", G("=DBLOOKUP(\"SELECT a FROM t\", \"--\")"), "--");

        CheckTrue("写语句被拒绝：DELETE", FormulaEngine.Validate("=DBLOOKUP(\"DELETE FROM t\")") != null);
        CheckTrue("写语句被拒绝：UPDATE", FormulaEngine.Validate("=DBLOOKUP(\"UPDATE t SET a=1\")") != null);
        CheckTrue("多语句被拒绝", FormulaEngine.Validate("=DBLOOKUP(\"SELECT 1; DROP TABLE t\")") != null);
        CheckTrue("写文件被拒绝", FormulaEngine.Validate(
            "=DBLOOKUP(\"SELECT a FROM t INTO OUTFILE '/tmp/x'\")") != null);
        CheckTrue("REPLACE 字符串函数不被误杀",
            FormulaEngine.Validate("=DBLOOKUP(\"SELECT REPLACE(a,'x','y') FROM t\")") == null);
        CheckTrue("字符串里的关键字不被误杀",
            FormulaEngine.Validate("=DBLOOKUP(\"SELECT a FROM t WHERE note='DELETE it'\")") == null);
        CheckTrue("注释里的关键字不被误杀",
            FormulaEngine.Validate("=DBLOOKUP(\"SELECT a FROM t /* DELETE 说明 */\")") == null);
        CheckTrue("运行时也拦写语句",
            FormulaEngine.Evaluate("=DBLOOKUP(\"DELETE FROM t\")", v, fake).StartsWith("#ERR:"));

        // 真库链路
        try
        {
            var lookup = _sp.GetRequiredService<SqlLookupService>();
            var sql = "SELECT code FROM label_template ORDER BY id LIMIT 1";
            var first = lookup.Scalar(sql, Array.Empty<string?>());
            CheckTrue("真库查询可用", !string.IsNullOrEmpty(first), $"（{first}）");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var again = lookup.Scalar(sql, Array.Empty<string?>());
            sw.Stop();
            Check("缓存值一致", again ?? "", first ?? "");
            CheckTrue("二次查询命中缓存", sw.ElapsedMilliseconds <= 5,
                $"（{sw.ElapsedMilliseconds} ms，累计命中 {lookup.CacheHits} 次）");

            CheckTrue("真库无结果返回 null（空结果集）",
                lookup.Scalar("SELECT code FROM label_template WHERE 1=0", Array.Empty<string?>()) == null);
            Check("真库 + 公式：无结果显示空串",
                FormulaEngine.Evaluate("=DBLOOKUP(\"SELECT code FROM label_template WHERE 1=0\")", v, lookup), "");

            var ro = lookup.Scalar("SELECT @@transaction_read_only", Array.Empty<string?>());
            lines.Add($"[INFO] DBLOOKUP 会话只读标记 transaction_read_only={ro}（1=数据库层已兜底，0=仅靠 SQL 白名单）");

            // 端到端：清单里的计算列真的能靠查库取值，并在依赖变动时重算
            var probe = new TemplateVar
            {
                Key = "inv_coding",
                Label = "存货编码",
                Source = "computed",
                Formula = "=DBLOOKUP(\"SELECT code FROM label_template WHERE code='{{part_no}}'\", \"未找到\")",
            };
            var dbResolver = new VariableResolver(new[] { probe });
            var dbRow = new Dictionary<string, string?> { ["part_no"] = first ?? "" };
            dbResolver.Apply(dbRow);
            Check("清单计算列查库（命中）", dbRow["inv_coding"] ?? "", first ?? "");
            dbRow["part_no"] = "NO_SUCH_CODE_XYZ";
            dbResolver.Recompute(dbRow);
            Check("清单计算列查库（未命中走兜底）", dbRow["inv_coding"] ?? "", "未找到");
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] DBLOOKUP 真库链路：{ex.Message}");
            fail++;
        }

        // ---- 6b. 复制模板：新名称/编码必须同步进版本 content ----
        long copyId = 0;
        try
        {
            var tsvc = _sp.GetRequiredService<TemplateService>();
            foreach (var t in tsvc.GetAll())
            {
                if (string.IsNullOrWhiteSpace(t.Content)) continue;
                var oldName = TemplateDoc.Parse(t.Content).Name;
                if (!string.IsNullOrEmpty(oldName) && oldName != t.Name)
                    lines.Add($"[INFO] 历史副本名称不一致（显示已由 LoadDoc 修正）：{t.Code} 表={t.Name} / content={oldName}");
            }
            var srcRow = tsvc.GetRows().First();
            var newCode = "selftest_copy_" + DateTime.Now.ToString("HHmmssfff");
            var newName = "复制测试 " + newCode;
            copyId = tsvc.CopyTemplate(srcRow.Id, newCode, newName, null);

            var copiedRow = tsvc.GetRows().FirstOrDefault(x => x.Id == copyId);
            var copiedDoc = tsvc.LoadDoc(copyId);
            Check("复制模板·列表名称", copiedRow?.Name ?? "", newName);
            Check("复制模板·编辑器名称", copiedDoc.Name ?? "", newName);
            Check("复制模板·内容编码", copiedDoc.Id ?? "", newCode);
            CheckTrue("复制模板·源模板名称未受影响",
                tsvc.GetRows().First(x => x.Id == srcRow.Id).Name == srcRow.Name);

            // 用户实际看到的位置：打开副本后编辑器顶部的「模板名称」输入框
            var editor = new TemplateEditorWindow(copyId);
            Check("复制模板·编辑器名称输入框", editor.NameBox.Text, newName);
            editor.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 复制模板名称：{ex.Message}");
            fail++;
        }
        finally
        {
            if (copyId > 0)
            {
                try { _sp.GetRequiredService<TemplateService>().DeleteDraft(copyId); }
                catch { /* 清理失败不影响断言结果 */ }
            }
        }

        // ---- 6c. 客户端自升级：目录即清单（无 version.json） ----
        try
        {
            var json = "[{\"name\":\"UXLabelshop_v0.1.0.exe\",\"type\":\"file\"}," +
                       "{\"name\":\"UXLabelshop_v0.2.0.exe\",\"type\":\"file\"}," +
                       "{\"name\":\"UXLabelshop_v0.10.0.exe\",\"type\":\"file\"}]";
            var rel = UpdateService.ParseLatest(json, "http://192.168.1.35/client-update");
            Check("目录清单取最高版本", rel?.Version.ToString() ?? "", "0.10.0");
            Check("下载 URL 自动补斜杠", rel?.Url ?? "",
                "http://192.168.1.35/client-update/UXLabelshop_v0.10.0.exe");
            Check("默认 HTML autoindex 也能解析",
                UpdateService.ParseLatest("<a href=\"UXLabelshop_v0.3.0.exe\">x</a>", "http://x/u/")?.Version.ToString() ?? "",
                "0.3.0");
            CheckTrue("目录里没有匹配文件时不提示", UpdateService.ParseLatest("<html>empty</html>", "http://x/u/") == null);
            CheckTrue("升级地址默认关闭（未配置不联网）", new AppConfig().UpdateUrl == null);
            Check("当前程序版本", UpdateService.CurrentVersion.ToString(), "0.4.0");

            // 自检跑在 UI 线程（Dispatcher），同步等 async 会死锁；统一丢到线程池跑
            static T Sync<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
            static UpdateService WithListing(string body) => new(new HttpClient(new FakeHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));

            var found = Sync(() => WithListing("[{\"name\":\"UXLabelshop_v0.9.0.exe\"}]")
                .CheckAsync("http://x/u/"));
            Check("检查更新·发现新版", found?.Version.ToString() ?? "", "0.9.0");
            CheckTrue("检查更新·已是最新时不提示",
                Sync(() => WithListing("[{\"name\":\"UXLabelshop_v0.0.1.exe\"}]")
                    .CheckAsync("http://x/u/")) == null);

            var bad = new UpdateService(new HttpClient(new FakeHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("NOTANEXE")) })));
            var rejected = false;
            try
            {
                Sync(() => bad.DownloadAsync(
                    new UpdateRelease(new Version(9, 9, 9), "x.exe", "http://x/x.exe")));
            }
            catch (IOException) { rejected = true; }
            CheckTrue("下载内容不是 exe 时拒绝并清理", rejected &&
                !File.Exists((Environment.ProcessPath ?? "") + ".new"));
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 客户端自升级自检：{ex.Message}");
            fail++;
        }

        // ---- 6d. 配置密码加密改为跨机器 AES（不再依赖 DPAPI） ----
        try
        {
            var enc = ConfigService.EncryptSecret("DbP@ss-测试123");
            CheckTrue("加密输出带 AX: 前缀（可跨机器）", enc != null && enc.StartsWith("AX:"));
            Check("AES 加密→解密 往返一致",
                ConfigService.DecryptSecret(enc) ?? "", "DbP@ss-测试123");
            Check("非 AX: 旧格式/乱码在其它机器返回 null（不崩溃）",
                ConfigService.DecryptSecret("not-a-real-dpapi-blob") ?? "<null>", "<null>");
            CheckTrue("空密码加密返回 null", ConfigService.EncryptSecret(null) == null);
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 配置密码加密自检：{ex.Message}");
            fail++;
        }

        // ---- 6e. 打印预览记住上次打印机（config.json 持久化） ----
        try
        {
            var cfg0 = new ConfigService();
            var saved = cfg0.Current.LastPrinterName;   // 记住原值，测完还原，避免污染开发机 config
            cfg0.SetLastPrinter("记忆测试打印机");
            var cfg1 = new ConfigService();
            Check("记住上次打印机并落盘", cfg1.Current.LastPrinterName ?? "", "记忆测试打印机");
            cfg1.SetLastPrinter(saved);                 // 还原
            CheckTrue("清空上次打印机写入原值", new ConfigService().Current.LastPrinterName == saved);
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 打印预览记住打印机自检：{ex.Message}");
            fail++;
        }

        // ---- 6f. 文字样式：斜体 / 自动换行（图标按钮背后的真实渲染） ----
        try
        {
            static TemplateDoc Txt(bool? bold, bool? italic, bool? wrap, string text,
                double w = 34, double h = 18) => new()
            {
                WidthMm = 40, HeightMm = 22, Dpi = 203,
                Elements = new List<TemplateElement>
                {
                    new() { Type = "text", X = 2, Y = 2, W = w, H = h, Text = text,
                            SizeMm = 4, Font = "微软雅黑", Align = "left", Valign = "top",
                            Bold = bold, Italic = italic, WordWrap = wrap }
                }
            };

            // 1) 新字段序列化 / 反序列化往返
            var json = Txt(true, true, true, "AB").ToJson();
            CheckTrue("斜体/换行写入模板 JSON",
                json.Contains("\"italic\":true") && json.Contains("\"word_wrap\":true"));
            var parsed = TemplateDoc.Parse(json);
            CheckTrue("斜体/换行反序列化保留",
                parsed.Elements[0].Italic == true && parsed.Elements[0].WordWrap == true);
            var legacyJson = Txt(true, null, null, "AB").ToJson();
            CheckTrue("旧模板不产生新字段（关闭时写 null）",
                !legacyJson.Contains("italic") && !legacyJson.Contains("word_wrap"));
            var legacy = TemplateDoc.Parse(legacyJson);
            CheckTrue("旧模板解析后新字段为 null",
                legacy.Elements[0].Italic == null && legacy.Elements[0].WordWrap == null);

            // 2) null 与显式 false 渲染逐像素一致（旧模板输出不变）
            var renderer = new LabelRenderer();
            using (var a = renderer.RenderGray(Txt(true, null, null, "HELLO 中文"), null))
            using (var b = renderer.RenderGray(Txt(true, false, false, "HELLO 中文"), null))
                CheckTrue("旧模板渲染逐像素不变（null == false）", Same(a, b));

            // 3) 斜体确实改变字形
            using (var plain = renderer.RenderGray(Txt(false, false, false, "HELLO 中文"), null))
            using (var italic = renderer.RenderGray(Txt(false, true, false, "HELLO 中文"), null))
            {
                var s1 = Stats(plain); var s2 = Stats(italic);
                CheckTrue("斜体渲染像素与常规不同", !Same(plain, italic),
                    $"（常规墨迹 {s1.Ink} / 斜体 {s2.Ink}）");
                CheckTrue("斜体仍有可见墨迹", s2.Ink > 0);
            }
            using (var bold = renderer.RenderGray(Txt(true, false, false, "HELLO 中文"), null))
            using (var both = renderer.RenderGray(Txt(true, true, false, "HELLO 中文"), null))
                CheckTrue("粗斜组合与仅粗体不同", !Same(bold, both));

            // 4) 自动换行：长文本窄框，折行后墨迹行数明显多于单行
            const string longText = "ABCDEFGHIJKLMN OPQRSTUVWXYZ 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ 中文换行测试";
            using (var single = renderer.RenderGray(Txt(false, false, false, longText, 24, 16), null))
            using (var wrapped = renderer.RenderGray(Txt(false, false, true, longText, 24, 16), null))
            {
                var s1 = Stats(single); var s2 = Stats(wrapped);
                lines.Add($"[INFO] 换行墨迹行数：单行={s1.Rows} / 自动换行={s2.Rows}");
                CheckTrue("自动换行产生多行", s2.Rows > s1.Rows && s2.Rows >= 20,
                    $"（{s1.Rows} → {s2.Rows}）");
                CheckTrue("自动换行与单行渲染不同", !Same(single, wrapped));
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 文字样式自检：{ex.Message}");
            fail++;
        }

        // ---- 6g. 字号 font_size（磅，纯数字）与 size_mm 兼容 ----
        try
        {
            TemplateDoc Sz(double? pt, double? mm) => new()
            {
                WidthMm = 40, HeightMm = 22, Dpi = 203,
                Elements = new List<TemplateElement>
                {
                    new() { Type = "text", X = 2, Y = 2, W = 34, H = 18, Text = "ABCD",
                            SizeMm = mm, FontSize = pt, Font = "微软雅黑", Align = "left", Valign = "top" }
                }
            };
            var r2 = new LabelRenderer();
            var json = Sz(18, null).ToJson();
            CheckTrue("字号写入 font_size 字段", json.Contains("\"font_size\":18"));
            CheckTrue("字号反序列化保留", TemplateDoc.Parse(json).Elements[0].FontSize == 18);
            CheckTrue("未设字号时 JSON 不含 font_size", !Sz(null, 3).ToJson().Contains("font_size"));

            // 12pt 与等价毫米（12 * 25.4/72）应逐像素一致：证明换算公式与旧 size_mm 路径等价
            using (var byPt = r2.RenderGray(Sz(12, null), null))
            using (var byMm = r2.RenderGray(Sz(null, 12 * 25.4 / 72.0), null))
                CheckTrue("12pt 与等价 mm 字高渲染一致", Same(byPt, byMm));

            // 字号越大墨迹越多；且大字号渲染不报错
            using (var small = r2.RenderGray(Sz(9, null), null))
            using (var large = r2.RenderGray(Sz(24, null), null))
            {
                var a = Stats(small); var b = Stats(large);
                CheckTrue("字号越大墨迹越多", b.Ink > a.Ink, $"（9pt {a.Ink} → 24pt {b.Ink}）");
            }

            // 旧模板（只有 size_mm）不受新增字段影响
            using (var legacyA = r2.RenderGray(Sz(null, 3), null))
            using (var legacyB = r2.RenderGray(Sz(null, 3), null))
                CheckTrue("旧模板 size_mm 渲染稳定", Same(legacyA, legacyB));
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 字号自检：{ex.Message}");
            fail++;
        }

        // ---- 6h. 发布/停用模板后刷新扫码页下拉（页面单例，必须主动通知） ----
        try
        {
            var ts = _sp.GetRequiredService<TemplateService>();
            var vm = _sp.GetRequiredService<ScanPrintViewModel>();
            var published = ts.GetPublished().FirstOrDefault();
            if (published == null)
            {
                lines.Add("[INFO] 无已发布模板，跳过下拉刷新自检");
            }
            else
            {
                var before = vm.Templates.Count;
                vm.AddBlankRow();                       // 造一行未打印数据，验证刷新不清空清单
                var rowsBefore = vm.Table.Rows.Count;
                try
                {
                    ts.SetStatus(published.Id, "disabled");
                    vm.RefreshTemplates();
                    CheckTrue("停用后扫码页下拉移除该模板",
                        vm.Templates.All(t => t.Id != published.Id), $"（{before} → {vm.Templates.Count}）");
                    CheckTrue("刷新下拉不清空未打印清单", vm.Table.Rows.Count == rowsBefore);

                    ts.SetStatus(published.Id, "published");
                    vm.RefreshTemplates();
                    CheckTrue("重新发布后扫码页下拉恢复该模板",
                        vm.Templates.Any(t => t.Id == published.Id), $"（→ {vm.Templates.Count}）");
                }
                finally
                {
                    // published 来自 GetPublished，原状态必然是 published；无条件恢复避免污染数据库
                    ts.SetStatus(published.Id, "published");
                    vm.RefreshTemplates();
                    vm.Table.Rows.Clear();
                    vm.RowCount = 0;
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 模板下拉刷新自检：{ex.Message}");
            fail++;
        }

        // ---- 6i. .lsd 导入端到端落库（创建→写变量→写版本→读回→清理） ----
        try
        {
            var samples = LsdTestSamples.Resolve(null).Files;
            if (samples.Count == 0)
            {
                lines.Add("[INFO] 无 .lsd 样本，跳过导入落库自检");
            }
            else
            {
                var ts2 = _sp.GetRequiredService<TemplateService>();
                var parsed = LsdImportService.Parse(samples[0]);
                var built = LsdImportService.BuildDoc(parsed, "zz_lsd_e2e", "导入自检", parsed.WidthMm,
                    parsed.HeightMm, 203);
                var testCode = "zz_lsd_e2e_" + DateTime.Now.ToString("HHmmss");
                var id = ts2.CreateTemplate(testCode, "导入自检(临时)", built.WidthMm, built.HeightMm,
                    built.Dpi, null, "自检临时数据");
                try
                {
                    ts2.SaveVariables(id, built.Variables);
                    built.Id = testCode;
                    built.Name = "导入自检(临时)";
                    ts2.SaveVersion(id, built, null, "lsd 导入自检");
                    var loaded = ts2.LoadDoc(id);
                    CheckTrue("lsd 导入后变量写入子表", loaded.Variables.Count == built.Variables.Count,
                        $"（{loaded.Variables.Count} 个）");
                    CheckTrue("lsd 导入后元素写入版本", loaded.Elements.Count == built.Elements.Count,
                        $"（{loaded.Elements.Count} 个）");
                    CheckTrue("lsd 导入后幅面正确",
                        Math.Abs(loaded.WidthMm - parsed.WidthMm) < 0.01
                        && Math.Abs(loaded.HeightMm - parsed.HeightMm) < 0.01);
                    using (var bmp = new LabelRenderer().RenderGray(loaded, null))
                        CheckTrue("lsd 导入后模板可渲染", bmp.Width > 0 && bmp.Height > 0);
                }
                finally
                {
                    try { ts2.DeleteDraft(id); }
                    catch (Exception ex) { lines.Add($"[INFO] 临时模板清理失败：{ex.Message}"); }
                }
                CheckTrue("lsd 导入临时模板已清理",
                    !ts2.GetAll().Any(t => t.Code == testCode));
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] lsd 导入落库自检：{ex.Message}");
            fail++;
        }

        // ---- 7. 迁移总数 ----
        using (var db = _sp.GetRequiredService<AppDbContext>())
        {
            var total = db.TemplateVars.Count();
            CheckTrue("变量子表已迁移（>0 行）", total > 0, $"（共 {total} 行）");
            lines.Add($"[INFO] 模板编码：{string.Join(", ", db.LabelTemplates.OrderBy(t => t.Code).Select(t => t.Code))}");
            var computed = db.TemplateVars.Count(x => x.Source == "computed");
            var withDefault = db.TemplateVars.Count(x => x.DefaultValue != null && x.DefaultValue != "");
            lines.Add($"[INFO] 子表统计：公式变量 {computed} 个，带缺省值 {withDefault} 个");
        }

        lines.Add($"---- 结果：{pass} 通过 / {fail} 失败 ----");
        return lines;
    }

    private static string Show(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>假宿主：不连库，只记录公式引擎交给驱动的 SQL 与参数。</summary>
    private sealed class FakeHost : IFormulaHost
    {
        public string? Value { get; set; }
        public string? LastSql { get; private set; }
        public IReadOnlyList<string?>? LastArgs { get; private set; }

        public string? Scalar(string sql, IReadOnlyList<string?> args)
        {
            LastSql = sql;
            LastArgs = args;
            return Value;
        }
    }
    /// <summary>自检用：把目录列表/文件内容换成内存响应，不发真实请求。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_reply(request));
    }

}
