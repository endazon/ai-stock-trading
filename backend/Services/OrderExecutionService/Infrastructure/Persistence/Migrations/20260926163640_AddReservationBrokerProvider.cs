using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 NFR-09, ADR-0045 決定2, #1051, IADR-0444 決定1: 予約に「送る先の取引環境（発注先の序数）」を残す。
    // **列の追加だけ**である（integer NULL）。既存行は null ＝「取引環境が不明」で読まれ、どちらの解放の門を開けても
    // 解放されない（原則 A）。既存行を埋める移行はしない（不明を SIMULATE と推測しない）。
    public partial class AddReservationBrokerProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BrokerProvider",
                table: "order_dispatch_reservations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrokerProvider",
                table: "order_dispatch_reservations");
        }
    }
}
