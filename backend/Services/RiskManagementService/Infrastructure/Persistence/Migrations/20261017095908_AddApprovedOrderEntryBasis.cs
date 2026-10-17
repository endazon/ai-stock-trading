using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-04, FR-10, #1384, IADR-0557 決定 4: 取引台帳の承認行 <c>approved_orders</c> に、新規建ての判断が建てた条件の記号
    /// （<c>EntryCondition</c>）と基準価格（<c>EntryReferencePrice</c>）の 2 列を足す。**追加のみ・既存行は NULL（記録なし）**。
    /// 起動時の自動移行で適用される。
    /// </summary>
    public partial class AddApprovedOrderEntryBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntryCondition",
                table: "approved_orders",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EntryReferencePrice",
                table: "approved_orders",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryCondition",
                table: "approved_orders");

            migrationBuilder.DropColumn(
                name: "EntryReferencePrice",
                table: "approved_orders");
        }
    }
}
