using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定2: 承認の出どころ（序数。1＝TradeDecision / 2＝OwnerClose / 3＝MaintenanceMarginReduction）を
    // 発注結果に残す。**列の追加だけ**である（integer NULL）。既存行は null ＝「分からない」で読まれ、S1 の決済の前の取消は
    // 従来どおり判断の手仕舞いと同じく取り消す側へ倒す（既存行を埋める移行はしない。出どころを推測で埋めると利用者の手仕舞いと取り違える）。
    public partial class AddExecutedOrderApprovalOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalOrigin",
                table: "executed_orders",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalOrigin",
                table: "executed_orders");
        }
    }
}
