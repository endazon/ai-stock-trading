using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketMonitorService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorSettingsSeedFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClearedByUserAt",
                table: "monitor_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SeededAt",
                table: "monitor_settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClearedByUserAt",
                table: "monitor_settings");

            migrationBuilder.DropColumn(
                name: "SeededAt",
                table: "monitor_settings");
        }
    }
}
