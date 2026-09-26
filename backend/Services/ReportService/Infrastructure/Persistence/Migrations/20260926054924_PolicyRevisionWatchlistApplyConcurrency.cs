using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // FR-13, ADR-0042 決定 1, #1029, IADR-0432（2026-09-26 追記）: policy_revision_attempts の WatchlistAppliedAt を同時実行のトークンにする。
    // **スキーマは変わらない**（トークンは UPDATE の WHERE 句に元の値〔IS NULL〕を足すだけで、列・索引・制約は同じ）。
    // モデルのスナップショットを進めるための空のマイグレーションである（スナップショットとモデルを食い違わせない）。
    /// <inheritdoc />
    public partial class PolicyRevisionWatchlistApplyConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // スキーマの変更なし（上の注記）。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // スキーマの変更なし（上の注記）。
        }
    }
}
