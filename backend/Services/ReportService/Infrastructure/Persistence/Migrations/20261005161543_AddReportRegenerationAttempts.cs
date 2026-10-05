using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // FR-06, FR-14, 計画 ADR-0052 決定 1・4・5, #1156, IADR-0491 決定 3・5: `/report regenerate` の試行の台帳
    // （`/policy` とは別枠の 1 日の回数上限・作り直した版の記録・断った回数）。表の追加だけ（既存の表は変えない）。
    /// <inheritdoc />
    public partial class AddReportRegenerationAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_regeneration_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    JstDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousVersion = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReportVersion = table.Column<int>(type: "integer", nullable: true),
                    UnsuppliedInputs = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    NotRestorableInputs = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_regeneration_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_regeneration_attempts_JstDate",
                table: "report_regeneration_attempts",
                column: "JstDate");

            migrationBuilder.CreateIndex(
                name: "IX_report_regeneration_attempts_PeriodKey_ReportVersion",
                table: "report_regeneration_attempts",
                columns: new[] { "PeriodKey", "ReportVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_regeneration_attempts");
        }
    }
}
