using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectiveStopOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "protective_stop_orders",
                columns: table => new
                {
                    EntryDecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StopDecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StopOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    EntrySide = table.Column<int>(type: "integer", nullable: false),
                    ProductType = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    TriggerPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    FxRateToBase = table.Column<decimal>(type: "numeric", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protective_stop_orders", x => x.EntryDecisionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_protective_stop_orders_State_CreatedAt",
                table: "protective_stop_orders",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "protective_stop_orders");
        }
    }
}
