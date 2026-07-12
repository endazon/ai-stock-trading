using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approved_orders",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    ProductType = table.Column<int>(type: "integer", nullable: false),
                    PositionEffect = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approved_orders", x => x.DecisionId);
                });

            migrationBuilder.CreateTable(
                name: "trade_fills",
                columns: table => new
                {
                    OrderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilledQuantity = table.Column<int>(type: "integer", nullable: false),
                    AveragePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_fills", x => x.OrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trade_fills_DecisionId",
                table: "trade_fills",
                column: "DecisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approved_orders");

            migrationBuilder.DropTable(
                name: "trade_fills");
        }
    }
}
