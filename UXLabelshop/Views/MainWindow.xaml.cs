using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Services;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class MainWindow : Window
{
    private readonly AppSession _session;
    private readonly ConfigService _config;
    private readonly UpdateService _updates;
    private UpdateRelease? _pendingUpdate;
    private bool _checking, _updating;

    public MainWindow(MainViewModel vm, AppSession session, ConfigService config, UpdateService updates)
    {
        InitializeComponent();
        _session = session;
        _config = config;
        _updates = updates;
        DataContext = vm;
        // 退出重登会新建主窗口（窗口一旦 Close 就不能再 Show）；
        // 旧窗口关闭前会清空 CurrentPage 解绑页面控件，这里负责把导航恢复回来
        if (vm.CurrentPage == null) vm.RefreshNav();
        // 退出重登后（可能换角色）重建导航；同一用户激活时不重置当前页
        var lastUserId = vm.CurrentUserKey;
        Activated += (_, _) =>
        {
            if (vm.CurrentUserKey != lastUserId)
            {
                lastUserId = vm.CurrentUserKey;
                vm.RefreshNav();
            }
        };

        Loaded += async (_, _) => await CheckForUpdatesAsync(manual: false);
    }

    // ---------------- 客户端自升级 ----------------

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    private void UpdateLater_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 只在启动时异步查一次（或手动点「检查更新」）。未配置地址、网络不通、超时都静默跳过，
    /// 绝不阻塞开机。发现新版仅提示，不强制、不自动下载。
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (App.IsSelfTest || _checking || _updating) return;
        var url = _config.Current.UpdateUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            if (manual)
                MessageBox.Show(this,
                    "尚未配置升级地址。\n请在登录页「数据库设置」里填写，或直接改 config.json 的 UpdateUrl。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _checking = true;
        try
        {
            var release = await _updates.CheckAsync(url);
            if (release == null)
            {
                if (manual)
                    MessageBox.Show(this, $"已是最新版本（{UpdateService.CurrentVersion}）。",
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _pendingUpdate = release;
            UpdateBannerText.Text = $"发现新版本 {release.Version}（当前 {UpdateService.CurrentVersion}）　·　{release.FileName}";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "检查更新失败");
            if (manual)
                MessageBox.Show(this, "检查更新失败：" + ex.Message, "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _checking = false; }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null || _updating) return;

        // 重启会清空未打印清单，先确认，别把正在干活的工位打断
        var rows = App.Services.GetRequiredService<ScanPrintViewModel>().Table.Rows.Count;
        if (rows > 0 && MessageBox.Show(this,
                $"当前清单有 {rows} 行尚未打印，重启后会清空。\n确定现在更新吗？",
                "更新确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        _updating = true;
        UpdateNowButton.IsEnabled = false;
        var release = _pendingUpdate;
        try
        {
            var progress = new Progress<double>(p =>
                UpdateBannerText.Text = $"正在下载 {release.Version} … {p:P0}");
            var staged = await _updates.DownloadAsync(release, progress);

            if (MessageBox.Show(this, "新版本已下载，是否立即重启生效？",
                    "更新就绪", MessageBoxButton.OKCancel, MessageBoxImage.Question,
                    MessageBoxResult.Cancel) != MessageBoxResult.OK)
            {
                try { File.Delete(staged); } catch { /* 用户选择稍后，下次重新下载 */ }
                return;
            }

            SuppressCloseConfirm = true;     // 自己触发的退出，不要再弹「确定退出吗」
            UpdateService.ApplyAndRestart(staged);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "应用更新失败");
            MessageBox.Show(this, "更新失败：" + ex.Message, "更新", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _updating = false;
            UpdateNowButton.IsEnabled = true;
        }
    }

    /// <summary>关闭窗口前的确认开关：「退出登录」已单独确认过，置 true 避免再弹一次；同时避免窗口已被销毁后重复解析。</summary>
    public bool SuppressCloseConfirm { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 只在真正由用户关闭「当前可见的主窗口」时确认；
        // 自检模式 / 未显示的窗口 / 退出重登 都不打扰
        if (!SuppressCloseConfirm && !App.IsSelfTest && IsVisible &&
            Application.Current.MainWindow == this)
        {
            var answer = MessageBox.Show(
                this,
                "确定要退出 UX_LabelShop 吗？\n未打印的清单内容将不会保存。",
                "退出确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        // 退出登录同样要确认，避免误点后被迫重新登录（自检模式跳过，防止自动化流程卡住）
        if (!App.IsSelfTest)
        {
            var answer = MessageBox.Show(
                this,
                "确定要退出登录吗？\n退出后需要重新输入账号密码。",
                "退出登录确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
        }

        _session.Logout();
        var login = App.Services.GetRequiredService<LoginWindow>();
        Application.Current.MainWindow = login;
        login.Show();

        SuppressCloseConfirm = true;
        // 页面是单例控件：先解绑，避免新主窗口再次把它们放进 ContentControl 时逻辑父级冲突
        if (DataContext is MainViewModel vm) vm.CurrentPage = null;
        Close();
    }
}
