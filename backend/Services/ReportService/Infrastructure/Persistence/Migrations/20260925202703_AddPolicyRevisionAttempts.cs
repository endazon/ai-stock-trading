using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 1: `/policy` の試行の台帳（1 日の回数上限・案の監査）。
    /// <inheritdoc />
    public partial class AddPolicyRevisionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "policy_revision_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    JstDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ReportVersion = table.Column<int>(type: "integer", nullable: true),
                    WatchlistChangesJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_revision_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_policy_revision_attempts_JstDate",
                table: "policy_revision_attempts",
                column: "JstDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policy_revision_attempts");
        }
    }
}
