using UXLabelshop.Models;

namespace UXLabelshop.Services;

/// <summary>进程内会话（单机单用户，无需 Token）。</summary>
public class AppSession
{
    public SysUser? CurrentUser { get; set; }

    public bool IsLoggedIn => CurrentUser != null;

    public bool IsAdmin => CurrentUser?.IsAdmin ?? false;

    public void Logout() => CurrentUser = null;
}
