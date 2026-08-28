using Microsoft.EntityFrameworkCore;

namespace AuditService.Infrastructure.Persistence;

// ADR-0001（Database per Service）, IADR-0019: 監査サービス専有の DbContext（追記専用の監査台帳）。
// NFR/IADR-0259 決定4: プロジェクト境界が消えたため、Tests から直接参照する型は public にする
// （InternalsVisibleTo は使わない。基盤 MSP:IADR-0282 の NotificationDbContext と同じ判断）。
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AuditEventRow>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.EventType).HasMaxLength(64).IsRequired();
            e.Property(r => r.Symbol).HasMaxLength(32);
            e.Property(r => r.Summary).HasMaxLength(512).IsRequired();
            // イベント全量は JSON（jsonb）で保持する。
            e.Property(r => r.Detail).HasColumnType("jsonb").IsRequired();
            // 注文単位の相関照会と、新しい順の期間照会のためインデックスを張る。
            e.HasIndex(r => r.CorrelationId);
            e.HasIndex(r => r.OccurredAt);
        });
    }
}
