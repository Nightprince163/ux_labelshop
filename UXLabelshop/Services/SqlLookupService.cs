using System.Data;
using System.Globalization;
using MySqlConnector;
using Serilog;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>
/// 公式里 DBLOOKUP 的执行器：
/// - 每次从连接池取一条连接（用完归还），不再长期持有单连接；
///   避免闲置后单连接死亡、重建时触发服务端对新建连接的反向 DNS 查询（约 10s）。连接池会
///   自动回收死连接、剔除无效连接；配合 DbKeepAliveService 后台心跳，池里始终是热连接。
/// - 单条查询 5 秒超时，超时按公式错误处理（可被 IFERROR 接住）；
/// - 结果按「SQL + 参数」做 60 秒 TTL 缓存，预览/试打/打印不会重复压库；
/// - 连接级故障自动丢弃并重试一次（连接池会回收死连接、补建新连接）。
/// 只做执行，SQL 的只读校验与参数化在 <see cref="SqlGuard"/> 里完成。
/// </summary>
public sealed class SqlLookupService : IFormulaHost, IDisposable
{
    private const int TimeoutSeconds = 5;
    private const int CacheLimit = 512;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConfigService _config;
    private readonly object _gate = new();
    private readonly object _execGate = new();
    private readonly Dictionary<string, (string? Value, DateTime At)> _cache = new(StringComparer.Ordinal);

    public SqlLookupService(ConfigService config) => _config = config;

    /// <summary>缓存命中/未命中统计，自检与排障用。</summary>
    public int CacheHits { get; private set; }
    public int QueryCount { get; private set; }

    public string? Scalar(string sql, IReadOnlyList<string?> args)
    {
        var key = Key(sql, args);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
            {
                CacheHits++;
                return hit.Value;
            }
        }

        var value = Query(sql, args);
        lock (_gate)
        {
            if (_cache.Count >= CacheLimit) Prune();
            _cache[key] = (value, DateTime.UtcNow);
        }
        return value;
    }

    public void ClearCache()
    {
        lock (_gate) _cache.Clear();
    }

    /// <summary>数据库连接参数变更后调用：清掉旧结果缓存，下次查询用新连接串（池按新串另开）。</summary>
    public void Reset()
    {
        ClearCache();
    }

    private static string Key(string sql, IReadOnlyList<string?> args) =>
        sql + "\u0001" + args.Count.ToString(CultureInfo.InvariantCulture) + "\u0001" +
        string.Join("\u0001", args.Select(a => a ?? "\u0000"));

    private string? Query(string sql, IReadOnlyList<string?> args)
    {
        // DBLOOKUP 可能在 UI 线程同步执行，这里串行化，避免并发压库
        lock (_execGate)
        {
            try
            {
                QueryCount++;
                return ExecScalar(sql, args);
            }
            catch (Exception ex) when (IsConnectionFault(ex))
            {
                // 连接级故障（网络中断 / 中间设备回收空闲 TCP / 连接被服务端断开等）：
                // 从池取到的连接若是死的，丢弃后重试一次，连接池会回收死连接、补建新连接。
                Log.Warning(ex, "DBLOOKUP 连接故障，重试一次（连接池将回收死连接并补建）");
                QueryCount++;
                return ExecScalar(sql, args); // 重试；若仍失败则抛出原始异常
            }
            catch (FormulaException) { throw; }
            catch (Exception ex)
            {
                var msg = ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (msg.Length > 160) msg = msg[..160] + "…";
                throw new FormulaException("DBLOOKUP 查询失败：" + msg);
            }
        }
    }

    private string? ExecScalar(string sql, IReadOnlyList<string?> args)
    {
        // 每次从连接池取一条连接，用完归还。连接池会回收死连接、自动剔除无效连接。
        using var conn = new MySqlConnection(_config.ConnectionString);
        conn.Open();
        TrySetReadOnly(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = TimeoutSeconds;
        for (var i = 0; i < args.Count; i++)
            cmd.Parameters.AddWithValue("@p" + i.ToString(CultureInfo.InvariantCulture), args[i] ?? "");

        using var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
        if (!reader.Read() || reader.FieldCount == 0) return null;
        if (reader.IsDBNull(0)) return "";
        return reader.GetValue(0) switch
        {
            null or DBNull => "",
            byte[] bytes => Convert.ToHexString(bytes),
            var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
    }

    /// <summary>判断是否为连接/网络级故障（可重试）：服务器静默断开、IO/套接字中断等。</summary>
    private static bool IsConnectionFault(Exception ex) =>
        ex is MySqlException { IsTransient: true }
        || ex is System.IO.IOException
        || ex is System.Net.Sockets.SocketException
        || ex is System.IO.EndOfStreamException
        || ex is System.ObjectDisposedException;

    /// <summary>把会话设成只读：即便 SQL 校验被绕过，写入也会被数据库拒绝。失败则降级为只靠白名单。</summary>
    private static void TrySetReadOnly(MySqlConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET SESSION TRANSACTION READ ONLY";
            cmd.CommandTimeout = TimeoutSeconds;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DBLOOKUP：无法设置只读会话，仅依赖 SQL 白名单");
        }
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _cache.Where(kv => now - kv.Value.At >= CacheTtl).Select(kv => kv.Key).ToList())
            _cache.Remove(k);

        if (_cache.Count < CacheLimit) return;
        foreach (var k in _cache.OrderBy(kv => kv.Value.At).Take(_cache.Count / 2).Select(kv => kv.Key).ToList())
            _cache.Remove(k);
    }

    public void Dispose() { }
}
