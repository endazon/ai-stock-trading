using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.Report.Worker.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    PeriodKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    BasedOn = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssumptionsVersion = table.Column<int>(type: "integer", nullable: false),
                    PolicySummary = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.PeriodKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reports_Kind_State_PeriodStart",
                table: "reports",
                columns: new[] { "Kind", "State", "PeriodStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reports");
        }
    }
}
