using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;
using UXLabelshop.Services;

namespace UXLabelshop.Views;

public partial class AddUserDialog : Window
{
    public SysUser? CreatedUser { get; private set; }

    public AddUserDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var username = UserNameBox.Text.Trim();
        var name = NameBox.Text.Trim();
        var role = ((System.Windows.Controls.ComboBoxItem)RoleCombo.SelectedItem).Tag?.ToString() ?? "user";
        var pwd = PwdBox.Text;

        if (string.IsNullOrWhiteSpace(username)) { MessageBox.Show("请输入工号"); return; }
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("请输入姓名"); return; }
        if (pwd.Length < 6) { MessageBox.Show("密码至少 6 位"); return; }

        try
        {
            CreatedUser = App.Services.GetRequiredService<UserService>().Add(username, name, role, pwd);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "新增失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
