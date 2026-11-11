using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UXLabelshop.Services;
using UXLabelshop.Views;

namespace UXLabelshop.ViewModels;

public partial class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public partial class MainViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly UsersPage _usersPage;
    private readonly ScanPrintPage _scanPage;
    private readonly PrintRecordsPage _recordsPage;
    private readonly TemplatesPage _templatesPage;

    [ObservableProperty]
    private object? _currentPage;

    public ObservableCollection<NavItem> NavItems { get; } = new();

    public string Welcome =>
        $"{_session.CurrentUser?.Name}（{(_session.CurrentUser?.Role == "admin" ? "管理员" : "用户")}）";

    /// <summary>当前会话用户标识，用于判断是否需要重建导航。</summary>
    public long? CurrentUserKey => _session.CurrentUser?.Id;

    public MainViewModel(AppSession session,
        ScanPrintPage scanPage,
        PrintRecordsPage recordsPage,
        TemplatesPage templatesPage,
        UsersPage usersPage)
    {
        _session = session;
        _scanPage = scanPage;
        _recordsPage = recordsPage;
        _templatesPage = templatesPage;
        _usersPage = usersPage;
        RefreshNav();
    }

    /// <summary>根据当前会话重建导航（退出重登不同角色时调用）。</summary>
    public void RefreshNav()
    {
        NavItems.Clear();
        NavItems.Add(new NavItem { Key = "scan", Title = "扫码打印" });
        NavItems.Add(new NavItem { Key = "records", Title = "打印记录" });
        NavItems.Add(new NavItem { Key = "templates", Title = "模板管理" });
        if (_session.IsAdmin)
            NavItems.Add(new NavItem { Key = "users", Title = "用户管理" });
        OnPropertyChanged(nameof(Welcome));
        Nav("scan");
    }

    [RelayCommand]
    private void Nav(string key)
    {
        CurrentPage = key switch
        {
            "scan" => _scanPage,
            "records" => _recordsPage,
            "templates" => _templatesPage,
            "users" => _session.IsAdmin ? _usersPage : CurrentPage,
            _ => CurrentPage,
        };
        foreach (var n in NavItems)
            n.IsSelected = n.Key == key;
    }
}
