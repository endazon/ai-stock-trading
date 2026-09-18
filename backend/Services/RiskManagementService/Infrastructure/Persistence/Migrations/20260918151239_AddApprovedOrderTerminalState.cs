using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovedOrderTerminalState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TerminalAt",
                table: "approved_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TerminalStatus",
                table: "approved_orders",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TerminalAt",
                table: "approved_orders");

            migrationBuilder.DropColumn(
                name: "TerminalStatus",
                table: "approved_orders");
        }
    }
}
