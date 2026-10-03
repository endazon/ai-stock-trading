using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定6: 予約の行にも承認の発注意図の「下限を掛けてラインを引いた」印を残す。突合が発注済みと
    // 確定したとき、ブローカーの注文から組み直す発注結果へ写す。**列の追加だけ**である（integer NULL）。既存行は null ＝「分からない」。
    public partial class AddReservationStopFloorSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StopFloorSource",
                table: "order_dispatch_reservations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StopFloorSource",
                table: "order_dispatch_reservations");
        }
    }
}
