using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace UXLabelshop.Services;

/// <summary>
/// 数据库初始化：
/// 1. 一次性清理旧 B/S 系统无用表（保留模板/版本/扫码规则/字段字典）；
/// 2. 确保新表存在（sys_user / label_print_record / label_print_record_item）；
/// 3. 空库时种子管理员 admin / Admin@123（首次登录强制改密）。
/// </summary>
public class DbInitService
{
    private readonly ConfigService _config;
    private readonly IServiceProvider _sp;

    public DbInitService(ConfigService config, IServiceProvider sp)
    {
        _config = config;
        _sp = sp;
    }

    public bool Initialized { get; private set; }

    private readonly object _lock = new();

    public void Initialize()
    {
        lock (_lock)
        {
            if (Initialized) return;
            InitializeCore();
            Initialized = true;
        }
    }

    private void InitializeCore()
    {
        using var conn = new MySqlConnection(_config.ConnectionString);
        conn.Open();

        // 1. 旧表清理（幂等）
        if (!_config.Current.LegacyCleanupDone)
        {
            Exec(conn, "SET FOREIGN_KEY_CHECKS=0");
            foreach (var t in new[]
                     {
                         "label_audit_log", "label_print_event", "label_print_item", "label_print_job",
                         "label_printer_station", "label_user", "label_role_perm", "label_perm", "label_role",
                     })
                Exec(conn, $"DROP TABLE IF EXISTS `{t}`");
            Exec(conn, "SET FOREIGN_KEY_CHECKS=1");

            _config.Current.LegacyCleanupDone = true;
            _config.Save();
            Serilog.Log.Information("旧表清理完成");
        }

        // 2. 新表 DDL（幂等）
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS `sys_user` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `username` VARCHAR(32) NOT NULL COMMENT '登录工号',
              `name` VARCHAR(64) NOT NULL,
              `pwd_hash` VARCHAR(128) NOT NULL COMMENT 'bcrypt',
              `role` ENUM('user','admin') NOT NULL DEFAULT 'user',
              `enabled` TINYINT NOT NULL DEFAULT 1,
              `must_change_pwd` TINYINT NOT NULL DEFAULT 0,
              `last_login_at` DATETIME NULL,
              `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
              `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
              PRIMARY KEY (`id`),
              UNIQUE KEY `uk_username` (`username`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='客户端用户'
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS `label_print_record` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `job_no` VARCHAR(32) NOT NULL COMMENT '打印任务号 yyyymmdd-nnnn',
              `template_id` BIGINT UNSIGNED NULL,
              `template_code` VARCHAR(64) NULL,
              `template_snapshot` JSON NULL COMMENT '打印时模板快照，保证历史可复现',
              `user_id` BIGINT UNSIGNED NULL,
              `username` VARCHAR(32) NULL,
              `printer_name` VARCHAR(64) NULL,
              `print_mode` VARCHAR(16) NOT NULL DEFAULT 'windows',
              `total_qty` INT NOT NULL DEFAULT 0,
              `ok_qty` INT NOT NULL DEFAULT 0,
              `status` ENUM('done','partial','failed','cancelled') NOT NULL DEFAULT 'done',
              `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
              PRIMARY KEY (`id`),
              UNIQUE KEY `uk_job_no` (`job_no`),
              KEY `idx_created` (`created_at`),
              KEY `idx_tpl` (`template_id`),
              KEY `idx_user` (`user_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='打印记录（一次清单打印一行）'
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS `label_print_record_item` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `record_id` BIGINT UNSIGNED NOT NULL,
              `seq` INT NOT NULL,
              `barcode` VARCHAR(128) NULL COMMENT '关键字段拆列，便于追溯',
              `model` VARCHAR(64) NULL,
              `batch` VARCHAR(64) NULL,
              `lot` VARCHAR(64) NULL,
              `dc` VARCHAR(32) NULL,
              `qty` VARCHAR(32) NULL,
              `data` JSON NULL COMMENT '完整变量数据',
              `status` ENUM('ok','fail','skipped') NOT NULL DEFAULT 'ok',
              PRIMARY KEY (`id`),
              KEY `idx_record` (`record_id`),
              KEY `idx_barcode` (`barcode`),
              CONSTRAINT `fk_item_record` FOREIGN KEY (`record_id`) REFERENCES `label_print_record` (`id`) ON DELETE CASCADE ON UPDATE RESTRICT
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='打印明细（每张标签一行，1行=1份）'
            """);

        // 3. 模板变量子表（变量的唯一权威来源；快照仍内嵌在版本/打印记录中）
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS `label_template_var` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `template_id` BIGINT UNSIGNED NOT NULL COMMENT '所属模板',
              `var_key` VARCHAR(64) NOT NULL COMMENT '变量 key',
              `label` VARCHAR(128) NULL COMMENT '显示名',
              `short` VARCHAR(128) NULL,
              `source` VARCHAR(16) NOT NULL DEFAULT 'scan' COMMENT 'scan/fixed/computed',
              `default_value` VARCHAR(255) NULL COMMENT '缺省值：scan 变量为空时套用',
              `formula` TEXT NULL COMMENT 'Excel 风格公式，以 = 开头',
              `sample` VARCHAR(255) NULL COMMENT '样例值（预览/公式测试用）',
              `value` VARCHAR(255) NULL COMMENT 'source=fixed 的固定值',
              `sort_no` INT NOT NULL DEFAULT 0 COMMENT '清单列顺序',
              `extra_json` JSON NULL COMMENT '原 JSON 里未纳入本表的字段（如 required），避免迁移丢信息',
              PRIMARY KEY (`id`),
              UNIQUE KEY `uk_tpl_key` (`template_id`,`var_key`),
              KEY `idx_tpl_sort` (`template_id`,`sort_no`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='模板变量定义（子表）'
            """);

        // 4. 一次性迁移：把当前 content JSON 里的 variables 抽到子表（幂等，按模板跳过已有行）
        if (!_config.Current.VarMigrationDone)
        {
            MigrateVariables(conn);
            _config.Current.VarMigrationDone = true;
            _config.Save();
            Serilog.Log.Information("模板变量迁移完成");
        }

        // 5. 种子管理员
        var count = ScalarLong(conn, "SELECT COUNT(*) FROM `sys_user`");
        if (count == 0)
        {
            var hash = AuthService.Hash("Admin@123");
            Exec(conn,
                $"INSERT INTO `sys_user` (`username`,`name`,`pwd_hash`,`role`,`enabled`,`must_change_pwd`) " +
                $"VALUES ('admin','系统管理员','{hash}','admin',1,1)");
            Serilog.Log.Information("已创建默认管理员 admin / Admin@123（首次登录强制改密）");
        }

        Serilog.Log.Information("数据库初始化完成");
    }

