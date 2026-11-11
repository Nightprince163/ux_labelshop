using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Services;

namespace UXLabelshop.Views;

public partial class ChangePasswordWindow : Window
{
    public ChangePasswordWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var pwd = NewPwdBox.Password;
        if (pwd.Length < 6) { MessageBox.Show("密码至少 6 位"); return; }
        if (pwd != ConfirmPwdBox.Password) { MessageBox.Show("两次输入的密码不一致"); return; }

        App.Services.GetRequiredService<AuthService>().ChangePassword(pwd);
        // 若启用「记住密码」，同步更新为新密码，否则下次会自动填入失效的旧密码
        var config = App.Services.GetRequiredService<ConfigService>();
        if (config.Current.RememberLoginPassword)
            config.SetRememberedPassword(pwd);
        DialogResult = true;
    }
}
