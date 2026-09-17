using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareStopColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mechanism",
                table: "protective_stop_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TriggeredAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TriggeredPrice",
                table: "protective_stop_orders",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mechanism",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "TriggeredAt",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "TriggeredPrice",
                table: "protective_stop_orders");
        }
    }
}