    /// <summary>
    /// 一次性迁移：把每个模板当前 content JSON 里的 variables 数组转成子表行。
    /// 未纳入子表的字段（required 等）原样塞进 extra_json，避免丢信息。
    /// 已有变量行的模板直接跳过 —— 保证重复启动不会覆盖用户后来编辑过的变量。
    /// </summary>
    private static void MigrateVariables(MySqlConnection conn)
    {
        var templates = new List<(long Id, string Content)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT t.`id`, v.`content`
                FROM `label_template` t
                JOIN `label_template_ver` v ON t.`current_ver_id` = v.`id`
                WHERE v.`content` IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                templates.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "key", "label", "short", "source", "default_value", "formula",
            "sample", "value", "sort_no", "var_key", "extra_json",
        };

        foreach (var (templateId, content) in templates)
        {
            if (ScalarLong(conn, $"SELECT COUNT(*) FROM `label_template_var` WHERE `template_id`={templateId}") > 0)
                continue;

            JsonDocument root;
            try { root = JsonDocument.Parse(content); }
            catch (JsonException ex) { Serilog.Log.Warning(ex, "模板 {Id} content 不是合法 JSON，跳过变量迁移", templateId); continue; }
            using (root)
            {
                if (!root.RootElement.TryGetProperty("variables", out var vars) ||
                    vars.ValueKind != JsonValueKind.Array) continue;

                var sort = 0;
                foreach (var v in vars.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    var key = GetStr(v, "key");
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var extra = new Dictionary<string, JsonElement>();
                    foreach (var p in v.EnumerateObject())
                        if (!known.Contains(p.Name)) extra[p.Name] = p.Value.Clone();

                    sort++;
                    using var ins = conn.CreateCommand();
                    ins.CommandText = """
                        INSERT IGNORE INTO `label_template_var`
                          (`template_id`,`var_key`,`label`,`short`,`source`,`default_value`,
                           `formula`,`sample`,`value`,`sort_no`,`extra_json`)
                        VALUES (@tid,@key,@label,@short,@source,@default_value,
                                @formula,@sample,@value,@sort_no,@extra_json)
                        """;
                    ins.Parameters.AddWithValue("@tid", templateId);
                    ins.Parameters.AddWithValue("@key", key);
                    ins.Parameters.AddWithValue("@label", (object?)GetStr(v, "label") ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@short", (object?)GetStr(v, "short") ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@source", string.IsNullOrEmpty(GetStr(v, "source")) ? "scan" : GetStr(v, "source"));
                    ins.Parameters.AddWithValue("@default_value", (object?)GetStr(v, "default_value") ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@formula", (object?)GetStr(v, "formula") ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@sample", (object?)GetStr(v, "sample") ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@value", (object?)GetStr(v, "value") ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@sort_no", sort);
                    ins.Parameters.AddWithValue("@extra_json",
                        extra.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(extra));
                    ins.ExecuteNonQuery();
                }
            }
        }
    }

    private static string? GetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static void Exec(MySqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(MySqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
