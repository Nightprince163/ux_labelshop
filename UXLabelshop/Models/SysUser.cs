namespace UXLabelshop.Models;

/// <summary>用户（两级角色：user / admin）。表 sys_user。</summary>
public class SysUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string PwdHash { get; set; } = "";
    public string Role { get; set; } = "user"; // user | admin
    public bool Enabled { get; set; } = true;
    public bool MustChangePwd { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsAdmin => Role == "admin";
}
