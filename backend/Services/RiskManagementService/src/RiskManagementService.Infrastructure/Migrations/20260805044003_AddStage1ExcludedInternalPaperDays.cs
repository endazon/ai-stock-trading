using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStage1ExcludedInternalPaperDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Stage1ExcludedInternalPaperDays",
                table: "stage_performance",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Stage1ExcludedInternalPaperDays",
                table: "stage_performance");
        }
    }
}
