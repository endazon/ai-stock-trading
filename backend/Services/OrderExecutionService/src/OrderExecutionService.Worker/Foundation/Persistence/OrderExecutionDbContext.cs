using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.OrderExecution.Worker.Foundation.Persistence;

// ADR-0001（Database per Service）: 発注執行サービス専有の DbContext（発注結果の履歴）。
internal sealed class OrderExecutionDbContext(DbContextOptions<OrderExecutionDbContext> options)
    : DbContext(options)
{
    public DbSet<ExecutedOrderRow> ExecutedOrders => Set<ExecutedOrderRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
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
