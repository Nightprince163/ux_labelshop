using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using UXLabelshop.Rendering;
using UXLabelshop.Services;
using UXLabelshop.ViewModels;
using UXLabelshop.Views;

namespace UXLabelshop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private static readonly string[] TestModes =
        { "uicheck", "flowtest", "dbtest", "rendertest", "selftest", "printtest", "vartest", "pbmark", "tplfix", "lsdtest", "lsddump" };

    /// <summary>命令行自检模式：不弹退出确认、不做交互阻塞。</summary>
    public static bool IsSelfTest { get; private set; }

    public static string AppDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsSelfTest = e.Args.Any(a => TestModes.Contains(a, StringComparer.OrdinalIgnoreCase));

        // 日志：exe 同目录 logs/，按天滚动
        Directory.CreateDirectory(Path.Combine(AppDir, "logs"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppDir, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 升级交接脚本启动的新进程：记一笔，旧版 .exe.old 特意保留，方便人工回滚
        if (e.Args.Contains("--after-update", StringComparer.OrdinalIgnoreCase))
            Log.Information("更新已完成，新版本 {Version} 启动成功（旧版本保留为同目录 .exe.old）",
                UXLabelshop.Services.UpdateService.CurrentVersion);

        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "UI 未处理异常");
            MessageBox.Show(args.Exception.Message, "程序异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            Log.Error(args.ExceptionObject as Exception, "AppDomain 未处理异常");

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 公式引擎的查库能力（DBLOOKUP）：连接在首次调用时才建立，这里只是挂上宿主
        FormulaEngine.DefaultHost = Services.GetRequiredService<SqlLookupService>();

        // 界面快照：UXLabelshop uicheck（离屏渲染主要页面到 logs/ui/*.png）
        if (e.Args.Contains("uicheck", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string> { $"uicheck @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                var loginResult = Services.GetRequiredService<AuthService>().Login("admin", "Admin@123");
                lines.Add(loginResult.Success ? "[OK] 登录" : "[INFO] admin 默认密码不可用，改用首个管理员身份：" + EnsureTestSession());
                lines.AddRange(Services.GetRequiredService<UiCheckService>()
                    .Run(Path.Combine(AppDir, "logs", "ui")));
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "uicheck.txt"), lines);
            Shutdown(0);
            return;
        }

        // 变量/公式自检：UXLabelshop vartest（函数覆盖 + 缺省值 + 计算列重算 + 子表迁移）
        if (e.Args.Contains("vartest", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                lines.AddRange(Services.GetRequiredService<VariableTestService>().Run());
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "vartest.txt"), lines);
            foreach (var l in lines) Log.Information("vartest: {Line}", l);
            Shutdown(0);
            return;
        }

        // 模板修正：UXLabelshop tplfix [目录]（把 *.fixed.json 写回库，生成新版本）
        if (e.Args.Contains("tplfix", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                if (EnsureTestSession() is var who && !string.IsNullOrEmpty(who))
                    lines.Add($"[INFO] 以 {who} 身份写入");
                var idx = Array.FindIndex(e.Args, a => a.Equals("tplfix", StringComparison.OrdinalIgnoreCase));
                var dir = e.Args.Skip(idx + 1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                lines.AddRange(Services.GetRequiredService<TemplateFixService>().Run(dir));
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "tplfix.txt"), lines);
            foreach (var l in lines) Log.Information("tplfix: {Line}", l);
            Shutdown(0);
            return;
        }

        // Pb 无铅标志：UXLabelshop pbmark [lsd目录] [--apply]（识别 + 写入模板 + 渲染样张）
        if (e.Args.Contains("pbmark", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                var idx = Array.FindIndex(e.Args, a => a.Equals("pbmark", StringComparison.OrdinalIgnoreCase));
                var dir = e.Args.Skip(idx + 1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                var apply = e.Args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
                lines.AddRange(Services.GetRequiredService<PbMarkService>().Run(dir, apply));
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "pbmark.txt"), lines);
            foreach (var l in lines) Log.Information("pbmark: {Line}", l);
            Shutdown(0);
            return;
        }

        // .lsd 导入自检：UXLabelshop lsdtest [样本目录]（只读，不写数据库）
        if (e.Args.Contains("lsdtest", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                var idx = Array.FindIndex(e.Args, a => a.Equals("lsdtest", StringComparison.OrdinalIgnoreCase));
                var dir = e.Args.Skip(idx + 1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                lines.AddRange(Services.GetRequiredService<LsdImportTestService>().Run(dir));
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "lsdtest.txt"), lines);
            foreach (var l in lines) Log.Information("lsdtest: {Line}", l);
            Shutdown(0);
            return;
        }

        // .lsd 解析/转换诊断：UXLabelshop lsddump <文件路径>（只读，输出全部元素）
        if (e.Args.Contains("lsddump", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                var idx = Array.FindIndex(e.Args, a => a.Equals("lsddump", StringComparison.OrdinalIgnoreCase));
                var file = e.Args.Skip(idx + 1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                var r = LsdImportService.Parse(file ?? "");
                lines.Add($"文件：{r.FileName}");
                lines.Add($"幅面：{r.WidthMm} × {r.HeightMm} mm　标题：{r.SavedTitle}　打印机：{r.PrinterHint}");
                lines.Add($"字体：{string.Join(", ", r.Fonts)}");
                lines.Add($"变量 {r.RawVariableNames.Count}：{string.Join(" | ", r.RawVariableNames)}");
                lines.Add($"静态文字 {r.StaticLabels.Count}：{string.Join(" | ", r.StaticLabels)}");
                lines.Add($"样例值 {r.SampleValues.Count}：{string.Join(" | ", r.SampleValues)}");
                lines.Add($"包围盒 {r.Boxes.Count} / 类名记录 {r.ClassBoxes.Count} / 图片 {r.Pictures.Count}");
                var doc = LsdImportService.BuildDoc(r, "lsd_dump", "dump", r.WidthMm, r.HeightMm, r.Dpi);
                lines.Add($"---- 元素 {doc.Elements.Count} ----");
                foreach (var el in doc.Elements)
                    lines.Add($"{el.Type,-8} x={el.X,6:0.00} y={el.Y,6:0.00} w={el.W,6:0.00} h={el.H,6:0.00} " +
                              $"size={el.SizeMm,5:0.00} bold={el.Bold} text={el.Text}");
                lines.Add("---- 变量 ----");
                foreach (var v in doc.Variables)
                    lines.Add($"{v.Key,-14} label={v.Label,-14} sample={v.Sample}");

                // 用 .lsd 里的样例值渲染成 PNG，便于和 LabelShop 原版面直接比对
                var values = doc.Variables.ToDictionary(v => v.Key, v => (string?)v.Sample);
                using (var bmp = new LabelRenderer().RenderGray(doc, values))
                {
                    var png = Path.Combine(AppDir, "logs", "lsddump.png");
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    lines.Add($"渲染图：{png}（{bmp.Width}×{bmp.Height}）");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "lsddump.txt"), lines);
            Shutdown(0);
            return;
        }

        // 打印自检：UXLabelshop printtest（打印机 DPI/纸型 + PDF 实样）
        if (e.Args.Contains("printtest", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                lines.AddRange(Services.GetRequiredService<PrintTestService>().Run());
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "printtest.txt"), lines);
            foreach (var l in lines) Log.Information("printtest: {Line}", l);
            Shutdown(0);
            return;
        }

        // 主流程自检：UXLabelshop flowtest（扫码→填充→导出→入库）
        if (e.Args.Contains("flowtest", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string> { $"flowtest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                var auth = Services.GetRequiredService<AuthService>();
                var loginResult = auth.Login("admin", "Admin@123");
                lines.Add(loginResult.Success ? "[OK] 登录" : "[INFO] 使用管理员身份：" + EnsureTestSession());
                lines.AddRange(Services.GetRequiredService<FlowTestService>().Run());
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "flowtest.txt"), lines);
            Shutdown(0);
            return;
        }

        // 数据库计时：UXLabelshop dbtest
        if (e.Args.Contains("dbtest", StringComparer.OrdinalIgnoreCase))
        {
            var lines = new List<string>();
            try
            {
                lines.AddRange(Services.GetRequiredService<DbTimingService>().Run());
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "dbtest.txt"), lines);
            Shutdown(0);
            return;
        }

        // 渲染自检：UXLabelshop rendertest（把全部模板渲染到 logs/render/*.png）
        if (e.Args.Contains("rendertest", StringComparer.OrdinalIgnoreCase))
        {
            var outDir = Path.Combine(AppDir, "logs", "render");
            var lines = new List<string> { $"rendertest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            try
            {
                Services.GetRequiredService<DbInitService>().Initialize();
                lines.AddRange(Services.GetRequiredService<RenderCheckService>()
                    .Run(outDir));
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            }
            File.WriteAllLines(Path.Combine(AppDir, "logs", "rendertest.txt"), lines);
            foreach (var l in lines) Log.Information("rendertest: {Line}", l);
            Shutdown(0);
            return;
        }

        // 自检模式：UXLabelshop selftest（无界面，结果写 logs/selftest.txt）
        if (e.Args.Contains("selftest", StringComparer.OrdinalIgnoreCase))
        {            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lines = new List<string> { $"selftest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            try
            {
                var init = Services.GetRequiredService<DbInitService>();
                init.Initialize();
                lines.Add("[OK] 数据库初始化");

                var auth = Services.GetRequiredService<AuthService>();
                var r = auth.Login("admin", "Admin@123");
                lines.Add(r.Success
                    ? $"[OK] admin 登录（须改密={r.MustChangePassword}），用户：{Services.GetRequiredService<AppSession>().CurrentUser?.Name}"
                    : $"[FAIL] admin 登录：{r.Message}");

                using var db = Services.GetRequiredService<AppDbContext>();
                lines.Add($"[INFO] 模板数={db.LabelTemplates.Count()}，扫码规则数={db.ScanRules.Count()}，用户数={db.SysUsers.Count()}");
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            }
            sw.Stop();
            lines.Add($"elapsed {sw.ElapsedMilliseconds} ms");
            File.WriteAllLines(Path.Combine(AppDir, "logs", "selftest.txt"), lines);
            foreach (var l in lines) Log.Information("selftest: {Line}", l);
            Shutdown(0);
            return;
        }

        var login = Services.GetRequiredService<LoginWindow>();
        login.Show();
    }

    /// <summary>自检用：admin 密码被修改后，直接以首个启用管理员身份建立会话（仅本地调试模式）。</summary>
    private static string EnsureTestSession()
    {
        using var db = Services.GetRequiredService<AppDbContext>();
        var admin = db.SysUsers.Where(u => u.Role == "admin" && u.Enabled).OrderBy(u => u.Id).FirstOrDefault();
        if (admin == null) return "无可用管理员";
        Services.GetRequiredService<AppSession>().CurrentUser = admin;
        return admin.Username;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection s)
    {
        // 服务
        s.AddSingleton<ConfigService>();
        s.AddSingleton<AppSession>();
        s.AddSingleton<DbInitService>();
        s.AddSingleton<AuthService>();
        s.AddSingleton<UserService>();
        s.AddSingleton<RenderCheckService>();
        s.AddSingleton<DbTimingService>();
        s.AddSingleton<TemplateService>();
        s.AddSingleton<ScanRuleService>();
        s.AddSingleton<PrintService>();
        s.AddSingleton<FlowTestService>();
        s.AddSingleton<PrintTestService>();
        s.AddSingleton<VariableTestService>();
        s.AddSingleton<PbMarkService>();
        s.AddSingleton<LsdImportTestService>();
        s.AddSingleton<UiCheckService>();
        s.AddSingleton<SqlLookupService>();
        s.AddSingleton<DbKeepAliveService>();
        s.AddSingleton<TemplateFixService>();
        s.AddSingleton<UpdateService>();
        s.AddTransient<AppDbContext>();

        // 视图模型
        s.AddTransient<LoginViewModel>();
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<UsersViewModel>();
        s.AddSingleton<ScanPrintViewModel>();
        s.AddSingleton<TemplatesViewModel>();
        s.AddSingleton<PrintRecordsViewModel>();

        // 视图（页面单例，窗口瞬态）
        s.AddSingleton<ScanPrintPage>();
        s.AddSingleton<PrintRecordsPage>();
        s.AddSingleton<TemplatesPage>();
        s.AddSingleton<UsersPage>();
        s.AddTransient<LoginWindow>();
        s.AddTransient<MainWindow>();
        s.AddTransient<ChangePasswordWindow>();
        s.AddTransient<DatabaseSettingsWindow>();
        s.AddTransient<AddUserDialog>();
        s.AddTransient<PrintPreviewWindow>();
    }
}
