using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;

namespace UXLabelshop.Services;

/// <summary>用户管理（管理员）。</summary>
public class UserService
{
    private readonly IServiceProvider _sp;
    private readonly AppSession _session;

    public UserService(IServiceProvider sp, AppSession session)
    {
        _sp = sp;
        _session = session;
    }

    public List<SysUser> GetAll()
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        return db.SysUsers.OrderBy(u => u.Id).ToList();
    }

    public SysUser Add(string username, string name, string role, string password)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        if (db.SysUsers.Any(u => u.Username == username))
            throw new InvalidOperationException($"工号 {username} 已存在");
        var user = new SysUser
        {
            Username = username,
            Name = name,
            Role = role == "admin" ? "admin" : "user",
            PwdHash = AuthService.Hash(password),
            MustChangePwd = true,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    public void Update(SysUser user)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var u = db.SysUsers.Single(x => x.Id == user.Id);
        u.Name = user.Name;
        u.Role = user.Role == "admin" ? "admin" : "user";
        u.Enabled = user.Enabled;
        db.SaveChanges();
    }

    public void ResetPassword(long userId, string newPassword)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var u = db.SysUsers.Single(x => x.Id == userId);
        u.PwdHash = AuthService.Hash(newPassword);
        u.MustChangePwd = true;
        db.SaveChanges();
    }

    /// <summary>不允许停用/降级最后一个启用的管理员（防止无人能管）。</summary>
    public void EnsureNotLastAdmin(SysUser user)
    {
        if (user.Role != "admin" || user.Enabled) return;
        using var db = _sp.GetRequiredService<AppDbContext>();
        if (db.SysUsers.Count(x => x.Role == "admin" && x.Enabled && x.Id != user.Id) == 0)
            throw new InvalidOperationException("不能停用唯一的管理员账号");
    }
}
