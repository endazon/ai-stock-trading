using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // NFR-06, FR-13, IADR-0634 決定 7・8: 方針の案の確定が第 2 要素で Verified だったかと、入れ替えの適用の予約（1 回だけ）の列を足す。
    // 既存の行は「Verified の記録なし」（false）・予約なし。段 2 では、本列が入る前に確定した案は適用されない（安全側）。
    /// <inheritdoc />
    public partial class AddPolicyProposalSecondFactorAndApplyReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProposalSecondFactorVerified",
                table: "policy_revision_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WatchlistApplyReservedAt",
                table: "policy_revision_attempts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProposalSecondFactorVerified",
                table: "policy_revision_attempts");

            migrationBuilder.DropColumn(
                name: "WatchlistApplyReservedAt",
                table: "policy_revision_attempts");
        }
    }
}
