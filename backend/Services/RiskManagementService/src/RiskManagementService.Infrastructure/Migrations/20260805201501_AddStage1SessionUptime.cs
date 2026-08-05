using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Infrastructure.Migrations
{
    /// <summary>
    /// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150:
    /// Stage 1 の「60 営業日」（期間カウント）の供給元を**稼働の観測ログ**（<c>stage1_session_uptime</c>）とし、
    /// <c>stage_performance</c> の <c>Stage1QualifiedTradingDays</c> / <c>Stage1ExcludedInternalPaperDays</c> 列を落とす。
    /// <para>
    /// 旧列には供給元が無く、常に 0 だった（#333 以来「日次の稼働分数を記録するドライバが無い」状態であり、
    /// IADR-0137 が残余リスクとして明記していた）。供給元が別テーブルへ移った以上この列は死ぬ。
    /// 日数を両方に持つと供給元が 2 つになり必ず食い違う——一方が算入規則（両仮説で 50% 以上）と
    /// 「1 取引日 1 行」を担保するのに対し、他方は担保しない。死んだ列を残すと「まだ使う値」に見え、
    /// 次の実装者が判定へ結線し直す余地が残る
    /// （IADR-0137 決定2 / IADR-0148 決定2 / IADR-0149 決定3 と同じ規律）。
    /// </para>
    /// <para>
    /// 新表の主キー <c>(SessionDateEasternTime, Provider)</c> が「1 取引日 1 発注先 1 行」＝観測の集約単位そのものを
    /// 担保する。probe の巡回が何度届いても行は増えない。稼働分数を 2 列持つのは、その日の実際の通常取引時間を
    /// 実装が知らないためである（半日 210 分／通常日 390 分の両窓で分数を持ち、両仮説で判定する）。
    /// </para>
    /// <para>
    /// <c>Down</c> は 2 列を <c>NOT NULL DEFAULT 0</c> で復元する（旧スキーマと同一）。観測ログの行は失われるが、
    /// 稼働観測そのものの記録は AuditService 側（<c>BrokerAvailabilityObserved</c>）に残る。
    /// </para>
    /// </summary>
    public partial class AddStage1SessionUptime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 供給元の無い列（常に 0）を落とす。値の損失は無い——この列に意味のある値が入る経路は存在しなかった。
            migrationBuilder.DropColumn(
                name: "Stage1ExcludedInternalPaperDays",
                table: "stage_performance");

            migrationBuilder.DropColumn(
                name: "Stage1QualifiedTradingDays",
                table: "stage_performance");

            // 1 取引日 1 発注先 1 行（複合主キーが観測の集約単位そのもの）。probe の巡回では行が増えない。
            migrationBuilder.CreateTable(
                name: "stage1_session_uptime",
                columns: table => new
                {
                    SessionDateEasternTime = table.Column<DateOnly>(type: "date", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    LastObservedMinuteOfDayEasternTime = table.Column<int>(type: "integer", nullable: false),
                    OperationalMinutesBeforeEarlyClose = table.Column<int>(type: "integer", nullable: false),
                    OperationalMinutesBeforeRegularClose = table.Column<int>(type: "integer", nullable: false),
                    MeetsUptimeThreshold = table.Column<bool>(type: "boolean", nullable: false),
                    QualifiesTowardStage1 = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stage1_session_uptime", x => new { x.SessionDateEasternTime, x.Provider });
                });

            migrationBuilder.CreateIndex(
                name: "IX_stage1_session_uptime_QualifiesTowardStage1_MeetsUptimeThre~",
                table: "stage1_session_uptime",
                columns: new[] { "QualifiesTowardStage1", "MeetsUptimeThreshold" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stage1_session_uptime");

            migrationBuilder.AddColumn<int>(
                name: "Stage1ExcludedInternalPaperDays",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Stage1QualifiedTradingDays",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
