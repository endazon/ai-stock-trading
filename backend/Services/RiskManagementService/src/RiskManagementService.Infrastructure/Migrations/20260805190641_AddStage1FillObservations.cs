using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Infrastructure.Migrations
{
    /// <summary>
    /// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3, IADR-0149:
    /// Stage 1 の取引件数（100 件）の供給元を**約定の観測ログ**（<c>stage1_fill_observations</c>）とし、
    /// <c>stage_performance.Stage1TradeCount</c> 列を落とす。
    /// <para>
    /// 旧列には供給元が無く、常に 0 だった（[#334] の集計関数は一度も呼ばれていない）。
    /// 供給元が別テーブルへ移った以上この列は死ぬ。件数を両方に持つと供給元が 2 つになり必ず食い違う——
    /// 一方が計上単位（1 注文 1 行）を担保し、他方は担保しないためである。
    /// 死んだ列を残すと「まだ使う値」に見え、次の実装者が判定へ結線し直す余地が残る
    /// （IADR-0137 決定2 / IADR-0148 決定2 と同じ規律）。
    /// </para>
    /// <para>
    /// <c>Down</c> は列を <c>NOT NULL DEFAULT 0</c> で復元する（旧スキーマと同一）。観測ログの行は失われるが、
    /// 約定そのものの記録は取引台帳（<c>trade_fills</c>）と AuditService 側に残る。
    /// </para>
    /// </summary>
    public partial class AddStage1FillObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 供給元の無い列（常に 0）を落とす。値の損失は無い——この列に意味のある値が入る経路は存在しなかった。
            migrationBuilder.DropColumn(
                name: "Stage1TradeCount",
                table: "stage_performance");

            // 1 注文 1 行（DecisionId が主キー＝計上単位「約定した新規建て注文 1 件」そのもの）。
            // 分割約定の続報・イベント再送でも行は増えない。
            migrationBuilder.CreateTable(
                name: "stage1_fill_observations",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionDateEasternTime = table.Column<DateOnly>(type: "date", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    PositionEffect = table.Column<int>(type: "integer", nullable: false),
                    CountsTowardStage1 = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stage1_fill_observations", x => x.DecisionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stage1_fill_observations_CountsTowardStage1",
                table: "stage1_fill_observations",
                column: "CountsTowardStage1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stage1_fill_observations");

            migrationBuilder.AddColumn<int>(
                name: "Stage1TradeCount",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
