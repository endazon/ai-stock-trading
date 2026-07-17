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

    public DbSet<ApprovedOrderRow> ApprovedOrders => Set<ApprovedOrderRow>();

    public DbSet<TradeFillRow> TradeFills => Set<TradeFillRow>();

    // FR-19, #154, IADR-0067: 相場操縦検知の入力＝注文アクティビティの射影。
    public DbSet<OrderActivityRow> OrderActivities => Set<OrderActivityRow>();

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

        // IADR-0018: 取引台帳（承認・約定）。追記専用。DecisionId/OrderId で冪等。
        mb.Entity<ApprovedOrderRow>(e =>
        {
            e.ToTable("approved_orders");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
        });

        mb.Entity<TradeFillRow>(e =>
        {
            e.ToTable("trade_fills");
            e.HasKey(r => r.OrderId);
            e.Property(r => r.OrderId).HasMaxLength(128).ValueGeneratedNever();
            // 承認 Intent との相関・時系列畳み込みのため DecisionId にインデックスを張る。
            e.HasIndex(r => r.DecisionId);
        });

        // FR-19, #154, IADR-0067: 注文アクティビティの射影（DecisionId で 1 注文＝1 行・更新される）。
        mb.Entity<OrderActivityRow>(e =>
        {
            e.ToTable("order_activity");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            // 相場操縦検知の窓照会（銘柄・市場別に発注時刻の範囲を切り出す）用の複合インデックス。
            e.HasIndex(r => new { r.Symbol, r.Market, r.PlacedAt });
        });
    }
}
