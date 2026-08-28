using Microsoft.EntityFrameworkCore;

namespace CostControlService.Infrastructure.Persistence;

// ADR-0001（Database per Service）, IADR-0027: 費用統制サービス専有の DbContext（月次費用台帳・追記専用）。
internal sealed class CostControlDbContext(DbContextOptions<CostControlDbContext> options)
    : DbContext(options)
{
    public DbSet<CostEntryRow> CostEntries => Set<CostEntryRow>();

    // IADR-0055 決定5: 消費済みメッセージ ID（LlmCostIncurred の二重計上防止）。費用台帳とは別テーブル。
    public DbSet<ProcessedMessageRow> ProcessedMessages => Set<ProcessedMessageRow>();

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

        mb.Entity<ProcessedMessageRow>(e =>
        {
            e.ToTable("processed_messages");
            // 主キー（一意制約）そのものが重複排除の要。挿入衝突＝既処理。
            e.HasKey(r => r.MessageId);
            e.Property(r => r.MessageId).ValueGeneratedNever();
            // #137, IADR-0059: 保持期間パージ（WHERE ProcessedAt < cutoff ORDER BY ProcessedAt）用。
            e.HasIndex(r => r.ProcessedAt);
        });
    }
}
