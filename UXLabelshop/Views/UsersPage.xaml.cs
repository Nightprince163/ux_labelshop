using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.ViewModels;

namespace UXLabelshop.Views;

public partial class UsersPage : UserControl
{
    private readonly UsersViewModel _vm;

    public UsersPage(UsersViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) => _vm.Load();
    }

    private void AddUserButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = App.Services.GetRequiredService<AddUserDialog>();
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true && dlg.CreatedUser is { } user)
            _vm.AddUser(user);
    }
}
