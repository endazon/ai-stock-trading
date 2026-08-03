using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.OrderExecution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_order_dispatch_reservations_State_CompletedAt",
                table: "order_dispatch_reservations",
                columns: new[] { "State", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_dispatch_reservations_State_CompletedAt",
                table: "order_dispatch_reservations");
        }
    }
}
