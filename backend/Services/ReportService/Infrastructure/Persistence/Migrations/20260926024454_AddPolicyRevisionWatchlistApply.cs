using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // FR-13, ADR-0042 決定 1, #1025, IADR-0433 決定 1: 案を作った時点の監視銘柄・適用の内訳・記録時刻（text）と、会話キー＋版の索引。
    /// <inheritdoc />
    public partial class AddPolicyRevisionWatchlistApply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WatchlistAppliedAt",
                table: "policy_revision_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchlistApplyJson",
                table: "policy_revision_attempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchlistSnapshotJson",
                table: "policy_revision_attempts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_revision_attempts_PeriodKey_ReportVersion",
                table: "policy_revision_attempts",
                columns: new[] { "PeriodKey", "ReportVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_policy_revision_attempts_PeriodKey_ReportVersion",
                table: "policy_revision_attempts");

            migrationBuilder.DropColumn(
                name: "WatchlistAppliedAt",
                table: "policy_revision_attempts");

            migrationBuilder.DropColumn(
                name: "WatchlistApplyJson",
                table: "policy_revision_attempts");

            migrationBuilder.DropColumn(
                name: "WatchlistSnapshotJson",
                table: "policy_revision_attempts");
        }
    }
}
