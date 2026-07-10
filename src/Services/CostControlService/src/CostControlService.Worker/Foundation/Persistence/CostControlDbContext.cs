using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// ADR-0001（Database per Service）, IADR-0027: 費用統制サービス専有の DbContext（月次費用台帳・追記専用）。
internal sealed class CostControlDbContext(DbContextOptions<CostControlDbContext> options)
    : DbContext(options)
{
    public DbSet<CostEntryRow> CostEntries => Set<CostEntryRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<CostEntryRow>(e =>
        {
            e.ToTable("cost_entries");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Month).HasMaxLength(7).IsRequired();
            // 月・カテゴリ別の集計に用いるインデックス。
            e.HasIndex(r => new { r.Month, r.Category });
        });
    }
}
