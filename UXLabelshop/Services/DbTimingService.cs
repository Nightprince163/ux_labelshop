using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace UXLabelshop.Services;

/// <summary>数据库连通性计时（排障用）。</summary>
public class DbTimingService
{
    private readonly ConfigService _config;
    private readonly IServiceProvider _sp;

    public DbTimingService(ConfigService config, IServiceProvider sp)
    {
        _config = config;
        _sp = sp;
    }

    public List<string> Run()
    {
        var lines = new List<string>();
        void Log(string msg) => lines.Add($"{DateTime.Now:HH:mm:ss.fff} {msg}");

        var sw = Stopwatch.StartNew();
        using (var conn = new MySqlConnection(_config.ConnectionString))
        {
            conn.Open();
            Log($"raw open #1: {sw.ElapsedMilliseconds} ms");
        }

        sw.Restart();
        using (var conn = new MySqlConnection(_config.ConnectionString))
        {
            conn.Open();
            conn.Close();
            Log($"raw open #2 (pooled): {sw.ElapsedMilliseconds} ms");
        }

        sw.Restart();
        using (var db = _sp.GetRequiredService<AppDbContext>())
        {
            var n = db.LabelTemplates.Count();
            Log($"EF count: {sw.ElapsedMilliseconds} ms (templates={n})");
        }

        sw.Restart();
        using (var db = _sp.GetRequiredService<AppDbContext>())
        {
            var n = db.SysUsers.Count();
            Log($"EF count #2: {sw.ElapsedMilliseconds} ms (users={n})");
        }

        return lines;
    }
}
