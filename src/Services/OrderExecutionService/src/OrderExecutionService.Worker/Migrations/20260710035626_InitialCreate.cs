using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.OrderExecution.Worker.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "executed_orders",
                columns: table => new
                {
                    OrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    ProductType = table.Column<int>(type: "integer", nullable: false),
                    PositionEffect = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    PlannedPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    FilledQuantity = table.Column<int>(type: "integer", nullable: false),
                    AveragePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SlippageRatio = table.Column<decimal>(type: "numeric", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_executed_orders", x => x.OrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_executed_orders_DecisionId",
                table: "executed_orders",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_executed_orders_ExecutedAt",
                table: "executed_orders",
                column: "ExecutedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "executed_orders");
        }
    }
}
