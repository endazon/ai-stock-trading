using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddStageGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stage_performance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BacktestPassed = table.Column<bool>(type: "boolean", nullable: false),
                    BacktestMaxDrawdownRatio = table.Column<decimal>(type: "numeric", nullable: false),
                    ObservedMaxDrawdownRatio = table.Column<decimal>(type: "numeric", nullable: false),
                    PaperDeviationExplained = table.Column<bool>(type: "boolean", nullable: false),
                    ControlViolationCount = table.Column<int>(type: "integer", nullable: false),
                    SlippageAndCostWithinExpected = table.Column<bool>(type: "boolean", nullable: false),
                    DailyLossLimitRespected = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stage_performance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stage_transitions",
                columns: table => new
                {
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    FromStage = table.Column<int>(type: "integer", nullable: false),
                    ToStage = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stage_transitions", x => x.Sequence);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stage_performance");

            migrationBuilder.DropTable(
                name: "stage_transitions");
        }
    }
}
