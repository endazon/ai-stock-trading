using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountEquityDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_equity_days",
                columns: table => new
                {
                    TradingDay = table.Column<DateOnly>(type: "date", nullable: false),
                    EquityInBase = table.Column<decimal>(type: "numeric", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_equity_days", x => x.TradingDay);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_equity_days");
        }
    }
}
