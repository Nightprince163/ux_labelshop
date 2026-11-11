using Microsoft.Extensions.DependencyInjection;

namespace UXLabelshop.Services;

public class LoginResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool MustChangePassword { get; init; }
    public static LoginResult Ok(bool mustChange) => new() { Success = true, MustChangePassword = mustChange };
    public static LoginResult Fail(string msg) => new() { Success = false, Message = msg };
}

public class AuthService
{
    private readonly IServiceProvider _sp;
    private readonly AppSession _session;

    public AuthService(IServiceProvider sp, AppSession session)
    {
        _sp = sp;
        _session = session;
    }

    public LoginResult Login(string username, string password)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var user = db.SysUsers.FirstOrDefault(u => u.Username == username);
        if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PwdHash))
            return LoginResult.Fail("工号或密码错误");
        if (!user.Enabled)
            return LoginResult.Fail("账号已停用，请联系管理员");

        user.LastLoginAt = DateTime.Now;
        db.SaveChanges();

        _session.CurrentUser = user;
        return LoginResult.Ok(user.MustChangePwd);
    }

    /// <summary>修改当前登录用户密码（并清除强制改密标记）。</summary>
    public void ChangePassword(string newPassword)
    {
        if (_session.CurrentUser is not { } me) throw new InvalidOperationException("未登录");
        using var db = _sp.GetRequiredService<AppDbContext>();
        var user = db.SysUsers.Single(u => u.Id == me.Id);
        user.PwdHash = Hash(newPassword);
        user.MustChangePwd = false;
        db.SaveChanges();
        _session.CurrentUser = user;
    }

    public void Logout() => _session.Logout();

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 10);
}
