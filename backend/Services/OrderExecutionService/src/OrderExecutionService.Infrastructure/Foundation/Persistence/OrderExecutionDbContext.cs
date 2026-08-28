using Microsoft.EntityFrameworkCore;

namespace OrderExecutionService.Infrastructure.Persistence;

// ADR-0001（Database per Service）: 発注執行サービス専有の DbContext（発注結果の履歴・発注予約）。
internal sealed class OrderExecutionDbContext(DbContextOptions<OrderExecutionDbContext> options)
    : DbContext(options)
{
    public DbSet<ExecutedOrderRow> ExecutedOrders => Set<ExecutedOrderRow>();

    // #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
    public DbSet<OrderDispatchReservationRow> DispatchReservations => Set<OrderDispatchReservationRow>();

    // #154, IADR-0067: 発注済み注文の訂正・取消（注文履歴テレメトリ）。追記専用。
    public DbSet<OrderLifecycleEventRow> OrderLifecycleEvents => Set<OrderLifecycleEventRow>();

    // FR-10, #331, IADR-0210: 保護逆指値レグ（ProtectiveStopGuard の巡回対象の権威）。
    public DbSet<ProtectiveStopOrderRow> ProtectiveStopOrders => Set<ProtectiveStopOrderRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // #131, IADR-0057: DecisionId を主キーにすることで、並行配送でも予約は高々1つに限定される
        // （＝ブローカ発注も高々1回）。この一意制約が二重発注防止の権威である。
        mb.Entity<OrderDispatchReservationRow>(e =>
        {
            e.ToTable("order_dispatch_reservations");
            e.HasKey(r => r.DecisionId);
            e.Property(r => r.DecisionId).ValueGeneratedNever();
            e.Property(r => r.BrokerOrderId).HasMaxLength(64);
            // 未確定（Reserved）の滞留＝要リコンサイルを洗い出すための検索用。
            e.HasIndex(r => r.State);
            // #137, IADR-0059: 保持期間パージ（WHERE State = Completed AND CompletedAt < cutoff
            // ORDER BY CompletedAt）用の複合インデックス。
            e.HasIndex(r => new { r.State, r.CompletedAt });
        });

        mb.Entity<ExecutedOrderRow>(e =>
        {
            e.ToTable("executed_orders");
            e.HasKey(r => r.OrderId);
            e.Property(r => r.OrderId).HasMaxLength(64).ValueGeneratedNever();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            // 監査・照会の既定並び（新しい順）と DecisionId 相関の検索用インデックス。
            e.HasIndex(r => r.ExecutedAt);
            e.HasIndex(r => r.DecisionId);
        });

        // FR-10, #331, IADR-0210 決定6: 保護逆指値レグ。EntryDecisionId 主キー（1 エントリー = 高々 1 保護・
        // 再発注は上書き）。Guard の巡回（State=Active を古い順）用の複合インデックスを持つ。
        mb.Entity<ProtectiveStopOrderRow>(e =>
        {
            e.ToTable("protective_stop_orders");
            e.HasKey(r => r.EntryDecisionId);
            e.Property(r => r.EntryDecisionId).ValueGeneratedNever();
            e.Property(r => r.StopOrderId).HasMaxLength(64).IsRequired();
            e.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            e.HasIndex(r => new { r.State, r.CreatedAt });
        });

        // #154, IADR-0067: 訂正・取消の追記専用台帳。
        mb.Entity<OrderLifecycleEventRow>(e =>
        {
            e.ToTable("order_lifecycle_events");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.OrderId).HasMaxLength(64).IsRequired();
            e.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
            // 注文単位の時系列照会（GetByOrderId・リコンサイル #141）用。
            e.HasIndex(r => new { r.OrderId, r.OccurredAt });
            // DecisionId 相関の検索用（既存の注文系と同じ相関キー）。
            e.HasIndex(r => r.DecisionId);
        });
    }
}
