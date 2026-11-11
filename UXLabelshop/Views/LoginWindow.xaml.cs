using System.Windows;
using System.Windows.Input;
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
        Loaded += (_, _) => StartWarmup();
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
                LoginButton.Content = "连接数据库…";
                _warmup.Wait(TimeSpan.FromSeconds(30));
                LoginButton.Content = "登 录";
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
                    MessageBox.Show("首次登录必须修改密码后使用。", "提示");
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
