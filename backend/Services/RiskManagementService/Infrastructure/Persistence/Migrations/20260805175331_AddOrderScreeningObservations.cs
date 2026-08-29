using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148:
    /// クラス C 統制違反件数の供給元を**発注審査の観測ログ**（<c>order_screening_observations</c>）とし、
    /// <c>stage_performance.ControlViolationCount</c> 列を落とす。
    /// <para>
    /// 旧列は非 nullable の <c>int</c> であり、**「未供給」と「違反 0 件」を区別できなかった**。
    /// 段階ゲートの他の入力（営業日数・取引件数）の 0 は「未充足＝昇格しない」に倒れるが、
    /// 違反件数の 0 は「合格」を意味するため、供給元の不在がそのまま合格として読まれていた（#387）。
    /// 供給元が別テーブルへ移った以上この列は死ぬ。死んだ列を残すと「まだ使う値」に見え、
    /// 次の実装者が判定へ結線し直す余地が残るため落とす（IADR-0137 決定2 と同じ規律）。
    /// </para>
    /// <para>
    /// <c>Down</c> は列を <c>NOT NULL DEFAULT 0</c> で復元する（旧スキーマと同一）。観測ログの行は失われるが、
    /// 拒否そのものの監査証跡は <c>OrderRejected</c> として AuditService 側に残る。
    /// </para>
    /// </summary>
    public partial class AddOrderScreeningObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 供給元の無い列（常に 0）を落とす。値の損失は無い——この列に意味のある値が入る経路は存在しなかった。
            migrationBuilder.DropColumn(
                name: "ControlViolationCount",
                table: "stage_performance");

            // 1 審査 1 行（DecisionId が主キー＝計上単位「1 回の発注拒否につき 1 件」そのもの）。
            // 承認された審査も残す——記録が違反だけだと「違反 0 件」を主張する根拠が無く、未供給と区別できない。
            migrationBuilder.CreateTable(
                name: "order_screening_observations",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    CountsTowardStage1 = table.Column<bool>(type: "boolean", nullable: false),
                    IsControlViolation = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_screening_observations", x => x.DecisionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_screening_observations_CountsTowardStage1_IsControlVi~",
                table: "order_screening_observations",
                columns: new[] { "CountsTowardStage1", "IsControlViolation" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_screening_observations");

            migrationBuilder.AddColumn<int>(
                name: "ControlViolationCount",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
