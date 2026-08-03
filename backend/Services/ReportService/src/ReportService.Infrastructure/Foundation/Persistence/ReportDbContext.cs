using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.Report.Infrastructure.Foundation.Persistence;

// ADR-0001（Database per Service）, IADR-0012/0024: 報告書サービス専有の DbContext。PeriodKey ごとに 1 行・Version 楽観排他。
internal sealed class ReportDbContext(DbContextOptions<ReportDbContext> options)
    : DbContext(options)
{
    public DbSet<ReportRow> Reports => Set<ReportRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<ReportRow>(e =>
        {
            e.ToTable("reports");
            e.HasKey(r => r.PeriodKey);
            e.Property(r => r.PeriodKey).HasMaxLength(64).ValueGeneratedNever();
            e.Property(r => r.BasedOn).HasMaxLength(64);
            e.Property(r => r.PolicySummary).HasMaxLength(8192);
            // FR-07, IADR-0071 決定5: レビュー局面（対話的確定）。既定は Drafting（列挙 int 既定 0）。
            e.Property(r => r.ReviewState);
            // 楽観的排他制御: Version を並行トークンとして扱う（更新時に一致を要求・IADR-0012）。
            e.Property(r => r.Version).IsConcurrencyToken();
            // 最新の確定済み日報の照会（種別・状態・期間）に用いるインデックス。
            e.HasIndex(r => new { r.Kind, r.State, r.PeriodStart });
        });
    }
}
