using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-20, #333, 06_daytrading-review §4.1〜§4.3: Stage 1（SIMULATE）の合格条件を
    /// 「実際に取引できた日数」と「取引件数」の 2 条件へ改める（INDEX 決定 34・42）。
    /// <para>
    /// あわせて <c>PaperDeviationExplained</c> を落とす。旧基準「バックテストとの乖離が説明可能な範囲」は
    /// **計画 §4.1 が合格基準から削除した**（検証不能な条件を機械判定のゲートに据えると、判定できないか
    /// 恣意的に判定されるかのどちらかになる）。検証の観点は月報の三者比較へ移設された。
    /// 列を残すと供給元の無い列が「まだ使う値」に見えるため落とす。
    /// </para>
    /// </summary>
    public partial class AddStage1Progress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaperDeviationExplained",
                table: "stage_performance");

            // 既定 0 ＝ fail-safe（供給が無い限り Stage 1 → 2 の昇格は起こらない）。
            migrationBuilder.AddColumn<int>(
                name: "Stage1QualifiedTradingDays",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Stage1TradeCount",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Stage1QualifiedTradingDays",
                table: "stage_performance");

            migrationBuilder.DropColumn(
                name: "Stage1TradeCount",
                table: "stage_performance");

            migrationBuilder.AddColumn<bool>(
                name: "PaperDeviationExplained",
                table: "stage_performance",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
