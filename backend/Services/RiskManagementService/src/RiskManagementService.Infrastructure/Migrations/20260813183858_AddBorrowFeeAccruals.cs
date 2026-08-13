using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBorrowFeeAccruals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "borrow_fee_accruals",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    TradingDay = table.Column<DateOnly>(type: "date", nullable: false),
                    RateAnnual = table.Column<decimal>(type: "numeric", nullable: false),
                    PositionValueUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    AccruedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_borrow_fee_accruals", x => new { x.Symbol, x.Market, x.TradingDay });
                });

            migrationBuilder.CreateTable(
                name: "borrow_fee_unavailable_days",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    TradingDay = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_borrow_fee_unavailable_days", x => new { x.Symbol, x.Market, x.TradingDay });
                });

            migrationBuilder.CreateIndex(
                name: "IX_borrow_fee_accruals_TradingDay",
                table: "borrow_fee_accruals",
                column: "TradingDay");

            migrationBuilder.CreateIndex(
                name: "IX_borrow_fee_unavailable_days_TradingDay",
                table: "borrow_fee_unavailable_days",
                column: "TradingDay");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "borrow_fee_accruals");

            migrationBuilder.DropTable(
                name: "borrow_fee_unavailable_days");
        }
    }
}
