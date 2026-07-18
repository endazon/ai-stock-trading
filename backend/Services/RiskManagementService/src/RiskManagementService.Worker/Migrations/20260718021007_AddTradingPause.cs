using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pause",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pause", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pause");
        }
    }
}
