using System.Threading;
using MySqlConnector;
using Serilog;
using UXLabelshop.Services;

namespace UXLabelshop.Services;

/// <summary>
/// 数据库连接保活：在后台线程定期执行一次轻量 SELECT 1，确保连接池里始终有"热"连接，
/// 且空闲 TCP 不被中间网络设备回收。
/// 意义：MySQL 服务端会对每条"新建"连接做反向 DNS 查询（约 10 秒），客户端闲置后连接失效、
/// 再操作就必须新建连接从而卡顿。保活让"新建连接的代价"发生在后台空闲时刻，用户操作时复用
/// 热连接，永不卡顿（除非服务端 skip-name-resolve 已开启，那样新建也只需几毫秒）。
/// 即便保活偶发触发一次新建（连接已死），也在后台线程执行，不阻塞 UI。
/// </summary>
public sealed class DbKeepAliveService : IDisposable
{
    private readonly ConfigService _config;
    private Timer? _timer;
    private readonly object _gate = new();

    public DbKeepAliveService(ConfigService config) => _config = config;

    /// <summary>启动保活心跳，interval 为两次心跳间隔。</summary>
    public void Start(TimeSpan interval)
    {
        Stop();
        lock (_gate)
        {
            _timer = new Timer(_ => Ping(), null, interval, interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Ping()
    {
        try
        {
            using var conn = new MySqlConnection(_config.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 5;
            cmd.ExecuteScalar();
        }
        catch (Exception ex)
        {
            // 保活失败忽略：可能连接暂时不可用，下次心跳再试；不影响用户操作
            Log.Debug(ex, "数据库保活心跳失败（忽略）");
        }
    }

    public void Dispose() => Stop();
}
