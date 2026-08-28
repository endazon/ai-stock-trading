using Microsoft.EntityFrameworkCore;

namespace ConfigurationService.Infrastructure.Persistence;

// ADR-0001（Database per Service）, IADR-0012/0021: 設定管理サービス専有の DbContext。
// 前提条件は単一行（Json＋Version）、変更履歴は追記専用。
internal sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options)
    : DbContext(options)
{
    public DbSet<AssumptionsRow> Assumptions => Set<AssumptionsRow>();

    public DbSet<AssumptionsChangeRow> AssumptionsChangeLog => Set<AssumptionsChangeRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AssumptionsRow>(e =>
        {
            e.ToTable("assumptions");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Json).HasColumnType("jsonb").IsRequired();
            // 楽観的排他制御: Version を並行トークンとして扱う（更新時に一致を要求・IADR-0012）。
            e.Property(r => r.Version).IsConcurrencyToken();
        });

        mb.Entity<AssumptionsChangeRow>(e =>
        {
            e.ToTable("assumptions_change_log");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Actor).HasMaxLength(256).IsRequired();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
            // 新しい順の照会が既定のため日時にインデックスを張る。
            e.HasIndex(r => r.ChangedAt);
        });
    }
}
