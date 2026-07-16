using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.OrderExecution.Worker.Foundation.Persistence;

// ADR-0001（Database per Service）: 発注執行サービス専有の DbContext（発注結果の履歴・発注予約）。
internal sealed class OrderExecutionDbContext(DbContextOptions<OrderExecutionDbContext> options)
    : DbContext(options)
{
    public DbSet<ExecutedOrderRow> ExecutedOrders => Set<ExecutedOrderRow>();

    // #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
    public DbSet<OrderDispatchReservationRow> DispatchReservations => Set<OrderDispatchReservationRow>();

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
    }
}
