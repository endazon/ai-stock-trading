using Microsoft.EntityFrameworkCore;

namespace ReportService.Infrastructure.Persistence;

// ADR-0001（Database per Service）, IADR-0012/0024: 報告書サービス専有の DbContext。PeriodKey ごとに 1 行・Version 楽観排他。
public sealed class ReportDbContext(DbContextOptions<ReportDbContext> options)
    : DbContext(options)
{
    public DbSet<ReportRow> Reports => Set<ReportRow>();

    // FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 1: `/policy` の試行の台帳（1 日の回数上限・案の監査）。
    public DbSet<PolicyRevisionAttemptRow> PolicyRevisionAttempts => Set<PolicyRevisionAttemptRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<ReportRow>(e =>
        {
            e.ToTable("reports");
            e.HasKey(r => r.PeriodKey);
            e.Property(r => r.PeriodKey).HasMaxLength(64).ValueGeneratedNever();
            e.Property(r => r.BasedOn).HasMaxLength(64);
            e.Property(r => r.PolicySummary).HasMaxLength(8192);
            // #840, IADR-0352 決定 5: 列挙名のカンマ区切り（全 12 語で 170 文字ほど）。語彙の追加に備えて余裕を持たせる。
            e.Property(r => r.UnsuppliedInputs).HasMaxLength(1024);
            // FR-07, IADR-0071 決定5: レビュー局面（対話的確定）。既定は Drafting（列挙 int 既定 0）。
            e.Property(r => r.ReviewState);
            // 楽観的排他制御: Version を並行トークンとして扱う（更新時に一致を要求・IADR-0012）。
            e.Property(r => r.Version).IsConcurrencyToken();
            // 最新の確定済み日報の照会（種別・状態・期間）に用いるインデックス。
            e.HasIndex(r => new { r.Kind, r.State, r.PeriodStart });
        });

        mb.Entity<PolicyRevisionAttemptRow>(e =>
        {
            e.ToTable("policy_revision_attempts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Actor).HasMaxLength(128);
            e.Property(a => a.PeriodKey).HasMaxLength(64);
            // 案の入れ替え（追加 5・除外 5・理由 200 文字まで）の JSON。
            e.Property(a => a.WatchlistChangesJson).HasMaxLength(8192);
            // #1025: 案を作った時点の監視銘柄（数十銘柄）と適用の内訳。
            e.Property(a => a.WatchlistSnapshotJson).HasMaxLength(8192);
            e.Property(a => a.WatchlistApplyJson).HasMaxLength(8192);
            // #1025: 確定した版の案を引く（会話キー＋版）。
            e.HasIndex(a => new { a.PeriodKey, a.ReportVersion });
            // 1 日の回数上限の判定（JST の暦日ごとの件数）。
            e.HasIndex(a => a.JstDate);
        });
    }
}
