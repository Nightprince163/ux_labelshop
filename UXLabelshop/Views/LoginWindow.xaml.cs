using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Services;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // 回填保存的密码（PasswordBox 不能绑定，且不把明文放进 ViewModel 属性）
        if (!string.IsNullOrEmpty(_vm.SavedPassword))
            PwdBox.Password = _vm.SavedPassword!;
        VersionText.Text = $"UX_LabelShop Client  ·  v{UpdateService.CurrentVersion}";
        Loaded += (_, _) =>
        {
            StartWarmup();
            StartScanAnimation();
        };
    }

    /// <summary>扫描线缓慢掠过芯片图；动画纯装饰，失败也不能影响登录。</summary>
    private void StartScanAnimation()
    {
        try
        {
            var move = new DoubleAnimation(0, 620, TimeSpan.FromSeconds(7))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            var fade = new DoubleAnimation(0, 0.5, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            ((TranslateTransform)ScanLine.RenderTransform)
                .BeginAnimation(TranslateTransform.YProperty, move);
            ScanLine.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "登录窗扫描线动画启动失败（忽略）");
        }
    }

    private Task? _warmup;

    /// <summary>后台预热数据库：首次连接握手较慢（服务端反查 DNS），提前开始。</summary>
    private void StartWarmup()
    {
        _warmup = Task.Run(() =>
        {
            try
            {
                App.Services.GetRequiredService<DbInitService>().Initialize();
                using var db = App.Services.GetRequiredService<AppDbContext>();
                _ = db.SysUsers.Count();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "数据库预热失败");
            }
        });
    }

    // ---------- 无边框窗口：拖动 / 最小化 / 关闭 ----------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject src) return;

        // 输入框、按钮、复选框以及登录卡片内部都不触发拖动，避免抢焦点/误拖
        for (var d = src; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TextBoxBase or PasswordBox or ButtonBase or ScrollViewer) return;
            if (ReferenceEquals(d, LoginCard)) return;
            if (ReferenceEquals(d, RootGrid)) break;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标未按下或已释放，忽略 */ }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 登录 ----------

    private void LoginButton_Click(object sender, RoutedEventArgs e) => DoLogin();

    private void PwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoLogin();
    }

    private bool _loggingIn;

    private void DoLogin()
    {
        // 回车会同时触发 KeyDown 与默认按钮 Click，加锁避免重复登录（重复开主窗口）
        if (_loggingIn) return;
        _loggingIn = true;
        LoginButton.IsEnabled = false;
        try
        {
            if (_warmup is { IsCompleted: false })
            {
                LoginButtonText.Text = "连接数据库…";
                _warmup.Wait(TimeSpan.FromSeconds(30));
                LoginButtonText.Text = "登 录   →";
            }

            _vm.LoginCommand.Execute(PwdBox.Password);
            var r = _vm.Result;
            if (r is not { Success: true }) return;

            if (r.MustChangePassword)
            {
                var dlg = App.Services.GetRequiredService<ChangePasswordWindow>();
                dlg.Owner = this;
                if (dlg.ShowDialog() != true)
                {
                    MessageBox.Show(this, "首次登录必须修改密码后使用。", "提示");
                    return;
                }
            }

            var main = App.Services.GetRequiredService<MainWindow>();
            Application.Current.MainWindow = main;
            main.Show();
            App.Services.GetService<DbKeepAliveService>()?.Start(TimeSpan.FromSeconds(30));
            Close();
        }
        finally
        {
            LoginButton.IsEnabled = true;
            _loggingIn = false;
        }
    }

    private void DbSettingButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = App.Services.GetRequiredService<DatabaseSettingsWindow>();
        dlg.Owner = this;
        dlg.ShowDialog();
    }
}
