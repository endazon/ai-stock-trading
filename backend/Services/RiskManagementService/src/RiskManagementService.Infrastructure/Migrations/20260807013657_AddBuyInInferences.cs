using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyInInferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "buy_in_inferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    LedgerShortQuantity = table.Column<int>(type: "integer", nullable: false),
                    BrokerShortQuantity = table.Column<int>(type: "integer", nullable: false),
                    InFlightCloseQuantity = table.Column<int>(type: "integer", nullable: false),
                    UnexplainedQuantity = table.Column<int>(type: "integer", nullable: false),
                    NewlyInferredQuantity = table.Column<int>(type: "integer", nullable: false),
                    BanUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    InferredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InferredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buy_in_inferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_buy_in_inferences_InferredOn",
                table: "buy_in_inferences",
                column: "InferredOn");

            migrationBuilder.CreateIndex(
                name: "IX_buy_in_inferences_Symbol_Market",
                table: "buy_in_inferences",
                columns: new[] { "Symbol", "Market" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "buy_in_inferences");
        }
    }
}
