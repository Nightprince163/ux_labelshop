using Microsoft.EntityFrameworkCore;
using UXLabelshop.Models;
using UXLabelshop.Services;

namespace UXLabelshop;

public class AppDbContext : DbContext
{
    private readonly ConfigService _config;

    // 已确认服务器为 MySQL 8.0.46，无需 AutoDetect（避免启动时额外连接）
    private static readonly MySqlServerVersion ServerVersion = new(new Version(8, 0, 46));

    public AppDbContext(ConfigService config)
    {
        _config = config;
    }

    public DbSet<SysUser> SysUsers => Set<SysUser>();
    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();
    public DbSet<LabelTemplateVer> TemplateVers => Set<LabelTemplateVer>();
    public DbSet<LabelTemplateVar> TemplateVars => Set<LabelTemplateVar>();
    public DbSet<LabelScanRule> ScanRules => Set<LabelScanRule>();
    public DbSet<LabelPrintRecord> PrintRecords => Set<LabelPrintRecord>();
    public DbSet<LabelPrintRecordItem> PrintRecordItems => Set<LabelPrintRecordItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseMySql(_config.ConnectionString, ServerVersion);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // 全局约定：C# 属性名 → snake_case 列名（沿用现有库命名）
        foreach (var entity in mb.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
        }

        mb.Entity<SysUser>(e =>
        {
            e.ToTable("sys_user");
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Username).IsUnique();
        });

        mb.Entity<LabelTemplate>(e =>
        {
            e.ToTable("label_template");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Source).HasMaxLength(16);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.Status);
        });

        mb.Entity<LabelTemplateVer>(e =>
        {
            e.ToTable("label_template_ver");
            e.Property(x => x.Content).HasColumnType("json");
            e.HasIndex(x => new { x.TemplateId, x.Ver }).IsUnique();
            e.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId);
        });

        mb.Entity<LabelTemplateVar>(e =>
        {
            e.ToTable("label_template_var");
            // key 是 MySQL 保留字，列名用 var_key，SQL 排查时无需反引号
            e.Property(x => x.Key).HasColumnName("var_key").HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Formula).HasColumnType("text");
            e.Property(x => x.ExtraJson).HasColumnType("json");
            e.HasIndex(x => new { x.TemplateId, x.Key }).IsUnique();
            e.HasIndex(x => new { x.TemplateId, x.SortNo });
        });

        mb.Entity<LabelScanRule>(e =>
        {
            e.ToTable("label_scan_rule");
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Mapping).HasColumnType("json");
            e.Property(x => x.SeqMap).HasColumnType("json");
            e.Property(x => x.SampleCodes).HasColumnType("json");
        });

        mb.Entity<LabelPrintRecord>(e =>
        {
            e.ToTable("label_print_record");
            e.Property(x => x.TemplateSnapshot).HasColumnType("json");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.PrintMode).HasMaxLength(16);
            e.HasIndex(x => x.JobNo).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.TemplateId);
            e.HasMany(x => x.Items).WithOne(x => x.Record)
                .HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<LabelPrintRecordItem>(e =>
        {
            e.ToTable("label_print_record_item");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Data).HasColumnType("json");
            e.HasIndex(x => x.RecordId);
            e.HasIndex(x => x.Barcode);
        });
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (name[i - 1] is not ('_' or 'A' or 'I') || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
