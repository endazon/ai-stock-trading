using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// ADR-0001（Database per Service）, IADR-0012: リスク管理サービス専有の DbContext。
// 設定・kill switch・ロックアウトは単一行、変更履歴は追記専用。
internal sealed class RiskManagementDbContext(DbContextOptions<RiskManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<RiskSettingsRow> RiskSettings => Set<RiskSettingsRow>();

    public DbSet<KillSwitchRow> KillSwitch => Set<KillSwitchRow>();

    public DbSet<LockoutRow> Lockout => Set<LockoutRow>();

    public DbSet<SettingsChangeRow> SettingsChangeLog => Set<SettingsChangeRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<RiskSettingsRow>(e =>
        {
            e.ToTable("risk_settings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Json).HasColumnType("jsonb").IsRequired();
            // 楽観的排他制御: Version を並行トークンとして扱う（更新時に一致を要求）。
            e.Property(r => r.Version).IsConcurrencyToken();
        });

        mb.Entity<KillSwitchRow>(e =>
        {
            e.ToTable("kill_switch");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Actor).HasMaxLength(256);
            e.Property(r => r.Reason).HasMaxLength(1024);
        });

        mb.Entity<LockoutRow>(e =>
        {
            e.ToTable("lockout");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
        });

        mb.Entity<SettingsChangeRow>(e =>
        {
            e.ToTable("settings_change_log");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Actor).HasMaxLength(256).IsRequired();
            e.Property(r => r.ChangeType).HasMaxLength(64).IsRequired();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
            // 新しい順の照会が既定のため日時にインデックスを張る。
            e.HasIndex(r => r.ChangedAt);
        });
    }
}
