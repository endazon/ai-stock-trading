using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // FR-07, FR-15, 計画 ADR-0068 決定2・3, #1373, IADR-0548 決定1・6: 確定した版の履歴の表を足し、既存の確定済みの行を写す。
    // reports の表は変えない（会話キーごとに 1 行のまま。期間キーも変えない）。
    /// <inheritdoc />
    public partial class AddReportConfirmedVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_confirmed_versions",
                columns: table => new
                {
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BasedOn = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssumptionsVersion = table.Column<int>(type: "integer", nullable: false),
                    PolicySummary = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: true),
                    UnsuppliedInputs = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_confirmed_versions", x => new { x.Kind, x.Sequence });
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_confirmed_versions_Kind_PeriodStart_ConfirmedAt",
                table: "report_confirmed_versions",
                columns: new[] { "Kind", "PeriodStart", "ConfirmedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_report_confirmed_versions_PeriodKey_Version",
                table: "report_confirmed_versions",
                columns: new[] { "PeriodKey", "Version" },
                unique: true);

            // IADR-0548 決定6: 既存の確定済みの行を、その時点の行の版のまま履歴へ写す（各会話キーにつき 1 本＝最初の確定版）。
            // 本変更の前は確定済みの行が不変だった（UpsertDraft が拒む）ので、行の内容はそのまま確定した版の内容である。
            // 通し番号は種別ごとに確定時刻・会話キーの順で 1 から振る。ConfirmedAt の無い確定済みの行は Confirm が作らない
            // （確定は必ず時刻を書く）ため写さない。State の 1 は ReportState.Confirmed（列挙の序数）。
            migrationBuilder.Sql(
                """
                INSERT INTO report_confirmed_versions
                    ("Kind", "Sequence", "PeriodKey", "PeriodStart", "Version", "ConfirmedAt",
                     "BasedOn", "AssumptionsVersion", "PolicySummary", "Body", "UnsuppliedInputs")
                SELECT r."Kind",
                       ROW_NUMBER() OVER (PARTITION BY r."Kind" ORDER BY r."ConfirmedAt", r."PeriodKey"),
                       r."PeriodKey", r."PeriodStart", r."Version", r."ConfirmedAt",
                       r."BasedOn", r."AssumptionsVersion", r."PolicySummary", r."Body", r."UnsuppliedInputs"
                FROM reports r
                WHERE r."State" = 1 AND r."ConfirmedAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_confirmed_versions");
        }
    }
}
