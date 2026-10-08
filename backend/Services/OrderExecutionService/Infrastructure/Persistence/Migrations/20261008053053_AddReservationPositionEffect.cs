using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, UC-06, ADR-0050 決定1, #1262, IADR-0515 追記(2): 予約の行にも送る発注の建て・決済の別（序数。0＝Open / 1＝Close）を残す。
    // 突合が発注済みと確定したとき、証券会社の照会は建て・決済の別を返さない（moomoo は Open で近似する）ため、この値で記録を書く。**列の追加だけ**である
    // （integer NULL）。既存行は null ＝「分からない」で読まれ、記録は照会の値のまま（是正前と同じ。推測で決済にしない）。
    public partial class AddReservationPositionEffect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PositionEffect",
                table: "order_dispatch_reservations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PositionEffect",
                table: "order_dispatch_reservations");
        }
    }
}
