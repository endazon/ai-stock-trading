using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, UC-06, ADR-0050 決定1, #1253, IADR-0515 追記(1): 予約の行にも承認の出どころ（序数。1＝TradeDecision / 2＝OwnerClose /
    // 3＝MaintenanceMarginReduction）を残す。突合が発注済みと確定したとき、ブローカーの注文から組み直す発注結果へ写す。**列の追加だけ**である
    // （integer NULL）。既存行は null ＝「分からない」で読まれ、S1 の決済の前の取消は従来どおり取り消す側へ倒す（推測で埋めない）。
    public partial class AddReservationApprovalOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalOrigin",
                table: "order_dispatch_reservations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalOrigin",
                table: "order_dispatch_reservations");
        }
    }
}
