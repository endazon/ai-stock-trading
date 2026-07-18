using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.Report.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewState",
                table: "reports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // IADR-0071 決定5: 既存の確定済み行はレビュー局面 Confirmed(3) に整合させる（新規列の既定 Drafting(0) では不整合）。
            // 列挙値: ReviewState.Confirmed = 3、ReportState.Confirmed = 1。
            migrationBuilder.Sql("""UPDATE "reports" SET "ReviewState" = 3 WHERE "State" = 1;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "reports");
        }
    }
}
