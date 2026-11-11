using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UXLabelshop.Models;
using UXLabelshop.Services;

namespace UXLabelshop.ViewModels;

public partial class UserRowVm : ObservableObject
{
    public SysUser User { get; }

    public long Id => User.Id;
    public string Username => User.Username;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _role;
    [ObservableProperty] private bool _enabled;

    public DateTime? LastLoginAt => User.LastLoginAt;

    public UserRowVm(SysUser user)
    {
        User = user;
        _name = user.Name;
        _role = user.Role;
        _enabled = user.Enabled;
    }

    partial void OnRoleChanged(string value) { User.Role = value; }
    partial void OnEnabledChanged(bool value) { User.Enabled = value; }
    partial void OnNameChanged(string value) { User.Name = value; }
}

public partial class UsersViewModel : ObservableObject
{
    private readonly UserService _users;
    private readonly AppSession _session;

    public ObservableCollection<UserRowVm> Users { get; } = new();

    [ObservableProperty] private string? _statusMessage;

    public UsersViewModel(UserService users, AppSession session)
    {
        _users = users;
        _session = session;
    }

    [RelayCommand]
    public void Load()
    {
        Users.Clear();
        foreach (var u in _users.GetAll())
            Users.Add(new UserRowVm(u));
        StatusMessage = null;
    }

    /// <summary>提交表格中的修改（姓名/角色/停用）。</summary>
    [RelayCommand]
    public void Save()
    {
        try
        {
            foreach (var row in Users)
            {
                _users.EnsureNotLastAdmin(row.User);
                _users.Update(row.User);
            }
            StatusMessage = $"已保存 {Users.Count} 条（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败：" + ex.Message;
        }
    }

    public void AddUser(SysUser user)
    {
        Users.Add(new UserRowVm(user));
        StatusMessage = $"已新增用户 {user.Username}（首次登录将强制改密）";
    }

    /// <summary>重置为随机密码（弹窗显示一次，用户下次登录须改密）。</summary>
    [RelayCommand]
    public void ResetPassword(UserRowVm? row)
    {
        if (row == null) return;
        var pwd = GeneratePassword();
        _users.ResetPassword(row.Id, pwd);
        System.Windows.MessageBox.Show(
            $"已重置 {row.Username} 的密码为：\n\n    {pwd}\n\n请通知该用户，其首次登录须改密。",
            "重置密码", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        StatusMessage = $"已重置 {row.Username} 的密码";
    }

    private static string GeneratePassword()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 10)
            .Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)])
            .ToArray());
    }
}
