using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace UXLabelshop.Services;

public class DbSettings
{
    public string Host { get; set; } = "192.168.1.35";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "ux_labelshop";
    public string User { get; set; } = "ux_label";
    /// <summary>AES（静态密钥，跨机器可解）加密后的密码，形如 "AX:<base64>"。</summary>
    public string? EncryptedPassword { get; set; }
}

public class AppConfig
{
    public DbSettings Db { get; set; } = new();
    public string? RememberedUsername { get; set; }
    /// <summary>是否记住登录密码（密码本身单独加密存放）。</summary>
    public bool RememberLoginPassword { get; set; }
    /// <summary>AES（静态密钥，跨机器可解）加密后的登录密码，形如 "AX:<base64>"。</summary>
    public string? EncryptedLoginPassword { get; set; }
    /// <summary>
    /// 客户端升级检查地址（例如 http://192.168.1.35/client-update/）。
    /// 留空 = 完全不检查更新；现场在「登录页 → 数据库/本机设置」里填写，或直接改 config.json。
    /// </summary>
    public string? UpdateUrl { get; set; }

    /// <summary>上次打印使用的打印机名；下次打印预览默认选中它（随 config.json 走，本机记忆）。</summary>
    public string? LastPrinterName { get; set; }

    /// <summary>一次性清理旧 B/S 系统无用表完成标记。</summary>
    public bool LegacyCleanupDone { get; set; }
    /// <summary>一次性把模板 variables 从 content JSON 迁到 label_template_var 完成标记。</summary>
    public bool VarMigrationDone { get; set; }
}

/// <summary>本地配置：exe 同目录 config.json（不随发布打包，现场首启生成）。</summary>
public class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(App.AppDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppConfig Current { get; private set; } = new();

    public ConfigService() => Load();

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Current = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取配置失败，使用默认配置");
        }
    }

    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, JsonOpts));
    }

    public string Password =>
        Environment.GetEnvironmentVariable("UX_DB_PASSWORD")   // 开发/自检优先
        ?? Unprotect(Current.Db.EncryptedPassword)
        ?? "";

    public void SetPassword(string password)
    {
        Current.Db.EncryptedPassword = string.IsNullOrEmpty(password) ? null : Protect(password);
        Save();
    }

    /// <summary>记住上次打印用的打印机；传入 null/空则清除。</summary>
    public void SetLastPrinter(string? name)
    {
        Current.LastPrinterName = string.IsNullOrWhiteSpace(name) ? null : name;
        Save();
    }

    /// <summary>已保存的登录密码（解密失败或换用户时返回 null）。</summary>
    public string? RememberedPassword => Unprotect(Current.EncryptedLoginPassword);

    /// <summary>登录成功后保存/清除凭据。记住密码时一定同时记住工号，否则下次无法自动填全。</summary>
    public void SaveCredentials(string username, bool rememberUsername,
        bool rememberPassword, string? password)
    {
        Current.RememberLoginPassword = rememberPassword;
        Current.RememberedUsername = (rememberUsername || rememberPassword) ? username : null;
        Current.EncryptedLoginPassword = rememberPassword ? Protect(password) : null;
        Save();
    }

    /// <summary>取消勾选「记住密码」时立即清除已存密码；改密成功后用同一方法写新密码。</summary>
    public void SetRememberedPassword(string? password)
    {
        Current.RememberLoginPassword = !string.IsNullOrEmpty(password);
        Current.EncryptedLoginPassword = string.IsNullOrEmpty(password) ? null : Protect(password);
        Save();
    }

    public string ConnectionString =>
        $"Server={Current.Db.Host};Port={Current.Db.Port};Database={Current.Db.Database};" +
        $"User ID={Current.Db.User};Password={Password};" +
        $"Character Set=utf8mb4;Connection Timeout=15;Default Command Timeout=60;" +
        $"AllowPublicKeyRetrieval=true;AllowUserVariables=true;" +
        $"Pooling=true;ConnectionIdleTimeout=3600;Keepalive=60;ConnectionProtocol=Socket;AutoEnlist=false";

    public bool TestConnection(out string error)
    {
        try
        {
            using var conn = new MySqlConnection(ConnectionString);
            conn.Open();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 加密凭据：始终使用可跨机器解密的 AES（静态密钥）。
    /// 旧版 DPAPI（CurrentUser）密文无 "AX:" 前缀，仅原机器原用户可解；
    /// 换机器后 Unprotect 返回 null，由用户重新输入一次即可，后续即转为可移植格式。
    /// </summary>
    private const string AesPrefix = "AX:";
    private static readonly byte[] AesKey = SHA256.HashData(Encoding.UTF8.GetBytes("UXLabelShop-Static-Config-Key-2026"));
    private static readonly byte[] AesIv = MD5.HashData(Encoding.UTF8.GetBytes("UXLabelShop-Static-IV-2026"));

    public static string? Protect(string? plain) => EncryptSecret(plain);
    public static string? Unprotect(string? enc) => DecryptSecret(enc);

    /// <summary>对外可测的加密（AES，输出 "AX:" + Base64）。</summary>
    public static string? EncryptSecret(string? plain)
    {
        if (plain == null) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = AesKey;
            aes.IV = AesIv;
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            using (var w = new StreamWriter(cs, Encoding.UTF8))
                w.Write(plain);
            return AesPrefix + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>对外可测的解密：AX: 前缀走 AES（跨机器可用）；否则尝试旧 DPAPI（仅原机）。</summary>
    public static string? DecryptSecret(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return null;
        try
        {
            if (enc.StartsWith(AesPrefix, StringComparison.Ordinal))
            {
                var data = Convert.FromBase64String(enc[AesPrefix.Length..]);
                using var aes = Aes.Create();
                aes.Key = AesKey;
                aes.IV = AesIv;
                using var ms = new MemoryStream(data);
                using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
                using var r = new StreamReader(cs, Encoding.UTF8);
                return r.ReadToEnd();
            }
            // 兼容旧版 DPAPI 密文（原机器原用户）
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // 换机器/换用户后解密失败 → 重新输入
        }
    }
}
